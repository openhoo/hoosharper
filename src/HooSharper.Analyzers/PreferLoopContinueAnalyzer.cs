using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace HooSharper.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferLoopContinueAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HOO1004";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Prefer an early continue",
        "Invert this condition and continue early",
        "HooSharper.CodeStyle",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Prefer a continue guard when a final if statement wraps the remaining work in a loop body.");

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
        if (ifStatement.Else is not null ||
            ifStatement.Statement is not BlockSyntax block ||
            block.Statements.Count == 0 ||
            ifStatement.ContainsDirectives)
        {
            return;
        }

        if (ifStatement.Parent is not BlockSyntax loopBody ||
            loopBody.Statements.LastOrDefault() != ifStatement ||
            !IsLoopBody(loopBody))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, ifStatement.IfKeyword.GetLocation()));
    }

    private static bool IsLoopBody(BlockSyntax block) => block.Parent switch
    {
        ForStatementSyntax forStatement => forStatement.Statement == block,
        ForEachStatementSyntax forEachStatement => forEachStatement.Statement == block,
        ForEachVariableStatementSyntax forEachVariableStatement => forEachVariableStatement.Statement == block,
        WhileStatementSyntax whileStatement => whileStatement.Statement == block,
        DoStatementSyntax doStatement => doStatement.Statement == block,
        _ => false,
    };
}
