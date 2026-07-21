using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace HooSharper.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RemoveRedundantNullConditionalGuardAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HOO1018";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Remove redundant null-conditional guard",
        "Remove the redundant null-conditional guard",
        "HooSharper.CodeStyle",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Remove a null guard around a single null-conditional expression statement for the same stable receiver.");

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
            ifStatement.Statement is not BlockSyntax { Statements.Count: 1 } block ||
            block.Statements[0] is not ExpressionStatementSyntax
            {
                Expression: ConditionalAccessExpressionSyntax conditionalAccess,
            } ||
            HasDirective(ifStatement) ||
            !TryGetNonNullCheckedReceiver(ifStatement.Condition, context, out var checkedReceiver, out var location) ||
            !IsStableReceiver(checkedReceiver, context.SemanticModel, context.CancellationToken) ||
            !IsStableReceiver(conditionalAccess.Expression, context.SemanticModel, context.CancellationToken))
        {
            return;
        }

        var checkedOperation = context.SemanticModel.GetOperation(checkedReceiver, context.CancellationToken);
        var accessedOperation = context.SemanticModel.GetOperation(conditionalAccess.Expression, context.CancellationToken);
        if (checkedOperation is null || accessedOperation is null ||
            !AreEquivalentReferences(checkedOperation, accessedOperation))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, location));
    }

    private static bool TryGetNonNullCheckedReceiver(
        ExpressionSyntax condition,
        SyntaxNodeAnalysisContext context,
        out ExpressionSyntax receiver,
        out Location location)
    {
        condition = WalkDownParentheses(condition);

        if (condition is IsPatternExpressionSyntax
            {
                Expression: var patternReceiver,
                Pattern: UnaryPatternSyntax
                {
                    RawKind: (int)SyntaxKind.NotPattern,
                    Pattern: ConstantPatternSyntax
                    {
                        Expression.RawKind: (int)SyntaxKind.NullLiteralExpression,
                    },
                },
            } isPattern)
        {
            receiver = WalkDownParentheses(patternReceiver);
            location = isPattern.IsKeyword.GetLocation();
            return true;
        }

        if (condition is BinaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.NotEqualsExpression,
                OperatorToken: var operatorToken,
            } inequality &&
            context.SemanticModel.GetOperation(inequality, context.CancellationToken) is IBinaryOperation
            {
                OperatorMethod: null,
                Type.TypeKind: not TypeKind.Dynamic,
            })
        {
            if (inequality.Left.IsKind(SyntaxKind.NullLiteralExpression))
            {
                receiver = WalkDownParentheses(inequality.Right);
                location = operatorToken.GetLocation();
                return true;
            }

            if (inequality.Right.IsKind(SyntaxKind.NullLiteralExpression))
            {
                receiver = WalkDownParentheses(inequality.Left);
                location = operatorToken.GetLocation();
                return true;
            }
        }

        receiver = null!;
        location = Location.None;
        return false;
    }

    private static bool IsStableReceiver(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        System.Threading.CancellationToken cancellationToken)
    {
        expression = WalkDownParentheses(expression);
        if (expression is not (IdentifierNameSyntax or ThisExpressionSyntax or BaseExpressionSyntax or
            MemberAccessExpressionSyntax { RawKind: (int)SyntaxKind.SimpleMemberAccessExpression }))
        {
            return false;
        }

        return IsStableOperation(semanticModel.GetOperation(expression, cancellationToken));
    }

    private static bool IsStableOperation(IOperation? operation)
    {
        operation = Unwrap(operation);
        return operation switch
        {
            ILocalReferenceOperation => true,
            IParameterReferenceOperation => true,
            IInstanceReferenceOperation => true,
            IFieldReferenceOperation field =>
                field.Field.IsReadOnly && (field.Instance is null || IsStableOperation(field.Instance)),
            IEventReferenceOperation => false,
            _ => false,
        };
    }

    private static bool AreEquivalentReferences(IOperation left, IOperation right)
    {
        left = Unwrap(left)!;
        right = Unwrap(right)!;

        return (left, right) switch
        {
            (ILocalReferenceOperation first, ILocalReferenceOperation second) =>
                SymbolEqualityComparer.Default.Equals(first.Local, second.Local),
            (IParameterReferenceOperation first, IParameterReferenceOperation second) =>
                SymbolEqualityComparer.Default.Equals(first.Parameter, second.Parameter),
            (IFieldReferenceOperation first, IFieldReferenceOperation second) =>
                SymbolEqualityComparer.Default.Equals(first.Field, second.Field) &&
                AreEquivalentReceivers(first.Instance, second.Instance),
            (IEventReferenceOperation first, IEventReferenceOperation second) =>
                SymbolEqualityComparer.Default.Equals(first.Event, second.Event) &&
                AreEquivalentReceivers(first.Instance, second.Instance),
            (IInstanceReferenceOperation, IInstanceReferenceOperation) => true,
            _ => false,
        };
    }

    private static bool AreEquivalentReceivers(IOperation? left, IOperation? right)
    {
        left = Unwrap(left);
        right = Unwrap(right);
        return left is null ? right is null : right is not null && AreEquivalentReferences(left, right);
    }

    private static IOperation? Unwrap(IOperation? operation)
    {
        while (operation is IConversionOperation { IsImplicit: true } conversion)
        {
            operation = conversion.Operand;
        }

        while (operation is IParenthesizedOperation parenthesized)
        {
            operation = parenthesized.Operand;
        }

        return operation;
    }

    private static ExpressionSyntax WalkDownParentheses(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression;
    }

    private static bool HasDirective(IfStatementSyntax ifStatement) =>
        ifStatement.DescendantTrivia(descendIntoTrivia: true).Any(static trivia => trivia.IsDirective);
}
