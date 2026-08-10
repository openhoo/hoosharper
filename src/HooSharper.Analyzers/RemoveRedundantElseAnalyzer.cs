using System.Linq;
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

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeIfStatement, SyntaxKind.IfStatement);
    }

    private static void AnalyzeIfStatement(SyntaxNodeAnalysisContext context)
    {
        var ifStatement = (IfStatementSyntax)context.Node;
        var elseClause = ifStatement.Else;
        if (elseClause is null || elseClause.Statement is IfStatementSyntax || ifStatement.Parent is ElseClauseSyntax ||
            HasDirective(elseClause) ||
            !DefinitelyTerminates(ifStatement.Statement, context.SemanticModel))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, elseClause.ElseKeyword.GetLocation()));
    }

    private static bool DefinitelyTerminates(StatementSyntax statement, SemanticModel semanticModel)
    {
        if (statement is ReturnStatementSyntax or ThrowStatementSyntax or ContinueStatementSyntax or BreakStatementSyntax)
        {
            return true;
        }

        if (statement is not BlockSyntax block)
        {
            return false;
        }
        if (block.Statements.Count == 0)
        {
            return false;
        }

        var lastStatement = block.Statements[block.Statements.Count - 1];
        if (lastStatement is ReturnStatementSyntax or ThrowStatementSyntax or ContinueStatementSyntax or BreakStatementSyntax)
        {
            return true;
        }


        for (var index = block.Statements.Count - 2; index >= 0; index--)
        {
            var candidate = block.Statements[index];
            var controlFlow = semanticModel.AnalyzeControlFlow(candidate);
            if (controlFlow?.StartPointIsReachable == true)
            {
                return candidate is ReturnStatementSyntax or ThrowStatementSyntax or
                    ContinueStatementSyntax or BreakStatementSyntax;
            }
        }

        return false;
    }

    private static bool HasDirective(SyntaxNode node) =>
        node.DescendantTrivia(descendIntoTrivia: true).Any(trivia => trivia.IsDirective);
}
