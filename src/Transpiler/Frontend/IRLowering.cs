using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SharpForge.Transpiler.IR;
using SharpForge.Transpiler.Pipeline;
using System.Globalization;

namespace SharpForge.Transpiler.Frontend;

/// <summary>
/// Lowers a Roslyn <see cref="CSharpCompilation"/> into the SharpForge IR.
/// Supports: namespaces, instance &amp; static classes, constructors, instance/static
/// methods, fields (declared as locals on <c>self</c> via the constructor), implicit
/// <c>this</c> field access, compound assignment, interpolated strings, and the
/// usual control-flow / arithmetic statements.
/// </summary>
public sealed class IRLowering
{
    private readonly HashSet<string> _ignoredClasses;
    private readonly HashSet<string> _ignoredNamespaces;
    private IRModule? _module;
    private IRType? _currentIrType;
    private string? _currentIrTypeFullName;
    private readonly Dictionary<ISymbol, string> _luaNames = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<INamedTypeSymbol, string> _luaObjectModuleNames = new(SymbolEqualityComparer.Default);
    private readonly Stack<LuaModuleBlockContext> _luaModuleBlockContexts = new();
    private int? _luaModuleInsertionIndex;
    private readonly Dictionary<ISymbol, FlattenedStructLocal> _flattenedStructLocals = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<ISymbol, FlattenedStructMember> _flattenedStructMembers = new(SymbolEqualityComparer.Default);
    private INamedTypeSymbol? _currentStructSelfType;
    private IReadOnlyDictionary<string, string>? _currentStructSelfFields;
    private readonly HashSet<string> _usedLuaNames = new(StringComparer.Ordinal);
    private int _luaNameCounter;
    private IAssemblySymbol? _compilationAssembly;
    private readonly List<INamedTypeSymbol> _knownExceptionTypes = new();

    public IRLowering(
        IEnumerable<string>? ignoredClasses = null,
        DirectoryInfo? sourceRoot = null,
        IEnumerable<string>? ignoredNamespaces = null)
    {
        _ignoredClasses = ignoredClasses is null
            ? new HashSet<string>(new[] { TranspileOptions.DefaultIgnoredClass }, StringComparer.Ordinal)
            : new HashSet<string>(ignoredClasses, StringComparer.Ordinal);
        _ignoredNamespaces = new HashSet<string>(
            ignoredNamespaces ?? new[] { TranspileOptions.DefaultIgnoredNamespace },
            StringComparer.Ordinal);
    }

    public IRModule Lower(CSharpCompilation compilation, CancellationToken cancellationToken)
    {
        var module = new IRModule();
        _module = module;
        _compilationAssembly = compilation.Assembly;
        _luaNames.Clear();
        _luaObjectModuleNames.Clear();
        _luaModuleBlockContexts.Clear();
        _luaModuleInsertionIndex = null;
        _flattenedStructLocals.Clear();
        _flattenedStructMembers.Clear();
        _currentStructSelfType = null;
        _currentStructSelfFields = null;
        _usedLuaNames.Clear();
        _luaNameCounter = 0;
        _knownExceptionTypes.Clear();
        var compilationUnits = new List<(CompilationUnitSyntax Root, SemanticModel Model)>();

        foreach (var tree in compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var model = compilation.GetSemanticModel(tree);
            var root = (CompilationUnitSyntax)tree.GetRoot(cancellationToken);
            compilationUnits.Add((root, model));

            RegisterFlattenedStructMembers(root, model, cancellationToken);
        }

        foreach (var (root, model) in compilationUnits)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var enumDecl in root.DescendantNodes().OfType<EnumDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(enumDecl, cancellationToken) is not { } symbol)
                {
                    continue;
                }

                if (_ignoredClasses.Contains(symbol.Name) || IsSFLibType(symbol))
                {
                    continue;
                }

                ValidateReservedIdentifiers(enumDecl);
                module.Enums.Add(LowerEnum(enumDecl, symbol, model, cancellationToken));
            }

            foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var symbol = model.GetDeclaredSymbol(typeDecl, cancellationToken);
                if (symbol is null)
                {
                    continue;
                }

                if (_ignoredClasses.Contains(symbol.Name))
                {
                    continue;
                }

                if (IsSFLibType(symbol))
                {
                    continue;
                }

                if (InheritsFromHandle(symbol))
                {
                    // JASS handle hierarchy (rooted at `handle`) consists of extern stubs
                    // emitted by sf-jassgen; never lower them to Lua.
                    continue;
                }

                if (IsExternalLuaObjectType(symbol))
                {
                    continue;
                }

                ValidateReservedIdentifiers(typeDecl);
                ValidateStructRuntimeFeatures(typeDecl, symbol, model, cancellationToken);
                RegisterExceptionType(symbol);
                module.Types.Add(LowerType(typeDecl, symbol, model, cancellationToken));
            }
        }

        module.Enums.Sort((left, right) => StringComparer.Ordinal.Compare(left.FullName, right.FullName));
        SortTypesByInheritance(module.Types);
        ValidateEntryPoints(module);
        _module = null;
        return module;
    }

    private void RegisterFlattenedStructMembers(CompilationUnitSyntax root, SemanticModel model, CancellationToken ct)
    {
        foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            var symbol = model.GetDeclaredSymbol(typeDecl, ct);
            if (symbol is null
                || _ignoredClasses.Contains(symbol.Name)
                || IsSFLibType(symbol)
                || InheritsFromHandle(symbol)
                || IsExternalLuaObjectType(symbol))
            {
                continue;
            }

            foreach (var member in typeDecl.Members)
            {
                switch (member)
                {
                    case FieldDeclarationSyntax field:
                    {
                        var isStatic = field.Modifiers.Any(SyntaxKind.StaticKeyword) || field.Modifiers.Any(SyntaxKind.ConstKeyword);
                        var memberType = model.GetTypeInfo(field.Declaration.Type).Type;
                        foreach (var variable in field.Declaration.Variables)
                        {
                            RegisterFlattenedStructMember(model.GetDeclaredSymbol(variable, ct), variable.Identifier.ValueText, memberType, isStatic);
                        }

                        break;
                    }

                    case PropertyDeclarationSyntax property when IsAutoProperty(property):
                        RegisterFlattenedStructMember(
                            model.GetDeclaredSymbol(property, ct),
                            property.Identifier.ValueText,
                            model.GetTypeInfo(property.Type).Type,
                            property.Modifiers.Any(SyntaxKind.StaticKeyword));
                        break;
                }
            }
        }
    }

    private void RegisterFlattenedStructMember(ISymbol? memberSymbol, string memberName, ITypeSymbol? memberType, bool isStatic)
    {
        if (memberSymbol is null
            || memberType is not INamedTypeSymbol structType
            || !CanFlattenStructType(structType))
        {
            return;
        }

        var fieldSlots = GetFlattenableStructFields(structType).ToArray();
        if (fieldSlots.Length == 0)
        {
            return;
        }

        var fieldMembers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in fieldSlots)
        {
            fieldMembers[field.Name] = $"{memberName}__{field.Name}";
        }

        _flattenedStructMembers[memberSymbol] = new FlattenedStructMember(isStatic, memberSymbol.ContainingType, fieldMembers);
    }

    private IREnum LowerEnum(EnumDeclarationSyntax enumDecl, INamedTypeSymbol symbol, SemanticModel model, CancellationToken ct)
    {
        var nsSegments = GetTypeContainerSegments(symbol);
        var irEnum = new IREnum
        {
            NamespaceSegments = nsSegments,
            Name = symbol.Name,
            FullName = string.Join('.', nsSegments.Append(symbol.Name)),
        };
        irEnum.Comments.AddRange(ExtractComments(enumDecl.GetLeadingTrivia()));

        if (HasFlagsAttribute(symbol))
        {
            AddDiagnostic(enumDecl.GetLocation(), "[Flags] enums are not supported yet; SharpForge currently emits plain numeric enum constants only");
        }

        foreach (var memberDecl in enumDecl.Members)
        {
            if (model.GetDeclaredSymbol(memberDecl, ct) is not IFieldSymbol memberSymbol)
            {
                continue;
            }

            var member = new IREnumMember
            {
                Name = memberDecl.Identifier.ValueText,
                Value = LowerEnumMemberValue(memberSymbol, memberDecl),
            };
            member.Comments.AddRange(ExtractComments(memberDecl.GetLeadingTrivia()));
            irEnum.Members.Add(member);
        }

        return irEnum;
    }



    private static bool InheritsFromHandle(INamedTypeSymbol symbol)
    {
        for (var t = symbol; t is not null; t = t.BaseType)
        {
            if (t.Name == "handle")
            {
                return true;
            }
        }
        return false;
    }

    private IRType LowerType(TypeDeclarationSyntax typeDecl, INamedTypeSymbol symbol, SemanticModel model, CancellationToken ct)
    {
        var nsSegments = GetTypeContainerSegments(symbol);
        var isTableLiteral = HasLuaTableLiteralAttribute(symbol);
        var irType = new IRType
        {
            NamespaceSegments = nsSegments,
            Name = symbol.Name,
            FullName = string.Join('.', nsSegments.Append(symbol.Name)),
            IsStatic = symbol.IsStatic,
            IsInterface = symbol.TypeKind == TypeKind.Interface,
            IsStruct = symbol.TypeKind == TypeKind.Struct,
            IsTableLiteral = isTableLiteral,
            BaseType = isTableLiteral ? null : GetLowerableBaseType(symbol),
            LuaClass = isTableLiteral ? null : GetLuaClass(symbol),
        };
        irType.Comments.AddRange(ExtractComments(typeDecl.GetLeadingTrivia()));
        irType.LuaRequires.AddRange(GetLuaAttributeValues(symbol, "Require"));

        _currentIrType = irType;
        _currentIrTypeFullName = irType.FullName;

        foreach (var iface in symbol.Interfaces.Where(i => !irType.IsStruct && !IsIgnoredClass(i) && !IsSFLibType(i)))
        {
            irType.Interfaces.Add(LowerTypeReference(iface));
        }

        foreach (var member in typeDecl.Members)
        {
            switch (member)
            {
                case MethodDeclarationSyntax when irType.IsInterface:
                    break;
                case ConstructorDeclarationSyntax c:
                    irType.Methods.Add(LowerConstructor(c, symbol, model, ct));
                    break;
                case MethodDeclarationSyntax m:
                    irType.Methods.Add(LowerMethod(m, symbol, model, ct));
                    break;
                case OperatorDeclarationSyntax o:
                    irType.Methods.Add(LowerOperator(o, model, ct));
                    break;
                case FieldDeclarationSyntax f when f.Modifiers.Any(SyntaxKind.StaticKeyword) || f.Modifiers.Any(SyntaxKind.ConstKeyword):
                    var staticFieldComments = ExtractComments(f.GetLeadingTrivia());
                    foreach (var v in f.Declaration.Variables)
                    {
                        if (TryAddFlattenedStructMemberFields(
                            irType,
                            model.GetDeclaredSymbol(v, ct),
                            v.Identifier.ValueText,
                            model.GetTypeInfo(f.Declaration.Type).Type,
                            v.Initializer,
                            isStatic: true,
                            staticFieldComments,
                            model))
                        {
                            continue;
                        }

                        var field = new IRField
                        {
                            Name = v.Identifier.ValueText,
                            Initializer = v.Initializer is null
                                ? LowerDefaultValue(model.GetTypeInfo(f.Declaration.Type).Type ?? throw new InvalidOperationException("Field type was not bound."))
                                : LowerExpr(v.Initializer.Value, model),
                            IsStatic = true,
                        };
                        field.Comments.AddRange(staticFieldComments);
                        irType.Fields.Add(field);
                    }
                    break;
                case FieldDeclarationSyntax f:
                    var fieldComments = ExtractComments(f.GetLeadingTrivia());
                    foreach (var v in f.Declaration.Variables)
                    {
                        if (TryAddFlattenedStructMemberFields(
                            irType,
                            model.GetDeclaredSymbol(v, ct),
                            v.Identifier.ValueText,
                            model.GetTypeInfo(f.Declaration.Type).Type,
                            v.Initializer,
                            isStatic: false,
                            fieldComments,
                            model))
                        {
                            continue;
                        }

                        var field = new IRField
                        {
                            Name = v.Identifier.ValueText,
                            Initializer = v.Initializer is null
                                ? LowerDefaultValue(model.GetTypeInfo(f.Declaration.Type).Type ?? throw new InvalidOperationException("Field type was not bound."))
                                : LowerExpr(v.Initializer.Value, model),
                            IsStatic = false,
                        };
                        field.Comments.AddRange(fieldComments);
                        irType.Fields.Add(field);
                    }
                    break;
                case PropertyDeclarationSyntax p:
                    if (IsAutoProperty(p))
                    {
                        if (TryAddFlattenedStructMemberFields(
                            irType,
                            model.GetDeclaredSymbol(p, ct),
                            p.Identifier.ValueText,
                            model.GetTypeInfo(p.Type).Type,
                            p.Initializer,
                            p.Modifiers.Any(SyntaxKind.StaticKeyword),
                            ExtractComments(p.GetLeadingTrivia()),
                            model))
                        {
                            break;
                        }

                        var field = new IRField
                        {
                            Name = p.Identifier.ValueText,
                            Initializer = p.Initializer is null
                                ? LowerDefaultValue(model.GetTypeInfo(p.Type).Type ?? throw new InvalidOperationException("Property type was not bound."))
                                : LowerExpr(p.Initializer.Value, model),
                            IsStatic = p.Modifiers.Any(SyntaxKind.StaticKeyword),
                        };
                        field.Comments.AddRange(ExtractComments(p.GetLeadingTrivia()));
                        irType.Fields.Add(field);
                    }
                    else
                    {
                        irType.Methods.AddRange(LowerProperty(p, symbol, model, ct));
                    }
                    break;
                case IndexerDeclarationSyntax indexer:
                    irType.Methods.AddRange(LowerIndexer(indexer, model, ct));
                    break;
                case EventFieldDeclarationSyntax e:
                    var eventComments = ExtractComments(e.GetLeadingTrivia());
                    foreach (var v in e.Declaration.Variables)
                    {
                        var field = new IRField
                        {
                            Name = v.Identifier.ValueText,
                            Initializer = new IRLiteral(null, IRLiteralKind.Nil),
                            IsStatic = e.Modifiers.Any(SyntaxKind.StaticKeyword),
                        };
                        field.Comments.AddRange(eventComments);
                        irType.Fields.Add(field);
                    }
                    break;
            }
        }

        if (!irType.IsStatic && !irType.IsInterface && !irType.IsTableLiteral && irType.Methods.All(m => !m.IsConstructor))
        {
            irType.Methods.Add(new IRFunction
            {
                Name = symbol.Name,
                LuaName = "New",
                InitLuaName = "__Init",
                BaseConstructorCall = GetImplicitBaseConstructorCall(symbol),
                IsConstructor = true,
                IsInstance = false,
            });
        }

        _currentIrType = null;
        _currentIrTypeFullName = null;
        return irType;
    }

    private static bool IsAutoProperty(PropertyDeclarationSyntax property)
        => property.ExpressionBody is null
           && property.AccessorList?.Accessors.All(a => a.Body is null && a.ExpressionBody is null) == true;

    private static IReadOnlyList<string> ExtractComments(SyntaxTriviaList triviaList)
    {
        var comments = new List<string>();
        foreach (var trivia in triviaList)
        {
            switch (trivia.Kind())
            {
                case SyntaxKind.SingleLineCommentTrivia:
                    comments.Add(StripSingleLineComment(trivia.ToFullString(), "//"));
                    break;
                case SyntaxKind.SingleLineDocumentationCommentTrivia:
                    comments.AddRange(StripPrefixedCommentLines(trivia.ToFullString(), "///"));
                    break;
                case SyntaxKind.MultiLineCommentTrivia:
                case SyntaxKind.MultiLineDocumentationCommentTrivia:
                    comments.AddRange(StripMultiLineComment(trivia.ToFullString()));
                    break;
            }
        }

        return comments;
    }

    private static string StripSingleLineComment(string text, string prefix)
    {
        var line = text.Trim();
        return line.StartsWith(prefix, StringComparison.Ordinal)
            ? line[prefix.Length..].TrimStart()
            : line;
    }

    private static IEnumerable<string> StripPrefixedCommentLines(string text, string prefix)
    {
        foreach (var rawLine in SplitCommentLines(text))
        {
            var line = rawLine.TrimStart();
            yield return line.StartsWith(prefix, StringComparison.Ordinal)
                ? line[prefix.Length..].TrimStart()
                : line;
        }
    }

    private static IEnumerable<string> StripMultiLineComment(string text)
    {
        var body = text.Trim();
        if (body.StartsWith("/**", StringComparison.Ordinal))
        {
            body = body[3..];
        }
        else if (body.StartsWith("/*", StringComparison.Ordinal))
        {
            body = body[2..];
        }

        if (body.EndsWith("*/", StringComparison.Ordinal))
        {
            body = body[..^2];
        }

        var lines = SplitCommentLines(body)
            .Select(line =>
            {
                var trimmed = line.Trim();
                return trimmed.StartsWith("*", StringComparison.Ordinal)
                    ? trimmed[1..].TrimStart()
                    : trimmed;
            })
            .ToArray();

        if (lines.Length == 0)
        {
            yield return string.Empty;
            yield break;
        }

        foreach (var line in lines)
        {
            yield return line;
        }
    }

    private static string[] SplitCommentLines(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

    private static IReadOnlyList<string> GetNamespaceSegments(INamespaceSymbol? ns)
    {
        if (ns is null || ns.IsGlobalNamespace)
        {
            return Array.Empty<string>();
        }

        var stack = new Stack<string>();
        for (var n = ns; n is not null && !n.IsGlobalNamespace; n = n.ContainingNamespace)
        {
            stack.Push(n.Name);
        }
        return stack.ToArray();
    }

    private static IReadOnlyList<string> GetTypeContainerSegments(INamedTypeSymbol type)
    {
        var segments = new List<string>(GetNamespaceSegments(type.ContainingNamespace));
        var containingTypes = new Stack<string>();
        for (var current = type.ContainingType; current is not null; current = current.ContainingType)
        {
            containingTypes.Push(current.Name);
        }

        segments.AddRange(containingTypes);
        return segments;
    }

    private IRFunction LowerConstructor(ConstructorDeclarationSyntax c, INamedTypeSymbol owner, SemanticModel model, CancellationToken ct)
    {
        var symbol = model.GetDeclaredSymbol(c, ct);
        if (c.Modifiers.Any(SyntaxKind.StaticKeyword))
        {
            var staticFn = new IRFunction
            {
                Name = ".cctor",
                LuaName = "__StaticInit",
                IsStatic = true,
                IsStaticConstructor = true,
            };
            staticFn.Comments.AddRange(ExtractComments(c.GetLeadingTrivia()));

            if (c.Body is { } staticBody)
            {
                LowerBlock(staticBody, staticFn.Body, model, ct);
            }

            return staticFn;
        }

        var fn = new IRFunction
        {
            Name = c.Identifier.ValueText,
            LuaName = symbol is null ? "New" : GetLuaMethodName(symbol),
            InitLuaName = symbol is null ? "__Init" : GetLuaConstructorInitName(symbol),
            IsConstructor = true,
            IsInstance = false, // emit with `.` because we create `self` ourselves
        };
        fn.Comments.AddRange(ExtractComments(c.GetLeadingTrivia()));

        AddLoweredParameters(fn, c.ParameterList.Parameters, model, ct);
        fn.ThisConstructorCall = LowerThisConstructorCall(c, model);
        fn.BaseConstructorCall = LowerBaseConstructorCall(c, owner, model);

        if (c.Body is { } body)
        {
            LowerBlock(body, fn.Body, model, ct);
        }
        else if (c.ExpressionBody is { } arrow)
        {
            fn.Body.Statements.Add(new IRExprStmt(LowerExpr(arrow.Expression, model)));
        }

        return fn;
    }

    private IRThisConstructorCall? LowerThisConstructorCall(ConstructorDeclarationSyntax constructor, SemanticModel model)
    {
        if (constructor.Initializer is not { } initializer
            || !initializer.IsKind(SyntaxKind.ThisConstructorInitializer)
            || model.GetSymbolInfo(initializer).Symbol is not IMethodSymbol thisCtor)
        {
            return null;
        }

        return new IRThisConstructorCall(
            LowerTypeReference(thisCtor.ContainingType),
            GetLuaConstructorInitName(thisCtor),
            initializer.ArgumentList.Arguments.Select(a => LowerExpr(a.Expression, model)).ToArray());
    }

    private IRBaseConstructorCall? LowerBaseConstructorCall(
        ConstructorDeclarationSyntax constructor,
        INamedTypeSymbol owner,
        SemanticModel model)
    {
        if (constructor.Initializer is { } initializer)
        {
            if (!initializer.IsKind(SyntaxKind.BaseConstructorInitializer))
            {
                return null;
            }

            var baseCtor = model.GetSymbolInfo(initializer).Symbol as IMethodSymbol;
            return baseCtor is null || IsExternalLuaObjectType(baseCtor.ContainingType)
                ? null
                : new IRBaseConstructorCall(
                    LowerTypeReference(baseCtor.ContainingType),
                    GetLuaConstructorInitName(baseCtor),
                    initializer.ArgumentList.Arguments.Select(a => LowerExpr(a.Expression, model)).ToArray());
        }

        return GetImplicitBaseConstructorCall(owner);
    }

    private IRFunction LowerMethod(MethodDeclarationSyntax m, INamedTypeSymbol owner, SemanticModel model, CancellationToken ct)
    {
        var isStatic = m.Modifiers.Any(SyntaxKind.StaticKeyword);
        var isStructInstanceMethod = owner.TypeKind == TypeKind.Struct && !isStatic;
        var symbol = model.GetDeclaredSymbol(m, ct);
        var fn = new IRFunction
        {
            Name = m.Identifier.ValueText,
            LuaName = symbol is null ? m.Identifier.ValueText : GetLuaMethodName(symbol),
            IsStatic = isStatic,
            IsInstance = !isStatic && !isStructInstanceMethod,
            IsCoroutine = m.Modifiers.Any(SyntaxKind.AsyncKeyword) && ContainsTaskDelay(m, model),
            IsEntryPoint = symbol is not null && IsEntryPointCandidate(symbol),
        };
        fn.Comments.AddRange(ExtractComments(m.GetLeadingTrivia()));

        IReadOnlyDictionary<string, string>? previousStructSelfFields = null;
        INamedTypeSymbol? previousStructSelfType = null;
        if (isStructInstanceMethod)
        {
            previousStructSelfType = _currentStructSelfType;
            previousStructSelfFields = _currentStructSelfFields;
            _currentStructSelfType = owner;
            _currentStructSelfFields = AddFlattenedStructSelfParameters(fn, owner);
        }

        try
        {
            AddLoweredTypeParameters(fn, m.TypeParameterList?.Parameters ?? default, model, ct);
            AddLoweredParameters(fn, m.ParameterList.Parameters, model, ct);

            if (m.DescendantNodes().OfType<YieldStatementSyntax>().Any())
            {
                fn.Body.Statements.Add(new IRReturn(new IRArrayLiteral(
                    m.DescendantNodes()
                        .OfType<YieldStatementSyntax>()
                        .Where(y => y.IsKind(SyntaxKind.YieldReturnStatement) && y.Expression is not null)
                        .Select(y => LowerExpr(y.Expression!, model))
                        .ToArray())));
                return fn;
            }

            if (m.Body is { } body)
            {
                LowerBlock(body, fn.Body, model, ct);
            }
            else if (m.ExpressionBody is { } arrow)
            {
                fn.Body.Statements.Add(LowerReturnExpression(arrow.Expression, model));
            }
        }
        finally
        {
            if (isStructInstanceMethod)
            {
                _currentStructSelfType = previousStructSelfType;
                _currentStructSelfFields = previousStructSelfFields;
            }
        }

        return fn;
    }

    private void ValidateEntryPoints(IRModule module)
    {
        var entries = module.Types
            .SelectMany(type => type.Methods
                .Where(method => method.IsEntryPoint)
                .Select(method => string.Join('.', type.NamespaceSegments.Append(type.Name).Append(method.Name))))
            .ToArray();

        if (entries.Length <= 1)
        {
            return;
        }

        AddDiagnostic("multiple static Main entry points found: " + string.Join(", ", entries));
    }

    private static bool IsEntryPointCandidate(IMethodSymbol method)
        => method.Name == "Main"
            && method.IsStatic
            && method.MethodKind == MethodKind.Ordinary
            && IsSupportedEntryPointReturnType(method.ReturnType)
            && IsSupportedEntryPointParameters(method.Parameters);

    private static bool IsSupportedEntryPointReturnType(ITypeSymbol returnType)
    {
        if (returnType.SpecialType is SpecialType.System_Void or SpecialType.System_Int32)
        {
            return true;
        }

        if (returnType is not INamedTypeSymbol namedType)
        {
            return false;
        }

        var original = namedType.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return original is "global::System.Threading.Tasks.Task"
            or "global::System.Threading.Tasks.Task<TResult>";
    }

    private static bool IsSupportedEntryPointParameters(IReadOnlyList<IParameterSymbol> parameters)
    {
        if (parameters.Count == 0)
        {
            return true;
        }

        return parameters.Count == 1
            && parameters[0].Type is IArrayTypeSymbol { Rank: 1, ElementType.SpecialType: SpecialType.System_String };
    }

    private IEnumerable<IRFunction> LowerProperty(PropertyDeclarationSyntax p, INamedTypeSymbol owner, SemanticModel model, CancellationToken ct)
    {
        var isStatic = p.Modifiers.Any(SyntaxKind.StaticKeyword);
        var isStructInstanceProperty = owner.TypeKind == TypeKind.Struct && !isStatic;
        var propertySymbol = model.GetDeclaredSymbol(p, ct);
        if (p.ExpressionBody is { } expressionBody)
        {
            var fn = new IRFunction
            {
                Name = "get_" + p.Identifier.ValueText,
                LuaName = "get_" + p.Identifier.ValueText,
                IsStatic = isStatic,
                IsInstance = !isStatic && !isStructInstanceProperty,
            };
            LowerPropertyBody(fn, owner, isStructInstanceProperty, () => fn.Body.Statements.Add(LowerReturnExpression(expressionBody.Expression, model)));
            yield return fn;
            yield break;
        }

        foreach (var accessor in p.AccessorList?.Accessors ?? Enumerable.Empty<AccessorDeclarationSyntax>())
        {
            if (accessor.IsKind(SyntaxKind.GetAccessorDeclaration))
            {
                var fn = new IRFunction
                {
                    Name = "get_" + p.Identifier.ValueText,
                    LuaName = "get_" + p.Identifier.ValueText,
                    IsStatic = isStatic,
                    IsInstance = !isStatic && !isStructInstanceProperty,
                };
                LowerPropertyBody(fn, owner, isStructInstanceProperty, () => LowerAccessorBody(accessor, fn.Body, model, ct));
                yield return fn;
            }
            else if (accessor.IsKind(SyntaxKind.SetAccessorDeclaration))
            {
                var fn = new IRFunction
                {
                    Name = "set_" + p.Identifier.ValueText,
                    LuaName = "set_" + p.Identifier.ValueText,
                    IsStatic = isStatic,
                    IsInstance = !isStatic && !isStructInstanceProperty,
                };
                LowerPropertyBody(fn, owner, isStructInstanceProperty, () =>
                {
                    fn.Parameters.Add(DeclareLuaName(propertySymbol?.SetMethod?.Parameters.FirstOrDefault(), "value"));
                    LowerAccessorBody(accessor, fn.Body, model, ct);
                });
                yield return fn;
            }
        }
    }

    private void LowerPropertyBody(IRFunction fn, INamedTypeSymbol owner, bool isStructInstanceProperty, Action lowerBody)
    {
        if (!isStructInstanceProperty)
        {
            lowerBody();
            return;
        }

        var previousStructSelfType = _currentStructSelfType;
        var previousStructSelfFields = _currentStructSelfFields;
        _currentStructSelfType = owner;
        _currentStructSelfFields = AddFlattenedStructSelfParameters(fn, owner);

        try
        {
            lowerBody();
        }
        finally
        {
            _currentStructSelfType = previousStructSelfType;
            _currentStructSelfFields = previousStructSelfFields;
        }
    }

    private IEnumerable<IRFunction> LowerIndexer(IndexerDeclarationSyntax indexer, SemanticModel model, CancellationToken ct)
    {
        var isStatic = indexer.Modifiers.Any(SyntaxKind.StaticKeyword);
        var indexerSymbol = model.GetDeclaredSymbol(indexer, ct);
        foreach (var accessor in indexer.AccessorList?.Accessors ?? Enumerable.Empty<AccessorDeclarationSyntax>())
        {
            var isGetter = accessor.IsKind(SyntaxKind.GetAccessorDeclaration);
            var fn = new IRFunction
            {
                Name = isGetter ? "get_Item" : "set_Item",
                LuaName = isGetter ? "get_Item" : "set_Item",
                IsStatic = isStatic,
                IsInstance = !isStatic,
            };

            // Use the accessor method's parameter symbols (not the property's) so that
            // GetSymbolInfo in the body resolves to the same symbols.
            var accessorSymbol = model.GetDeclaredSymbol(accessor, ct) as IMethodSymbol;
            var syntaxParams = indexer.ParameterList.Parameters;
            for (var i = 0; i < syntaxParams.Count; i++)
            {
                var paramSymbol = accessorSymbol is not null && i < accessorSymbol.Parameters.Length
                    ? accessorSymbol.Parameters[i]
                    : model.GetDeclaredSymbol(syntaxParams[i], ct);
                fn.Parameters.Add(DeclareLuaName(paramSymbol, syntaxParams[i].Identifier.ValueText));
            }
            if (!isGetter && accessorSymbol is not null)
            {
                fn.Parameters.Add(DeclareLuaName(accessorSymbol.Parameters.LastOrDefault(), "value"));
            }

            LowerAccessorBody(accessor, fn.Body, model, ct);
            yield return fn;
        }
    }

    private void LowerAccessorBody(AccessorDeclarationSyntax accessor, IRBlock body, SemanticModel model, CancellationToken ct)
    {
        if (accessor.Body is { } block)
        {
            LowerBlock(block, body, model, ct);
        }
        else if (accessor.ExpressionBody is { } expressionBody)
        {
            body.Statements.Add(accessor.IsKind(SyntaxKind.GetAccessorDeclaration)
                ? LowerReturnExpression(expressionBody.Expression, model)
                : new IRExprStmt(LowerExpr(expressionBody.Expression, model)));
        }
    }

    private IRFunction LowerOperator(OperatorDeclarationSyntax o, SemanticModel model, CancellationToken ct)
    {
        var symbol = model.GetDeclaredSymbol(o, ct);
        var fn = new IRFunction
        {
            Name = symbol?.Name ?? GetLuaOperatorName(o.OperatorToken.ValueText),
            LuaName = symbol is null ? GetLuaOperatorName(o.OperatorToken.ValueText) : GetLuaMethodName(symbol),
            IsStatic = true,
            IsInstance = false,
        };

        AddLoweredParameters(fn, o.ParameterList.Parameters, model, ct);

        if (o.Body is { } body)
        {
            LowerBlock(body, fn.Body, model, ct);
        }
        else if (o.ExpressionBody is { } arrow)
        {
            fn.Body.Statements.Add(LowerReturnExpression(arrow.Expression, model));
        }

        return fn;
    }

    private void AddLoweredParameters(
        IRFunction fn,
        IEnumerable<ParameterSyntax> parameters,
        SemanticModel model,
        CancellationToken ct)
    {
        foreach (var parameter in parameters)
        {
            var symbol = model.GetDeclaredSymbol(parameter, ct);
            var type = symbol?.Type ?? (parameter.Type is null ? null : model.GetTypeInfo(parameter.Type).Type);
            var names = AddLoweredParameter(fn, symbol, parameter.Identifier.ValueText, type);
            AddParameterDefaultInitializers(fn, names, parameter, symbol, type, model);
        }
    }

    private void AddLoweredTypeParameters(
        IRFunction fn,
        SeparatedSyntaxList<TypeParameterSyntax> typeParameters,
        SemanticModel model,
        CancellationToken ct)
    {
        foreach (var typeParameter in typeParameters)
        {
            fn.Parameters.Add(DeclareLuaName(model.GetDeclaredSymbol(typeParameter, ct), typeParameter.Identifier.ValueText));
        }
    }

    private IReadOnlyList<string> AddLoweredParameter(IRFunction fn, ISymbol? symbol, string baseName, ITypeSymbol? type)
    {
        if (symbol is not null
            && type is INamedTypeSymbol structType
            && CanFlattenStructType(structType)
            && GetFlattenableStructFields(structType).Any())
        {
            var fieldLocals = AddFlattenedStructParameters(fn, baseName, structType);
            _flattenedStructLocals[symbol] = new FlattenedStructLocal(fieldLocals);
            return fieldLocals.Values.ToArray();
        }

        var name = DeclareLuaName(symbol, baseName);
        fn.Parameters.Add(name);
        return [name];
    }

    private void AddParameterDefaultInitializers(
        IRFunction fn,
        IReadOnlyList<string> parameterNames,
        ParameterSyntax parameter,
        IParameterSymbol? symbol,
        ITypeSymbol? type,
        SemanticModel model)
    {
        if (parameter.Default?.Value is not { } defaultValue)
        {
            return;
        }

        if (parameterNames.Count > 1 && type is INamedTypeSymbol structType)
        {
            foreach (var (name, field) in parameterNames.Zip(GetFlattenableStructFields(structType)))
            {
                fn.ParameterDefaults.Add(new IRParameterDefault(name, LowerDefaultValue(field.Type)));
            }
            return;
        }

        var loweredDefault = LowerParameterDefaultValue(defaultValue, symbol, type, model);
        if (IsNilLiteral(loweredDefault))
        {
            return;
        }

        fn.ParameterDefaults.Add(new IRParameterDefault(parameterNames[0], loweredDefault));
    }

    private IRExpr LowerParameterDefaultValue(ExpressionSyntax defaultValue, IParameterSymbol? symbol, ITypeSymbol? type, SemanticModel model)
    {
        if (defaultValue.IsKind(SyntaxKind.DefaultLiteralExpression))
        {
            return type is null ? new IRLiteral(null, IRLiteralKind.Nil) : LowerDefaultValue(type);
        }

        if (defaultValue is DefaultExpressionSyntax defaultExpression)
        {
            var defaultType = model.GetTypeInfo(defaultExpression.Type).Type ?? type;
            return defaultType is null ? new IRLiteral(null, IRLiteralKind.Nil) : LowerDefaultValue(defaultType);
        }

        if (symbol?.HasExplicitDefaultValue == true && symbol.ExplicitDefaultValue is null)
        {
            return new IRLiteral(null, IRLiteralKind.Nil);
        }

        return LowerExpr(defaultValue, model);
    }

    private static bool IsNilLiteral(IRExpr expr)
        => expr is IRLiteral { Kind: IRLiteralKind.Nil };

    private IReadOnlyDictionary<string, string> AddFlattenedStructSelfParameters(IRFunction fn, INamedTypeSymbol structType)
        => AddFlattenedStructParameters(fn, "self", structType);

    private IReadOnlyDictionary<string, string> AddFlattenedStructParameters(IRFunction fn, string baseName, INamedTypeSymbol structType)
    {
        var fieldLocals = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in GetFlattenableStructFields(structType))
        {
            var parameterName = AllocateLuaName($"{EscapeLuaKeyword(baseName)}__{field.Name}");
            fieldLocals[field.Name] = parameterName;
            fn.Parameters.Add(parameterName);
        }

        return fieldLocals;
    }

    private void LowerStatements(IEnumerable<StatementSyntax> stmts, IRBlock target, SemanticModel model, CancellationToken ct)
    {
        var statements = stmts.ToArray();
        for (var i = 0; i < statements.Length; i++)
        {
            var s = statements[i];
            ct.ThrowIfCancellationRequested();
            foreach (var comment in ExtractComments(s.GetLeadingTrivia()))
            {
                target.Statements.Add(new IRRawComment(comment));
            }

            var tracksLuaModuleInsertion = _luaModuleBlockContexts.Count > 0
                && ReferenceEquals(_luaModuleBlockContexts.Peek().Block, target);
            if (tracksLuaModuleInsertion)
            {
                _luaModuleInsertionIndex = target.Statements.Count;
            }
            try
            {
                target.Statements.Add(LowerStatement(s, model, ct));
            }
            finally
            {
                if (tracksLuaModuleInsertion)
                {
                    _luaModuleInsertionIndex = null;
                }
            }

            foreach (var comment in ExtractComments(s.GetTrailingTrivia()))
            {
                target.Statements.Add(new IRRawComment(comment));
            }

        }
    }

    private IRStmt LowerStatement(StatementSyntax s, SemanticModel model, CancellationToken ct)
    {
        switch (s)
        {
            case BlockSyntax b:
                var blk = new IRBlock();
                LowerBlock(b, blk, model, ct);
                return blk;

            case LocalDeclarationStatementSyntax ld:
                var first = ld.Declaration.Variables.First();
                var declaredSymbol = model.GetDeclaredSymbol(first);
                if (TryLowerStructLocalDeclaration(ld, first, declaredSymbol, model) is { } structLocalDeclaration)
                {
                    return structLocalDeclaration;
                }

                var localName = DeclareLuaName(declaredSymbol, first.Identifier.ValueText);
                TrackLuaObjectModuleBinding(first, localName, model);
                return new IRLocalDecl(
                    localName,
                    first.Initializer is null ? null : LowerExpr(first.Initializer.Value, model));

            case ForStatementSyntax fs:
                return LowerFor(fs, model, ct);

            case ForEachStatementSyntax fe:
                return LowerForEach(fe, model, ct);

            case TryStatementSyntax ts:
                return LowerTry(ts, model, ct);

            case ThrowStatementSyntax throwStatement:
                return LowerThrow(throwStatement, model);

            case ExpressionStatementSyntax es when es.Expression is AssignmentExpressionSyntax ae:
                return LowerAssignment(ae, model);

            case ExpressionStatementSyntax es when TryLowerLuaInteropStatement(es.Expression, model) is { } luaInteropStatement:
                return luaInteropStatement;

                case ExpressionStatementSyntax es when TryLowerConditionalDelegateInvocationStatement(es.Expression, model) is { } conditionalDelegateInvocation:
                    return conditionalDelegateInvocation;

            case ExpressionStatementSyntax es when IsIncrementOrDecrement(es.Expression):
                return LowerIncrementOrDecrement(es.Expression, model);

            case ExpressionStatementSyntax es when es.Expression is InvocationExpressionSyntax inv && LowerInvocationToStatements(inv, model) is { } invStatements:
                return invStatements;

            case ExpressionStatementSyntax es:
                return new IRExprStmt(LowerExpr(es.Expression, model));

            case ReturnStatementSyntax rs:
                return rs.Expression is null ? new IRReturn(null) : LowerReturnExpression(rs.Expression, model);

            case IfStatementSyntax ifs when TryLowerDeclarationPatternIf(ifs, model, ct) is { } patternIf:
                return patternIf;

            case IfStatementSyntax ifs:
                var thenBlk = new IRBlock();
                LowerBlock(ifs.Statement, thenBlk, model, ct);
                IRBlock? elseBlk = null;
                if (ifs.Else is { } el)
                {
                    elseBlk = new IRBlock();
                    LowerBlock(el.Statement, elseBlk, model, ct);
                }
                return new IRIf(LowerExpr(ifs.Condition, model), thenBlk, elseBlk);

            case SwitchStatementSyntax switchStatement:
                return LowerSwitch(switchStatement, model, ct);

            case WhileStatementSyntax ws:
                var whileBody = new IRBlock();
                LowerBlock(ws.Statement, whileBody, model, ct);
                return new IRWhile(LowerExpr(ws.Condition, model), whileBody);

            case BreakStatementSyntax:
                return new IRBreak();

            case ContinueStatementSyntax:
                return new IRContinue();

            case GotoStatementSyntax gotoStatement:
                AddUnsupportedDiagnostic(gotoStatement, "statement");
                return new IRRawComment($"unsupported stmt: {gotoStatement.Kind()}");

            default:
                return UnsupportedStatement(s);
        }
    }

    private IRStmt LowerSwitch(SwitchStatementSyntax switchStatement, SemanticModel model, CancellationToken ct)
    {
        var sections = new List<IRSwitchSection>();
        foreach (var section in switchStatement.Sections)
        {
            var labels = new List<IRExpr>();
            var isDefault = false;
            foreach (var label in section.Labels)
            {
                switch (label)
                {
                    case CaseSwitchLabelSyntax caseLabel:
                        if (!model.GetConstantValue(caseLabel.Value, ct).HasValue)
                        {
                            AddDiagnostic(caseLabel.GetLocation(), "switch case labels must be compile-time constants");
                        }
                        labels.Add(LowerExpr(caseLabel.Value, model));
                        break;
                    case DefaultSwitchLabelSyntax:
                        isDefault = true;
                        break;
                    default:
                        AddUnsupportedDiagnostic(label, "switch label");
                        break;
                }
            }

            var body = new IRBlock();
            LowerStatements(section.Statements, body, model, ct);
            sections.Add(new IRSwitchSection(labels, isDefault, body));
        }

        return new IRSwitch(LowerExpr(switchStatement.Expression, model), sections);
    }

    private IRStmt LowerFor(ForStatementSyntax fs, SemanticModel model, CancellationToken ct)
    {
        IRStmt? initializer = null;
        if (fs.Declaration is { } declaration)
        {
            var first = declaration.Variables.First();
            initializer = new IRLocalDecl(
                DeclareLuaName(model.GetDeclaredSymbol(first), first.Identifier.ValueText),
                first.Initializer is null ? null : LowerExpr(first.Initializer.Value, model));
        }
        else if (fs.Initializers.Count > 0)
        {
            initializer = LowerForExpression(fs.Initializers[0], model);
        }

        var body = new IRBlock();
        LowerBlock(fs.Statement, body, model, ct);
        var incrementors = fs.Incrementors.Select(i => LowerForExpression(i, model)).ToArray();

        return new IRFor(initializer, fs.Condition is null ? null : LowerExpr(fs.Condition, model), incrementors, body);
    }

    private IRStmt LowerForExpression(ExpressionSyntax expression, SemanticModel model)
        => expression switch
        {
            AssignmentExpressionSyntax assignment => LowerAssignment(assignment, model),
            _ when IsIncrementOrDecrement(expression) => LowerIncrementOrDecrement(expression, model),
            _ => new IRExprStmt(LowerExpr(expression, model)),
        };

    private IRStmt LowerForEach(ForEachStatementSyntax fe, SemanticModel model, CancellationToken ct)
    {
        var collection = LowerExpr(fe.Expression, model);
        var itemSymbol = model.GetDeclaredSymbol(fe, ct);
        var body = new IRBlock();
        var itemName = DeclareLuaName(itemSymbol, fe.Identifier.ValueText);
        LowerBlock(fe.Statement, body, model, ct);

        // IIpairs: foreach on a type implementing IIpairs<T> uses IpairsNext
        var collectionType = model.GetTypeInfo(fe.Expression).Type;
        IRExpr? iterator = null;
        if (collectionType is INamedTypeSymbol namedType && ImplementsIIpairs(namedType))
        {
            var typeRef = LowerTypeReference(namedType);
            iterator = new IRMemberAccess(typeRef, "IpairsNext");
        }

        return new IRForEach(itemName, collection, body, iterator);
    }

    private IRStmt LowerTry(TryStatementSyntax ts, SemanticModel model, CancellationToken ct)
    {
        var tryBlock = new IRBlock();
        LowerBlock(ts.Block, tryBlock, model, ct);

        var catches = new List<IRCatch>();
        foreach (var catchClause in ts.Catches)
        {
            var catchVariable = catchClause.Declaration is null
                ? null
                : DeclareLuaName(model.GetDeclaredSymbol(catchClause.Declaration), catchClause.Declaration.Identifier.ValueText);

            var catchType = catchClause.Declaration is null
                ? null
                : model.GetTypeInfo(catchClause.Declaration.Type).Type as INamedTypeSymbol;
            if (catchType is not null && catchClause.Declaration is not null && !DerivesFromSystemException(catchType))
            {
                AddUnsupportedDiagnostic(catchClause.Declaration.Type, "catch type");
            }

            var catchesAny = catchClause.Declaration is null;
            var catchesAnySharpForgeException = catchType is not null && IsSystemException(catchType);
            var headers = catchType is null || catchesAnySharpForgeException
                ? Array.Empty<string>()
                : GetExceptionHeadersAssignableTo(catchType).ToArray();

            var catchBlock = new IRBlock();
            LowerBlock(catchClause.Block, catchBlock, model, ct);
            catches.Add(new IRCatch(catchVariable, headers, catchesAnySharpForgeException, catchesAny, catchBlock));
        }

        IRBlock? finallyBlock = null;
        if (ts.Finally is { } finallyClause)
        {
            finallyBlock = new IRBlock();
            LowerBlock(finallyClause.Block, finallyBlock, model, ct);
        }

        return new IRTry(tryBlock, catches, finallyBlock);
    }

    private IRStmt LowerThrow(ThrowStatementSyntax throwStatement, SemanticModel model)
    {
        if (throwStatement.Expression is null)
        {
            return new IRThrow(null);
        }

        var thrownType = model.GetTypeInfo(throwStatement.Expression).Type as INamedTypeSymbol;
        if (thrownType is null || !DerivesFromSystemException(thrownType))
        {
            return new IRThrow(LowerExpr(throwStatement.Expression, model));
        }

        var header = GetExceptionHeader(thrownType);
        var message = TryLowerExceptionMessage(throwStatement.Expression, model)
            ?? LowerExpr(throwStatement.Expression, model);
        return new IRThrow(new IRStringConcat(new IRExpr[]
        {
            new IRLiteral(header, IRLiteralKind.String),
            message,
        }));
    }

    private IRExpr? TryLowerExceptionMessage(ExpressionSyntax expression, SemanticModel model)
    {
        if (expression is not ObjectCreationExpressionSyntax creation
            || creation.ArgumentList is null
            || creation.ArgumentList.Arguments.Count == 0)
        {
            return null;
        }

        var firstArgument = creation.ArgumentList.Arguments[0].Expression;
        return model.GetTypeInfo(firstArgument).Type?.SpecialType == SpecialType.System_String
            ? LowerExpr(firstArgument, model)
            : null;
    }

    private IRStmt LowerAssignment(AssignmentExpressionSyntax ae, SemanticModel model)
    {
        if (ae.IsKind(SyntaxKind.CoalesceAssignmentExpression))
        {
            var coalesceTarget = LowerExpr(ae.Left, model);
            var assignIfNil = new IRBlock();
            assignIfNil.Statements.Add(new IRAssign(coalesceTarget, LowerExpr(ae.Right, model)));
            return new IRIf(new IRBinary("==", coalesceTarget, new IRLiteral(null, IRLiteralKind.Nil)), assignIfNil, null);
        }

        // Tuple deconstruction: var (a, b) = expr;
        if (ae.IsKind(SyntaxKind.SimpleAssignmentExpression)
            && ae.Left is DeclarationExpressionSyntax { Designation: ParenthesizedVariableDesignationSyntax parenDesignation })
        {
            var initType = model.GetTypeInfo(ae.Right).Type;
            if (initType is INamedTypeSymbol tupleType && tupleType.IsTupleType)
            {
                var names = new List<string>();
                foreach (var variable in parenDesignation.Variables)
                {
                    if (variable is SingleVariableDesignationSyntax sv)
                    {
                        var sym = model.GetDeclaredSymbol(sv);
                        names.Add(DeclareLuaName(sym, sv.Identifier.ValueText));
                    }
                }

                var values = LowerStructArgumentValues(ae.Right, tupleType, model);
                return new IRMultiLocalDecl(names, values);
            }
        }

        if (ae.IsKind(SyntaxKind.SimpleAssignmentExpression) && TryLowerStructAssignment(ae.Left, ae.Right, model) is { } structAssignment)
        {
            return structAssignment;
        }

        if (ae.IsKind(SyntaxKind.SimpleAssignmentExpression) && TryLowerAccessorAssignment(ae.Left, ae.Right, model) is { } accessorAssignment)
        {
            return accessorAssignment;
        }

        var target = LowerExpr(ae.Left, model);
        if (ae.IsKind(SyntaxKind.AddAssignmentExpression) && IsStringExpression(ae.Left, model))
        {
            var parts = new List<IRExpr> { target };
            if (ae.Right is BinaryExpressionSyntax nestedAdd
                && nestedAdd.IsKind(SyntaxKind.AddExpression)
                && IsStringExpression(nestedAdd, model))
            {
                parts.AddRange(FlattenStringConcat(nestedAdd, model));
            }
            else
            {
                parts.Add(LowerExpr(ae.Right, model));
            }

            return new IRAssign(target, new IRStringConcat(parts));
        }

        var value = LowerExpr(ae.Right, model);

        if (ae.IsKind(SyntaxKind.SimpleAssignmentExpression))
        {
            return new IRAssign(target, value);
        }

        // Compound assignment: x op= v  ==>  x = x op v
        var op = ae.OperatorToken.ValueText.TrimEnd('=');
        return new IRAssign(target, new IRBinary(op, target, value));
    }

    private static bool IsIncrementOrDecrement(ExpressionSyntax expression)
        => expression.IsKind(SyntaxKind.PostIncrementExpression)
           || expression.IsKind(SyntaxKind.PostDecrementExpression)
           || expression.IsKind(SyntaxKind.PreIncrementExpression)
           || expression.IsKind(SyntaxKind.PreDecrementExpression);

    private IRStmt LowerIncrementOrDecrement(ExpressionSyntax expression, SemanticModel model)
    {
        var operand = expression switch
        {
            PrefixUnaryExpressionSyntax prefix => prefix.Operand,
            PostfixUnaryExpressionSyntax postfix => postfix.Operand,
            _ => expression,
        };
        var op = expression.IsKind(SyntaxKind.PostDecrementExpression) || expression.IsKind(SyntaxKind.PreDecrementExpression)
            ? "-"
            : "+";
        var target = LowerExpr(operand, model);
        return new IRAssign(target, new IRBinary(op, target, new IRLiteral(1, IRLiteralKind.Integer)));
    }

    private void LowerBlock(BlockSyntax block, IRBlock target, SemanticModel model, CancellationToken ct)
    {
        var pushedLuaModuleBlock = TryPushLuaModuleBlock(target);
        try
        {
            LowerStatements(block.Statements, target, model, ct);
            AddCommentStatements(block.CloseBraceToken.LeadingTrivia, target);
        }
        finally
        {
            PopLuaModuleBlock(pushedLuaModuleBlock);
        }
    }

    private void LowerBlock(StatementSyntax statement, IRBlock target, SemanticModel model, CancellationToken ct)
    {
        var pushedLuaModuleBlock = TryPushLuaModuleBlock(target);
        try
        {
            if (statement is BlockSyntax block)
            {
                LowerStatements(block.Statements, target, model, ct);
                AddCommentStatements(block.CloseBraceToken.LeadingTrivia, target);
                return;
            }

            LowerStatements([statement], target, model, ct);
        }
        finally
        {
            PopLuaModuleBlock(pushedLuaModuleBlock);
        }
    }

    private bool TryPushLuaModuleBlock(IRBlock block)
    {
        if (_luaModuleBlockContexts.Count > 0)
        {
            return false;
        }

        _luaModuleBlockContexts.Push(new LuaModuleBlockContext(block));
        return true;
    }

    private void PopLuaModuleBlock(bool pushed)
    {
        if (pushed)
        {
            _luaModuleBlockContexts.Pop();
        }
    }

    private static void AddCommentStatements(SyntaxTriviaList triviaList, IRBlock target)
    {
        foreach (var comment in ExtractComments(triviaList))
        {
            target.Statements.Add(new IRRawComment(comment));
        }
    }

    private IRExpr LowerExpr(ExpressionSyntax e, SemanticModel model)
    {
        switch (e)
        {
            case LiteralExpressionSyntax lit:
                return LowerLiteral(lit);

            case InterpolatedStringExpressionSyntax istr:
                return LowerInterpolatedString(istr, model);

            case IdentifierNameSyntax id:
                return LowerIdentifier(id, model);

            case GenericNameSyntax genericName:
                return LowerGenericName(genericName, model);

            case ThisExpressionSyntax:
                return new IRIdentifier("self");

            case MemberAccessExpressionSyntax ma:
                if (TryLowerStringEmptyMemberAccess(ma, model) is { } stringEmpty)
                {
                    return stringEmpty;
                }

                if (TryLowerRuntimeTypeMetadataAccess(ma, model) is { } runtimeTypeMetadata)
                {
                    return runtimeTypeMetadata;
                }

                if (TryLowerLuaObjectMemberAccess(ma, model) is { } luaObjectMemberAccess)
                {
                    return luaObjectMemberAccess;
                }

                if (TryLowerFlattenedStructFieldAccess(ma, model) is { } flattenedStructFieldAccess)
                {
                    return flattenedStructFieldAccess;
                }

                if (TryLowerCollectionLengthAccess(ma, model) is { } collectionLength)
                {
                    return collectionLength;
                }

                if (TryLowerPropertyGet(ma, model) is { } propertyGet)
                {
                    return propertyGet;
                }

                return new IRMemberAccess(LowerExpr(ma.Expression, model), ma.Name.Identifier.ValueText);

            case ElementAccessExpressionSyntax elementAccess:
                if (IsCollectionElementAccess(elementAccess, model))
                {
                    return new IRElementAccess(
                        LowerExpr(elementAccess.Expression, model),
                        LowerExpr(elementAccess.ArgumentList.Arguments[0].Expression, model));
                }

                if (TryLowerIndexerGet(elementAccess, model) is { } indexerGet)
                {
                    return indexerGet;
                }

                return new IRElementAccess(
                    LowerExpr(elementAccess.Expression, model),
                    LowerExpr(elementAccess.ArgumentList.Arguments[0].Expression, model));

            case InvocationExpressionSyntax inv:
                return LowerInvocation(inv, model);

            case CastExpressionSyntax cast:
                if (IsStructRuntimeCast(cast, model))
                {
                    AddDiagnostic(cast.GetLocation(), "struct casts are not supported; SharpForge structs are compile-time value shapes and cannot be recovered from object/interface values");
                }

                return LowerExpr(cast.Expression, model);

            case ParenthesizedLambdaExpressionSyntax lambda:
                return LowerAnonymousFunction(lambda, model);

            case SimpleLambdaExpressionSyntax lambda:
                return LowerAnonymousFunction(lambda, model);

            case AnonymousMethodExpressionSyntax anonymousMethod:
                return LowerAnonymousFunction(anonymousMethod, model);

            case ObjectCreationExpressionSyntax obj:
                return LowerObjectCreation(obj, model);

            case ImplicitObjectCreationExpressionSyntax obj:
                return LowerObjectCreation(obj, model);

            case ArrayCreationExpressionSyntax arrayCreation:
                return arrayCreation.Initializer is null
                    ? new IRArrayNew(LowerExpr(arrayCreation.Type.RankSpecifiers[0].Sizes[0], model))
                    : new IRArrayLiteral(arrayCreation.Initializer.Expressions.Select(item => LowerExpr(item, model)).ToArray());

            case ImplicitArrayCreationExpressionSyntax implicitArrayCreation:
                return new IRArrayLiteral(implicitArrayCreation.Initializer.Expressions.Select(item => LowerExpr(item, model)).ToArray());

            case TupleExpressionSyntax tupleExpr:
                var tupleType = model.GetTypeInfo(tupleExpr).Type;
                if (tupleType is INamedTypeSymbol tt && tt.IsTupleType)
                {
                    var tupleFields = tt.TupleElements.Select((e, i) => (e.Name, Value: LowerExpr(tupleExpr.Arguments[i].Expression, model))).ToArray();
                    return new IRTableLiteralNew(tupleFields);
                }
                return new IRArrayLiteral(tupleExpr.Arguments.Select(a => LowerExpr(a.Expression, model)).ToArray());

            case AssignmentExpressionSyntax assignment when assignment.IsKind(SyntaxKind.CoalesceAssignmentExpression):
                return new IRCoalesceAssignment(LowerExpr(assignment.Left, model), LowerExpr(assignment.Right, model));

            case BinaryExpressionSyntax bin:
                if (bin.IsKind(SyntaxKind.IsExpression))
                {
                    return LowerTypeTest(bin.Left, bin.Right, model, isAsExpression: false);
                }

                if (bin.IsKind(SyntaxKind.AsExpression))
                {
                    return LowerTypeTest(bin.Left, bin.Right, model, isAsExpression: true);
                }

                if (model.GetSymbolInfo(bin).Symbol is IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator } op)
                {
                    var loweredArgs = LowerCallArguments([bin.Left, bin.Right], op.Parameters, model);
                    return BuildCallExpression(
                        loweredArgs,
                        args => new IRInvocation(
                            new IRMemberAccess(LowerTypeReference(op.ContainingType), GetLuaMethodName(op)),
                            args));
                }

                if (bin.IsKind(SyntaxKind.EqualsExpression) || bin.IsKind(SyntaxKind.NotEqualsExpression))
                {
                    var leftType = model.GetTypeInfo(bin.Left).Type;
                    var rightType = model.GetTypeInfo(bin.Right).Type;
                    if (leftType is INamedTypeSymbol { TypeKind: TypeKind.Struct } leftStruct
                        && rightType is INamedTypeSymbol { TypeKind: TypeKind.Struct }
                        && SymbolEqualityComparer.Default.Equals(leftType, rightType)
                        && CanFlattenStructType(leftStruct)
                        && GetFlattenableStructFields(leftStruct).Any())
                    {
                        var eq = BuildStructEqualityExpression(bin.Left, bin.Right, leftStruct, model);
                        return bin.IsKind(SyntaxKind.NotEqualsExpression) ? new IRUnary("not", eq) : eq;
                    }
                }

                if (bin.IsKind(SyntaxKind.AddExpression) && IsStringExpression(bin, model))
                {
                    return new IRStringConcat(FlattenStringConcat(bin, model).ToArray());
                }

                return new IRBinary(MapBinaryOp(bin.OperatorToken.ValueText), LowerExpr(bin.Left, model), LowerExpr(bin.Right, model));

            case PrefixUnaryExpressionSyntax pre:
                return new IRUnary(pre.OperatorToken.ValueText, LowerExpr(pre.Operand, model));

            case PostfixUnaryExpressionSyntax post when post.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                return LowerExpr(post.Operand, model);

            case ParenthesizedExpressionSyntax par:
                return LowerExpr(par.Expression, model);

            case ConditionalExpressionSyntax ternary:
                return new IRTernary(
                    LowerExpr(ternary.Condition, model),
                    LowerExpr(ternary.WhenTrue, model),
                    LowerExpr(ternary.WhenFalse, model));

            case AwaitExpressionSyntax awaitExpression:
                return LowerExpr(awaitExpression.Expression, model);

            case IsPatternExpressionSyntax isPattern:
                return LowerIsPatternExpression(isPattern, model);

            default:
                return UnsupportedExpression(e);
        }
    }

    private IRStmt? LowerInvocationToStatements(InvocationExpressionSyntax inv, SemanticModel model)
    {
        var symbol = model.GetSymbolInfo(inv).Symbol as IMethodSymbol;
        if (symbol is null)
        {
            return null;
        }

        var packStruct = IsPackStructType(symbol.ContainingType);
        var loweredArgs = LowerCallArguments(inv.ArgumentList.Arguments, symbol.Parameters, model, packStruct);
        if (loweredArgs.PreludeStatements.Count == 0)
        {
            return null;
        }

        // Build call directly without IIFE — prelude will be separate statements
        var callExpr = LowerInvocationCore(inv, symbol, loweredArgs.Arguments, model);

        // PackStruct: unpack struct return values from table into flattened fields
        if (TryUnpackPackStructReturn(callExpr, symbol) is { } unpacked)
        {
            callExpr = unpacked;
        }

        var stmts = new List<IRStmt>(loweredArgs.PreludeStatements.Count + 1);
        stmts.AddRange(loweredArgs.PreludeStatements);
        stmts.Add(new IRExprStmt(callExpr));
        return new IRStatementList(stmts);
    }

    private IRExpr LowerInvocation(InvocationExpressionSyntax inv, SemanticModel model)
    {
        var symbol = model.GetSymbolInfo(inv).Symbol as IMethodSymbol;
        if (symbol is null)
        {
            var args = inv.ArgumentList.Arguments.Select(a => LowerExpr(a.Expression, model)).ToArray();
            return new IRInvocation(LowerExpr(inv.Expression, model), args);
        }

        var packStruct = IsPackStructType(symbol.ContainingType);
        var loweredArgs = LowerCallArguments(inv.ArgumentList.Arguments, symbol.Parameters, model, packStruct);
        var callExpr = BuildCallExpression(loweredArgs, args => LowerInvocationCore(inv, symbol, args, model));

        // PackStruct: unpack struct return values from table into flattened fields
        if (TryUnpackPackStructReturn(callExpr, symbol) is { } unpacked)
        {
            return unpacked;
        }

        return callExpr;
    }

    private IRStmt? TryLowerConditionalDelegateInvocationStatement(ExpressionSyntax expression, SemanticModel model)
    {
        if (expression is not ConditionalAccessExpressionSyntax
            {
                Expression: { } receiverExpression,
                WhenNotNull: InvocationExpressionSyntax
                {
                    Expression: MemberBindingExpressionSyntax { Name.Identifier.ValueText: "Invoke" }
                } invocation
            })
        {
            return null;
        }

        var symbol = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        if (symbol is not { MethodKind: MethodKind.DelegateInvoke })
        {
            return null;
        }

        var delegateName = AllocateLuaName("delegate");
        var delegateIdentifier = new IRIdentifier(delegateName);
        var loweredArgs = LowerCallArguments(invocation.ArgumentList.Arguments, symbol.Parameters, model);
        var thenBlock = new IRBlock();
        thenBlock.Statements.AddRange(loweredArgs.PreludeStatements);
        thenBlock.Statements.Add(new IRExprStmt(new IRInvocation(delegateIdentifier, loweredArgs.Arguments)));

        return new IRStatementList([
            new IRLocalDecl(delegateName, LowerExpr(receiverExpression, model)),
            new IRIf(
                new IRBinary("~=", delegateIdentifier, new IRLiteral(null, IRLiteralKind.Nil)),
                thenBlock,
                null),
        ]);
    }

    private IRExpr LowerInvocationCore(InvocationExpressionSyntax inv, IMethodSymbol symbol, IReadOnlyList<IRExpr> args, SemanticModel model)
    {
        if (symbol is { MethodKind: MethodKind.DelegateInvoke })
        {
            return new IRInvocation(LowerExpr(inv.Expression, model), args);
        }

        if (IsTaskDelay(symbol))
        {
            return new IRRuntimeInvocation("CorWait__", args);
        }

        if (TryLowerGetTypeInvocation(inv, symbol, model) is { } getTypeInvocation)
        {
            return getTypeInvocation;
        }

        if (TryLowerLuaInteropInvocation(symbol, args) is { } luaInteropInvocation)
        {
            // Struct read-from-list: decompose LuaInterop.Get<T> result into flattened fields
            if (symbol is { Name: "Get", TypeArguments.Length: 1 }
                && symbol.TypeArguments[0] is INamedTypeSymbol structType
                && CanFlattenStructType(structType)
                && GetFlattenableStructFields(structType).ToArray() is { Length: > 0 } fields
                && luaInteropInvocation is IRLuaAccess access)
            {
                var tempName = AllocateLuaName("__struct_tmp");
                var body = new IRBlock();
                body.Statements.Add(new IRLocalDecl(tempName, access));
                var tableFields = fields
                    .Select(f => (f.Name, Value: (IRExpr)new IRMemberAccess(new IRIdentifier(tempName), f.Name)))
                    .ToArray();
                body.Statements.Add(new IRReturn(new IRTableLiteralNew(tableFields)));
                return BuildImmediatelyInvokedExpression(body);
            }

            return luaInteropInvocation;
        }

        if (TryLowerRegexInvocation(inv, symbol, args, model) is { } regexInvocation)
        {
            return regexInvocation;
        }

        var callArgs = PrependGenericMethodTypeArguments(symbol, args);

        if (inv.Expression is MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax } && symbol is { IsStatic: false })
        {
            return new IRInvocation(
                new IRMemberAccess(LowerTypeReference(symbol.ContainingType), GetLuaMethodName(symbol)),
                new[] { new IRIdentifier("self") }.Concat(callArgs).ToArray());
        }

        if (symbol is { IsStatic: false, ContainingType.TypeKind: TypeKind.Struct }
            && CanFlattenStructType(symbol.ContainingType))
        {
            if (inv.Expression is MemberAccessExpressionSyntax structMemberAccess)
            {
                var selfArgs = LowerStructArgumentValues(structMemberAccess.Expression, symbol.ContainingType, model);
                return new IRInvocation(
                    new IRMemberAccess(LowerTypeReference(symbol.ContainingType), GetLuaMethodName(symbol)),
                    selfArgs.Concat(callArgs).ToArray());
            }

            if (inv.Expression is IdentifierNameSyntax && SymbolEqualityComparer.Default.Equals(symbol.ContainingType, _currentStructSelfType))
            {
                return new IRInvocation(
                    new IRMemberAccess(LowerTypeReference(symbol.ContainingType), GetLuaMethodName(symbol)),
                    GetCurrentStructSelfArguments(symbol.ContainingType).Concat(callArgs).ToArray());
            }
        }

        if (inv.Expression is SimpleNameSyntax && symbol is { IsStatic: false })
        {
            return new IRInvocation(
                new IRMemberAccess(new IRIdentifier("self"), GetLuaMethodName(symbol)),
                callArgs,
                UseColon: true);
        }

        if (inv.Expression is SimpleNameSyntax && symbol is { IsStatic: true })
        {
            if (IsIgnoredClass(symbol.ContainingType))
            {
                return new IRInvocation(new IRIdentifier(symbol.Name), callArgs);
            }

            return new IRInvocation(
                new IRMemberAccess(LowerTypeReference(symbol.ContainingType), GetLuaMethodName(symbol)),
                callArgs);
        }

        if (inv.Expression is MemberAccessExpressionSyntax memberAccess && symbol is { IsStatic: false })
        {
            if (IsExternalLuaObjectType(symbol.ContainingType))
            {
                var member = GetLuaObjectMember(symbol);
                return new IRInvocation(
                    new IRMemberAccess(LowerExpr(memberAccess.Expression, model), member.Name),
                    callArgs,
                    UseColon: member.UseColon);
            }

            return new IRInvocation(
                new IRMemberAccess(LowerExpr(memberAccess.Expression, model), GetLuaMethodName(symbol)),
                callArgs,
                UseColon: true);
        }

        if (inv.Expression is MemberAccessExpressionSyntax staticAccess && symbol is { IsStatic: true })
        {
            if (IsIgnoredClass(symbol.ContainingType))
            {
                return new IRInvocation(new IRIdentifier(symbol.Name), callArgs);
            }

            if (IsExternalLuaObjectType(symbol.ContainingType))
            {
                // Special case: math.pow(x, y) → (x) ^ (y)
                if (symbol.ContainingType.Name == "math" && symbol.Name == "pow" && callArgs.Count == 2)
                {
                    return new IRBinary("^", callArgs[0], callArgs[1]);
                }

                var member = GetLuaObjectMember(symbol);
                return new IRInvocation(
                    new IRMemberAccess(GetLuaObjectTypeTarget(symbol.ContainingType), member.Name),
                    callArgs,
                    UseColon: member.UseColon);
            }

            return new IRInvocation(
                new IRMemberAccess(LowerExpr(staticAccess.Expression, model), GetLuaMethodName(symbol)),
                callArgs);
        }

        // Hard error for any call on a type defined outside the compilation (BCL / external API not explicitly handled above).
        if (_compilationAssembly is not null
            && !SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, _compilationAssembly))
        {
            AddDiagnostic(inv.GetLocation(), $"unsupported external method '{symbol.ContainingType?.ToDisplayString()}.{symbol.Name}'; SharpForge has no lowering for this API");
            return new IRLiteral(null, IRLiteralKind.Nil);
        }

        return new IRInvocation(LowerExpr(inv.Expression, model), callArgs);
    }

    private sealed record LoweredCallArguments(IReadOnlyList<IRExpr> Arguments, IReadOnlyList<IRStmt> PreludeStatements);

    private IRExpr BuildCallExpression(LoweredCallArguments loweredArgs, Func<IReadOnlyList<IRExpr>, IRExpr> buildCall)
    {
        var call = buildCall(loweredArgs.Arguments);
        if (loweredArgs.PreludeStatements.Count == 0)
        {
            return call;
        }

        var body = new IRBlock();
        body.Statements.AddRange(loweredArgs.PreludeStatements);
        body.Statements.Add(new IRReturn(call));
        return BuildImmediatelyInvokedExpression(body);
    }

    private IReadOnlyList<IRExpr> PrependGenericMethodTypeArguments(IMethodSymbol? symbol, IReadOnlyList<IRExpr> args)
    {
        if (symbol is not { IsGenericMethod: true } || symbol.TypeArguments.Length == 0)
        {
            return args;
        }

        return LowerRuntimeTypeArguments(symbol.TypeArguments).Concat(args).ToArray();
    }

    private IEnumerable<IRExpr> LowerRuntimeTypeArguments(IEnumerable<ITypeSymbol> typeArguments)
    {
        foreach (var typeArgument in typeArguments)
        {
            yield return LowerRuntimeTypeTarget(typeArgument);
        }
    }

    private LoweredCallArguments LowerCallArguments(IEnumerable<ExpressionSyntax> arguments, IEnumerable<IParameterSymbol> parameters, SemanticModel model, bool packStruct = false)
    {
        var lowered = new List<IRExpr>();
        var prelude = new List<IRStmt>();
        var argumentList = arguments.ToArray();
        var parameterList = parameters.ToArray();

        for (var i = 0; i < argumentList.Length; i++)
        {
            var argument = argumentList[i];
            var parameter = i < parameterList.Length ? parameterList[i] : null;
            if (TryLowerDelegateMethodGroupArgument(argument, parameter, model) is { } delegateMethodGroup)
            {
                lowered.Add(delegateMethodGroup);
                continue;
            }

            if (parameter?.Type is INamedTypeSymbol structType
                && CanFlattenStructType(structType)
                && GetFlattenableStructFields(structType).ToArray() is { Length: > 0 } fields)
            {
                if (packStruct)
                {
                    var values = LowerStructArgumentValues(argument, structType, model);
                    if (values.Count == fields.Length)
                    {
                        var tableFields = fields.Select((f, j) => (f.Name, values[j])).ToArray();
                        lowered.Add(new IRTableLiteralNew(tableFields));
                    }
                    else
                    {
                        // Multi-return: capture into temp locals via prelude, then pack
                        var tempNames = fields.Select(f => AllocateLuaName($"__pack_{f.Name}")).ToArray();
                        prelude.Add(new IRMultiLocalDecl(tempNames, new[] { LowerExpr(argument, model) }));
                        var tableFields = fields
                            .Select((f, j) => (f.Name, Value: (IRExpr)new IRIdentifier(tempNames[j])))
                            .ToArray();
                        lowered.Add(new IRTableLiteralNew(tableFields));
                    }
                }
                else
                {
                    lowered.AddRange(LowerCallArgumentValues(argument, parameter, model, i < argumentList.Length - 1, prelude));
                }
                continue;
            }

            if (TryLowerStructFieldAccessPrelude(argument, model, prelude, out var fieldRef))
            {
                lowered.Add(fieldRef);
                continue;
            }

            lowered.Add(LowerExpr(argument, model));
        }

        return new LoweredCallArguments(lowered, prelude);
    }

    private LoweredCallArguments LowerCallArguments(SeparatedSyntaxList<ArgumentSyntax> arguments, IReadOnlyList<IParameterSymbol> parameters, SemanticModel model, bool packStruct = false)
    {
        if (!arguments.Any(argument => argument.NameColon is not null))
        {
            return LowerCallArguments(arguments.Select(argument => argument.Expression), parameters, model, packStruct);
        }

        var prelude = new List<IRStmt>();
        var loweredByIndex = new Dictionary<int, IReadOnlyList<IRExpr>>();
        var mappedArguments = new List<(ArgumentSyntax Argument, IParameterSymbol Parameter, int Index)>();
        var maxIndex = -1;
        foreach (var argument in arguments)
        {
            var parameter = GetArgumentParameter(arguments, argument, parameters);
            if (parameter is null)
            {
                continue;
            }

            var index = GetParameterIndex(parameters, parameter);
            if (index < 0)
            {
                continue;
            }

            mappedArguments.Add((argument, parameter, index));
            maxIndex = Math.Max(maxIndex, index);
        }

        foreach (var mappedArgument in mappedArguments)
        {
            loweredByIndex[mappedArgument.Index] = LowerCallArgumentValues(
                mappedArgument.Argument.Expression,
                mappedArgument.Parameter,
                model,
                mappedArgument.Index < maxIndex,
                prelude);
        }

        var lowered = new List<IRExpr>();
        for (var i = 0; i <= maxIndex; i++)
        {
            if (loweredByIndex.TryGetValue(i, out var values))
            {
                lowered.AddRange(values);
                continue;
            }

            lowered.AddRange(CreateNilArgumentPlaceholders(parameters[i]));
        }

        return new LoweredCallArguments(lowered, prelude);
    }

    private LoweredCallArguments LowerCallArguments(SeparatedSyntaxList<ArgumentSyntax> arguments, IEnumerable<IParameterSymbol> parameters, SemanticModel model)
        => LowerCallArguments(arguments, parameters.ToArray(), model);

    private IRExpr LowerArgument(ExpressionSyntax argument, IParameterSymbol? parameter, SemanticModel model)
    {
        if (TryLowerDelegateMethodGroupArgument(argument, parameter, model) is { } delegateMethodGroup)
        {
            return delegateMethodGroup;
        }

        return LowerExpr(argument, model);
    }

    private IReadOnlyList<IRExpr> LowerArgumentValues(ExpressionSyntax argument, IParameterSymbol parameter, SemanticModel model)
    {
        if (parameter.Type is INamedTypeSymbol structType
            && CanFlattenStructType(structType)
            && GetFlattenableStructFields(structType).Any())
        {
            return LowerStructArgumentValues(argument, structType, model);
        }

        return [LowerArgument(argument, parameter, model)];
    }

    private IReadOnlyList<IRExpr> LowerCallArgumentValues(
        ExpressionSyntax argument,
        IParameterSymbol parameter,
        SemanticModel model,
        bool hasTrailingArguments,
        List<IRStmt> prelude)
    {
        if (parameter.Type is not INamedTypeSymbol structType
            || !CanFlattenStructType(structType))
        {
            return [LowerArgument(argument, parameter, model)];
        }

        var fields = GetFlattenableStructFields(structType).ToArray();
        if (fields.Length == 0)
        {
            return [LowerArgument(argument, parameter, model)];
        }

        var values = LowerStructArgumentValues(argument, structType, model);
        if (!hasTrailingArguments || fields.Length == 1 || values.Count != 1)
        {
            return values;
        }

        var baseName = EscapeLuaKeyword(parameter.Name);
        var tempNames = fields.Select(field => AllocateLuaName($"{baseName}__{field.Name}")).ToArray();
        prelude.Add(new IRMultiLocalDecl(tempNames, values));
        return tempNames.Select(name => (IRExpr)new IRIdentifier(name)).ToArray();
    }

    private IReadOnlyList<IRExpr> CreateNilArgumentPlaceholders(IParameterSymbol parameter)
    {
        if (parameter.Type is INamedTypeSymbol structType
            && CanFlattenStructType(structType)
            && GetFlattenableStructFields(structType).Any())
        {
            return GetFlattenableStructFields(structType)
                .Select(_ => (IRExpr)new IRLiteral(null, IRLiteralKind.Nil))
                .ToArray();
        }

        return [new IRLiteral(null, IRLiteralKind.Nil)];
    }

    private static int GetParameterIndex(IReadOnlyList<IParameterSymbol> parameters, IParameterSymbol parameter)
    {
        for (var i = 0; i < parameters.Count; i++)
        {
            if (SymbolEqualityComparer.Default.Equals(parameters[i], parameter))
            {
                return i;
            }
        }

        return -1;
    }

    private IRExpr? TryLowerDelegateMethodGroupArgument(ExpressionSyntax argument, IParameterSymbol? parameter, SemanticModel model)
    {
        if (argument is AnonymousFunctionExpressionSyntax
            || parameter?.Type is not INamedTypeSymbol { DelegateInvokeMethod: { } invokeMethod })
        {
            return null;
        }

        var method = GetMethodGroupSymbol(argument, model);
        if (method is null)
        {
            return null;
        }

        var parameterNames = invokeMethod.Parameters
            .Select((invokeParameter, index) => AllocateLuaName(string.IsNullOrWhiteSpace(invokeParameter.Name) ? $"arg{index}" : invokeParameter.Name))
            .ToArray();
        var parameterReferences = parameterNames.Select(name => (IRExpr)new IRIdentifier(name)).ToArray();
        var call = LowerMethodGroupInvocation(argument, method, parameterReferences, model);
        var body = new IRBlock();

        if (invokeMethod.ReturnsVoid)
        {
            body.Statements.Add(new IRExprStmt(call));
        }
        else
        {
            body.Statements.Add(new IRReturn(call));
        }

        return new IRFunctionExpression(parameterNames, body);
    }

    private static IMethodSymbol? GetMethodGroupSymbol(ExpressionSyntax argument, SemanticModel model)
    {
        var symbolInfo = model.GetSymbolInfo(argument);
        return symbolInfo.Symbol as IMethodSymbol
            ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
    }

    private IRExpr LowerMethodGroupInvocation(ExpressionSyntax argument, IMethodSymbol method, IReadOnlyList<IRExpr> args, SemanticModel model)
    {
        if (argument is MemberAccessExpressionSyntax memberAccessExpression && method is { IsStatic: false })
        {
            if (IsExternalLuaObjectType(method.ContainingType))
            {
                var member = GetLuaObjectMember(method);
                return new IRInvocation(
                    new IRMemberAccess(LowerExpr(memberAccessExpression.Expression, model), member.Name),
                    args,
                    UseColon: member.UseColon);
            }

            return new IRInvocation(
                new IRMemberAccess(LowerExpr(memberAccessExpression.Expression, model), GetLuaMethodName(method)),
                args,
                UseColon: true);
        }

        if (argument is IdentifierNameSyntax && method is { IsStatic: false })
        {
            return new IRInvocation(
                new IRMemberAccess(new IRIdentifier("self"), GetLuaMethodName(method)),
                args,
                UseColon: true);
        }

        if (method.IsStatic && IsIgnoredClass(method.ContainingType))
        {
            return new IRInvocation(new IRIdentifier(method.Name), args);
        }

        return new IRInvocation(
            new IRMemberAccess(LowerTypeReferenceForAccess(method.ContainingType), GetLuaMethodName(method)),
            args);
    }

    private IReadOnlyList<IRExpr> LowerStructArgumentValues(ExpressionSyntax expression, INamedTypeSymbol structType, SemanticModel model)
    {
        if (expression is BaseObjectCreationExpressionSyntax creation
            && TryGetStructFieldValues(creation, model, out _, out var creationValues))
        {
            return creationValues;
        }

        if (expression is TupleExpressionSyntax tupleExpr)
        {
            return tupleExpr.Arguments.Select(a => LowerExpr(a.Expression, model)).ToArray();
        }

        if (TryGetFlattenedStructValueExpressions(expression, structType, model, out var flattenedValues))
        {
            return flattenedValues;
        }

        return new[] { LowerExpr(expression, model) };
    }

    private IRExpr? TryLowerRegexInvocation(InvocationExpressionSyntax invocation, IMethodSymbol symbol, IReadOnlyList<IRExpr> args, SemanticModel model)
    {
        if (!IsRegexType(symbol.ContainingType))
        {
            return null;
        }

        if (symbol is not { Name: "IsMatch", IsStatic: true })
        {
            AddDiagnostic(invocation.GetLocation(), $"unsupported Regex API '{symbol.Name}'; only Regex.IsMatch(input, constantPattern) is supported");
            return new IRLiteral(false, IRLiteralKind.Boolean);
        }

        if (args.Count != 2 || invocation.ArgumentList.Arguments.Count != 2)
        {
            AddDiagnostic(invocation.GetLocation(), "unsupported Regex.IsMatch overload; only Regex.IsMatch(input, constantPattern) is supported");
            return new IRLiteral(false, IRLiteralKind.Boolean);
        }

        var patternExpression = invocation.ArgumentList.Arguments[1].Expression;
        var constantPattern = model.GetConstantValue(patternExpression);
        if (!constantPattern.HasValue || constantPattern.Value is not string regexPattern)
        {
            AddDiagnostic(patternExpression.GetLocation(), "Regex patterns must be compile-time constant strings so SharpForge can validate the Lua pattern output");
            return new IRLiteral(false, IRLiteralKind.Boolean);
        }

        if (!LuaRegexPatternCompiler.TryCompile(regexPattern, out var luaPattern, out var diagnostic))
        {
            AddDiagnostic(patternExpression.GetLocation(), diagnostic);
            return new IRLiteral(false, IRLiteralKind.Boolean);
        }

        return new IRBinary(
            "~=",
            new IRInvocation(
                new IRMemberAccess(new IRIdentifier("string"), "find"),
                new[] { args[0], new IRLiteral(luaPattern, IRLiteralKind.String) }),
            new IRLiteral(null, IRLiteralKind.Nil));
    }

    private static IRExpr BuildImmediatelyInvokedExpression(IRBlock body)
        => new IRInvocation(new IRFunctionExpression(Array.Empty<string>(), body), Array.Empty<IRExpr>());

    private IRStmt BuildTableRemoveStatement(string arrayName, IRExpr index)
        => new IRExprStmt(new IRInvocation(
            new IRMemberAccess(new IRIdentifier("table"), "remove"),
            new IRExpr[] { new IRIdentifier(arrayName), new IRBinary("+", index, new IRLiteral(1, IRLiteralKind.Integer)) }));

    private IRStmt BuildTableInsertStatement(string arrayName, IRExpr value)
        => new IRExprStmt(new IRInvocation(
            new IRMemberAccess(new IRIdentifier("table"), "insert"),
            new[] { new IRIdentifier(arrayName), value }));

    private IReadOnlyList<IRExpr> GetCurrentStructSelfArguments(INamedTypeSymbol structType)
    {
        if (_currentStructSelfFields is null)
        {
            return Array.Empty<IRExpr>();
        }

        return GetFlattenableStructFields(structType)
            .Select(field => _currentStructSelfFields.TryGetValue(field.Name, out var fieldName) ? new IRIdentifier(fieldName) : null)
            .OfType<IRExpr>()
            .ToArray();
    }

    private bool TryGetFlattenedStructValueExpressions(
        ExpressionSyntax expression,
        INamedTypeSymbol structType,
        SemanticModel model,
        out IReadOnlyList<IRExpr> values)
    {
        var fields = GetFlattenableStructFields(structType).ToArray();
        if (fields.Length == 0)
        {
            values = Array.Empty<IRExpr>();
            return false;
        }

        if (expression is ThisExpressionSyntax && _currentStructSelfFields is not null)
        {
            values = fields
                .Select(field => _currentStructSelfFields.TryGetValue(field.Name, out var fieldName) ? new IRIdentifier(fieldName) : null)
                .OfType<IRExpr>()
                .ToArray();
            return values.Count == fields.Length;
        }

        if (model.GetSymbolInfo(expression).Symbol is { } symbol)
        {
            if (_flattenedStructLocals.TryGetValue(symbol, out var flattenedLocal))
            {
                values = fields
                    .Select(field => flattenedLocal.FieldLocals.TryGetValue(field.Name, out var fieldName) ? new IRIdentifier(fieldName) : null)
                    .OfType<IRExpr>()
                    .ToArray();
                return values.Count == fields.Length;
            }

            if (_flattenedStructMembers.TryGetValue(symbol, out var flattenedMember))
            {
                if (!TryGetFlattenedStructMemberTarget(flattenedMember, expression, model, out var target))
                {
                    values = Array.Empty<IRExpr>();
                    return false;
                }

                values = fields
                    .Select(field => flattenedMember.FieldMembers.TryGetValue(field.Name, out var memberName) ? new IRMemberAccess(target, memberName) : null)
                    .OfType<IRExpr>()
                    .ToArray();
                return values.Count == fields.Length;
            }

            if (symbol is IMethodSymbol or IPropertySymbol
                && expression is IdentifierNameSyntax or MemberAccessExpressionSyntax or InvocationExpressionSyntax
                && GetSymbolType(symbol) is INamedTypeSymbol symbolType
                && CanFlattenStructType(symbolType))
            {
                // PackStruct returns are tables, not flattened — can't decompose directly
                if (IsPackStructType(symbol is IMethodSymbol m ? m.ContainingType : symbolType))
                {
                    values = Array.Empty<IRExpr>();
                    return false;
                }

                values = [LowerExpr(expression, model)];
                return true;
            }
        }

        if (expression is IdentifierNameSyntax or MemberAccessExpressionSyntax)
        {
            var target = LowerExpr(expression, model);
            values = fields.Select(field => new IRMemberAccess(target, field.Name)).ToArray();
            return true;
        }

        values = Array.Empty<IRExpr>();
        return false;
    }

    private IRStmt? TryLowerStructLocalDeclaration(
        LocalDeclarationStatementSyntax statement,
        VariableDeclaratorSyntax variable,
        ISymbol? localSymbol,
        SemanticModel model)
    {
        if (localSymbol is null
            || statement.Declaration.Variables.Count != 1
            || variable.Initializer?.Value is not { } initializer
            || !CanFlattenStructLocal(statement, localSymbol, model))
        {
            return null;
        }

        // PackStruct return: two-statement unpack (temp + field extraction)
        if (TryGetPackStructReturnType(initializer, model) is INamedTypeSymbol packStructType
            && GetFlattenableStructFields(packStructType).ToArray() is { Length: > 0 } packFields)
        {
            var packBaseName = EscapeLuaKeyword(variable.Identifier.ValueText);
            var packFieldLocals = new Dictionary<string, string>(StringComparer.Ordinal);
            var packLocalNames = new List<string>(packFields.Length);
            foreach (var field in packFields)
            {
                var localName = AllocateLuaName($"{packBaseName}__{field.Name}");
                packFieldLocals[field.Name] = localName;
                packLocalNames.Add(localName);
            }

            _flattenedStructLocals[localSymbol] = new FlattenedStructLocal(packFieldLocals);

            var tmpName = AllocateLuaName("__unpack_tmp");
            var tmpInit = new IRLocalDecl(tmpName, LowerExpr(initializer, model));
            var fieldValues = packFields
                .Select(f => (IRExpr)new IRMemberAccess(new IRIdentifier(tmpName), f.Name))
                .ToArray();
            var multiLocal = new IRMultiLocalDecl(packLocalNames, fieldValues);
            return new IRStatementList([tmpInit, multiLocal]);
        }

        if (!TryGetStructExpressionValues(initializer, model, out var fields, out var values))
        {
            return null;
        }

        var baseName = EscapeLuaKeyword(variable.Identifier.ValueText);
        var fieldLocals = new Dictionary<string, string>(StringComparer.Ordinal);
        var localNames = new List<string>(fields.Count);
        foreach (var field in fields)
        {
            var localName = AllocateLuaName($"{baseName}__{field.Name}");
            fieldLocals[field.Name] = localName;
            localNames.Add(localName);
        }

        _flattenedStructLocals[localSymbol] = new FlattenedStructLocal(fieldLocals);
        if (initializer is ConditionalExpressionSyntax ternary)
        {
            var branchBlock = new IRBlock();
            branchBlock.Statements.Add(LowerStructConditionalAssignment(
                ternary,
                fields,
                localNames.Select(name => (IRExpr)new IRIdentifier(name)).ToArray(),
                model));
            return new IRStatementList([
                new IRMultiLocalDecl(localNames, Array.Empty<IRExpr>()),
                branchBlock,
            ]);
        }

        return new IRMultiLocalDecl(localNames, values);
    }

    private IRStmt? TryLowerStructAssignment(ExpressionSyntax left, ExpressionSyntax right, SemanticModel model)
    {
        if (!TryGetStructExpressionValues(right, model, out var fields, out var values)
            || !TryGetFlattenedStructAssignmentTargets(left, fields, model, out var targets, out var receiverTemp))
        {
            return null;
        }

        if (right is ConditionalExpressionSyntax ternary)
        {
            return LowerStructConditionalAssignmentThroughTemps(ternary, fields, targets, model, receiverTemp);
        }

        if (receiverTemp is not null)
        {
            return new IRStatementList([receiverTemp, new IRMultiAssign(targets, values)]);
        }

        return new IRMultiAssign(targets, values);
    }

    private IRStmt LowerStructConditionalAssignmentThroughTemps(
        ConditionalExpressionSyntax ternary,
        IReadOnlyList<StructFieldSlot> fields,
        IReadOnlyList<IRExpr> targets,
        SemanticModel model,
        IRStmt? receiverTemp = null)
    {
        var tempNames = fields.Select(field => AllocateLuaName($"ternary__{field.Name}")).ToArray();
        var tempTargets = tempNames.Select(name => (IRExpr)new IRIdentifier(name)).ToArray();

        var assignmentBlock = new IRBlock();
        assignmentBlock.Statements.Add(LowerStructConditionalAssignment(ternary, fields, tempTargets, model));
        assignmentBlock.Statements.Add(new IRMultiAssign(targets, tempTargets));

        var statements = new List<IRStmt>();
        if (receiverTemp is not null)
        {
            statements.Add(receiverTemp);
        }

        statements.Add(new IRMultiLocalDecl(tempNames, Array.Empty<IRExpr>()));
        statements.Add(assignmentBlock);
        return new IRStatementList(statements);
    }

    private IRStmt LowerStructConditionalAssignment(
        ConditionalExpressionSyntax ternary,
        IReadOnlyList<StructFieldSlot> fields,
        IReadOnlyList<IRExpr> targets,
        SemanticModel model)
    {
        var thenBlock = new IRBlock();
        thenBlock.Statements.Add(new IRMultiAssign(targets, LowerStructBranchValues(ternary.WhenTrue, fields, model)));

        var elseBlock = new IRBlock();
        elseBlock.Statements.Add(new IRMultiAssign(targets, LowerStructBranchValues(ternary.WhenFalse, fields, model)));

        return new IRIf(LowerExpr(ternary.Condition, model), thenBlock, elseBlock);
    }

    private IReadOnlyList<IRExpr> LowerStructBranchValues(
        ExpressionSyntax expression,
        IReadOnlyList<StructFieldSlot> fields,
        SemanticModel model)
    {
        if (model.GetTypeInfo(expression).Type is INamedTypeSymbol branchType
            && CanFlattenStructType(branchType))
        {
            return LowerStructArgumentValues(expression, branchType, model);
        }

        return fields.Select(_ => (IRExpr)new IRLiteral(null, IRLiteralKind.Nil)).ToArray();
    }

    private bool TryGetFlattenedStructAssignmentTargets(
        ExpressionSyntax left,
        IReadOnlyList<StructFieldSlot> fields,
        SemanticModel model,
        out IReadOnlyList<IRExpr> targets,
        out IRStmt? receiverTemp)
    {
        targets = Array.Empty<IRExpr>();
        receiverTemp = null;
        if (model.GetSymbolInfo(left).Symbol is not { } symbol)
        {
            return false;
        }

        if (_flattenedStructLocals.TryGetValue(symbol, out var flattenedLocal))
        {
            var localTargets = new List<IRExpr>(fields.Count);
            foreach (var field in fields)
            {
                if (!flattenedLocal.FieldLocals.TryGetValue(field.Name, out var localName))
                {
                    return false;
                }

                localTargets.Add(new IRIdentifier(localName));
            }

            targets = localTargets;
            return true;
        }

        if (!_flattenedStructMembers.TryGetValue(symbol, out var flattenedMember))
        {
            return false;
        }

        if (!TryGetFlattenedStructMemberTarget(flattenedMember, left, model, out var target))
        {
            return false;
        }

        // If the target receiver expression contains an invocation (e.g.
        // `obj.AddComponent<T>().rotation = ...`), evaluate it once into a
        // temp local so that side effects run exactly once across all field
        // assignments in the IRMultiAssign.  Simple member access chains on
        // locals (e.g. `bolt.transform.position`) are safe to repeat.
        if (ReceiverHasInvocation(left))
        {
            var tempName = AllocateLuaName("receiver");
            receiverTemp = new IRLocalDecl(tempName, target);
            target = new IRIdentifier(tempName);
        }

        var memberTargets = new List<IRExpr>(fields.Count);
        foreach (var field in fields)
        {
            if (!flattenedMember.FieldMembers.TryGetValue(field.Name, out var memberName))
            {
                return false;
            }

            memberTargets.Add(new IRMemberAccess(target, memberName));
        }

        targets = memberTargets;
        return true;
    }

    private static bool ReceiverHasInvocation(ExpressionSyntax expression)
    {
        return expression.DescendantNodes().Any(node => node is InvocationExpressionSyntax);
    }

    /// <summary>
    /// Detects <c>expr.Field</c> where <c>expr</c> returns a flattenable struct
    /// (e.g. <c>GetAbilityData(level).TargetCount</c>).  The struct-returning call
    /// emits multiple values, so we spill them into prelude locals and return just
    /// the requested field identifier.  Returns <c>true</c> when the pattern matched
    /// and <paramref name="prelude"/> received the spill statement.
    /// </summary>
    private bool TryLowerStructFieldAccessPrelude(
        ExpressionSyntax argument,
        SemanticModel model,
        List<IRStmt> prelude,
        out IRExpr fieldRef)
    {
        fieldRef = null!;

        if (argument is not MemberAccessExpressionSyntax access)
        {
            return false;
        }

        // If the receiver is an identifier that is already a flattened
        // local or member, the normal LowerExpr path handles it.
        if (access.Expression is IdentifierNameSyntax id
            && model.GetSymbolInfo(id).Symbol is { } idSymbol
            && (_flattenedStructLocals.ContainsKey(idSymbol)
                || _flattenedStructMembers.ContainsKey(idSymbol)))
        {
            return false;
        }

        // Only handle struct-returning invocations/calls, not plain member
        // access chains on already-known identifiers or `this`.
        if (access.Expression is IdentifierNameSyntax or ThisExpressionSyntax
            && model.GetSymbolInfo(access.Expression).Symbol is ILocalSymbol or IParameterSymbol or null)
        {
            return false;
        }

        var receiverType = model.GetTypeInfo(access.Expression).Type;
        if (receiverType is not INamedTypeSymbol structType
            || !CanFlattenStructType(structType)
            || !GetFlattenableStructFields(structType).Any())
        {
            return false;
        }

        var memberSymbol = model.GetSymbolInfo(access).Symbol;
        if (memberSymbol is not IFieldSymbol and not IPropertySymbol { DeclaringSyntaxReferences.Length: 0 })
        {
            return false;
        }

        var fields = GetFlattenableStructFields(structType).ToArray();
        var tempNames = fields.Select(f => AllocateLuaName($"field__{f.Name}")).ToArray();
        var callExpr = LowerExpr(access.Expression, model);

        prelude.Add(new IRMultiLocalDecl(tempNames, [callExpr]));

        var targetFieldName = access.Name.Identifier.ValueText;
        var fieldIndex = Array.FindIndex(fields, f => f.Name == targetFieldName);
        if (fieldIndex >= 0)
        {
            fieldRef = new IRIdentifier(tempNames[fieldIndex]);
        }
        else
        {
            fieldRef = new IRIdentifier("--[[unsupported flattened field]]nil");
        }

        return true;
    }

    private IRExpr? TryLowerFlattenedStructFieldAccess(MemberAccessExpressionSyntax access, SemanticModel model)
    {
        if (model.GetSymbolInfo(access).Symbol is not IFieldSymbol and not IPropertySymbol)
        {
            return null;
        }

        if (access.Expression is ThisExpressionSyntax
            && _currentStructSelfFields is not null
            && _currentStructSelfFields.TryGetValue(access.Name.Identifier.ValueText, out var selfFieldName))
        {
            return new IRIdentifier(selfFieldName);
        }

        if (access.Expression is IdentifierNameSyntax id
            && model.GetSymbolInfo(id).Symbol is { } localSymbol
            && _flattenedStructLocals.TryGetValue(localSymbol, out var flattenedLocal))
        {
            return flattenedLocal.FieldLocals.TryGetValue(access.Name.Identifier.ValueText, out var localName)
                ? new IRIdentifier(localName)
                : null;
        }

        if (model.GetSymbolInfo(access.Expression).Symbol is { } memberSymbol
            && _flattenedStructMembers.TryGetValue(memberSymbol, out var flattenedMember))
        {
            return flattenedMember.FieldMembers.TryGetValue(access.Name.Identifier.ValueText, out var memberName)
                && TryGetFlattenedStructMemberTarget(flattenedMember, access.Expression, model, out var target)
                ? new IRMemberAccess(target, memberName)
                : null;
        }

        // Field accesses on struct-returning expressions (e.g.
        // `GetAbilityData(...).TargetCount`) are handled at the statement
        // level in LowerInvocation where prelude locals can be declared,
        // avoiding IIFE closure/GC overhead.
        return null;
    }

    private bool TryGetFlattenedStructMemberTarget(FlattenedStructMember member, ExpressionSyntax expression, SemanticModel model, out IRExpr target)
    {
        if (member.IsStatic)
        {
            target = LowerTypeReferenceForAccess(member.ContainingType);
            return true;
        }

        if (expression is MemberAccessExpressionSyntax access)
        {
            target = LowerExpr(access.Expression, model);
            return true;
        }

        if (expression is IdentifierNameSyntax or ThisExpressionSyntax)
        {
            target = new IRIdentifier("self");
            return true;
        }

        target = new IRIdentifier("--[[unsupported flattened struct member target]]nil");
        return false;
    }

    private bool TryAddFlattenedStructMemberFields(
        IRType owner,
        ISymbol? memberSymbol,
        string memberName,
        ITypeSymbol? memberType,
        EqualsValueClauseSyntax? initializer,
        bool isStatic,
        IReadOnlyList<string> comments,
        SemanticModel model)
    {
        if (memberSymbol is null
            || memberType is not INamedTypeSymbol structType
            || !CanFlattenStructType(structType))
        {
            return false;
        }

        var fieldSlots = GetFlattenableStructFields(structType).ToArray();
        if (fieldSlots.Length == 0)
        {
            return false;
        }

        var values = fieldSlots.ToDictionary(f => f.Name, f => LowerDefaultValue(f.Type), StringComparer.Ordinal);
        if (initializer?.Value is BaseObjectCreationExpressionSyntax creation)
        {
            if (!TryGetStructFieldValues(creation, model, out var initializedFields, out var initializedValues))
            {
                return false;
            }

            for (var i = 0; i < initializedFields.Count; i++)
            {
                values[initializedFields[i].Name] = initializedValues[i];
            }
        }
        else if (initializer is not null)
        {
            return false;
        }

        var fieldMembers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in fieldSlots)
        {
            var flattenedName = $"{memberName}__{field.Name}";
            fieldMembers[field.Name] = flattenedName;

            var irField = new IRField
            {
                Name = flattenedName,
                Initializer = values[field.Name],
                IsStatic = isStatic,
            };
            irField.Comments.AddRange(comments);
            owner.Fields.Add(irField);
        }

        _flattenedStructMembers[memberSymbol] = new FlattenedStructMember(isStatic, memberSymbol.ContainingType, fieldMembers);
        return true;
    }

    private bool CanFlattenStructLocal(LocalDeclarationStatementSyntax statement, ISymbol localSymbol, SemanticModel model)
    {
        if (statement.Parent is not BlockSyntax block)
        {
            return false;
        }

        var startIndex = block.Statements.IndexOf(statement);
        if (startIndex < 0)
        {
            return false;
        }

        for (var i = startIndex + 1; i < block.Statements.Count; i++)
        {
            foreach (var id in block.Statements[i].DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (!SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id).Symbol, localSymbol))
                {
                    continue;
                }

                if (id.Parent is MemberAccessExpressionSyntax access
                    && access.Expression == id
                    && model.GetSymbolInfo(access).Symbol is IFieldSymbol or IPropertySymbol or IMethodSymbol)
                {
                    continue;
                }

                if (id.Parent is AssignmentExpressionSyntax assignment
                    && assignment.Left == id
                    && assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                    && IsStructExpression(assignment.Right, model))
                {
                    continue;
                }

                if (id.Parent is BinaryExpressionSyntax binary
                    && model.GetSymbolInfo(binary).Symbol is IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator })
                {
                    continue;
                }

                if (IsSupportedStructArgumentUse(id, model))
                {
                    continue;
                }

                if (IsSupportedStructAssignmentSourceUse(id, model))
                {
                    continue;
                }

                if (IsSupportedStructStringUse(id, model))
                {
                    continue;
                }

                if (id.Parent is ReturnStatementSyntax returnStmt
                    && returnStmt.Expression == id
                    && IsStructExpression(id, model))
                {
                    continue;
                }

                return false;
            }
        }

        return true;
    }

    private bool IsSupportedStructArgumentUse(IdentifierNameSyntax id, SemanticModel model)
    {
        if (id.Parent is not ArgumentSyntax argument
            || argument.Expression != id
            || argument.Parent is not BaseArgumentListSyntax argumentList)
        {
            return false;
        }

        var parameter = argumentList.Parent switch
        {
            InvocationExpressionSyntax invocation when model.GetSymbolInfo(invocation).Symbol is IMethodSymbol method
                => GetArgumentParameter(argumentList.Arguments, argument, method.Parameters),
            BaseObjectCreationExpressionSyntax creation when model.GetSymbolInfo(creation).Symbol is IMethodSymbol ctor
                => GetArgumentParameter(argumentList.Arguments, argument, ctor.Parameters),
            _ => null,
        };

        return parameter?.Type is INamedTypeSymbol structType
               && CanFlattenStructType(structType)
               && GetFlattenableStructFields(structType).Any();
    }

    private bool IsSupportedStructAssignmentSourceUse(IdentifierNameSyntax id, SemanticModel model)
    {
        if (id.Parent is not AssignmentExpressionSyntax assignment
            || assignment.Right != id
            || !assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
            || !TryGetStructExpressionValues(id, model, out var fields, out _))
        {
            return false;
        }

        return TryGetFlattenedStructAssignmentTargets(assignment.Left, fields, model, out _, out _);
    }

    private bool IsSupportedStructStringUse(IdentifierNameSyntax id, SemanticModel model)
    {
        if (FindStringConcatToStringMethod(id, model) is not IMethodSymbol { ContainingType.TypeKind: TypeKind.Struct } toStringMethod
            || !CanFlattenStructType(toStringMethod.ContainingType))
        {
            return false;
        }

        ExpressionSyntax current = id;
        while (current.Parent is ParenthesizedExpressionSyntax parenthesized)
        {
            current = parenthesized;
        }

        return current.Parent switch
        {
            InterpolationSyntax interpolation when interpolation.Expression == current => true,
            BinaryExpressionSyntax binary when (binary.Left == current || binary.Right == current)
                                               && binary.IsKind(SyntaxKind.AddExpression)
                                               && IsStringExpression(binary, model) => true,
            _ => false,
        };
    }

    private static IParameterSymbol? GetArgumentParameter(
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        ArgumentSyntax argument,
        IReadOnlyList<IParameterSymbol> parameters)
    {
        if (argument.NameColon is { } nameColon)
        {
            return parameters.FirstOrDefault(parameter => parameter.Name == nameColon.Name.Identifier.ValueText);
        }

        var index = arguments.IndexOf(argument);
        return index >= 0 && index < parameters.Count ? parameters[index] : null;
    }

    private IRStmt? TryLowerStructReturn(ExpressionSyntax expression, SemanticModel model)
    {
        if (expression is BaseObjectCreationExpressionSyntax creation
            && TryGetStructFieldValues(creation, model, out _, out var values))
        {
            return new IRMultiReturn(values);
        }

        // `return default` for tuple types → bare `return` (nil in Lua, stops iterators).
        if (IsDefaultExpression(expression) && model.GetTypeInfo(expression).Type is INamedTypeSymbol defType && defType.IsTupleType)
        {
            return new IRReturn(null);
        }

        if (model.GetTypeInfo(expression).Type is INamedTypeSymbol flatType
            && CanFlattenStructType(flatType)
            && GetFlattenableStructFields(flatType).Any())
        {
            return new IRMultiReturn(LowerStructArgumentValues(expression, flatType, model));
        }

        return null;
    }

    private static bool IsDefaultExpression(ExpressionSyntax expression)
        => expression is DefaultExpressionSyntax
           || (expression is PostfixUnaryExpressionSyntax postfix
               && postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression)
               && postfix.Operand is DefaultExpressionSyntax);

    private IRStmt LowerReturnExpression(ExpressionSyntax expression, SemanticModel model)
        => TryLowerStructReturn(expression, model) ?? new IRReturn(LowerExpr(expression, model));

    private bool TryGetStructExpressionValues(
        ExpressionSyntax expression,
        SemanticModel model,
        out IReadOnlyList<StructFieldSlot> fields,
        out IReadOnlyList<IRExpr> values)
    {
        if (expression is BaseObjectCreationExpressionSyntax creation)
        {
            return TryGetStructFieldValues(creation, model, out fields, out values);
        }

        var type = model.GetTypeInfo(expression).Type as INamedTypeSymbol;
        if (type is null || !CanFlattenStructType(type))
        {
            fields = Array.Empty<StructFieldSlot>();
            values = Array.Empty<IRExpr>();
            return false;
        }

        var fieldSlots = GetFlattenableStructFields(type).ToArray();
        if (fieldSlots.Length == 0)
        {
            fields = Array.Empty<StructFieldSlot>();
            values = Array.Empty<IRExpr>();
            return false;
        }

        fields = fieldSlots;
        values = LowerStructArgumentValues(expression, type, model);
        return true;
    }

    private bool IsStructExpression(ExpressionSyntax expression, SemanticModel model)
        => model.GetTypeInfo(expression).Type is INamedTypeSymbol type
           && CanFlattenStructType(type)
           && GetFlattenableStructFields(type).Any();

    private bool CanFlattenStructType(INamedTypeSymbol type)
        => type.TypeKind == TypeKind.Struct
           && !IsIgnoredClass(type);

    private bool TryGetStructFieldValues(
        BaseObjectCreationExpressionSyntax creation,
        SemanticModel model,
        out IReadOnlyList<StructFieldSlot> fields,
        out IReadOnlyList<IRExpr> values)
    {
        fields = Array.Empty<StructFieldSlot>();
        values = Array.Empty<IRExpr>();

        var type = (model.GetSymbolInfo(creation).Symbol as IMethodSymbol)?.ContainingType
            ?? model.GetTypeInfo(creation).Type as INamedTypeSymbol;
        if (type is null || !CanFlattenStructType(type))
        {
            return false;
        }

        var fieldSlots = GetFlattenableStructFields(type).ToArray();
        if (fieldSlots.Length == 0)
        {
            return false;
        }

        var fieldValues = fieldSlots.ToDictionary(f => f.Name, f => LowerDefaultValue(f.Type), StringComparer.Ordinal);
        var ctor = model.GetSymbolInfo(creation).Symbol as IMethodSymbol;
        var args = creation.ArgumentList?.Arguments ?? default;
        if (args.Count > 0)
        {
            var parameterToField = ctor is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : GetStructConstructorFieldMap(ctor, model);

            if (ctor is not null && parameterToField.Count == ctor.Parameters.Length)
            {
                for (var i = 0; i < args.Count; i++)
                {
                    var parameter = args[i].NameColon is { } nameColon
                        ? ctor.Parameters.FirstOrDefault(p => p.Name == nameColon.Name.Identifier.ValueText)
                        : i < ctor.Parameters.Length ? ctor.Parameters[i] : null;
                    if (parameter is null || !parameterToField.TryGetValue(parameter.Name, out var fieldName))
                    {
                        return false;
                    }

                    fieldValues[fieldName] = LowerExpr(args[i].Expression, model);
                }
            }
            else if (args.Count == fieldSlots.Length)
            {
                for (var i = 0; i < args.Count; i++)
                {
                    fieldValues[fieldSlots[i].Name] = LowerExpr(args[i].Expression, model);
                }
            }
            else
            {
                return false;
            }
        }

        if (creation.Initializer is not null)
        {
            foreach (var expression in creation.Initializer.Expressions)
            {
                if (expression is not AssignmentExpressionSyntax assignment)
                {
                    return false;
                }

                var fieldName = assignment.Left switch
                {
                    IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                    MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
                    _ => null,
                };

                if (fieldName is null || !fieldValues.ContainsKey(fieldName))
                {
                    return false;
                }

                fieldValues[fieldName] = LowerExpr(assignment.Right, model);
            }
        }

        fields = fieldSlots;
        values = fieldSlots.Select(f => fieldValues[f.Name]).ToArray();
        return true;
    }

    private static IEnumerable<StructFieldSlot> GetFlattenableStructFields(INamedTypeSymbol type)
    {
        if (type.IsTupleType)
        {
            return type.TupleElements
                .Select((e, i) => new StructFieldSlot(e.Name, e.Type, i))
                .ToArray();
        }

        return type.GetMembers()
            .Where(m => m is IFieldSymbol { IsStatic: false, IsConst: false } || m is IPropertySymbol { IsStatic: false } property && IsAutoPropertySymbol(property))
            .Select(m => new StructFieldSlot(m.Name, m switch
            {
                IFieldSymbol field => field.Type,
                IPropertySymbol property => property.Type,
                _ => throw new InvalidOperationException(),
            }, GetSyntaxSortKey(m)))
            .OrderBy(f => f.SortKey)
            .ThenBy(f => f.Name, StringComparer.Ordinal);
    }

    private static int GetSyntaxSortKey(ISymbol symbol)
        => symbol.DeclaringSyntaxReferences.FirstOrDefault()?.Span.Start ?? int.MaxValue;

    private static Dictionary<string, string> GetStructConstructorFieldMap(IMethodSymbol ctor, SemanticModel model)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var syntax = ctor.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as ConstructorDeclarationSyntax;
        if (syntax?.Body is null)
        {
            return result;
        }

        var ctorModel = model.SyntaxTree == syntax.SyntaxTree
            ? model
            : model.Compilation.GetSemanticModel(syntax.SyntaxTree);

        foreach (var assignment in syntax.Body.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                || ctorModel.GetSymbolInfo(assignment.Left).Symbol is not IFieldSymbol and not IPropertySymbol)
            {
                continue;
            }

            var fieldName = assignment.Left switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                MemberAccessExpressionSyntax memberAccess when memberAccess.Expression is ThisExpressionSyntax => memberAccess.Name.Identifier.ValueText,
                _ => null,
            };
            if (fieldName is null)
            {
                continue;
            }

            var parameterName = assignment.Right switch
            {
                IdentifierNameSyntax identifier when ctorModel.GetSymbolInfo(identifier).Symbol is IParameterSymbol parameter => parameter.Name,
                _ => null,
            };
            if (parameterName is not null)
            {
                result[parameterName] = fieldName;
            }
        }

        return result;
    }

    private IRExpr LowerAnonymousFunction(AnonymousFunctionExpressionSyntax anonymousFunction, SemanticModel model)
    {
        var parameters = GetAnonymousFunctionParameters(anonymousFunction)
            .Select(parameter => DeclareLuaName(model.GetDeclaredSymbol(parameter), parameter.Identifier.ValueText))
            .ToArray();
        var body = new IRBlock();

        switch (anonymousFunction)
        {
            case ParenthesizedLambdaExpressionSyntax { Block: { } block }:
                LowerBlock(block, body, model, CancellationToken.None);
                break;
            case ParenthesizedLambdaExpressionSyntax { ExpressionBody: { } expressionBody }:
                LowerExpressionAnonymousFunctionBody(expressionBody, body, model, anonymousFunction);
                break;
            case SimpleLambdaExpressionSyntax { Block: { } block }:
                LowerBlock(block, body, model, CancellationToken.None);
                break;
            case SimpleLambdaExpressionSyntax { ExpressionBody: { } expressionBody }:
                LowerExpressionAnonymousFunctionBody(expressionBody, body, model, anonymousFunction);
                break;
            case AnonymousMethodExpressionSyntax { Block: { } block }:
                LowerBlock(block, body, model, CancellationToken.None);
                break;
        }

        return new IRFunctionExpression(parameters, body);
    }

    private static IEnumerable<ParameterSyntax> GetAnonymousFunctionParameters(AnonymousFunctionExpressionSyntax anonymousFunction)
        => anonymousFunction switch
        {
            ParenthesizedLambdaExpressionSyntax lambda => lambda.ParameterList.Parameters,
            SimpleLambdaExpressionSyntax lambda => new[] { lambda.Parameter },
            AnonymousMethodExpressionSyntax { ParameterList: { } parameterList } => parameterList.Parameters,
            _ => Array.Empty<ParameterSyntax>(),
        };

    private void LowerExpressionAnonymousFunctionBody(ExpressionSyntax expressionBody, IRBlock body, SemanticModel model, AnonymousFunctionExpressionSyntax anonymousFunction)
    {
        if (AnonymousFunctionReturnsVoid(anonymousFunction, model))
        {
            body.Statements.Add(new IRExprStmt(LowerExpr(expressionBody, model)));
            return;
        }

        // Use LowerReturnExpression so tuple returns become IRMultiReturn (not IRTableLiteralNew).
        body.Statements.Add(LowerReturnExpression(expressionBody, model));
    }

    private static bool AnonymousFunctionReturnsVoid(AnonymousFunctionExpressionSyntax anonymousFunction, SemanticModel model)
        => (model.GetTypeInfo(anonymousFunction).ConvertedType as INamedTypeSymbol)?.DelegateInvokeMethod?.ReturnsVoid == true;

    private IRExpr LowerTypeTest(ExpressionSyntax value, ExpressionSyntax typeSyntax, SemanticModel model, bool isAsExpression)
    {
        var type = GetTypeSymbol(typeSyntax, model);
        if (type is null)
        {
            AddDiagnostic(typeSyntax.GetLocation(), $"unsupported type: {typeSyntax.Kind()}");
            return new IRLiteral(false, IRLiteralKind.Boolean);
        }

        if (type is INamedTypeSymbol namedType && CanFlattenStructType(namedType))
        {
            AddDiagnostic(typeSyntax.GetLocation(), "struct runtime type checks are not supported; SharpForge structs are compile-time value shapes and do not carry runtime type metadata");
        }

        return isAsExpression
            ? new IRAs(LowerExpr(value, model), LowerRuntimeTypeTarget(type))
            : new IRIs(LowerExpr(value, model), LowerRuntimeTypeTarget(type));
    }

    private IRExpr? TryLowerGetTypeInvocation(InvocationExpressionSyntax invocation, IMethodSymbol symbol, SemanticModel model)
    {
        if (!IsSystemObjectGetType(symbol)
            || invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return null;
        }

        if (model.GetTypeInfo(memberAccess.Expression).Type is INamedTypeSymbol receiverType
            && CanFlattenStructType(receiverType))
        {
            AddDiagnostic(invocation.GetLocation(), "struct GetType() is not supported; SharpForge structs are compile-time value shapes and do not carry runtime type metadata");
        }

        return new IRMemberAccess(LowerExpr(memberAccess.Expression, model), "__sf_type");
    }

    private IRExpr? TryLowerRuntimeTypeMetadataAccess(MemberAccessExpressionSyntax access, SemanticModel model)
    {
        if (access.Name.Identifier.ValueText is not ("Name" or "FullName")
            || model.GetSymbolInfo(access).Symbol is not IPropertySymbol property
            || property.Name != access.Name.Identifier.ValueText
            || !IsSystemType(model.GetTypeInfo(access.Expression).Type))
        {
            return null;
        }

        return new IRMemberAccess(LowerExpr(access.Expression, model), access.Name.Identifier.ValueText);
    }

    private static bool IsSystemObjectGetType(IMethodSymbol symbol)
        => symbol is { IsStatic: false, Name: "GetType", Parameters.Length: 0 }
           && symbol.ContainingType.SpecialType == SpecialType.System_Object
           && symbol.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::System.Type";

    private static bool IsSystemType(ITypeSymbol? type)
        => type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::System.Type";

    private IRExpr LowerIsPatternExpression(IsPatternExpressionSyntax expression, SemanticModel model)
    {
        var value = LowerExpr(expression.Expression, model);
        return LowerPatternTest(value, expression.Pattern, model);
    }

    private IRExpr LowerPatternTest(IRExpr value, PatternSyntax pattern, SemanticModel model)
    {
        switch (pattern)
        {
            case ParenthesizedPatternSyntax parenthesized:
                return LowerPatternTest(value, parenthesized.Pattern, model);

            case UnaryPatternSyntax unary when unary.IsKind(SyntaxKind.NotPattern):
                return new IRUnary("!", LowerPatternTest(value, unary.Pattern, model));

            case ConstantPatternSyntax constant:
                return new IRBinary("==", value, LowerExpr(constant.Expression, model));

            case TypePatternSyntax typePattern:
                return LowerTypePatternTest(value, typePattern.Type, model);

            case DeclarationPatternSyntax { Designation: DiscardDesignationSyntax } declarationPattern:
                return LowerTypePatternTest(value, declarationPattern.Type, model);

            case DeclarationPatternSyntax declarationPattern:
                AddDiagnostic(declarationPattern.GetLocation(), "declaration patterns are not supported; use an explicit cast or 'as' assignment after a separate type check");
                return new IRLiteral(false, IRLiteralKind.Boolean);

            default:
                AddDiagnostic(pattern.GetLocation(), $"unsupported pattern: {pattern.Kind()}");
                return new IRLiteral(false, IRLiteralKind.Boolean);
        }
    }

    private IRExpr LowerTypePatternTest(IRExpr value, TypeSyntax typeSyntax, SemanticModel model)
    {
        var type = GetTypeSymbol(typeSyntax, model);
        if (type is null)
        {
            AddDiagnostic(typeSyntax.GetLocation(), $"unsupported type: {typeSyntax.Kind()}");
            return new IRLiteral(false, IRLiteralKind.Boolean);
        }

        if (type is INamedTypeSymbol namedType && CanFlattenStructType(namedType))
        {
            AddDiagnostic(typeSyntax.GetLocation(), "struct runtime type checks are not supported; SharpForge structs are compile-time value shapes and do not carry runtime type metadata");
        }

        return new IRIs(value, LowerRuntimeTypeTarget(type));
    }

    private IRExpr LowerRuntimeTypeTarget(ITypeSymbol type)
        => type switch
        {
            INamedTypeSymbol namedType => LowerTypeReference(namedType),
            ITypeParameterSymbol typeParameter => new IRIdentifier(GetLuaName(typeParameter, typeParameter.Name)),
            _ => new IRLiteral(null, IRLiteralKind.Nil),
        };

    private static ITypeSymbol? GetTypeSymbol(SyntaxNode typeSyntax, SemanticModel model)
        => model.GetTypeInfo(typeSyntax).Type
           ?? model.GetSymbolInfo(typeSyntax).Symbol as ITypeSymbol;

    private IRStmt? TryLowerDeclarationPatternIf(IfStatementSyntax ifStatement, SemanticModel model, CancellationToken ct)
    {
        if (ifStatement.Condition is not IsPatternExpressionSyntax
            {
                Pattern: DeclarationPatternSyntax
                {
                    Designation: SingleVariableDesignationSyntax designation,
                    Type: { } patternType
                }
            } isPattern)
        {
            return null;
        }

        var declaredSymbol = model.GetDeclaredSymbol(designation, ct);
        var localName = DeclareLuaName(declaredSymbol, designation.Identifier.ValueText);
        var block = new IRBlock();
        block.Statements.Add(new IRLocalDecl(localName, LowerExpr(isPattern.Expression, model)));

        var thenBlock = new IRBlock();
        LowerBlock(ifStatement.Statement, thenBlock, model, ct);
        IRBlock? elseBlock = null;
        if (ifStatement.Else is { } elseClause)
        {
            elseBlock = new IRBlock();
            LowerBlock(elseClause.Statement, elseBlock, model, ct);
        }

        block.Statements.Add(new IRIf(LowerTypePatternTest(new IRIdentifier(localName), patternType, model), thenBlock, elseBlock));
        return block;
    }

    private bool IsStructRuntimeCast(CastExpressionSyntax cast, SemanticModel model)
    {
        var targetType = model.GetTypeInfo(cast.Type).Type;
        var sourceType = model.GetTypeInfo(cast.Expression).Type;
        return IsFlattenableStructType(targetType) || IsFlattenableStructType(sourceType);
    }

    private bool IsFlattenableStructType(ITypeSymbol? type)
        => type is INamedTypeSymbol { SpecialType: SpecialType.None } namedType
           && CanFlattenStructType(namedType)
           && GetFlattenableStructFields(namedType).Any();

    private void ValidateStructRuntimeFeatures(TypeDeclarationSyntax typeDecl, INamedTypeSymbol symbol, SemanticModel model, CancellationToken ct)
    {
        ValidateUnsupportedStructRuntimeConversions(typeDecl, model);

        if (symbol.TypeKind != TypeKind.Struct)
        {
            return;
        }

        foreach (var methodDecl in typeDecl.Members.OfType<MethodDeclarationSyntax>())
        {
            if (model.GetDeclaredSymbol(methodDecl, ct) is not { } method)
            {
                continue;
            }

            if (IsObjectEqualsOverride(method))
            {
                AddDiagnostic(methodDecl.Identifier.GetLocation(), "struct Equals(object) is not supported; SharpForge structs are compile-time value shapes and cannot be boxed for runtime equality");
            }
            else if (IsGetHashCodeOverride(method))
            {
                AddDiagnostic(methodDecl.Identifier.GetLocation(), "struct GetHashCode() is not supported; SharpForge structs do not support boxed/hash-based value identity");
            }
        }
    }

    private static bool IsObjectEqualsOverride(IMethodSymbol method)
        => method is { IsOverride: true, Name: "Equals", Parameters.Length: 1 }
           && method.Parameters[0].Type.SpecialType == SpecialType.System_Object;

    private static bool IsGetHashCodeOverride(IMethodSymbol method)
        => method is { IsOverride: true, Name: "GetHashCode", Parameters.Length: 0 };

    private void ValidateUnsupportedStructRuntimeConversions(SyntaxNode node, SemanticModel model)
    {
        foreach (var expression in node.DescendantNodes().OfType<ExpressionSyntax>())
        {
            if (expression is CastExpressionSyntax or TypeSyntax)
            {
                continue;
            }

            var conversion = model.GetConversion(expression);
            if (conversion.IsBoxing && IsFlattenableStructType(model.GetTypeInfo(expression).Type))
            {
                AddDiagnostic(expression.GetLocation(), "struct boxing conversions are not supported; SharpForge structs are compile-time value shapes and cannot be stored as object/interface values");
            }
        }

        foreach (var isPattern in node.DescendantNodes().OfType<IsPatternExpressionSyntax>())
        {
            if (isPattern.Pattern is DeclarationPatternSyntax { Type: { } declarationType }
                && IsFlattenableStructType(model.GetTypeInfo(declarationType).Type))
            {
                AddDiagnostic(declarationType.GetLocation(), "struct runtime type checks are not supported; SharpForge structs are compile-time value shapes and do not carry runtime type metadata");
            }
            else if (isPattern.Pattern is TypePatternSyntax { Type: { } typePattern }
                && IsFlattenableStructType(model.GetTypeInfo(typePattern).Type))
            {
                AddDiagnostic(typePattern.GetLocation(), "struct runtime type checks are not supported; SharpForge structs are compile-time value shapes and do not carry runtime type metadata");
            }
        }

        foreach (var returnStatement in node.DescendantNodes().OfType<ReturnStatementSyntax>())
        {
            if (returnStatement.Expression is not { } expression
                || model.GetEnclosingSymbol(returnStatement.SpanStart) is not IMethodSymbol method
                || !IsRuntimeObjectLikeType(method.ReturnType)
                || !IsFlattenableStructType(model.GetTypeInfo(expression).Type))
            {
                continue;
            }

            AddDiagnostic(expression.GetLocation(), "struct boxing conversions are not supported; SharpForge structs are compile-time value shapes and cannot be stored as object/interface values");
        }

        foreach (var variable in node.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            if (variable.Initializer?.Value is not { } initializer
                || model.GetDeclaredSymbol(variable) is not { } symbol
                || !IsRuntimeObjectLikeType(GetSymbolType(symbol))
                || !IsFlattenableStructType(model.GetTypeInfo(initializer).Type))
            {
                continue;
            }

            AddDiagnostic(initializer.GetLocation(), "struct boxing conversions are not supported; SharpForge structs are compile-time value shapes and cannot be stored as object/interface values");
        }

        foreach (var assignment in node.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                || !IsRuntimeObjectLikeType(model.GetTypeInfo(assignment.Left).Type)
                || !IsFlattenableStructType(model.GetTypeInfo(assignment.Right).Type))
            {
                continue;
            }

            AddDiagnostic(assignment.Right.GetLocation(), "struct boxing conversions are not supported; SharpForge structs are compile-time value shapes and cannot be stored as object/interface values");
        }
    }

    private static ITypeSymbol? GetSymbolType(ISymbol symbol)
        => symbol switch
        {
            ILocalSymbol local => local.Type,
            IFieldSymbol field => field.Type,
            IPropertySymbol property => property.Type,
            _ => null,
        };

    private static bool IsRuntimeObjectLikeType(ITypeSymbol? type)
        => type is not null && (type.SpecialType == SpecialType.System_Object || type.TypeKind == TypeKind.Interface);

    private IRExpr LowerObjectCreation(BaseObjectCreationExpressionSyntax obj, SemanticModel model)
    {
        var typeSymbol = (model.GetSymbolInfo(obj).Symbol as IMethodSymbol)?.ContainingType
            ?? model.GetTypeInfo(obj).Type;

        if (typeSymbol is null)
        {
            return new IRIdentifier($"--[[unsupported expr: {obj.Kind()}]]nil");
        }

        if (typeSymbol is ITypeParameterSymbol typeParameter)
        {
            return ApplyObjectInitializer(
                obj,
                new IRInvocation(new IRMemberAccess(LowerRuntimeTypeTarget(typeParameter), "New"), Array.Empty<IRExpr>()),
                model);
        }

        if (typeSymbol is not INamedTypeSymbol type)
        {
            AddDiagnostic(obj.GetLocation(), $"unsupported object creation type: {typeSymbol.Kind}");
            return new IRLiteral(null, IRLiteralKind.Nil);
        }

        if (IsRegexType(type))
        {
            AddDiagnostic(obj.GetLocation(), "Regex constructors are not supported; only static Regex.IsMatch(input, constantPattern) is supported");
            return new IRLiteral(null, IRLiteralKind.Nil);
        }

        var ctor = model.GetSymbolInfo(obj).Symbol as IMethodSymbol;
        var loweredArgs = ctor is null
            ? new LoweredCallArguments(obj.ArgumentList?.Arguments.Select(a => LowerExpr(a.Expression, model)).ToArray() ?? Array.Empty<IRExpr>(), Array.Empty<IRStmt>())
            : obj.ArgumentList is null
                ? new LoweredCallArguments(Array.Empty<IRExpr>(), Array.Empty<IRStmt>())
                : LowerCallArguments(obj.ArgumentList.Arguments, ctor.Parameters, model);
        var args = loweredArgs.Arguments;

        if (CanFlattenStructType(type) && TryGetStructFieldValues(obj, model, out var structFields, out var structValues))
        {
            return new IRTableLiteralNew(structFields.Zip(structValues, (field, value) => (field.Name, value)).ToArray());
        }

        if (HasLuaTableLiteralAttribute(type))
        {
            var fields = new List<(string Key, IRExpr Value)>();

            fields.AddRange(args.Select((value, i) => (Key: ctor?.Parameters[i].Name ?? $"field{i}", Value: value)));

            if (obj.Initializer is not null)
            {
                foreach (var expression in obj.Initializer.Expressions)
                {
                    if (expression is not AssignmentExpressionSyntax assignment)
                    {
                        continue;
                    }

                    string? key = assignment.Left switch
                    {
                        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                        MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
                        _ => null
                    };

                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    fields.Add((key, LowerExpr(assignment.Right, model)));
                }
            }

            return new IRTableLiteralNew(fields);
        }

        if (IsExternalLuaObjectType(type))
        {
            return ApplyObjectInitializer(
                obj,
                BuildCallExpression(
                    loweredArgs,
                    lowered => new IRInvocation(
                        new IRMemberAccess(GetLuaObjectTypeTarget(type), ctor is null ? "new" : GetLuaObjectConstructorName(ctor)),
                        lowered)),
                model);
        }

        return ApplyObjectInitializer(
            obj,
            BuildCallExpression(
                loweredArgs,
                lowered => new IRInvocation(new IRMemberAccess(LowerTypeReference(type), ctor is null ? "New" : GetLuaMethodName(ctor)), lowered)),
            model);
    }

    private IRExpr ApplyObjectInitializer(BaseObjectCreationExpressionSyntax obj, IRExpr creation, SemanticModel model)
    {
        if (obj.Initializer is null || obj.Initializer.Expressions.Count == 0)
        {
            return creation;
        }

        var objectName = AllocateLuaName("obj");
        var objectRef = new IRIdentifier(objectName);
        var body = new IRBlock();
        body.Statements.Add(new IRLocalDecl(objectName, creation));

        foreach (var expression in obj.Initializer.Expressions)
        {
            if (expression is not AssignmentExpressionSyntax assignment || !assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
            {
                AddDiagnostic(expression.GetLocation(), $"unsupported object initializer expression: {expression.Kind()}");
                continue;
            }

            body.Statements.Add(LowerObjectInitializerAssignment(objectRef, assignment, model));
        }

        body.Statements.Add(new IRReturn(objectRef));
        return new IRInvocation(new IRFunctionExpression(Array.Empty<string>(), body), Array.Empty<IRExpr>());
    }

    private IRStmt LowerObjectInitializerAssignment(IRExpr objectRef, AssignmentExpressionSyntax assignment, SemanticModel model)
    {
        var target = LowerObjectInitializerTarget(objectRef, assignment.Left, model);
        if (model.GetSymbolInfo(assignment.Left).Symbol is IPropertySymbol property && !IsAutoPropertySymbol(property))
        {
            var setterTarget = target is IRMemberAccess memberAccess ? memberAccess.Target : objectRef;
            return new IRExprStmt(new IRInvocation(
                new IRMemberAccess(setterTarget, "set_" + property.Name),
                new[] { LowerExpr(assignment.Right, model) },
                UseColon: !property.IsStatic));
        }

        return new IRAssign(target, LowerExpr(assignment.Right, model));
    }

    private IRExpr LowerObjectInitializerTarget(IRExpr objectRef, ExpressionSyntax target, SemanticModel model)
    {
        return target switch
        {
            IdentifierNameSyntax identifier => new IRMemberAccess(objectRef, identifier.Identifier.ValueText),
            MemberAccessExpressionSyntax memberAccess => new IRMemberAccess(
                LowerObjectInitializerTarget(objectRef, memberAccess.Expression, model),
                memberAccess.Name.Identifier.ValueText),
            _ => UnsupportedObjectInitializerTarget(target),
        };
    }

    private IRExpr UnsupportedObjectInitializerTarget(ExpressionSyntax target)
    {
        AddDiagnostic(target.GetLocation(), $"unsupported object initializer target: {target.Kind()}");
        return new IRIdentifier("--[[unsupported object initializer target]]nil");
    }

    private void TrackLuaObjectModuleBinding(VariableDeclaratorSyntax variable, string localName, SemanticModel model)
    {
        if (variable.Initializer?.Value is not InvocationExpressionSyntax invocation
            || model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol symbol
            || !IsLuaInteropMethod(symbol)
            || symbol.Name != "Require"
            || symbol.TypeArguments.Length != 1
            || symbol.TypeArguments[0] is not INamedTypeSymbol type
            || !IsExternalLuaObjectType(type))
        {
            return;
        }

        _luaObjectModuleNames[type] = localName;
    }

    private IRExpr? TryLowerCollectionLengthAccess(MemberAccessExpressionSyntax access, SemanticModel model)
    {
        var symbol = model.GetSymbolInfo(access).Symbol;
        if (symbol is not IPropertySymbol { Name: "Length" })
        {
            return null;
        }

        var type = model.GetTypeInfo(access.Expression).Type;
        if (type is IArrayTypeSymbol)
        {
            return new IRLength(LowerExpr(access.Expression, model));
        }

        return null;
    }

    private IRExpr? TryLowerLuaObjectMemberAccess(MemberAccessExpressionSyntax access, SemanticModel model)
    {
        var symbol = model.GetSymbolInfo(access).Symbol;
        if (symbol is not IFieldSymbol and not IPropertySymbol)
        {
            return null;
        }

        if (symbol.ContainingType is null || !IsExternalLuaObjectType(symbol.ContainingType))
        {
            return null;
        }

        var target = symbol.IsStatic
            ? GetLuaObjectTypeTarget(symbol.ContainingType)
            : LowerExpr(access.Expression, model);
        return new IRMemberAccess(target, GetLuaObjectMemberName(symbol));
    }

    private bool IsCollectionElementAccess(ElementAccessExpressionSyntax access, SemanticModel model)
    {
        var type = model.GetTypeInfo(access.Expression).Type;
        return type is IArrayTypeSymbol;
    }

    private static bool IsRegexType(ITypeSymbol? type)
        => type is INamedTypeSymbol { Name: "Regex", ContainingNamespace: { } ns }
           && ns.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::System.Text.RegularExpressions";

    private static bool IsTaskDelay(IMethodSymbol symbol)
        => symbol is { Name: "Delay", IsStatic: true, ContainingType: { Name: "Task", ContainingNamespace: { } ns } }
           && ns.ToDisplayString() == "System.Threading.Tasks";

    private IRStmt? TryLowerLuaInteropStatement(ExpressionSyntax expression, SemanticModel model)
    {
        if (expression is not InvocationExpressionSyntax invocation
            || model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol symbol
            || !IsLuaInteropMethod(symbol))
        {
            return null;
        }

        var args = invocation.ArgumentList.Arguments.Select(a => LowerExpr(a.Expression, model)).ToArray();
        return symbol.Name switch
        {
            "Set" when args.Length == 3 => new IRAssign(new IRLuaAccess(args[0], args[1]), args[2]),
            "SetGlobal" when args.Length == 2 => new IRAssign(new IRLuaGlobal(args[0]), args[1]),
            _ => null,
        };
    }

    private IRExpr? TryLowerLuaInteropInvocation(IMethodSymbol symbol, IReadOnlyList<IRExpr> args)
    {
        if (!IsLuaInteropMethod(symbol))
        {
            return null;
        }

        return symbol.Name switch
        {
            "CreateTable" when args.Count == 0 => new IRLuaTable(),
            "Require" when args.Count == 1 => new IRLuaRequire(args[0]),
            "Get" when args.Count == 2 => new IRLuaAccess(args[0], args[1]),
            "GetGlobal" when args.Count == 1 => new IRLuaGlobal(args[0]),
            "Call" when args.Count >= 2 => new IRInvocation(new IRLuaAccess(args[0], args[1]), args.Skip(2).ToArray()),
            "CallMethod" when args.Count >= 2 => new IRLuaMethodInvocation(args[0], args[1], args.Skip(2).ToArray()),
            "CallGlobal" when args.Count >= 1 => new IRInvocation(new IRLuaGlobal(args[0]), args.Skip(1).ToArray()),
            "Eq" when args.Count == 2 => new IRBinary("==", args[0], args[1]),
            "Eq" when symbol.TypeArguments is [INamedTypeSymbol structType] && CanFlattenStructType(structType) && GetFlattenableStructFields(structType).Any() =>
                BuildOpEqualityCall(structType, args),
            "Lt" when args.Count == 2 => new IRBinary("<", args[0], args[1]),
            "Gt" when args.Count == 2 => new IRBinary(">", args[0], args[1]),
            _ => null,
        };
    }

    private bool IsLuaInteropMethod(IMethodSymbol symbol)
        => symbol is { IsStatic: true, ContainingType: { Name: "LuaInterop", ContainingNamespace: { } ns } }
           && IsSFLibInteropNamespace(ns);

    private bool IsLuaObjectType(INamedTypeSymbol symbol)
        => InheritsFromLuaObject(symbol);

    private bool IsLuaImplementedClass(INamedTypeSymbol symbol)
        => GetLuaAttributeValue(symbol, "Class") is { Length: > 0 };

    private bool IsExternalLuaObjectType(INamedTypeSymbol symbol)
        => IsLuaObjectType(symbol) && !IsLuaImplementedClass(symbol);

    private bool InheritsFromLuaObject(INamedTypeSymbol symbol)
    {
        for (var type = symbol; type is not null; type = type.BaseType)
        {
            if (type is { Name: "LuaObject", ContainingNamespace: { } ns } && IsSFLibInteropNamespace(ns))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsTaskDelay(SyntaxNode node, SemanticModel model)
        => node.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(invocation => model.GetSymbolInfo(invocation).Symbol as IMethodSymbol)
            .Any(symbol => symbol is not null && IsTaskDelay(symbol));

    private bool IsSFLibType(INamedTypeSymbol symbol)
        => _ignoredNamespaces.Contains(symbol.ContainingNamespace.ToDisplayString());

    private bool IsSFLibInteropNamespace(INamespaceSymbol ns)
        => _ignoredNamespaces.Contains(ns.ToDisplayString());

    private IRStmt? TryLowerAccessorAssignment(ExpressionSyntax left, ExpressionSyntax right, SemanticModel model)
    {
        if (left is IdentifierNameSyntax id && model.GetSymbolInfo(id).Symbol is IPropertySymbol property && !IsAutoPropertySymbol(property))
        {
            IRExpr target = property.IsStatic ? LowerTypeReference(property.ContainingType) : new IRIdentifier("self");
            return new IRExprStmt(new IRInvocation(
                new IRMemberAccess(target, "set_" + property.Name),
                new[] { LowerExpr(right, model) },
                UseColon: !property.IsStatic));
        }

        if (left is MemberAccessExpressionSyntax memberAccess && model.GetSymbolInfo(memberAccess).Symbol is IPropertySymbol memberProperty && !IsAutoPropertySymbol(memberProperty))
        {
            return new IRExprStmt(new IRInvocation(
                new IRMemberAccess(LowerExpr(memberAccess.Expression, model), "set_" + memberProperty.Name),
                new[] { LowerExpr(right, model) },
                UseColon: !memberProperty.IsStatic));
        }

        if (left is ElementAccessExpressionSyntax elementAccess && model.GetSymbolInfo(elementAccess).Symbol is IPropertySymbol { IsIndexer: true } indexer)
        {
            var args = elementAccess.ArgumentList.Arguments.Select(a => LowerExpr(a.Expression, model)).Append(LowerExpr(right, model)).ToArray();
            return new IRExprStmt(new IRInvocation(
                new IRMemberAccess(LowerExpr(elementAccess.Expression, model), "set_Item"),
                args,
                UseColon: !indexer.IsStatic));
        }

        return null;
    }

    private IRExpr? TryLowerPropertyGet(MemberAccessExpressionSyntax access, SemanticModel model)
    {
        if (model.GetSymbolInfo(access).Symbol is not IPropertySymbol property || property.IsIndexer || IsAutoPropertySymbol(property))
        {
            return null;
        }

        if (property is { IsStatic: false, ContainingType.TypeKind: TypeKind.Struct }
            && CanFlattenStructType(property.ContainingType))
        {
            return new IRInvocation(
                new IRMemberAccess(LowerTypeReference(property.ContainingType), "get_" + property.Name),
                LowerStructArgumentValues(access.Expression, property.ContainingType, model));
        }

        return new IRInvocation(
            new IRMemberAccess(LowerExpr(access.Expression, model), "get_" + property.Name),
            Array.Empty<IRExpr>(),
            UseColon: !property.IsStatic);
    }

    private static IRExpr? TryLowerStringEmptyMemberAccess(MemberAccessExpressionSyntax access, SemanticModel model)
        => access.Name.Identifier.ValueText == "Empty"
           && model.GetSymbolInfo(access).Symbol is IFieldSymbol { ContainingType.SpecialType: SpecialType.System_String }
            ? new IRLiteral(string.Empty, IRLiteralKind.String)
            : null;

    private IRExpr? TryLowerIndexerGet(ElementAccessExpressionSyntax access, SemanticModel model)
    {
        if (model.GetSymbolInfo(access).Symbol is not IPropertySymbol { IsIndexer: true } indexer)
        {
            return null;
        }

        return new IRInvocation(
            new IRMemberAccess(LowerExpr(access.Expression, model), "get_Item"),
            access.ArgumentList.Arguments.Select(a => LowerExpr(a.Expression, model)).ToArray(),
            UseColon: !indexer.IsStatic);
    }

    private static bool IsAutoPropertySymbol(IPropertySymbol property)
        => property.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax())
            .OfType<PropertyDeclarationSyntax>()
            .Any(IsAutoProperty);

    /// <summary>
    /// Resolves bare identifiers via the semantic model. Instance fields/properties/
    /// methods of the enclosing type get rewritten as <c>self.X</c> so callers don't
    /// have to spell <c>this.</c> explicitly.
    /// </summary>
    private IRExpr LowerIdentifier(IdentifierNameSyntax id, SemanticModel model)
    {
        var symbol = model.GetSymbolInfo(id).Symbol;
        if (symbol is IPropertySymbol property && !IsAutoPropertySymbol(property))
        {
            if (property is { IsStatic: false, ContainingType.TypeKind: TypeKind.Struct }
                && SymbolEqualityComparer.Default.Equals(property.ContainingType, _currentStructSelfType)
                && CanFlattenStructType(property.ContainingType))
            {
                return new IRInvocation(
                    new IRMemberAccess(LowerTypeReference(property.ContainingType), "get_" + property.Name),
                    GetCurrentStructSelfArguments(property.ContainingType));
            }

            IRExpr target = property.IsStatic ? LowerTypeReferenceForAccess(property.ContainingType) : new IRIdentifier("self");
            return new IRInvocation(
                new IRMemberAccess(target, "get_" + property.Name),
                Array.Empty<IRExpr>(),
                UseColon: !property.IsStatic);
        }

        if (symbol is INamedTypeSymbol type)
        {
            return LowerTypeReferenceForAccess(type);
        }

        if (symbol is { IsStatic: true } and (IFieldSymbol or IPropertySymbol) && symbol.ContainingType is not null)
        {
            if (IsIgnoredClass(symbol.ContainingType))
            {
                return new IRIdentifier(id.Identifier.ValueText);
            }

            return new IRMemberAccess(LowerTypeReferenceForAccess(symbol.ContainingType), id.Identifier.ValueText);
        }

        if (symbol is IMethodSymbol { IsStatic: true } staticMethod)
        {
            if (IsIgnoredClass(staticMethod.ContainingType))
            {
                return new IRIdentifier(staticMethod.Name);
            }

            return new IRMemberAccess(LowerTypeReferenceForAccess(staticMethod.ContainingType), GetLuaMethodName(staticMethod));
        }

        if (symbol is { IsStatic: false } and (IFieldSymbol or IPropertySymbol or IMethodSymbol)
            && symbol.ContainingType is not null)
        {
            if (symbol is IFieldSymbol or IPropertySymbol
                && SymbolEqualityComparer.Default.Equals(symbol.ContainingType, _currentStructSelfType)
                && _currentStructSelfFields is not null
                && _currentStructSelfFields.TryGetValue(symbol.Name, out var selfFieldName))
            {
                return new IRIdentifier(selfFieldName);
            }

            return symbol is IMethodSymbol instanceMethod
                ? new IRMemberAccess(new IRIdentifier("self"), GetLuaMethodName(instanceMethod))
                : new IRMemberAccess(new IRIdentifier("self"), id.Identifier.ValueText);
        }
        if (symbol is ILocalSymbol or IParameterSymbol or IRangeVariableSymbol)
        {
            if (_flattenedStructLocals.TryGetValue(symbol, out var flattenedLocal))
            {
                return new IRTableLiteralNew(flattenedLocal.FieldLocals
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => (pair.Key, Value: (IRExpr)new IRIdentifier(pair.Value)))
                    .ToArray());
            }

            return new IRIdentifier(GetLuaName(symbol, id.Identifier.ValueText));
        }

        return new IRIdentifier(EscapeLuaKeyword(id.Identifier.ValueText));
    }

    private IRExpr LowerGenericName(GenericNameSyntax genericName, SemanticModel model)
    {
        var symbol = model.GetSymbolInfo(genericName).Symbol;
        if (symbol is INamedTypeSymbol type)
        {
            return LowerTypeReferenceForAccess(type);
        }

        if (symbol is ITypeParameterSymbol typeParameter)
        {
            return LowerRuntimeTypeTarget(typeParameter);
        }

        if (symbol is IMethodSymbol method)
        {
            if (method.IsStatic)
            {
                return IsIgnoredClass(method.ContainingType)
                    ? new IRIdentifier(method.Name)
                    : new IRMemberAccess(LowerTypeReferenceForAccess(method.ContainingType), GetLuaMethodName(method));
            }

            return new IRMemberAccess(new IRIdentifier("self"), GetLuaMethodName(method));
        }

        return new IRIdentifier(EscapeLuaKeyword(genericName.Identifier.ValueText));
    }

    private string DeclareLuaName(ISymbol? symbol, string name)
    {
        var luaName = AllocateLuaName(name);
        if (symbol is not null)
        {
            _luaNames[symbol] = luaName;
            _luaNames[symbol.OriginalDefinition] = luaName;
        }
        return luaName;
    }

    private string GetLuaName(ISymbol symbol, string fallback)
    {
        if (_luaNames.TryGetValue(symbol.OriginalDefinition, out var originalName))
        {
            return originalName;
        }

        if (_luaNames.TryGetValue(symbol, out var name))
        {
            return name;
        }

        var allocatedName = AllocateLuaName(fallback);
        _luaNames[symbol] = allocatedName;
        return allocatedName;
    }

    private string AllocateLuaName(string name)
    {
        var baseName = EscapeLuaKeyword(name);
        if (_usedLuaNames.Add(baseName))
        {
            return baseName;
        }

        string candidate;
        do
        {
            candidate = baseName + (++_luaNameCounter).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        while (!_usedLuaNames.Add(candidate));
        return candidate;
    }

    private static string EscapeLuaKeyword(string name)
        => name is "and" or "break" or "do" or "else" or "elseif" or "end" or "false" or "for" or "function" or "goto" or "if" or "in" or "local" or "nil" or "not" or "or" or "repeat" or "return" or "then" or "true" or "until" or "while" or "table"
            ? "KW__" + name
            : name;

    private void ValidateReservedIdentifiers(SyntaxNode node)
    {
        foreach (var token in node.DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken)))
        {
            if (token.ValueText.Contains("__", StringComparison.Ordinal))
            {
                AddReservedIdentifierDiagnostic(token);
            }
        }
    }

    private string GetLuaMethodName(IMethodSymbol method)
    {
        if (GetLuaAttributeName(method) is { Length: > 0 } attributeName)
        {
            return attributeName;
        }

        if (method.MethodKind == MethodKind.UserDefinedOperator)
        {
            return HasOverloadSiblings(method, MethodKind.UserDefinedOperator)
                ? AppendLuaParameterSignature(method.Name, method.Parameters)
                : method.Name;
        }

        if (method.MethodKind == MethodKind.Constructor)
        {
            return HasConstructorOverloads(method)
                ? AppendLuaParameterSignature("New", method.Parameters)
                : "New";
        }

        return HasOverloadSiblings(method, MethodKind.Ordinary)
            ? AppendLuaParameterSignature(method.Name, method.Parameters)
            : method.Name;
    }

    private static bool HasConstructorOverloads(IMethodSymbol method)
        => method.ContainingType.InstanceConstructors.Count(c => !c.IsImplicitlyDeclared) > 1;

    private static bool HasOverloadSiblings(IMethodSymbol method, MethodKind methodKind)
    {
        var overloads = method.ContainingType.GetMembers(method.Name)
            .OfType<IMethodSymbol>()
            .Where(m => !m.IsImplicitlyDeclared && m.MethodKind == methodKind)
            .ToArray();

        return overloads.Length > 1;
    }

    private static string AppendLuaParameterSignature(string name, IReadOnlyList<IParameterSymbol> parameters)
        => $"{name}__{GetLuaParameterSignature(parameters)}";

    private static string GetLuaParameterSignature(IReadOnlyList<IParameterSymbol> parameters)
        => parameters.Count == 0
            ? "0"
            : string.Concat(parameters.Select(parameter => GetLuaTypeSignature(parameter.Type)));

    private static string GetLuaTypeSignature(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol arrayType)
        {
            return "a" + GetLuaTypeSignature(arrayType.ElementType);
        }

        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullableType
            && nullableType.TypeArguments.Length == 1)
        {
            return GetLuaTypeSignature(nullableType.TypeArguments[0]);
        }

        if (type.TypeKind == TypeKind.Enum)
        {
            return SimplifyLuaSignatureTypeName(type.Name);
        }

        return type.SpecialType switch
        {
            SpecialType.System_Boolean => "b",
            SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_Int16 or SpecialType.System_UInt16
                or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64
                => "i",
            SpecialType.System_Single => "f",
            SpecialType.System_Double or SpecialType.System_Decimal => "d",
            SpecialType.System_String => "s",
            SpecialType.System_Object => "o",
            _ => type is INamedTypeSymbol { TypeArguments.Length: > 0 } namedType
                ? SimplifyLuaSignatureTypeName(namedType.Name) + string.Concat(namedType.TypeArguments.Select(GetLuaTypeSignature))
                : SimplifyLuaSignatureTypeName(type.Name),
        };
    }

    private static string SimplifyLuaSignatureTypeName(string name)
    {
        var simplified = new string(name
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

        return simplified.Length == 0 ? "p" : simplified;
    }

    private static string GetLuaOperatorName(string op)
        => op switch
        {
            "+" => "op_Addition",
            "-" => "op_Subtraction",
            "*" => "op_Multiply",
            "/" => "op_Division",
            "==" => "op_Equality",
            "!=" => "op_Inequality",
            _ => "op_Operator",
        };

    private string GetLuaConstructorInitName(IMethodSymbol method)
        => GetLuaMethodName(method).Replace("New", "__Init", StringComparison.Ordinal);

    private static IRTypeReference LowerTypeReference(INamedTypeSymbol type)
        => new(GetTypeContainerSegments(type), type.Name);

    private IRExpr LowerTypeReferenceForAccess(INamedTypeSymbol type)
        => IsExternalLuaObjectType(type) ? GetLuaObjectTypeTarget(type) : LowerTypeReference(type);

    private IRLuaClass? GetLuaClass(INamedTypeSymbol type)
    {
        if (GetLuaAttributeValue(type, "Class") is not { Length: > 0 } className)
        {
            return null;
        }

        var moduleBindings = new List<IRLuaModuleBinding>();
        var baseType = GetLuaClassBase(type, moduleBindings);
        return new IRLuaClass(className, baseType, moduleBindings);
    }

    private IRExpr? GetLuaClassBase(INamedTypeSymbol type, List<IRLuaModuleBinding> moduleBindings)
    {
        var baseType = type.BaseType;
        if (baseType is null
            || baseType.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType
            || IsIgnoredClass(baseType))
        {
            return null;
        }

        if (!IsExternalLuaObjectType(baseType))
        {
            return LowerTypeReference(baseType);
        }

        if (GetLuaAttributeValue(baseType, "Module") is not { Length: > 0 } moduleName)
        {
            return GetLuaObjectTypeTarget(baseType);
        }

        var localName = AllocateLuaName(GetLuaAttributeValue(baseType, "Name") ?? baseType.Name);
        moduleBindings.Add(new IRLuaModuleBinding(localName, moduleName));
        return new IRIdentifier(localName);
    }

    private IRExpr GetLuaObjectTypeTarget(INamedTypeSymbol type)
    {
        if (_luaObjectModuleNames.TryGetValue(type, out var localName)
            || _luaObjectModuleNames.TryGetValue(type.OriginalDefinition, out localName))
        {
            return new IRIdentifier(localName);
        }

        if (GetLuaAttributeValue(type, "Module") is { Length: > 0 } moduleName)
        {
            return EnsureLuaObjectModuleLocal(type, moduleName);
        }

        return new IRIdentifier(GetLuaAttributeValue(type, "Name") ?? type.Name);
    }

    private IRExpr EnsureLuaObjectModuleLocal(INamedTypeSymbol type, string moduleName)
    {
        if (_luaModuleBlockContexts.Count == 0)
        {
            return new IRLuaRequire(new IRLiteral(moduleName, IRLiteralKind.String));
        }

        var context = _luaModuleBlockContexts.Peek();
        if (context.ModuleLocals.TryGetValue(type, out var localName)
            || context.ModuleLocals.TryGetValue(type.OriginalDefinition, out localName))
        {
            return new IRIdentifier(localName);
        }

        localName = AllocateLuaName(GetLuaAttributeValue(type, "Name") ?? type.Name);
        context.ModuleLocals[type] = localName;
        InsertLuaModuleLocal(context, localName, moduleName);
        return new IRIdentifier(localName);
    }

    private void InsertLuaModuleLocal(LuaModuleBlockContext context, string localName, string moduleName)
    {
        var index = context.NextModuleInsertionIndex ?? GetInitialLuaModuleInsertionIndex(context.Block);

        context.Block.Statements.Insert(index, new IRLocalDecl(
            localName,
            new IRLuaRequire(new IRLiteral(moduleName, IRLiteralKind.String))));
        context.NextModuleInsertionIndex = index + 1;
    }

    private int GetInitialLuaModuleInsertionIndex(IRBlock block)
    {
        if ((_luaModuleInsertionIndex ?? 0) == 0)
        {
            return _luaModuleInsertionIndex ?? 0;
        }

        for (var i = 0; i < block.Statements.Count; i++)
        {
            if (block.Statements[i] is not IRRawComment)
            {
                return i + 1;
            }
        }

        return block.Statements.Count;
    }

    private string GetLuaObjectConstructorName(IMethodSymbol ctor)
        => GetLuaAttributeValue(ctor, "StaticMethod")
           ?? GetLuaAttributeValue(ctor, "Name")
           ?? "new";

    private (string Name, bool UseColon) GetLuaObjectMember(IMethodSymbol method)
    {
        if (GetLuaAttributeValue(method, "StaticMethod") is { Length: > 0 } staticName)
        {
            return (staticName, false);
        }

        if (GetLuaAttributeValue(method, "Method") is { Length: > 0 } methodName)
        {
            return (methodName, true);
        }

        return (GetLuaAttributeValue(method, "Name") ?? method.Name, !method.IsStatic);
    }

    private string GetLuaObjectMemberName(ISymbol symbol)
        => GetLuaAttributeValue(symbol, "Name") ?? symbol.Name;

    private string? GetLuaAttributeName(IMethodSymbol method)
        => GetLuaAttributeValue(method, "StaticMethod")
           ?? GetLuaAttributeValue(method, "Method")
           ?? GetLuaAttributeValue(method, "Name");

    private string? GetLuaAttributeValue(ISymbol symbol, string name)
        => GetLuaAttributeValues(symbol, name).FirstOrDefault();

    private bool HasLuaTableLiteralAttribute(INamedTypeSymbol symbol)
        => HasLuaBoolAttribute(symbol, "TableLiteral");

    private bool IsPackStructType(ITypeSymbol? type)
        => type is INamedTypeSymbol named && HasLuaBoolAttribute(named, "PackStruct");

    private bool HasLuaBoolAttribute(INamedTypeSymbol symbol, string propertyName)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            if (attribute.AttributeClass is not { Name: "LuaAttribute", ContainingNamespace: { } ns }
                || !IsSFLibInteropNamespace(ns))
            {
                continue;
            }

            foreach (var arg in attribute.NamedArguments)
            {
                if (arg.Key == propertyName && arg.Value.Value is true)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private IEnumerable<string> GetLuaAttributeValues(ISymbol symbol, string name)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            if (attribute.AttributeClass is not { Name: "LuaAttribute", ContainingNamespace: { } ns }
                || !IsSFLibInteropNamespace(ns))
            {
                continue;
            }

            foreach (var argument in attribute.NamedArguments)
            {
                if (argument.Key == name && argument.Value.Value is string value && value.Length > 0)
                {
                    yield return value;
                }
            }
        }
    }

    private IRBaseConstructorCall? GetImplicitBaseConstructorCall(INamedTypeSymbol owner)
    {
        var baseType = GetLowerableBaseType(owner);
        if (baseType is null || owner.BaseType is null)
        {
            return null;
        }

        var parameterlessBaseCtor = owner.BaseType.InstanceConstructors.FirstOrDefault(c => c.Parameters.Length == 0);
        return parameterlessBaseCtor is null
            ? null
            : new IRBaseConstructorCall(baseType, GetLuaConstructorInitName(parameterlessBaseCtor), Array.Empty<IRExpr>());
    }

    private IRTypeReference? GetLowerableBaseType(INamedTypeSymbol symbol)
    {
        var baseType = symbol.BaseType;
        if (baseType is null
            || baseType.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType
            || IsIgnoredClass(baseType)
            || IsExternalLuaObjectType(baseType))
        {
            return null;
        }

        return LowerTypeReference(baseType);
    }

    private sealed class LuaModuleBlockContext(IRBlock block)
    {
        public IRBlock Block { get; } = block;
        public Dictionary<INamedTypeSymbol, string> ModuleLocals { get; } = new(SymbolEqualityComparer.Default);
        public int? NextModuleInsertionIndex { get; set; }
    }

    private sealed record FlattenedStructLocal(IReadOnlyDictionary<string, string> FieldLocals);

    private sealed record FlattenedStructMember(bool IsStatic, INamedTypeSymbol ContainingType, IReadOnlyDictionary<string, string> FieldMembers);

    private sealed record StructFieldSlot(string Name, ITypeSymbol Type, int SortKey);

    private static bool HasUserDefinedEquality(INamedTypeSymbol type)
        => type.GetMembers().Any(m =>
            m is IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator, Name: "op_Equality" }
            || (m is IMethodSymbol { Name: "Equals", IsOverride: false, Parameters.Length: 1 } eq
                && SymbolEqualityComparer.Default.Equals(eq.Parameters[0].Type, type)));

    private static IRExpr BuildFieldComparison(StructFieldSlot[] fields, Func<int, IRExpr> leftSelector, Func<int, IRExpr> rightSelector)
    {
        IRExpr? combined = null;
        for (var i = 0; i < fields.Length; i++)
        {
            var left = leftSelector(i);
            var right = rightSelector(i);
            var isFloat = fields[i].Type.SpecialType is SpecialType.System_Single or SpecialType.System_Double;
            var fieldEq = isFloat
                ? new IRBinary("<",
                    new IRInvocation(
                        new IRMemberAccess(new IRIdentifier("math"), "abs"),
                        new[] { new IRBinary("-", left, right) }),
                    new IRLiteral(0.0001, IRLiteralKind.Real))
                : new IRBinary("==", left, right);

            combined = combined is null ? fieldEq : new IRBinary("and", combined, fieldEq);
        }

        return combined ?? new IRLiteral(true, IRLiteralKind.Boolean);
    }

    private static IRFunction BuildOpEqualityFunction(StructFieldSlot[] fields)
    {
        var func = new IRFunction
        {
            Name = "op_Equality",
            LuaName = "op_Equality",
            IsStatic = true,
        };

        for (var i = 0; i < fields.Length; i++)
        {
            func.Parameters.Add($"a__{fields[i].Name}");
        }

        for (var i = 0; i < fields.Length; i++)
        {
            func.Parameters.Add($"b__{fields[i].Name}");
        }

        func.Body.Statements.Add(new IRReturn(BuildFieldComparison(
            fields,
            i => new IRIdentifier($"a__{fields[i].Name}"),
            i => new IRIdentifier($"b__{fields[i].Name}"))));
        return func;
    }

    private static IRFunction BuildOpEqPackFunction(StructFieldSlot[] fields)
    {
        var func = new IRFunction
        {
            Name = "opEqPack__",
            LuaName = "opEqPack__",
            IsStatic = true,
        };

        func.Parameters.Add("a");
        func.Parameters.Add("b");

        func.Body.Statements.Add(new IRReturn(BuildFieldComparison(
            fields,
            i => new IRMemberAccess(new IRIdentifier("a"), fields[i].Name),
            i => new IRMemberAccess(new IRIdentifier("b"), fields[i].Name))));
        return func;
    }

    private IRExpr BuildStructEqualityExpression(ExpressionSyntax leftExpr, ExpressionSyntax rightExpr, INamedTypeSymbol structType, SemanticModel model)
    {
        var leftFields = LowerStructArgumentValues(leftExpr, structType, model);
        var rightFields = LowerStructArgumentValues(rightExpr, structType, model);

        EnsureOpEqualityGenerated(structType);

        var nsSegments = GetTypeContainerSegments(structType);
        var typeRef = new IRTypeReference(nsSegments, structType.Name);
        var allArgs = leftFields.Concat(rightFields).ToArray();

        return new IRInvocation(
            new IRMemberAccess(typeRef, "op_Equality"),
            allArgs);
    }

    private IRExpr BuildOpEqualityCall(INamedTypeSymbol structType, IReadOnlyList<IRExpr> args)
    {
        EnsureOpEqualityGenerated(structType);
        var nsSegments = GetTypeContainerSegments(structType);
        var typeRef = new IRTypeReference(nsSegments, structType.Name);
        return new IRInvocation(new IRMemberAccess(typeRef, "op_Equality"), args.ToArray());
    }

    private void EnsureOpEqualityGenerated(INamedTypeSymbol structType)
    {
        if (HasUserDefinedEquality(structType))
        {
            return;
        }

        var fields = GetFlattenableStructFields(structType).ToArray();
        if (fields.Length == 0)
        {
            return;
        }

        var nsSegments = GetTypeContainerSegments(structType);
        var fullName = string.Join('.', nsSegments.Append(structType.Name));

        // Try _module.Types first, fall back to _currentIrType (not yet in module)
        var irType = _module?.Types.FirstOrDefault(t => t.FullName == fullName)
                     ?? (_currentIrTypeFullName == fullName ? _currentIrType : null);
        if (irType is null || irType.Methods.Any(m => m.LuaName == "op_Equality"))
        {
            return;
        }

        irType.Methods.Add(BuildOpEqualityFunction(fields));
        irType.Methods.Add(BuildOpEqPackFunction(fields));
    }

    private IRExpr? TryUnpackPackStructReturn(IRExpr call, IMethodSymbol method)
    {
        if (!IsPackStructType(method.ContainingType))
        {
            return null;
        }

        if (method.ReturnType is not INamedTypeSymbol returnType
            || !CanFlattenStructType(returnType)
            || GetFlattenableStructFields(returnType).ToArray() is not { Length: > 0 } fields)
        {
            return null;
        }

        var tempName = AllocateLuaName("__struct_tmp");
        var body = new IRBlock();
        body.Statements.Add(new IRLocalDecl(tempName, call));
        var tableFields = fields
            .Select(f => (f.Name, Value: (IRExpr)new IRMemberAccess(new IRIdentifier(tempName), f.Name)))
            .ToArray();
        body.Statements.Add(new IRReturn(new IRTableLiteralNew(tableFields)));
        return BuildImmediatelyInvokedExpression(body);
    }

    private static bool ImplementsIIpairs(INamedTypeSymbol type)
        => type.AllInterfaces.Any(i =>
            i is { Name: "IIpairs", ContainingNamespace: { } ns }
            && ns.ToDisplayString() == "SFLib.Interop");

    private INamedTypeSymbol? TryGetPackStructReturnType(ExpressionSyntax expression, SemanticModel model)
    {
        if (expression is not (InvocationExpressionSyntax or ElementAccessExpressionSyntax or MemberAccessExpressionSyntax))
        {
            return null;
        }

        var symbol = model.GetSymbolInfo(expression).Symbol;
        INamedTypeSymbol? containingType = symbol switch
        {
            IMethodSymbol method => method.ContainingType,
            IPropertySymbol property => property.ContainingType,
            _ => null,
        };

        if (containingType is null || !IsPackStructType(containingType))
        {
            return null;
        }

        var returnType = symbol switch
        {
            IMethodSymbol method => method.ReturnType,
            IPropertySymbol property => property.Type,
            _ => null,
        };

        return returnType is INamedTypeSymbol named && CanFlattenStructType(named) && GetFlattenableStructFields(named).Any()
            ? named
            : null;
    }

    private static void SortTypesByInheritance(List<IRType> types)
    {
        types.Sort((a, b) => string.CompareOrdinal(a.FullName, b.FullName));
        var byName = types.ToDictionary(t => t.FullName, StringComparer.Ordinal);
        var sorted = new List<IRType>(types.Count);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);

        foreach (var type in types)
        {
            Visit(type);
        }

        // Enforce nesting order: parent types must appear before their nested types.
        // Topological sort may not resolve this when there are circular dependencies
        // between parent and nested types (parent methods reference nested type).
        var finalOrder = new List<IRType>(sorted.Count);
        var placed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in sorted)
        {
            PlaceWithParents(type, sorted, byName, finalOrder, placed);
        }

        types.Clear();
        types.AddRange(finalOrder);

        void Visit(IRType type)
        {
            if (visited.Contains(type.FullName) || !visiting.Add(type.FullName))
            {
                return;
            }

            foreach (var dependencyName in GetTypeDependencies(type))
            {
                if (byName.TryGetValue(dependencyName, out var dependency) && dependency.FullName != type.FullName)
                {
                    Visit(dependency);
                }
            }

            visiting.Remove(type.FullName);
            visited.Add(type.FullName);
            sorted.Add(type);
        }
    }

    private static void PlaceWithParents(IRType type, List<IRType> sorted, Dictionary<string, IRType> byName, List<IRType> finalOrder, HashSet<string> placed)
    {
        if (placed.Contains(type.FullName))
        {
            return;
        }

        // If this type has namespace segments that match a parent type, place the parent first.
        if (type.NamespaceSegments.Count > 0)
        {
            var parentPath = string.Join('.', type.NamespaceSegments);
            if (byName.TryGetValue(parentPath, out var parent) && !placed.Contains(parentPath))
            {
                PlaceWithParents(parent, sorted, byName, finalOrder, placed);
            }
        }

        placed.Add(type.FullName);
        finalOrder.Add(type);
    }

    private static IEnumerable<string> GetTypeDependencies(IRType type)
    {
        var dependencies = new SortedSet<string>(StringComparer.Ordinal);

        if (type.BaseType is { } baseType)
        {
            dependencies.Add(GetTypeReferenceFullName(baseType));
        }

        if (type.LuaClass?.BaseType is { } luaClassBaseType)
        {
            CollectTypeReferences(luaClassBaseType, dependencies);
        }

        foreach (var iface in type.Interfaces)
        {
            dependencies.Add(GetTypeReferenceFullName(iface));
        }

        foreach (var field in type.Fields.Where(f => f.IsStatic && f.Initializer is not null))
        {
            CollectTypeReferences(field.Initializer!, dependencies);
        }

        foreach (var method in type.Methods)
        {
            CollectTypeReferences(method.Body, dependencies);
        }

        return dependencies;
    }

    private static void CollectTypeReferences(IRStmt stmt, ISet<string> dependencies)
    {
        switch (stmt)
        {
            case IRStatementList list:
                foreach (var child in list.Statements)
                {
                    CollectTypeReferences(child, dependencies);
                }
                break;
            case IRBlock block:
                foreach (var child in block.Statements)
                {
                    CollectTypeReferences(child, dependencies);
                }
                break;
            case IRLocalDecl local when local.Initializer is not null:
                CollectTypeReferences(local.Initializer, dependencies);
                break;
            case IRAssign assign:
                CollectTypeReferences(assign.Target, dependencies);
                CollectTypeReferences(assign.Value, dependencies);
                break;
            case IRExprStmt exprStmt:
                CollectTypeReferences(exprStmt.Expression, dependencies);
                break;
            case IRBaseConstructorCall baseConstructorCall:
                dependencies.Add(GetTypeReferenceFullName(baseConstructorCall.BaseType));
                foreach (var arg in baseConstructorCall.Arguments)
                {
                    CollectTypeReferences(arg, dependencies);
                }
                break;
            case IRThisConstructorCall thisConstructorCall:
                foreach (var arg in thisConstructorCall.Arguments)
                {
                    CollectTypeReferences(arg, dependencies);
                }
                break;
            case IRReturn ret when ret.Value is not null:
                CollectTypeReferences(ret.Value, dependencies);
                break;
            case IRIf ifStmt:
                CollectTypeReferences(ifStmt.Condition, dependencies);
                CollectTypeReferences(ifStmt.Then, dependencies);
                if (ifStmt.Else is not null)
                {
                    CollectTypeReferences(ifStmt.Else, dependencies);
                }
                break;
            case IRSwitch switchStmt:
                CollectTypeReferences(switchStmt.Value, dependencies);
                foreach (var section in switchStmt.Sections)
                {
                    foreach (var label in section.Labels)
                    {
                        CollectTypeReferences(label, dependencies);
                    }
                    CollectTypeReferences(section.Body, dependencies);
                }
                break;
            case IRWhile whileStmt:
                CollectTypeReferences(whileStmt.Condition, dependencies);
                CollectTypeReferences(whileStmt.Body, dependencies);
                break;
            case IRFor forStmt:
                if (forStmt.Initializer is not null)
                {
                    CollectTypeReferences(forStmt.Initializer, dependencies);
                }
                if (forStmt.Condition is not null)
                {
                    CollectTypeReferences(forStmt.Condition, dependencies);
                }
                foreach (var incrementor in forStmt.Incrementors)
                {
                    CollectTypeReferences(incrementor, dependencies);
                }
                CollectTypeReferences(forStmt.Body, dependencies);
                break;
            case IRForEach forEach:
                CollectTypeReferences(forEach.Collection, dependencies);
                CollectTypeReferences(forEach.Body, dependencies);
                break;
            case IRTry tryStmt:
                CollectTypeReferences(tryStmt.Try, dependencies);
                foreach (var catchClause in tryStmt.Catches)
                {
                    CollectTypeReferences(catchClause.Body, dependencies);
                }
                if (tryStmt.Finally is not null)
                {
                    CollectTypeReferences(tryStmt.Finally, dependencies);
                }
                break;
            case IRThrow throwStmt when throwStmt.Value is not null:
                CollectTypeReferences(throwStmt.Value, dependencies);
                break;
        }
    }

    private static void CollectTypeReferences(IRExpr expr, ISet<string> dependencies)
    {
        switch (expr)
        {
            case IRTypeReference typeReference:
                dependencies.Add(GetTypeReferenceFullName(typeReference));
                break;
            case IRMemberAccess memberAccess:
                CollectTypeReferences(memberAccess.Target, dependencies);
                break;
            case IRElementAccess elementAccess:
                CollectTypeReferences(elementAccess.Target, dependencies);
                CollectTypeReferences(elementAccess.Index, dependencies);
                break;
            case IRLength length:
                CollectTypeReferences(length.Target, dependencies);
                break;
            case IRInvocation invocation:
                CollectTypeReferences(invocation.Callee, dependencies);
                foreach (var arg in invocation.Arguments)
                {
                    CollectTypeReferences(arg, dependencies);
                }
                break;
            case IRArrayLiteral arrayLiteral:
                foreach (var item in arrayLiteral.Items)
                {
                    CollectTypeReferences(item, dependencies);
                }
                break;
            case IRArrayNew arrayNew:
                CollectTypeReferences(arrayNew.Size, dependencies);
                break;
            case IRStringConcat stringConcat:
                foreach (var part in stringConcat.Parts)
                {
                    CollectTypeReferences(part, dependencies);
                }
                break;
            case IRLuaRequire luaRequire:
                CollectTypeReferences(luaRequire.ModuleName, dependencies);
                break;
            case IRLuaGlobal luaGlobal:
                CollectTypeReferences(luaGlobal.Name, dependencies);
                break;
            case IRLuaAccess luaAccess:
                CollectTypeReferences(luaAccess.Target, dependencies);
                CollectTypeReferences(luaAccess.Name, dependencies);
                break;
            case IRLuaMethodInvocation luaMethodInvocation:
                CollectTypeReferences(luaMethodInvocation.Target, dependencies);
                CollectTypeReferences(luaMethodInvocation.Name, dependencies);
                foreach (var arg in luaMethodInvocation.Arguments)
                {
                    CollectTypeReferences(arg, dependencies);
                }
                break;
            case IRRuntimeInvocation runtimeInvocation:
                foreach (var arg in runtimeInvocation.Arguments)
                {
                    CollectTypeReferences(arg, dependencies);
                }
                break;
            case IRBinary binary:
                CollectTypeReferences(binary.Left, dependencies);
                CollectTypeReferences(binary.Right, dependencies);
                break;
            case IRUnary unary:
                CollectTypeReferences(unary.Operand, dependencies);
                break;
            case IRTernary ternary:
                CollectTypeReferences(ternary.Condition, dependencies);
                CollectTypeReferences(ternary.WhenTrue, dependencies);
                CollectTypeReferences(ternary.WhenFalse, dependencies);
                break;
            case IRIs isExpr:
                CollectTypeReferences(isExpr.Value, dependencies);
                CollectTypeReferences(isExpr.Type, dependencies);
                break;
            case IRAs asExpr:
                CollectTypeReferences(asExpr.Value, dependencies);
                CollectTypeReferences(asExpr.Type, dependencies);
                break;
            case IRTableLiteralNew tableLiteralNew:
                foreach (var (_, value) in tableLiteralNew.Fields)
                {
                    CollectTypeReferences(value, dependencies);
                }
                break;
        }
    }

    private static string GetTypeReferenceFullName(IRTypeReference type)
        => string.Join('.', type.NamespaceSegments.Append(type.Name));

    private bool IsIgnoredClass(INamedTypeSymbol? symbol)
        => symbol is not null && _ignoredClasses.Contains(symbol.Name);

    private IRExpr LowerInterpolatedString(InterpolatedStringExpressionSyntax istr, SemanticModel model)
    {
        var parts = new List<IRExpr>();
        foreach (var content in istr.Contents)
        {
            IRExpr part = content switch
            {
                InterpolatedStringTextSyntax t when string.IsNullOrEmpty(t.TextToken.ValueText)
                    => null!,
                InterpolatedStringTextSyntax t
                    => new IRLiteral(t.TextToken.ValueText, IRLiteralKind.String),
                InterpolationSyntax i
                    => LowerInterpolation(i, model),
                _ => new IRLiteral(string.Empty, IRLiteralKind.String),
            };

            if (part is null)
            {
                continue;
            }

            parts.Add(part);
        }

        return parts.Count switch
        {
            0 => new IRLiteral(string.Empty, IRLiteralKind.String),
            1 => parts[0],
            _ => new IRStringConcat(parts),
        };
    }

    private IRExpr LowerInterpolation(InterpolationSyntax interpolation, SemanticModel model)
    {
        var expression = LowerStringConcatPart(interpolation.Expression, model);
        var format = interpolation.FormatClause?.FormatStringToken.ValueText;
        if (string.IsNullOrWhiteSpace(format))
        {
            return expression;
        }

        if (TryMapInterpolationFormat(format, out var luaFormat))
        {
            return new IRInvocation(
                new IRMemberAccess(new IRIdentifier("string"), "format"),
                new IRExpr[] { new IRLiteral(luaFormat, IRLiteralKind.String), expression });
        }

        AddDiagnostic(interpolation.FormatClause!.GetLocation(), $"interpolation format '{format}' is not supported; supported formats are F/f, D/d, and X/x with optional precision");
        return expression;
    }

    private static bool TryMapInterpolationFormat(string format, out string luaFormat)
    {
        luaFormat = string.Empty;
        if (format.Length == 0)
        {
            return false;
        }

        var specifier = format[0];
        var precisionText = format[1..];
        if (precisionText.Length > 0 && !precisionText.All(char.IsDigit))
        {
            return false;
        }

        var precision = precisionText.Length == 0 ? (int?)null : int.Parse(precisionText);
        luaFormat = specifier switch
        {
            'F' or 'f' => $"%.{precision ?? 2}f",
            'D' or 'd' => precision is > 0 ? $"%0{precision.Value}d" : "%d",
            'X' => precision is > 0 ? $"%0{precision.Value}X" : "%X",
            'x' => precision is > 0 ? $"%0{precision.Value}x" : "%x",
            _ => string.Empty,
        };

        return luaFormat.Length > 0;
    }

    private IEnumerable<IRExpr> FlattenStringConcat(BinaryExpressionSyntax expression, SemanticModel model)
    {
        foreach (var side in new[] { expression.Left, expression.Right })
        {
            if (side is BinaryExpressionSyntax nested && nested.IsKind(SyntaxKind.AddExpression) && IsStringExpression(nested, model))
            {
                foreach (var part in FlattenStringConcat(nested, model))
                {
                    yield return part;
                }
            }
            else
            {
                yield return LowerStringConcatPart(side, model);
            }
        }
    }

    private IRExpr LowerStringConcatPart(ExpressionSyntax expression, SemanticModel model)
    {
        var lowered = LowerExpr(expression, model);
        var toStringMethod = FindStringConcatToStringMethod(expression, model);
        if (toStringMethod is null)
        {
            return lowered;
        }

        if (toStringMethod.ContainingType.TypeKind == TypeKind.Struct
            && CanFlattenStructType(toStringMethod.ContainingType))
        {
            return new IRInvocation(
                new IRMemberAccess(LowerTypeReference(toStringMethod.ContainingType), GetLuaMethodName(toStringMethod)),
                LowerStructArgumentValues(expression, toStringMethod.ContainingType, model));
        }

        var valueName = AllocateLuaName("strPart");
        var valueRef = new IRIdentifier(valueName);
        var body = new IRBlock();
        body.Statements.Add(new IRLocalDecl(valueName, lowered));

        var thenBlock = new IRBlock();
        thenBlock.Statements.Add(new IRReturn(new IRInvocation(
            new IRMemberAccess(valueRef, GetLuaMethodName(toStringMethod)),
            Array.Empty<IRExpr>(),
            UseColon: true)));

        body.Statements.Add(new IRIf(
            new IRBinary("~=", valueRef, new IRLiteral(null, IRLiteralKind.Nil)),
            thenBlock,
            null));
        body.Statements.Add(new IRReturn(new IRLiteral(null, IRLiteralKind.Nil)));
        return BuildImmediatelyInvokedExpression(body);
    }

    private static IMethodSymbol? FindStringConcatToStringMethod(ExpressionSyntax expression, SemanticModel model)
    {
        if (model.GetTypeInfo(expression).Type is not INamedTypeSymbol type
            || type.SpecialType != SpecialType.None
            || type.TypeKind is not (TypeKind.Class or TypeKind.Struct))
        {
            return null;
        }

        // System.Exception overrides ToString() to include stack trace etc.
        // In the Jass/Lua target, exception values should be emitted as-is.
        if (DerivesFromSystemException(type))
        {
            return null;
        }

        for (var current = type; current is not null; current = current.BaseType)
        {
            var method = current.GetMembers("ToString")
                .OfType<IMethodSymbol>()
                .FirstOrDefault(candidate =>
                    !candidate.IsStatic
                    && candidate.MethodKind == MethodKind.Ordinary
                    && candidate.Parameters.Length == 0
                    && candidate.ReturnType.SpecialType == SpecialType.System_String);

            if (method is null)
            {
                continue;
            }

            return method.ContainingType.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType
                ? null
                : method;
        }

        return null;
    }

    private static bool DerivesFromSystemException(ITypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.MetadataName == "Exception"
                && current.ContainingNamespace?.ToDisplayString() == "System")
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSystemException(ITypeSymbol type)
        => type.MetadataName == "Exception"
           && type.ContainingNamespace?.ToDisplayString() == "System";

    private void RegisterExceptionType(INamedTypeSymbol type)
    {
        if (!DerivesFromSystemException(type)
            || _knownExceptionTypes.Any(existing => SymbolEqualityComparer.Default.Equals(existing, type)))
        {
            return;
        }

        _knownExceptionTypes.Add(type);
    }

    private string GetExceptionHeader(INamedTypeSymbol type)
    {
        RegisterExceptionType(type);
        return "SF__" + GetExceptionId(type);
    }

    private IEnumerable<string> GetExceptionHeadersAssignableTo(INamedTypeSymbol catchType)
    {
        RegisterExceptionType(catchType);
        foreach (var knownType in _knownExceptionTypes)
        {
            if (DerivesFrom(knownType, catchType))
            {
                yield return GetExceptionHeader(knownType);
            }
        }
    }

    private static bool DerivesFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
            {
                return true;
            }
        }

        return false;
    }

    private static string GetExceptionId(INamedTypeSymbol type)
    {
        var name = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        const uint offset = 2166136261;
        const uint prime = 16777619;
        var hash = offset;
        foreach (var ch in name)
        {
            hash ^= ch;
            hash *= prime;
        }

        return "E" + hash.ToString("x8", CultureInfo.InvariantCulture);
    }

    private static bool IsStringExpression(ExpressionSyntax expression, SemanticModel model)
    {
        var type = model.GetTypeInfo(expression);
        return type.Type?.SpecialType == SpecialType.System_String
            || type.ConvertedType?.SpecialType == SpecialType.System_String;
    }

    private static IRExpr LowerLiteral(LiteralExpressionSyntax lit)
    {
        return lit.Kind() switch
        {
            SyntaxKind.NullLiteralExpression => new IRLiteral(null, IRLiteralKind.Nil),
            SyntaxKind.TrueLiteralExpression => new IRLiteral(true, IRLiteralKind.Boolean),
            SyntaxKind.FalseLiteralExpression => new IRLiteral(false, IRLiteralKind.Boolean),
            SyntaxKind.NumericLiteralExpression => lit.Token.Value switch
            {
                int i => new IRLiteral(i, IRLiteralKind.Integer),
                long l => new IRLiteral(l, IRLiteralKind.Integer),
                double d => new IRLiteral(d, IRLiteralKind.Real),
                float f => new IRLiteral(f, IRLiteralKind.Real),
                _ => new IRLiteral(lit.Token.Value, IRLiteralKind.Real),
            },
            SyntaxKind.StringLiteralExpression => new IRLiteral(lit.Token.ValueText, IRLiteralKind.String),
            _ => new IRLiteral(null, IRLiteralKind.Nil),
        };
    }

    private static IRExpr LowerDefaultValue(TypeSyntax type)
    {
        if (type is PredefinedTypeSyntax predefined)
        {
            return predefined.Keyword.Kind() switch
            {
                SyntaxKind.BoolKeyword => new IRLiteral(false, IRLiteralKind.Boolean),
                SyntaxKind.ByteKeyword or SyntaxKind.SByteKeyword or SyntaxKind.ShortKeyword or SyntaxKind.UShortKeyword
                    or SyntaxKind.IntKeyword or SyntaxKind.UIntKeyword or SyntaxKind.LongKeyword or SyntaxKind.ULongKeyword
                    => new IRLiteral(0, IRLiteralKind.Integer),
                SyntaxKind.FloatKeyword or SyntaxKind.DoubleKeyword or SyntaxKind.DecimalKeyword
                    => new IRLiteral(0.0, IRLiteralKind.Real),
                _ => new IRLiteral(null, IRLiteralKind.Nil),
            };
        }

        return new IRLiteral(null, IRLiteralKind.Nil);
    }

    private static IRExpr LowerDefaultValue(ITypeSymbol type)
        => type.TypeKind == TypeKind.Enum
            ? new IRLiteral(0, IRLiteralKind.Integer)
            : type.SpecialType switch
        {
            SpecialType.System_Boolean => new IRLiteral(false, IRLiteralKind.Boolean),
            SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_Int16 or SpecialType.System_UInt16
                or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64
                => new IRLiteral(0, IRLiteralKind.Integer),
            SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal
                => new IRLiteral(0.0, IRLiteralKind.Real),
            _ => new IRLiteral(null, IRLiteralKind.Nil),
        };

    private IRLiteral LowerEnumMemberValue(IFieldSymbol member, EnumMemberDeclarationSyntax declaration)
    {
        const long maxExactLuaInteger = 9_007_199_254_740_991L;
        if (!member.HasConstantValue || member.ConstantValue is null)
        {
            return new IRLiteral(0, IRLiteralKind.Integer);
        }

        long value;
        try
        {
            value = member.ConstantValue switch
            {
                ulong unsigned when unsigned <= maxExactLuaInteger => (long)unsigned,
                ulong => throw new OverflowException(),
                _ => Convert.ToInt64(member.ConstantValue, System.Globalization.CultureInfo.InvariantCulture),
            };
        }
        catch (OverflowException)
        {
            AddDiagnostic(declaration.GetLocation(), $"enum member '{member.Name}' has a value that cannot be represented exactly as a Lua number");
            return new IRLiteral(0, IRLiteralKind.Integer);
        }

        if (value < -maxExactLuaInteger || value > maxExactLuaInteger)
        {
            AddDiagnostic(declaration.GetLocation(), $"enum member '{member.Name}' has a value that cannot be represented exactly as a Lua number");
        }

        return new IRLiteral(value, IRLiteralKind.Integer);
    }

    private static bool HasFlagsAttribute(INamedTypeSymbol symbol)
        => symbol.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::System.FlagsAttribute");

    private static string MapBinaryOp(string op) => op switch
    {
        "==" => "==",
        "!=" => "~=",
        "&&" => "and",
        "||" => "or",
        _ => op,
    };

    private IRStmt UnsupportedStatement(StatementSyntax statement)
    {
        AddUnsupportedDiagnostic(statement, "statement");
        return new IRRawComment($"unsupported stmt: {statement.Kind()}");
    }

    private IRExpr UnsupportedExpression(ExpressionSyntax expression)
    {
        AddUnsupportedDiagnostic(expression, "expression");
        return new IRIdentifier($"--[[unsupported expr: {expression.Kind()}]]nil");
    }

    private void AddUnsupportedDiagnostic(SyntaxNode node, string kind)
    {
        AddDiagnostic(node.GetLocation(), $"unsupported {kind}: {node.Kind()}");
    }

    private void AddReservedIdentifierDiagnostic(SyntaxToken token)
    {
        AddDiagnostic(token.GetLocation(), $"reserved identifier '{token.ValueText}': user identifiers must not contain '__'");
    }

    private void AddDiagnostic(Location location, string message)
    {
        if (_module is null)
        {
            return;
        }

        var span = location.GetLineSpan();
        var line = span.StartLinePosition.Line + 1;
        var character = span.StartLinePosition.Character + 1;
        _module.Diagnostics.Add($"{span.Path}({line},{character}): {message}");
    }

    private void AddDiagnostic(string message)
    {
        _module?.Diagnostics.Add(message);
    }
}
