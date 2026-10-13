using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace HooSharper.Internal;

internal static class NullComparisonSafety
{
    internal static bool IsSafeBinary(IOperation? operation) =>
        operation is IBinaryOperation
        {
            OperatorMethod: null,
            Type.SpecialType: SpecialType.System_Boolean,
        } binary &&
        !HasImplicitUserDefinedConversion(binary.LeftOperand) &&
        !HasImplicitUserDefinedConversion(binary.RightOperand);

    internal static bool IsSafeNullCheck(
        ExpressionSyntax condition,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        while (condition is ParenthesizedExpressionSyntax parenthesized)
        {
            condition = parenthesized.Expression;
        }

        return condition is IsPatternExpressionSyntax
            {
                Pattern: ConstantPatternSyntax { Expression.RawKind: (int)SyntaxKind.NullLiteralExpression },
            } or IsPatternExpressionSyntax
            {
                Pattern: UnaryPatternSyntax
                {
                    RawKind: (int)SyntaxKind.NotPattern,
                    Pattern: ConstantPatternSyntax { Expression.RawKind: (int)SyntaxKind.NullLiteralExpression },
                },
            } || condition is BinaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.EqualsExpression or (int)SyntaxKind.NotEqualsExpression,
            } && IsSafeBinary(semanticModel.GetOperation(condition, cancellationToken)) ||
            TryGetNullableHasValueGuard(condition, semanticModel, cancellationToken, out _);
    }

    internal static bool TryGetNullableHasValueGuard(
        ExpressionSyntax condition,
        SemanticModel semanticModel,
        CancellationToken cancellationToken,
        out ExpressionSyntax target)
    {
        condition = WalkDownParentheses(condition);
        if (condition is PrefixUnaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.LogicalNotExpression,
            } logicalNot &&
            WalkDownParentheses(logicalNot.Operand) is MemberAccessExpressionSyntax memberAccess &&
            semanticModel.GetOperation(logicalNot, cancellationToken) is IUnaryOperation
            {
                OperatorMethod: null,
                Type.SpecialType: SpecialType.System_Boolean,
            } &&
            semanticModel.GetOperation(memberAccess, cancellationToken) is IPropertyReferenceOperation
            {
                Property.Name: "HasValue",
                Property.ContainingType.OriginalDefinition.SpecialType: SpecialType.System_Nullable_T,
            })
        {
            target = memberAccess.Expression;
            return true;
        }

        target = null!;
        return false;
    }

    private static ExpressionSyntax WalkDownParentheses(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression;
    }

    private static bool HasImplicitUserDefinedConversion(IOperation operation)
    {
        while (true)
        {
            switch (operation)
            {
                case IParenthesizedOperation parenthesized:
                    operation = parenthesized.Operand;
                    break;
                case IConversionOperation { IsImplicit: true } conversion:
                    if (conversion.OperatorMethod is not null)
                    {
                        return true;
                    }

                    operation = conversion.Operand;
                    break;
                default:
                    return false;
            }
        }
    }
}
