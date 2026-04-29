using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SharpForge.Transpiler.IR;

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
    private IRModule? _module;

    public IRLowering(IEnumerable<string>? ignoredClasses = null)
    {
        _ignoredClasses = ignoredClasses is null
            ? new HashSet<string>(new[] { "SF__JASSGEN" }, StringComparer.Ordinal)
            : new HashSet<string>(ignoredClasses, StringComparer.Ordinal);
    }

    public IRModule Lower(CSharpCompilation compilation, CancellationToken cancellationToken)
    {
        var module = new IRModule();
        _module = module;

        foreach (var tree in compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

                if (InheritsFromHandle(symbol))
                {
                    // JASS handle hierarchy (rooted at `handle`) consists of extern stubs
                    // emitted by sf-jassgen; never lower them to Lua.
                    continue;
                }

                module.Types.Add(LowerType(typeDecl, symbol, model, cancellationToken));
            }
        }

        module.Types.Sort((a, b) => string.CompareOrdinal(a.FullName, b.FullName));
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
        var nsSegments = GetNamespaceSegments(symbol.ContainingNamespace);
        var irType = new IRType
        {
            NamespaceSegments = nsSegments,
            Name = symbol.Name,
            FullName = string.Join('.', nsSegments.Append(symbol.Name)),
            IsStatic = symbol.IsStatic,
        };

        foreach (var member in typeDecl.Members)
        {
            switch (member)
            {
                case ConstructorDeclarationSyntax c:
                    irType.Methods.Add(LowerConstructor(c, symbol, model, ct));
                    break;
                case MethodDeclarationSyntax m:
                    irType.Methods.Add(LowerMethod(m, symbol, model, ct));
                    break;
                case FieldDeclarationSyntax f when f.Modifiers.Any(SyntaxKind.StaticKeyword):
                    foreach (var v in f.Declaration.Variables)
                    {
                        irType.Fields.Add(new IRField
                        {
                            Name = v.Identifier.ValueText,
                            Initializer = v.Initializer is null
                                ? LowerDefaultValue(f.Declaration.Type)
                                : LowerExpr(v.Initializer.Value, model),
                            IsStatic = true,
                        });
                    }
                    break;
                case FieldDeclarationSyntax f:
                    foreach (var v in f.Declaration.Variables)
                    {
                        irType.Fields.Add(new IRField
                        {
                            Name = v.Identifier.ValueText,
                            Initializer = v.Initializer is null
                                ? LowerDefaultValue(f.Declaration.Type)
                                : LowerExpr(v.Initializer.Value, model),
                            IsStatic = false,
                        });
                    }
                    break;
                case PropertyDeclarationSyntax p:
                    irType.Fields.Add(new IRField
                    {
                        Name = p.Identifier.ValueText,
                        Initializer = p.Initializer is null
                            ? LowerDefaultValue(p.Type)
                            : LowerExpr(p.Initializer.Value, model),
                        IsStatic = p.Modifiers.Any(SyntaxKind.StaticKeyword),
                    });
                    break;
            }
        }

        if (!irType.IsStatic && irType.Methods.All(m => !m.IsConstructor))
        {
            irType.Methods.Add(new IRFunction
            {
                Name = symbol.Name,
                LuaName = "New",
                IsConstructor = true,
                IsInstance = false,
            });
        }

        return irType;
    }

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

            if (c.Body is { } staticBody)
            {
                LowerStatements(staticBody.Statements, staticFn.Body, model, ct);
            }

            return staticFn;
        }

        var fn = new IRFunction
        {
            Name = c.Identifier.ValueText,
            LuaName = symbol is null ? "New" : GetLuaMethodName(symbol),
            IsConstructor = true,
            IsInstance = false, // emit with `.` because we create `self` ourselves
        };

        foreach (var p in c.ParameterList.Parameters)
        {
            fn.Parameters.Add(p.Identifier.ValueText);
        }

        if (c.Body is { } body)
        {
            LowerStatements(body.Statements, fn.Body, model, ct);
        }
        else if (c.ExpressionBody is { } arrow)
        {
            fn.Body.Statements.Add(new IRExprStmt(LowerExpr(arrow.Expression, model)));
        }

        return fn;
    }

    private IRFunction LowerMethod(MethodDeclarationSyntax m, INamedTypeSymbol owner, SemanticModel model, CancellationToken ct)
    {
        var isStatic = m.Modifiers.Any(SyntaxKind.StaticKeyword);
        var symbol = model.GetDeclaredSymbol(m, ct);
        var fn = new IRFunction
        {
            Name = m.Identifier.ValueText,
            LuaName = symbol is null ? m.Identifier.ValueText : GetLuaMethodName(symbol),
            IsStatic = isStatic,
            IsInstance = !isStatic,
        };

        foreach (var p in m.ParameterList.Parameters)
        {
            fn.Parameters.Add(p.Identifier.ValueText);
        }

        if (m.Body is { } body)
        {
            LowerStatements(body.Statements, fn.Body, model, ct);
        }
        else if (m.ExpressionBody is { } arrow)
        {
            fn.Body.Statements.Add(new IRReturn(LowerExpr(arrow.Expression, model)));
        }

        return fn;
    }

    private void LowerStatements(IEnumerable<StatementSyntax> stmts, IRBlock target, SemanticModel model, CancellationToken ct)
    {
        foreach (var s in stmts)
        {
            ct.ThrowIfCancellationRequested();
            target.Statements.Add(LowerStatement(s, model, ct));
        }
    }

    private IRStmt LowerStatement(StatementSyntax s, SemanticModel model, CancellationToken ct)
    {
        switch (s)
        {
            case BlockSyntax b:
                var blk = new IRBlock();
                LowerStatements(b.Statements, blk, model, ct);
                return blk;

            case LocalDeclarationStatementSyntax ld:
                var first = ld.Declaration.Variables.First();
                return new IRLocalDecl(
                    first.Identifier.ValueText,
                    first.Initializer is null ? null : LowerExpr(first.Initializer.Value, model));

            case ForStatementSyntax fs:
                return LowerFor(fs, model, ct);

            case ExpressionStatementSyntax es when es.Expression is AssignmentExpressionSyntax ae:
                return LowerAssignment(ae, model);

            case ExpressionStatementSyntax es when IsIncrementOrDecrement(es.Expression):
                return LowerIncrementOrDecrement(es.Expression, model);

            case ExpressionStatementSyntax es:
                return new IRExprStmt(LowerExpr(es.Expression, model));

            case ReturnStatementSyntax rs:
                return new IRReturn(rs.Expression is null ? null : LowerExpr(rs.Expression, model));

            case IfStatementSyntax ifs:
                var thenBlk = new IRBlock();
                LowerStatements(UnwrapBlock(ifs.Statement), thenBlk, model, ct);
                IRBlock? elseBlk = null;
                if (ifs.Else is { } el)
                {
                    elseBlk = new IRBlock();
                    LowerStatements(UnwrapBlock(el.Statement), elseBlk, model, ct);
                }
                return new IRIf(LowerExpr(ifs.Condition, model), thenBlk, elseBlk);

            case WhileStatementSyntax ws:
                var whileBody = new IRBlock();
                LowerStatements(UnwrapBlock(ws.Statement), whileBody, model, ct);
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
                first.Identifier.ValueText,
                first.Initializer is null ? null : LowerExpr(first.Initializer.Value, model));
        }
        else if (fs.Initializers.Count > 0)
        {
            initializer = LowerForExpression(fs.Initializers[0], model);
        }

        var body = new IRBlock();
        LowerStatements(UnwrapBlock(fs.Statement), body, model, ct);
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

    private IRStmt LowerAssignment(AssignmentExpressionSyntax ae, SemanticModel model)
    {
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

    private static IEnumerable<StatementSyntax> UnwrapBlock(StatementSyntax s)
        => s is BlockSyntax b ? b.Statements : new[] { s };

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
                return new IRMemberAccess(LowerExpr(ma.Expression, model), ma.Name.Identifier.ValueText);

            case InvocationExpressionSyntax inv:
                var args = inv.ArgumentList.Arguments.Select(a => LowerExpr(a.Expression, model)).ToArray();
                return LowerInvocation(inv, args, model);

            case ObjectCreationExpressionSyntax obj:
                return LowerObjectCreation(obj, model);

            case BinaryExpressionSyntax bin:
                return new IRBinary(MapBinaryOp(bin.OperatorToken.ValueText), LowerExpr(bin.Left, model), LowerExpr(bin.Right, model));

            case PrefixUnaryExpressionSyntax pre:
                return new IRUnary(pre.OperatorToken.ValueText, LowerExpr(pre.Operand, model));

            case ParenthesizedExpressionSyntax par:
                return LowerExpr(par.Expression, model);

            default:
                return UnsupportedExpression(e);
        }
    }

    private IRExpr LowerInvocation(InvocationExpressionSyntax inv, IReadOnlyList<IRExpr> args, SemanticModel model)
    {
        var symbol = model.GetSymbolInfo(inv).Symbol as IMethodSymbol;
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

            return new IRInvocation(
                new IRMemberAccess(LowerExpr(staticAccess.Expression, model), GetLuaMethodName(symbol)),
                args);
        }

        return new IRInvocation(LowerExpr(inv.Expression, model), args);
    }

    private IRExpr LowerObjectCreation(ObjectCreationExpressionSyntax obj, SemanticModel model)
    {
        var args = obj.ArgumentList?.Arguments.Select(a => LowerExpr(a.Expression, model)).ToArray()
            ?? Array.Empty<IRExpr>();

        var type = (model.GetSymbolInfo(obj).Symbol as IMethodSymbol)?.ContainingType
            ?? model.GetTypeInfo(obj).Type as INamedTypeSymbol;

        if (type is null)
        {
            return new IRIdentifier($"--[[unsupported expr: {obj.Kind()}]]nil");
        }

        var ctor = model.GetSymbolInfo(obj).Symbol as IMethodSymbol;
        return new IRInvocation(new IRMemberAccess(LowerTypeReference(type), ctor is null ? "New" : GetLuaMethodName(ctor)), args);
    }

    /// <summary>
    /// Resolves bare identifiers via the semantic model. Instance fields/properties/
    /// methods of the enclosing type get rewritten as <c>self.X</c> so callers don't
    /// have to spell <c>this.</c> explicitly.
    /// </summary>
    private IRExpr LowerIdentifier(IdentifierNameSyntax id, SemanticModel model)
    {
        var symbol = model.GetSymbolInfo(id).Symbol;
        if (symbol is INamedTypeSymbol type)
        {
            return LowerTypeReference(type);
        }

        if (symbol is { IsStatic: true } and (IFieldSymbol or IPropertySymbol) && symbol.ContainingType is not null)
        {
            if (IsIgnoredClass(symbol.ContainingType))
            {
                return new IRIdentifier(id.Identifier.ValueText);
            }

            return new IRMemberAccess(LowerTypeReference(symbol.ContainingType), id.Identifier.ValueText);
        }

        if (symbol is IMethodSymbol { IsStatic: true } staticMethod)
        {
            if (IsIgnoredClass(staticMethod.ContainingType))
            {
                return new IRIdentifier(staticMethod.Name);
            }

            return new IRMemberAccess(LowerTypeReference(staticMethod.ContainingType), GetLuaMethodName(staticMethod));
        }

        if (symbol is { IsStatic: false } and (IFieldSymbol or IPropertySymbol or IMethodSymbol)
            && symbol.ContainingType is not null)
        {
            return symbol is IMethodSymbol instanceMethod
                ? new IRMemberAccess(new IRIdentifier("self"), GetLuaMethodName(instanceMethod))
                : new IRMemberAccess(new IRIdentifier("self"), id.Identifier.ValueText);
        }
        return new IRIdentifier(id.Identifier.ValueText);
    }

    private static string GetLuaMethodName(IMethodSymbol method)
    {
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

    private static IRTypeReference LowerTypeReference(INamedTypeSymbol type)
        => new(GetNamespaceSegments(type.ContainingNamespace), type.Name);

    private bool IsIgnoredClass(INamedTypeSymbol? symbol)
        => symbol is not null && _ignoredClasses.Contains(symbol.Name);

    private IRExpr LowerInterpolatedString(InterpolatedStringExpressionSyntax istr, SemanticModel model)
    {
        // Build a left-folded `..` chain. Empty literal segments are dropped so the
        // output matches hand-written Lua (`a .. " - " .. b`, not `"" .. a .. ...`).
        IRExpr? acc = null;
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

            acc = acc is null ? part : new IRBinary("..", acc, part);
        }

        return acc ?? new IRLiteral(string.Empty, IRLiteralKind.String);
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
        if (_module is null)
        {
            return;
        }

        var span = node.GetLocation().GetLineSpan();
        var line = span.StartLinePosition.Line + 1;
        var character = span.StartLinePosition.Character + 1;
        _module.Diagnostics.Add($"{span.Path}({line},{character}): unsupported {kind}: {node.Kind()}");
    }
}
