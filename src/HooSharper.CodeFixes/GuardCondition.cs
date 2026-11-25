using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace HooSharper.CodeFixes;

internal static class GuardCondition
{
    private static bool HasSignificantTrivia(IEnumerable<SyntaxTrivia> trivia) =>
        trivia.Any(item => !item.IsKind(SyntaxKind.WhitespaceTrivia) && !item.IsKind(SyntaxKind.EndOfLineTrivia));

    internal static ExpressionSyntax Negate(
        ExpressionSyntax condition,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var originalCondition = condition;
        condition = WalkDownParentheses(condition);
        if (condition != originalCondition &&
            HasSignificantTrivia(originalCondition.DescendantTrivia(descendIntoTrivia: true)))
        {
            // Removing these parentheses would also remove comments attached
            // to their interior operand edges.
            return ParenthesizedNegation(originalCondition);
        }

        if (condition is BinaryExpressionSyntax comparison &&
            (TryInvertOrderedComparison(comparison, semanticModel, cancellationToken, out var inverse) ||
             TryInvertEquality(comparison, semanticModel, cancellationToken, out inverse)))
            return inverse;

        return condition switch
        {
            PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalNotExpression } logicalNot
                when condition == originalCondition &&
                    IsBuiltInLogicalNot(logicalNot, semanticModel, cancellationToken) &&
                    !HasSignificantTrivia(logicalNot.DescendantTrivia(descendIntoTrivia: true)) =>
                WalkDownParentheses(logicalNot.Operand).WithoutTrivia(),
            IdentifierNameSyntax or MemberAccessExpressionSyntax or InvocationExpressionSyntax =>
                SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, condition.WithoutTrivia()),
            _ => ParenthesizedNegation(originalCondition),
        };
    }

    private static bool TryInvertOrderedComparison(BinaryExpressionSyntax expression, SemanticModel model,
        CancellationToken token, out BinaryExpressionSyntax inverse)
    {
        inverse = expression;
        var (kind, op) = expression.Kind() switch
        {
            SyntaxKind.LessThanExpression => (SyntaxKind.GreaterThanOrEqualExpression, SyntaxKind.GreaterThanEqualsToken),
            SyntaxKind.LessThanOrEqualExpression => (SyntaxKind.GreaterThanExpression, SyntaxKind.GreaterThanToken),
            SyntaxKind.GreaterThanExpression => (SyntaxKind.LessThanOrEqualExpression, SyntaxKind.LessThanEqualsToken),
            SyntaxKind.GreaterThanOrEqualExpression => (SyntaxKind.LessThanExpression, SyntaxKind.LessThanToken),
            _ => (SyntaxKind.None, SyntaxKind.None),
        };
        if (kind == SyntaxKind.None || model.GetOperation(expression, token) is not
            IBinaryOperation { IsLifted: false } operation)
            return false;
        // For NaN and nullable operands the opposite comparison is not the
        // complement. Nor do custom operators have to agree with one another.
        var type = operation.LeftOperand.Type;
        // A user-convertible operand may bind a different custom operator for
        // the reverse spelling, even when the original comparison bound to int.
        if (type is null || !SymbolEqualityComparer.Default.Equals(type, operation.RightOperand.Type) ||
            model.GetConversion(expression.Left, token).IsUserDefined || model.GetConversion(expression.Right, token).IsUserDefined)
            return false;
        bool builtIn = operation.OperatorMethod is null && type.SpecialType is SpecialType.System_Char or
            SpecialType.System_SByte or SpecialType.System_Byte or SpecialType.System_Int16 or SpecialType.System_UInt16 or
            SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64 or
            SpecialType.System_Decimal;
        bool timeSpan = type.Name == "TimeSpan" && operation.OperatorMethod is { } method &&
            SymbolEqualityComparer.Default.Equals(type, method.ContainingType) &&
            SymbolEqualityComparer.Default.Equals(type,
                model.Compilation.GetSpecialType(SpecialType.System_Object).ContainingAssembly.GetTypeByMetadataName("System.TimeSpan"));
        if (builtIn || timeSpan)
        {
            inverse = ReplaceOperator(expression.WithoutTrivia(), kind, op);
            return true;
        }
        return false;
    }

    private static bool IsBuiltInLogicalNot(
        PrefixUnaryExpressionSyntax expression,
        SemanticModel semanticModel,
        CancellationToken cancellationToken) =>
        semanticModel.GetOperation(expression, cancellationToken) is
            IUnaryOperation { OperatorMethod: null };

    private static bool TryInvertEquality(BinaryExpressionSyntax expression, SemanticModel model,
        CancellationToken token, out BinaryExpressionSyntax inverse)
    {
        inverse = expression;
        if (!expression.IsKind(SyntaxKind.EqualsExpression) && !expression.IsKind(SyntaxKind.NotEqualsExpression) ||
            model.GetOperation(expression, token) is not IBinaryOperation { OperatorMethod: null })
            return false;

        inverse = expression.IsKind(SyntaxKind.EqualsExpression)
            ? ReplaceOperator(expression.WithoutTrivia(), SyntaxKind.NotEqualsExpression, SyntaxKind.ExclamationEqualsToken)
            : ReplaceOperator(expression.WithoutTrivia(), SyntaxKind.EqualsExpression, SyntaxKind.EqualsEqualsToken);
        // Metadata need not define == and != as a pair. The inverse spelling can
        // call a user operator even when this expression uses reference equality.
        return model.GetSpeculativeSymbolInfo(expression.SpanStart, inverse, SpeculativeBindingOption.BindAsExpression)
            .Symbol is IMethodSymbol { MethodKind: MethodKind.BuiltinOperator };
    }

    private static BinaryExpressionSyntax ReplaceOperator(
        BinaryExpressionSyntax expression,
        SyntaxKind expressionKind,
        SyntaxKind tokenKind) =>
        SyntaxFactory.BinaryExpression(
            expressionKind,
            expression.Left,
            SyntaxFactory.Token(tokenKind).WithTriviaFrom(expression.OperatorToken),
            expression.Right);

    private static ExpressionSyntax WalkDownParentheses(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression;
    }

    private static PrefixUnaryExpressionSyntax ParenthesizedNegation(ExpressionSyntax condition) =>
        SyntaxFactory.PrefixUnaryExpression(
            SyntaxKind.LogicalNotExpression,
            // The caller restores the original condition's edge trivia on the
            // complete negation. Keeping it here as well duplicates comments.
            SyntaxFactory.ParenthesizedExpression(condition.WithoutTrivia()));
}
