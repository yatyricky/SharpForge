using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SharpForge.Transpiler.IR;
using SharpForge.Transpiler.Pipeline;

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
    private readonly HashSet<string> _libraryFolderNames;
    private readonly string? _sourceRoot;
    private IRModule? _module;
    private readonly Dictionary<ISymbol, string> _luaNames = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<INamedTypeSymbol, string> _luaObjectModuleNames = new(SymbolEqualityComparer.Default);
    private readonly Stack<LuaModuleBlockContext> _luaModuleBlockContexts = new();
    private int? _luaModuleInsertionIndex;
    private readonly Dictionary<ISymbol, (string KeyName, string ValueName)> _dictionaryForEachItems = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<ISymbol, FlattenedStructLocal> _flattenedStructLocals = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<ISymbol, FlattenedStructMember> _flattenedStructMembers = new(SymbolEqualityComparer.Default);
    private INamedTypeSymbol? _currentStructSelfType;
    private IReadOnlyDictionary<string, string>? _currentStructSelfFields;
    private readonly HashSet<string> _usedLuaNames = new(StringComparer.Ordinal);
    private int _luaNameCounter;

    public IRLowering(
        IEnumerable<string>? ignoredClasses = null,
        DirectoryInfo? sourceRoot = null,
        IEnumerable<string>? libraryFolders = null)
    {
        _ignoredClasses = ignoredClasses is null
            ? new HashSet<string>(new[] { TranspileOptions.DefaultIgnoredClass }, StringComparer.Ordinal)
            : new HashSet<string>(ignoredClasses, StringComparer.Ordinal);
        _libraryFolderNames = new HashSet<string>(
            (libraryFolders ?? Array.Empty<string>())
                .Select(NormalizeFolderName)
                .Where(name => !string.IsNullOrWhiteSpace(name)),
            StringComparer.OrdinalIgnoreCase);
        _sourceRoot = sourceRoot is null
            ? null
            : Path.GetFullPath(sourceRoot.FullName);
    }

    public IRModule Lower(CSharpCompilation compilation, CancellationToken cancellationToken)
    {
        var module = new IRModule();
        _module = module;
        _luaNames.Clear();
        _luaObjectModuleNames.Clear();
        _luaModuleBlockContexts.Clear();
        _luaModuleInsertionIndex = null;
        _dictionaryForEachItems.Clear();
        _flattenedStructLocals.Clear();
        _flattenedStructMembers.Clear();
        _currentStructSelfType = null;
        _currentStructSelfFields = null;
        _usedLuaNames.Clear();
        _luaNameCounter = 0;

        foreach (var tree in compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsInLibraryFolder(tree))
            {
                continue;
            }

            var model = compilation.GetSemanticModel(tree);
            var root = (CompilationUnitSyntax)tree.GetRoot(cancellationToken);

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

                if (IsSharpLibType(symbol))
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
                module.Types.Add(LowerType(typeDecl, symbol, model, cancellationToken));
            }
        }

        SortTypesByInheritance(module.Types);
        ValidateEntryPoints(module);
        _module = null;
        return module;
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

        foreach (var iface in symbol.Interfaces.Where(i => !IsIgnoredClass(i)))
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
                case FieldDeclarationSyntax f when f.Modifiers.Any(SyntaxKind.StaticKeyword):
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
                                ? LowerDefaultValue(f.Declaration.Type)
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
                                ? LowerDefaultValue(f.Declaration.Type)
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
                                ? LowerDefaultValue(p.Type)
                                : LowerExpr(p.Initializer.Value, model),
                            IsStatic = p.Modifiers.Any(SyntaxKind.StaticKeyword),
                        };
                        field.Comments.AddRange(ExtractComments(p.GetLeadingTrivia()));
                        irType.Fields.Add(field);
                    }
                    else
                    {
                        irType.Methods.AddRange(LowerProperty(p, model, ct));
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
            BaseConstructorCall = LowerBaseConstructorCall(c, owner, model),
            IsConstructor = true,
            IsInstance = false, // emit with `.` because we create `self` ourselves
        };
        fn.Comments.AddRange(ExtractComments(c.GetLeadingTrivia()));

        AddLoweredParameters(fn, c.ParameterList.Parameters, model, ct);

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
                fn.Body.Statements.Add(new IRReturn(LowerExpr(arrow.Expression, model)));
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

    private IEnumerable<IRFunction> LowerProperty(PropertyDeclarationSyntax p, SemanticModel model, CancellationToken ct)
    {
        var isStatic = p.Modifiers.Any(SyntaxKind.StaticKeyword);
        var propertySymbol = model.GetDeclaredSymbol(p, ct);
        if (p.ExpressionBody is { } expressionBody)
        {
            yield return new IRFunction
            {
                Name = "get_" + p.Identifier.ValueText,
                LuaName = "get_" + p.Identifier.ValueText,
                IsStatic = isStatic,
                IsInstance = !isStatic,
                Body = new IRBlock { Statements = { new IRReturn(LowerExpr(expressionBody.Expression, model)) } },
            };
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
                    IsInstance = !isStatic,
                };
                LowerAccessorBody(accessor, fn.Body, model, ct);
                yield return fn;
            }
            else if (accessor.IsKind(SyntaxKind.SetAccessorDeclaration))
            {
                var fn = new IRFunction
                {
                    Name = "set_" + p.Identifier.ValueText,
                    LuaName = "set_" + p.Identifier.ValueText,
                    IsStatic = isStatic,
                    IsInstance = !isStatic,
                };
                fn.Parameters.Add(DeclareLuaName(propertySymbol?.SetMethod?.Parameters.FirstOrDefault(), "value"));
                LowerAccessorBody(accessor, fn.Body, model, ct);
                yield return fn;
            }
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

            foreach (var parameter in indexer.ParameterList.Parameters)
            {
                fn.Parameters.Add(DeclareLuaName(model.GetDeclaredSymbol(parameter, ct), parameter.Identifier.ValueText));
            }
            if (!isGetter)
            {
                fn.Parameters.Add(DeclareLuaName(indexerSymbol?.SetMethod?.Parameters.LastOrDefault(), "value"));
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
                ? new IRReturn(LowerExpr(expressionBody.Expression, model))
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
            fn.Body.Statements.Add(new IRReturn(LowerExpr(arrow.Expression, model)));
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
            AddLoweredParameter(fn, symbol, parameter.Identifier.ValueText, symbol?.Type ?? (parameter.Type is null ? null : model.GetTypeInfo(parameter.Type).Type));
        }
    }

    private void AddLoweredParameter(IRFunction fn, ISymbol? symbol, string baseName, ITypeSymbol? type)
    {
        if (symbol is not null
            && type is INamedTypeSymbol structType
            && CanFlattenStructType(structType)
            && GetFlattenableStructFields(structType).Any())
        {
            var fieldLocals = AddFlattenedStructParameters(fn, baseName, structType);
            _flattenedStructLocals[symbol] = new FlattenedStructLocal(fieldLocals);
            return;
        }

        fn.Parameters.Add(DeclareLuaName(symbol, baseName));
    }

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
        foreach (var s in stmts)
        {
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
                return new IRThrow(throwStatement.Expression is null ? null : LowerExpr(throwStatement.Expression, model));

            case ExpressionStatementSyntax es when es.Expression is AssignmentExpressionSyntax ae:
                return LowerAssignment(ae, model);

            case ExpressionStatementSyntax es when TryLowerLuaInteropStatement(es.Expression, model) is { } luaInteropStatement:
                return luaInteropStatement;

            case ExpressionStatementSyntax es when IsIncrementOrDecrement(es.Expression):
                return LowerIncrementOrDecrement(es.Expression, model);

            case ExpressionStatementSyntax es:
                return new IRExprStmt(LowerExpr(es.Expression, model));

            case ReturnStatementSyntax rs:
                if (rs.Expression is not null && TryLowerStructReturn(rs.Expression, model) is { } structReturn)
                {
                    return structReturn;
                }

                return new IRReturn(rs.Expression is null ? null : LowerExpr(rs.Expression, model));

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

            case WhileStatementSyntax ws:
                var whileBody = new IRBlock();
                LowerBlock(ws.Statement, whileBody, model, ct);
                return new IRWhile(LowerExpr(ws.Condition, model), whileBody);

            case BreakStatementSyntax:
                return new IRBreak();

            case ContinueStatementSyntax:
                return new IRContinue();

            default:
                return UnsupportedStatement(s);
        }
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
        var collectionType = model.GetTypeInfo(fe.Expression).Type;
        var collection = LowerExpr(fe.Expression, model);
        var itemSymbol = model.GetDeclaredSymbol(fe, ct);
        var body = new IRBlock();
        if (!IsDictionaryType(collectionType))
        {
            var itemName = DeclareLuaName(itemSymbol, fe.Identifier.ValueText);
            LowerBlock(fe.Statement, body, model, ct);
            return new IRForEach(itemName, collection, body, UseListIterator: IsListType(collectionType));
        }

        var itemBaseName = EscapeLuaKeyword(fe.Identifier.ValueText);
        var keyName = AllocateLuaName(itemBaseName + "__Key");
        var valueName = AllocateLuaName(itemBaseName + "__Value");
        if (itemSymbol is not null && CanInlineDictionaryForEachItem(fe.Statement, model, itemSymbol))
        {
            _dictionaryForEachItems[itemSymbol] = (keyName, valueName);
            try
            {
                LowerBlock(fe.Statement, body, model, ct);
            }
            finally
            {
                _dictionaryForEachItems.Remove(itemSymbol);
            }

            return new IRDictionaryForEach(null, keyName, valueName, collection, body);
        }

        var dictionaryItemName = DeclareLuaName(itemSymbol, fe.Identifier.ValueText);
        LowerBlock(fe.Statement, body, model, ct);
        return new IRDictionaryForEach(dictionaryItemName, keyName, valueName, collection, body);
    }

    private IRStmt LowerTry(TryStatementSyntax ts, SemanticModel model, CancellationToken ct)
    {
        if (ts.Catches.Count > 1)
        {
            AddUnsupportedDiagnostic(ts.Catches[1], "catch");
        }

        var tryBlock = new IRBlock();
        LowerBlock(ts.Block, tryBlock, model, ct);

        IRBlock? catchBlock = null;
        string? catchVariable = null;
        if (ts.Catches.Count > 0)
        {
            var catchClause = ts.Catches[0];
            catchVariable = catchClause.Declaration is null
                ? null
                : DeclareLuaName(model.GetDeclaredSymbol(catchClause.Declaration), catchClause.Declaration.Identifier.ValueText);
            catchBlock = new IRBlock();
            LowerBlock(catchClause.Block, catchBlock, model, ct);
        }

        IRBlock? finallyBlock = null;
        if (ts.Finally is { } finallyClause)
        {
            finallyBlock = new IRBlock();
            LowerBlock(finallyClause.Block, finallyBlock, model, ct);
        }

        return new IRTry(tryBlock, catchVariable, catchBlock, finallyBlock);
    }

    private IRStmt LowerAssignment(AssignmentExpressionSyntax ae, SemanticModel model)
    {
        if (ae.IsKind(SyntaxKind.SimpleAssignmentExpression) && TryLowerStructAssignment(ae.Left, ae.Right, model) is { } structAssignment)
        {
            return structAssignment;
        }

        if (ae.IsKind(SyntaxKind.SimpleAssignmentExpression) && TryLowerDictionaryAssignment(ae.Left, ae.Right, model) is { } dictionaryAssignment)
        {
            return dictionaryAssignment;
        }

        if (ae.IsKind(SyntaxKind.SimpleAssignmentExpression) && TryLowerListAssignment(ae.Left, ae.Right, model) is { } listAssignment)
        {
            return listAssignment;
        }

        if (ae.IsKind(SyntaxKind.SimpleAssignmentExpression) && TryLowerAccessorAssignment(ae.Left, ae.Right, model) is { } accessorAssignment)
        {
            return accessorAssignment;
        }

        var target = LowerExpr(ae.Left, model);
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

            case ThisExpressionSyntax:
                return new IRIdentifier("self");

            case MemberAccessExpressionSyntax ma:
                if (TryLowerLuaObjectMemberAccess(ma, model) is { } luaObjectMemberAccess)
                {
                    return luaObjectMemberAccess;
                }

                if (TryLowerFlattenedStructFieldAccess(ma, model) is { } flattenedStructFieldAccess)
                {
                    return flattenedStructFieldAccess;
                }

                if (TryLowerSharpLibKeyValueAccess(ma, model) is { } keyValueAccess)
                {
                    return keyValueAccess;
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
                if (TryLowerDictionaryGet(elementAccess, model) is { } dictionaryGet)
                {
                    return dictionaryGet;
                }

                if (TryLowerListGet(elementAccess, model) is { } listGet)
                {
                    return listGet;
                }

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

            case ParenthesizedLambdaExpressionSyntax lambda:
                return LowerAnonymousFunction(lambda, model);

            case SimpleLambdaExpressionSyntax lambda:
                return LowerAnonymousFunction(lambda, model);

            case AnonymousMethodExpressionSyntax anonymousMethod:
                return LowerAnonymousFunction(anonymousMethod, model);

            case ObjectCreationExpressionSyntax obj:
                return LowerObjectCreation(obj, model);

            case ArrayCreationExpressionSyntax arrayCreation:
                return arrayCreation.Initializer is null
                    ? new IRArrayNew(LowerExpr(arrayCreation.Type.RankSpecifiers[0].Sizes[0], model))
                    : new IRArrayLiteral(arrayCreation.Initializer.Expressions.Select(item => LowerExpr(item, model)).ToArray());

            case ImplicitArrayCreationExpressionSyntax implicitArrayCreation:
                return new IRArrayLiteral(implicitArrayCreation.Initializer.Expressions.Select(item => LowerExpr(item, model)).ToArray());

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
                    return new IRInvocation(
                        new IRMemberAccess(LowerTypeReference(op.ContainingType), GetLuaMethodName(op)),
                        LowerArguments([bin.Left, bin.Right], op.Parameters, model));
                }

                if (bin.IsKind(SyntaxKind.AddExpression) && IsStringExpression(bin, model))
                {
                    return new IRStringConcat(FlattenStringConcat(bin, model).ToArray());
                }

                return new IRBinary(MapBinaryOp(bin.OperatorToken.ValueText), LowerExpr(bin.Left, model), LowerExpr(bin.Right, model));

            case PrefixUnaryExpressionSyntax pre:
                return new IRUnary(pre.OperatorToken.ValueText, LowerExpr(pre.Operand, model));

            case ParenthesizedExpressionSyntax par:
                return LowerExpr(par.Expression, model);

            case AwaitExpressionSyntax awaitExpression:
                return LowerExpr(awaitExpression.Expression, model);

            default:
                return UnsupportedExpression(e);
        }
    }

    private IRExpr LowerInvocation(InvocationExpressionSyntax inv, SemanticModel model)
    {
        var symbol = model.GetSymbolInfo(inv).Symbol as IMethodSymbol;
        var args = symbol is null
            ? inv.ArgumentList.Arguments.Select(a => LowerExpr(a.Expression, model)).ToArray()
            : LowerArguments(inv.ArgumentList.Arguments.Select(a => a.Expression), symbol.Parameters, model);
        if (symbol is { MethodKind: MethodKind.DelegateInvoke })
        {
            return new IRInvocation(LowerExpr(inv.Expression, model), args);
        }

        if (symbol is not null && IsTaskDelay(symbol))
        {
            return new IRRuntimeInvocation("CorWait__", args);
        }

        if (symbol is not null && TryLowerLuaInteropInvocation(symbol, args) is { } luaInteropInvocation)
        {
            return luaInteropInvocation;
        }

        if (symbol is { Name: "Add", IsStatic: false } && IsListType(symbol.ContainingType) && inv.Expression is MemberAccessExpressionSyntax addAccess && args.Count == 1)
        {
            return new IRListAdd(LowerExpr(addAccess.Expression, model), args[0]);
        }

        if (symbol is { Name: "Sort", IsStatic: false } && IsListType(symbol.ContainingType) && inv.Expression is MemberAccessExpressionSyntax sortAccess && args.Count <= 1)
        {
            return new IRListSort(LowerExpr(sortAccess.Expression, model), args.Count == 0 ? null : args[0]);
        }

        if (symbol is { Name: "Add" or "Set", IsStatic: false } && IsDictionaryType(symbol.ContainingType) && inv.Expression is MemberAccessExpressionSyntax dictSetAccess && args.Count == 2)
        {
            return new IRDictionarySet(LowerExpr(dictSetAccess.Expression, model), args[0], args[1]);
        }

        if (symbol is { Name: "Remove", IsStatic: false } && IsDictionaryType(symbol.ContainingType) && inv.Expression is MemberAccessExpressionSyntax dictRemoveAccess && args.Count == 1)
        {
            return new IRDictionaryRemove(LowerExpr(dictRemoveAccess.Expression, model), args[0]);
        }

        if (symbol is { Name: "Get", IsStatic: false } && IsDictionaryType(symbol.ContainingType) && inv.Expression is MemberAccessExpressionSyntax dictGetAccess && args.Count == 1)
        {
            return new IRDictionaryGet(LowerExpr(dictGetAccess.Expression, model), args[0]);
        }

        if (inv.Expression is MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax } && symbol is { IsStatic: false })
        {
            return new IRInvocation(
                new IRMemberAccess(LowerTypeReference(symbol.ContainingType), GetLuaMethodName(symbol)),
                new[] { new IRIdentifier("self") }.Concat(args).ToArray());
        }

        if (symbol is { IsStatic: false, ContainingType.TypeKind: TypeKind.Struct }
            && CanFlattenStructType(symbol.ContainingType))
        {
            if (inv.Expression is MemberAccessExpressionSyntax structMemberAccess)
            {
                var selfArgs = LowerStructArgumentValues(structMemberAccess.Expression, symbol.ContainingType, model);
                return new IRInvocation(
                    new IRMemberAccess(LowerTypeReference(symbol.ContainingType), GetLuaMethodName(symbol)),
                    selfArgs.Concat(args).ToArray());
            }

            if (inv.Expression is IdentifierNameSyntax && SymbolEqualityComparer.Default.Equals(symbol.ContainingType, _currentStructSelfType))
            {
                return new IRInvocation(
                    new IRMemberAccess(LowerTypeReference(symbol.ContainingType), GetLuaMethodName(symbol)),
                    GetCurrentStructSelfArguments(symbol.ContainingType).Concat(args).ToArray());
            }
        }

        if (inv.Expression is IdentifierNameSyntax && symbol is { IsStatic: false })
        {
            return new IRInvocation(
                new IRMemberAccess(new IRIdentifier("self"), GetLuaMethodName(symbol)),
                args,
                UseColon: true);
        }

        if (inv.Expression is IdentifierNameSyntax && symbol is { IsStatic: true })
        {
            if (IsIgnoredClass(symbol.ContainingType))
            {
                return new IRInvocation(new IRIdentifier(symbol.Name), args);
            }

            return new IRInvocation(
                new IRMemberAccess(LowerTypeReference(symbol.ContainingType), GetLuaMethodName(symbol)),
                args);
        }

        if (inv.Expression is MemberAccessExpressionSyntax memberAccess && symbol is { IsStatic: false })
        {
            if (IsExternalLuaObjectType(symbol.ContainingType))
            {
                var member = GetLuaObjectMember(symbol);
                return new IRInvocation(
                    new IRMemberAccess(LowerExpr(memberAccess.Expression, model), member.Name),
                    args,
                    UseColon: member.UseColon);
            }

            return new IRInvocation(
                new IRMemberAccess(LowerExpr(memberAccess.Expression, model), GetLuaMethodName(symbol)),
                args,
                UseColon: true);
        }

        if (inv.Expression is MemberAccessExpressionSyntax staticAccess && symbol is { IsStatic: true })
        {
            if (IsIgnoredClass(symbol.ContainingType))
            {
                return new IRInvocation(new IRIdentifier(symbol.Name), args);
            }

            if (IsExternalLuaObjectType(symbol.ContainingType))
            {
                var member = GetLuaObjectMember(symbol);
                return new IRInvocation(
                    new IRMemberAccess(GetLuaObjectTypeTarget(symbol.ContainingType), member.Name),
                    args,
                    UseColon: member.UseColon);
            }

            return new IRInvocation(
                new IRMemberAccess(LowerExpr(staticAccess.Expression, model), GetLuaMethodName(symbol)),
                args);
        }

        return new IRInvocation(LowerExpr(inv.Expression, model), args);
    }

    private IReadOnlyList<IRExpr> LowerArguments(IEnumerable<ExpressionSyntax> arguments, IEnumerable<IParameterSymbol> parameters, SemanticModel model)
    {
        var lowered = new List<IRExpr>();
        using var argumentEnumerator = arguments.GetEnumerator();
        using var parameterEnumerator = parameters.GetEnumerator();

        while (argumentEnumerator.MoveNext())
        {
            var parameter = parameterEnumerator.MoveNext() ? parameterEnumerator.Current : null;
            if (parameter?.Type is INamedTypeSymbol structType
                && CanFlattenStructType(structType)
                && GetFlattenableStructFields(structType).Any())
            {
                lowered.AddRange(LowerStructArgumentValues(argumentEnumerator.Current, structType, model));
                continue;
            }

            lowered.Add(LowerExpr(argumentEnumerator.Current, model));
        }

        return lowered;
    }

    private IReadOnlyList<IRExpr> LowerStructArgumentValues(ExpressionSyntax expression, INamedTypeSymbol structType, SemanticModel model)
    {
        if (expression is ObjectCreationExpressionSyntax creation
            && TryGetStructFieldValues(creation, model, out _, out var creationValues))
        {
            return creationValues;
        }

        if (TryGetFlattenedStructValueExpressions(expression, structType, model, out var flattenedValues))
        {
            return flattenedValues;
        }

        return new[] { LowerExpr(expression, model) };
    }

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
                var target = GetFlattenedStructMemberTarget(flattenedMember);
                values = fields
                    .Select(field => flattenedMember.FieldMembers.TryGetValue(field.Name, out var memberName) ? new IRMemberAccess(target, memberName) : null)
                    .OfType<IRExpr>()
                    .ToArray();
                return values.Count == fields.Length;
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
            || !CanFlattenStructLocal(statement, localSymbol, model)
            || !TryGetStructExpressionValues(initializer, model, out var fields, out var values))
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
        return new IRMultiLocalDecl(localNames, values);
    }

    private IRStmt? TryLowerStructAssignment(ExpressionSyntax left, ExpressionSyntax right, SemanticModel model)
    {
        if (left is not IdentifierNameSyntax id
            || model.GetSymbolInfo(id).Symbol is not { } localSymbol
            || !_flattenedStructLocals.TryGetValue(localSymbol, out var flattened)
            || !TryGetStructExpressionValues(right, model, out var fields, out var values))
        {
            return null;
        }

        var targets = new List<IRExpr>(fields.Count);
        foreach (var field in fields)
        {
            if (!flattened.FieldLocals.TryGetValue(field.Name, out var localName))
            {
                return null;
            }

            targets.Add(new IRIdentifier(localName));
        }

        return new IRMultiAssign(targets, values);
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
                ? new IRMemberAccess(GetFlattenedStructMemberTarget(flattenedMember), memberName)
                : null;
        }

        return null;
    }

    private IRExpr GetFlattenedStructMemberTarget(FlattenedStructMember member)
        => member.IsStatic ? LowerTypeReferenceForAccess(member.ContainingType) : new IRIdentifier("self");

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
        if (initializer?.Value is ObjectCreationExpressionSyntax creation)
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

                return false;
            }
        }

        return true;
    }

    private IRStmt? TryLowerStructReturn(ExpressionSyntax expression, SemanticModel model)
    {
        if (expression is ObjectCreationExpressionSyntax creation
            && TryGetStructFieldValues(creation, model, out _, out var values))
        {
            return new IRMultiReturn(values);
        }

        return null;
    }

    private bool TryGetStructExpressionValues(
        ExpressionSyntax expression,
        SemanticModel model,
        out IReadOnlyList<StructFieldSlot> fields,
        out IReadOnlyList<IRExpr> values)
    {
        if (expression is ObjectCreationExpressionSyntax creation)
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
        ObjectCreationExpressionSyntax creation,
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
        => type.GetMembers()
            .Where(m => m is IFieldSymbol { IsStatic: false, IsConst: false } || m is IPropertySymbol { IsStatic: false } property && IsAutoPropertySymbol(property))
            .Select(m => new StructFieldSlot(m.Name, m switch
            {
                IFieldSymbol field => field.Type,
                IPropertySymbol property => property.Type,
                _ => throw new InvalidOperationException(),
            }, GetSyntaxSortKey(m)))
            .OrderBy(f => f.SortKey)
            .ThenBy(f => f.Name, StringComparer.Ordinal);

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

        foreach (var assignment in syntax.Body.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                || model.GetSymbolInfo(assignment.Left).Symbol is not IFieldSymbol and not IPropertySymbol)
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
                IdentifierNameSyntax identifier when model.GetSymbolInfo(identifier).Symbol is IParameterSymbol parameter => parameter.Name,
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
        var expression = LowerExpr(expressionBody, model);
        if (AnonymousFunctionReturnsVoid(anonymousFunction, model))
        {
            body.Statements.Add(new IRExprStmt(expression));
            return;
        }

        body.Statements.Add(new IRReturn(expression));
    }

    private static bool AnonymousFunctionReturnsVoid(AnonymousFunctionExpressionSyntax anonymousFunction, SemanticModel model)
        => (model.GetTypeInfo(anonymousFunction).ConvertedType as INamedTypeSymbol)?.DelegateInvokeMethod?.ReturnsVoid == true;

    private IRExpr LowerTypeTest(ExpressionSyntax value, ExpressionSyntax typeSyntax, SemanticModel model, bool isAsExpression)
    {
        var type = model.GetTypeInfo(typeSyntax).Type as INamedTypeSymbol;
        if (type is null)
        {
            return UnsupportedExpression(typeSyntax);
        }

        return isAsExpression
            ? new IRAs(LowerExpr(value, model), LowerTypeReference(type))
            : new IRIs(LowerExpr(value, model), LowerTypeReference(type));
    }

    private IRExpr LowerObjectCreation(ObjectCreationExpressionSyntax obj, SemanticModel model)
    {
        var type = (model.GetSymbolInfo(obj).Symbol as IMethodSymbol)?.ContainingType
            ?? model.GetTypeInfo(obj).Type as INamedTypeSymbol;

        if (type is null)
        {
            return new IRIdentifier($"--[[unsupported expr: {obj.Kind()}]]nil");
        }

        if (IsDictionaryType(type))
        {
            return new IRDictionaryNew();
        }

        if (IsListType(type))
        {
            return new IRListNew(obj.Initializer?.Expressions.Select(item => LowerExpr(item, model)).ToArray() ?? Array.Empty<IRExpr>());
        }

        var ctor = model.GetSymbolInfo(obj).Symbol as IMethodSymbol;
        var args = ctor is null
            ? obj.ArgumentList?.Arguments.Select(a => LowerExpr(a.Expression, model)).ToArray() ?? Array.Empty<IRExpr>()
            : LowerArguments(obj.ArgumentList?.Arguments.Select(a => a.Expression) ?? Enumerable.Empty<ExpressionSyntax>(), ctor.Parameters, model);

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
            return new IRInvocation(
                new IRMemberAccess(GetLuaObjectTypeTarget(type), ctor is null ? "new" : GetLuaObjectConstructorName(ctor)),
                args);
        }

        return new IRInvocation(new IRMemberAccess(LowerTypeReference(type), ctor is null ? "New" : GetLuaMethodName(ctor)), args);
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
        if (symbol is not IPropertySymbol { Name: "Length" or "Count" })
        {
            return null;
        }

        var type = model.GetTypeInfo(access.Expression).Type;
        if (type is IArrayTypeSymbol)
        {
            return new IRLength(LowerExpr(access.Expression, model));
        }

        if (IsListType(type))
        {
            return new IRListCount(LowerExpr(access.Expression, model));
        }

        if (IsDictionaryType(type))
        {
            return new IRDictionaryCount(LowerExpr(access.Expression, model));
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

    private static bool IsListType(ITypeSymbol? type)
        => type is INamedTypeSymbol { Name: "List", ContainingNamespace: { } ns }
           && (ns.ToDisplayString() == "System.Collections.Generic" || IsSharpLibNamespace(ns));

    private static bool IsDictionaryType(ITypeSymbol? type)
        => type is INamedTypeSymbol { Name: "Dictionary", ContainingNamespace: { } ns }
           && IsSharpLibNamespace(ns);

    private static bool IsSharpLibKeyValueType(ITypeSymbol? type)
        => type is INamedTypeSymbol { Name: "KeyValue", ContainingNamespace: { } ns }
           && IsSharpLibNamespace(ns);

    private static bool IsTaskDelay(IMethodSymbol symbol)
        => symbol is { Name: "Delay", IsStatic: true, ContainingType: { Name: "Task", ContainingNamespace: { } ns } }
           && (IsSharpLibNamespace(ns) || ns.ToDisplayString() == "System.Threading.Tasks");

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

    private static IRExpr? TryLowerLuaInteropInvocation(IMethodSymbol symbol, IReadOnlyList<IRExpr> args)
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
            _ => null,
        };
    }

    private static bool IsLuaInteropMethod(IMethodSymbol symbol)
        => symbol is { IsStatic: true, ContainingType: { Name: "LuaInterop", ContainingNamespace: { } ns } }
           && IsSharpLibNamespace(ns);

    private static bool IsLuaObjectType(INamedTypeSymbol symbol)
        => InheritsFromLuaObject(symbol);

    private static bool IsLuaImplementedClass(INamedTypeSymbol symbol)
        => GetLuaAttributeValue(symbol, "Class") is { Length: > 0 };

    private static bool IsExternalLuaObjectType(INamedTypeSymbol symbol)
        => IsLuaObjectType(symbol) && !IsLuaImplementedClass(symbol);

    private static bool InheritsFromLuaObject(INamedTypeSymbol symbol)
    {
        for (var type = symbol; type is not null; type = type.BaseType)
        {
            if (type is { Name: "LuaObject", ContainingNamespace: { } ns } && IsSharpLibNamespace(ns))
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

    private static bool IsSharpLibType(INamedTypeSymbol symbol)
        => IsSharpLibNamespace(symbol.ContainingNamespace);

    private static bool IsSharpLibNamespace(INamespaceSymbol ns)
    {
        var name = ns.ToDisplayString();
        return name == "SFLib"
               || name == "SharpLib"
               || name == "SharpLib.Collections";
    }

    private bool IsInLibraryFolder(SyntaxTree tree)
    {
        if (_sourceRoot is null || _libraryFolderNames.Count == 0 || string.IsNullOrWhiteSpace(tree.FilePath))
        {
            return false;
        }

        var relative = Path.GetRelativePath(_sourceRoot, Path.GetFullPath(tree.FilePath));
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return false;
        }

        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Take(Math.Max(0, segments.Length - 1)).Any(_libraryFolderNames.Contains);
    }

    private static string NormalizeFolderName(string folder)
        => Path.GetFileName(Path.TrimEndingDirectorySeparator(folder.Trim()));

    private IRStmt? TryLowerDictionaryAssignment(ExpressionSyntax left, ExpressionSyntax right, SemanticModel model)
    {
        if (left is not ElementAccessExpressionSyntax elementAccess || model.GetTypeInfo(elementAccess.Expression).Type is not { } type || !IsDictionaryType(type))
        {
            return null;
        }

        return new IRExprStmt(new IRDictionarySet(
            LowerExpr(elementAccess.Expression, model),
            LowerExpr(elementAccess.ArgumentList.Arguments[0].Expression, model),
            LowerExpr(right, model)));
    }

    private IRStmt? TryLowerListAssignment(ExpressionSyntax left, ExpressionSyntax right, SemanticModel model)
    {
        if (left is not ElementAccessExpressionSyntax elementAccess || model.GetTypeInfo(elementAccess.Expression).Type is not { } type || !IsListType(type))
        {
            return null;
        }

        return new IRExprStmt(new IRListSet(
            LowerExpr(elementAccess.Expression, model),
            LowerExpr(elementAccess.ArgumentList.Arguments[0].Expression, model),
            LowerExpr(right, model)));
    }

    private IRExpr? TryLowerDictionaryGet(ElementAccessExpressionSyntax access, SemanticModel model)
    {
        var type = model.GetTypeInfo(access.Expression).Type;
        return IsDictionaryType(type)
            ? new IRDictionaryGet(LowerExpr(access.Expression, model), LowerExpr(access.ArgumentList.Arguments[0].Expression, model))
            : null;
    }

    private IRExpr? TryLowerListGet(ElementAccessExpressionSyntax access, SemanticModel model)
    {
        var type = model.GetTypeInfo(access.Expression).Type;
        return IsListType(type)
            ? new IRListGet(LowerExpr(access.Expression, model), LowerExpr(access.ArgumentList.Arguments[0].Expression, model))
            : null;
    }

    private IRExpr? TryLowerSharpLibKeyValueAccess(MemberAccessExpressionSyntax access, SemanticModel model)
    {
        if (model.GetSymbolInfo(access).Symbol is not IPropertySymbol { Name: "Key" or "Value" } property || !IsSharpLibKeyValueType(property.ContainingType))
        {
            return null;
        }

        if (access.Expression is IdentifierNameSyntax id
            && model.GetSymbolInfo(id).Symbol is { } itemSymbol
            && _dictionaryForEachItems.TryGetValue(itemSymbol, out var itemNames))
        {
            return new IRIdentifier(property.Name == "Key" ? itemNames.KeyName : itemNames.ValueName);
        }

        return new IRMemberAccess(LowerExpr(access.Expression, model), property.Name == "Key" ? "k" : "v");
    }

    private bool CanInlineDictionaryForEachItem(StatementSyntax statement, SemanticModel model, ISymbol itemSymbol)
    {
        foreach (var id in statement.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (!SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id).Symbol, itemSymbol))
            {
                continue;
            }

            if (id.Parent is MemberAccessExpressionSyntax access
                && access.Expression == id
                && access.Name.Identifier.ValueText is "Key" or "Value"
                && model.GetSymbolInfo(access).Symbol is IPropertySymbol { Name: "Key" or "Value" } property
                && IsSharpLibKeyValueType(property.ContainingType))
            {
                continue;
            }

            return false;
        }

        return true;
    }

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

        return new IRInvocation(
            new IRMemberAccess(LowerExpr(access.Expression, model), "get_" + property.Name),
            Array.Empty<IRExpr>(),
            UseColon: !property.IsStatic);
    }

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

    private string DeclareLuaName(ISymbol? symbol, string name)
    {
        var luaName = AllocateLuaName(name);
        if (symbol is not null)
        {
            _luaNames[symbol] = luaName;
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

    private static string GetLuaMethodName(IMethodSymbol method)
    {
        if (GetLuaAttributeName(method) is { Length: > 0 } attributeName)
        {
            return attributeName;
        }

        if (method.MethodKind == MethodKind.UserDefinedOperator)
        {
            return method.Name;
        }

        if (method.MethodKind == MethodKind.Constructor)
        {
            var explicitConstructors = method.ContainingType.InstanceConstructors
                .Where(c => !c.IsImplicitlyDeclared)
                .ToArray();
            return explicitConstructors.Length > 1 ? $"New_{method.Parameters.Length}" : "New";
        }

        var overloads = method.ContainingType.GetMembers(method.Name)
            .OfType<IMethodSymbol>()
            .Where(m => !m.IsImplicitlyDeclared && m.MethodKind == MethodKind.Ordinary)
            .ToArray();
        return overloads.Length > 1 ? $"{method.Name}_{method.Parameters.Length}" : method.Name;
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

    private static string GetLuaConstructorInitName(IMethodSymbol method)
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

    private static string GetLuaObjectConstructorName(IMethodSymbol ctor)
        => GetLuaAttributeValue(ctor, "StaticMethod")
           ?? GetLuaAttributeValue(ctor, "Name")
           ?? "new";

    private static (string Name, bool UseColon) GetLuaObjectMember(IMethodSymbol method)
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

    private static string GetLuaObjectMemberName(ISymbol symbol)
        => GetLuaAttributeValue(symbol, "Name") ?? symbol.Name;

    private static string? GetLuaAttributeName(IMethodSymbol method)
        => GetLuaAttributeValue(method, "StaticMethod")
           ?? GetLuaAttributeValue(method, "Method")
           ?? GetLuaAttributeValue(method, "Name");

    private static string? GetLuaAttributeValue(ISymbol symbol, string name)
        => GetLuaAttributeValues(symbol, name).FirstOrDefault();

    private static bool HasLuaTableLiteralAttribute(INamedTypeSymbol symbol)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            if (attribute.AttributeClass is not { Name: "LuaAttribute", ContainingNamespace: { } ns }
                || !IsSharpLibNamespace(ns))
            {
                continue;
            }

            foreach (var arg in attribute.NamedArguments)
            {
                if (arg.Key == "TableLiteral" && arg.Value.Value is true)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static IEnumerable<string> GetLuaAttributeValues(ISymbol symbol, string name)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            if (attribute.AttributeClass is not { Name: "LuaAttribute", ContainingNamespace: { } ns }
                || !IsSharpLibNamespace(ns))
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

        types.Clear();
        types.AddRange(sorted);

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

        foreach (var staticConstructor in type.Methods.Where(m => m.IsStaticConstructor))
        {
            CollectTypeReferences(staticConstructor.Body, dependencies);
        }

        return dependencies;
    }

    private static void CollectTypeReferences(IRStmt stmt, ISet<string> dependencies)
    {
        switch (stmt)
        {
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
            case IRDictionaryForEach dictionaryForEach:
                CollectTypeReferences(dictionaryForEach.Dictionary, dependencies);
                CollectTypeReferences(dictionaryForEach.Body, dependencies);
                break;
            case IRTry tryStmt:
                CollectTypeReferences(tryStmt.Try, dependencies);
                if (tryStmt.Catch is not null)
                {
                    CollectTypeReferences(tryStmt.Catch, dependencies);
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
            case IRDictionaryCount dictionaryCount:
                CollectTypeReferences(dictionaryCount.Table, dependencies);
                break;
            case IRDictionaryGet dictionaryGet:
                CollectTypeReferences(dictionaryGet.Table, dependencies);
                CollectTypeReferences(dictionaryGet.Key, dependencies);
                break;
            case IRDictionarySet dictionarySet:
                CollectTypeReferences(dictionarySet.Table, dependencies);
                CollectTypeReferences(dictionarySet.Key, dependencies);
                CollectTypeReferences(dictionarySet.Value, dependencies);
                break;
            case IRDictionaryRemove dictionaryRemove:
                CollectTypeReferences(dictionaryRemove.Table, dependencies);
                CollectTypeReferences(dictionaryRemove.Key, dependencies);
                break;
            case IRListNew listNew:
                foreach (var item in listNew.Items)
                {
                    CollectTypeReferences(item, dependencies);
                }
                break;
            case IRListCount listCount:
                CollectTypeReferences(listCount.List, dependencies);
                break;
            case IRListGet listGet:
                CollectTypeReferences(listGet.List, dependencies);
                CollectTypeReferences(listGet.Index, dependencies);
                break;
            case IRListSet listSet:
                CollectTypeReferences(listSet.List, dependencies);
                CollectTypeReferences(listSet.Index, dependencies);
                CollectTypeReferences(listSet.Value, dependencies);
                break;
            case IRListAdd listAdd:
                CollectTypeReferences(listAdd.List, dependencies);
                CollectTypeReferences(listAdd.Value, dependencies);
                break;
            case IRListSort listSort:
                CollectTypeReferences(listSort.List, dependencies);
                if (listSort.Comparer is not null)
                {
                    CollectTypeReferences(listSort.Comparer, dependencies);
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
            case IRIs isExpr:
                CollectTypeReferences(isExpr.Value, dependencies);
                dependencies.Add(GetTypeReferenceFullName(isExpr.Type));
                break;
            case IRAs asExpr:
                CollectTypeReferences(asExpr.Value, dependencies);
                dependencies.Add(GetTypeReferenceFullName(asExpr.Type));
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
                    => LowerExpr(i.Expression, model),
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
                yield return LowerExpr(side, model);
            }
        }
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
                float f => new IRLiteral((double)f, IRLiteralKind.Real),
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
        => type.SpecialType switch
        {
            SpecialType.System_Boolean => new IRLiteral(false, IRLiteralKind.Boolean),
            SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_Int16 or SpecialType.System_UInt16
                or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64
                => new IRLiteral(0, IRLiteralKind.Integer),
            SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal
                => new IRLiteral(0.0, IRLiteralKind.Real),
            _ => new IRLiteral(null, IRLiteralKind.Nil),
        };

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
