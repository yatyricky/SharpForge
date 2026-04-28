using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpForge.Transpiler.Analyzers;

/// <summary>
/// Aggregates the SharpForge desync-linter rules and runs them against a
/// Roslyn compilation. Implemented as plain syntax/semantic walkers (not
/// <c>DiagnosticAnalyzer</c>s) so they can be invoked directly from the CLI
/// pipeline without hosting a Roslyn analysis context.
/// </summary>
public sealed class DesyncLinter
{
    public IReadOnlyList<LintDiagnostic> Run(CSharpCompilation compilation, CancellationToken cancellationToken)
    {
        var results = new List<LintDiagnostic>();

        foreach (var tree in compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot(cancellationToken);

            // W3R0006: GetLocalPlayer() must only flow into visual-only APIs.
            // Bring-up version: flag *any* call to GetLocalPlayer outside an
            // 'if' condition so authors get an early signal. Refined later.
            foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (inv.Expression is IdentifierNameSyntax id && id.Identifier.ValueText == "GetLocalPlayer")
                {
                    var enclosingIf = inv.FirstAncestorOrSelf<IfStatementSyntax>();
                    if (enclosingIf is null || !enclosingIf.Condition.Span.Contains(inv.Span))
                    {
                        results.Add(new LintDiagnostic(
                            "W3R0006",
                            "GetLocalPlayer() must only be used to gate visual operations inside an `if` condition.",
                            DiagnosticSeverity.Error,
                            inv.GetLocation()));
                    }
                }
            }
        }

        return results;
    }
}

public sealed record LintDiagnostic(string Id, string Message, DiagnosticSeverity Severity, Location Location);
