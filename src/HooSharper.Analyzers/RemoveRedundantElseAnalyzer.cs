using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace HooSharper.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RemoveRedundantElseAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HOO1003";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Remove a redundant else",
        "Remove this redundant else",
        "HooSharper.CodeStyle",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Remove an else branch when the preceding if branch definitely terminates control flow.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeIfStatement, SyntaxKind.IfStatement);
    }

    private static void AnalyzeIfStatement(SyntaxNodeAnalysisContext context)
    {
        var ifStatement = (IfStatementSyntax)context.Node;
        if (!HooSharper.Internal.ControlFlowScopeSafety.CanRemoveElse(
                ifStatement, context.SemanticModel, context.CancellationToken))
        {
            return;
        }

        var elseClause = ifStatement.Else!;
        context.ReportDiagnostic(Diagnostic.Create(Rule, elseClause.ElseKeyword.GetLocation()));
    }

}
