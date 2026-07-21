using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace HooSharper.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferEarlyReturnAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HOO1001";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Prefer an early return",
        "Invert this condition and return early",
        "HooSharper.CodeStyle",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Prefer a guard clause when an if statement wraps the remaining statements of a void method.");

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
        if (ifStatement.Else is not null || ifStatement.Statement is not BlockSyntax block || block.Statements.Count == 0)
        {
            return;
        }

        if (ifStatement.Parent is not BlockSyntax parentBlock || parentBlock.Statements.LastOrDefault() != ifStatement)
        {
            return;
        }

        if (parentBlock.Parent is not MethodDeclarationSyntax method || !method.ReturnType.IsKind(SyntaxKind.PredefinedType) ||
            !((PredefinedTypeSyntax)method.ReturnType).Keyword.IsKind(SyntaxKind.VoidKeyword))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, ifStatement.IfKeyword.GetLocation()));
    }
}
