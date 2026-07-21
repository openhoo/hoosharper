using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace HooSharper.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SimplifyBooleanComparisonAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HOO1006";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Simplify boolean comparison",
        "Simplify this boolean comparison",
        "HooSharper.CodeStyle",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Comparisons between a non-nullable bool expression and a boolean literal can be simplified.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(
            AnalyzeComparison,
            SyntaxKind.EqualsExpression,
            SyntaxKind.NotEqualsExpression);
    }

    private static void AnalyzeComparison(SyntaxNodeAnalysisContext context)
    {
        var comparison = (BinaryExpressionSyntax)context.Node;
        if (!TryGetBooleanLiteralOperand(comparison, out _))
        {
            return;
        }

        if (context.SemanticModel.GetOperation(comparison, context.CancellationToken) is not IBinaryOperation operation ||
            operation.OperatorMethod is not null ||
            operation.LeftOperand.Type?.SpecialType != SpecialType.System_Boolean ||
            operation.RightOperand.Type?.SpecialType != SpecialType.System_Boolean ||
            operation.Type?.SpecialType != SpecialType.System_Boolean)
        {
            return;
        }


        context.ReportDiagnostic(Diagnostic.Create(Rule, comparison.OperatorToken.GetLocation()));
    }

    internal static bool TryGetBooleanLiteralOperand(
        BinaryExpressionSyntax comparison,
        out ExpressionSyntax expression)
    {
        if (IsBooleanLiteral(comparison.Right))
        {
            expression = comparison.Left;
            return true;
        }

        if (IsBooleanLiteral(comparison.Left))
        {
            expression = comparison.Right;
            return true;
        }

        expression = null!;
        return false;
    }

    private static bool IsBooleanLiteral(ExpressionSyntax expression) =>
        expression.IsKind(SyntaxKind.TrueLiteralExpression) ||
        expression.IsKind(SyntaxKind.FalseLiteralExpression);
}
