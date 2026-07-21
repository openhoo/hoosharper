using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace HooSharper.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class OmitBracesForSingleLineIfAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HOO1002";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Omit braces for a single-statement if",
        "Remove braces from this single-statement if",
        "HooSharper.CodeStyle",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Single-statement if branches do not need braces.");

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
        ReportIfSafe(context, ifStatement.Statement);

        if (ifStatement.Else is { Statement: not IfStatementSyntax } elseClause)
        {
            ReportIfSafe(context, elseClause.Statement);
        }
    }

    private static void ReportIfSafe(SyntaxNodeAnalysisContext context, StatementSyntax statement)
    {
        if (statement is not BlockSyntax { Statements.Count: 1 } block)
        {
            return;
        }

        var nestedStatement = block.Statements[0];
        if (nestedStatement is LocalDeclarationStatementSyntax or LocalFunctionStatementSyntax || HasDirective(block))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, block.OpenBraceToken.GetLocation()));
    }

    private static bool HasDirective(BlockSyntax block)
    {
        foreach (var trivia in block.DescendantTrivia(descendIntoTrivia: true))
        {
            if (trivia.IsDirective)
            {
                return true;
            }
        }

        return false;
    }
}
