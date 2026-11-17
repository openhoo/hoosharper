using System.Collections.Immutable;
using System.Linq;
using System.Threading;
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

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

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
        if (CanSimplify(comparison, context.SemanticModel, context.CancellationToken) &&
            !HasEligibleAncestor(comparison, context.SemanticModel, context.CancellationToken))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, comparison.OperatorToken.GetLocation()));
        }
    }

    internal static bool CanSimplify(BinaryExpressionSyntax comparison, SemanticModel model, CancellationToken token) =>
        !comparison.ContainsDirectives && !comparison.ContainsDiagnostics &&
        TryGetBooleanLiteralOperand(comparison, out var operand) &&
        model.GetOperation(comparison, token) is IBinaryOperation { OperatorMethod: null } operation &&
        IsBooleanOperand(operation.LeftOperand) && IsBooleanOperand(operation.RightOperand) &&
        operation.Type?.SpecialType == SpecialType.System_Boolean && !ContainsUserDefinedNot(operation) &&
        HasIndependentBooleanType(operand, model, comparison.SpanStart);

    // Removing == true (or !) must leave a bool even when overload resolution
    // no longer supplies a target type, as with Pick(default == true).
    internal static bool HasIndependentBooleanType(ExpressionSyntax operand, SemanticModel model, int position) =>
        model.GetSpeculativeTypeInfo(position, operand, SpeculativeBindingOption.BindAsExpression)
            .Type?.SpecialType == SpecialType.System_Boolean;

    private static bool IsBooleanOperand(IOperation operation) =>
        operation.Type?.SpecialType == SpecialType.System_Boolean &&
        operation is not IConversionOperation { IsImplicit: true, OperatorMethod: not null };

    private static bool HasEligibleAncestor(
        BinaryExpressionSyntax comparison,
        SemanticModel semanticModel,
        System.Threading.CancellationToken cancellationToken)
    {
        for (var ancestor = comparison.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor is BinaryExpressionSyntax parent &&
                CanSimplify(parent, semanticModel, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsUserDefinedNot(IOperation operation) =>
        operation.DescendantsAndSelf().OfType<IUnaryOperation>().Any(unary =>
            unary.OperatorKind == UnaryOperatorKind.Not && unary.OperatorMethod is not null);

    internal static bool TryGetBooleanLiteralOperand(
        BinaryExpressionSyntax comparison,
        out ExpressionSyntax expression)
    {
        if (!comparison.IsKind(SyntaxKind.EqualsExpression) &&
            !comparison.IsKind(SyntaxKind.NotEqualsExpression))
        {
            expression = null!;
            return false;
        }

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
