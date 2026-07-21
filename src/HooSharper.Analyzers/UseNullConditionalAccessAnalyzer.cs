using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace HooSharper.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UseNullConditionalAccessAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HOO1015";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Use null-conditional access",
        "Use null-conditional access",
        "HooSharper.CodeStyle",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Replace a conditional null check and immediate member access with null-conditional access.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static compilationContext =>
        {
            var expressionType = compilationContext.Compilation.GetTypeByMetadataName(
                "System.Linq.Expressions.Expression`1");
            compilationContext.RegisterSyntaxNodeAction(
                nodeContext => AnalyzeConditionalExpression(nodeContext, expressionType),
                SyntaxKind.ConditionalExpression);
        });
    }

    private static void AnalyzeConditionalExpression(
        SyntaxNodeAnalysisContext context,
        INamedTypeSymbol? expressionType)
    {
        var conditional = (ConditionalExpressionSyntax)context.Node;
        if (context.Node.SyntaxTree.Options is not CSharpParseOptions { LanguageVersion: >= LanguageVersion.CSharp6 } ||
            HasDirective(conditional) ||
            !HasCandidateShape(conditional) ||
            IsWithinExpressionTree(conditional, context.SemanticModel, expressionType, context.CancellationToken) ||
            !TryGetCandidate(conditional, context.SemanticModel, context.CancellationToken,
                out var receiver, out var access))
        {
            return;
        }

        var receiverOperation = Unwrap(context.SemanticModel.GetOperation(receiver, context.CancellationToken));
        if (!IsStableReceiver(receiverOperation) || receiverOperation?.Type is null or { TypeKind: TypeKind.Dynamic })
        {
            return;
        }

        if (!IsSupportedAccess(access, receiverOperation, context.SemanticModel, context.CancellationToken))
        {
            return;
        }

        var replacement = CreateReplacement(access);
        var originalType = context.SemanticModel.GetTypeInfo(conditional, context.CancellationToken);
        var replacementType = context.SemanticModel.GetSpeculativeTypeInfo(
            conditional.SpanStart,
            replacement,
            SpeculativeBindingOption.BindAsExpression);
        if (!SymbolEqualityComparer.Default.Equals(originalType.ConvertedType, replacementType.ConvertedType))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, conditional.QuestionToken.GetLocation()));
    }

    public static bool TryGetCandidate(
        ConditionalExpressionSyntax conditional,
        SemanticModel semanticModel,
        System.Threading.CancellationToken cancellationToken,
        out ExpressionSyntax receiver,
        out ExpressionSyntax access)
    {
        if (!TryGetNullTest(conditional.Condition, semanticModel, cancellationToken,
                out var testedReceiver, out var trueWhenNull))
        {
            receiver = null!;
            access = null!;
            return false;
        }

        var nullArm = trueWhenNull ? conditional.WhenTrue : conditional.WhenFalse;
        var accessArm = trueWhenNull ? conditional.WhenFalse : conditional.WhenTrue;
        if (!IsNullLiteral(nullArm) || !TryGetAccessReceiver(accessArm, out var accessedReceiver))
        {
            receiver = null!;
            access = null!;
            return false;
        }

        var testedOperation = contextOperation(semanticModel, testedReceiver, cancellationToken);
        var accessedOperation = contextOperation(semanticModel, accessedReceiver, cancellationToken);
        if (testedOperation is null || accessedOperation is null ||
            !AreEquivalentReferences(testedOperation, accessedOperation))
        {
            receiver = null!;
            access = null!;
            return false;
        }

        receiver = accessedReceiver;
        access = WalkDownParentheses(accessArm);
        return true;

        static IOperation? contextOperation(
            SemanticModel model,
            ExpressionSyntax expression,
            System.Threading.CancellationToken token) => Unwrap(model.GetOperation(expression, token));
    }

    private static bool TryGetNullTest(
        ExpressionSyntax condition,
        SemanticModel semanticModel,
        System.Threading.CancellationToken cancellationToken,
        out ExpressionSyntax receiver,
        out bool trueWhenNull)
    {
        condition = WalkDownParentheses(condition);
        if (condition is IsPatternExpressionSyntax { Expression: var expression, Pattern: var pattern })
        {
            if (pattern is ConstantPatternSyntax { Expression: LiteralExpressionSyntax literal } &&
                literal.IsKind(SyntaxKind.NullLiteralExpression))
            {
                receiver = expression;
                trueWhenNull = true;
                return true;
            }

            if (pattern is UnaryPatternSyntax
                {
                    RawKind: (int)SyntaxKind.NotPattern,
                    Pattern: ConstantPatternSyntax { Expression: LiteralExpressionSyntax notLiteral },
                } && notLiteral.IsKind(SyntaxKind.NullLiteralExpression))
            {
                receiver = expression;
                trueWhenNull = false;
                return true;
            }
        }

        if (condition is BinaryExpressionSyntax comparison &&
            (comparison.IsKind(SyntaxKind.EqualsExpression) ||
             comparison.IsKind(SyntaxKind.NotEqualsExpression)))
        {
            if (semanticModel.GetOperation(comparison, cancellationToken) is IBinaryOperation { OperatorMethod: null } &&
                TryGetNullAndReceiver(comparison, out receiver))
            {
                trueWhenNull = comparison.IsKind(SyntaxKind.EqualsExpression);
                return true;
            }
        }

        receiver = null!;
        trueWhenNull = false;
        return false;
    }

    private static bool TryGetNullAndReceiver(BinaryExpressionSyntax comparison, out ExpressionSyntax receiver)
    {
        if (IsNullLiteral(comparison.Left))
        {
            receiver = comparison.Right;
            return true;
        }

        if (IsNullLiteral(comparison.Right))
        {
            receiver = comparison.Left;
            return true;
        }

        receiver = null!;
        return false;
    }

    private static bool TryGetAccessReceiver(ExpressionSyntax expression, out ExpressionSyntax receiver)
    {
        expression = WalkDownParentheses(expression);
        if (expression is MemberAccessExpressionSyntax
            {
                RawKind: (int)SyntaxKind.SimpleMemberAccessExpression,
                Expression: var memberReceiver,
            })
        {
            receiver = memberReceiver;
            return true;
        }

        if (expression is InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax
                {
                    RawKind: (int)SyntaxKind.SimpleMemberAccessExpression,
                    Expression: var invocationReceiver,
                },
            })
        {
            receiver = invocationReceiver;
            return true;
        }

        receiver = null!;
        return false;
    }

    private static bool IsSupportedAccess(
        ExpressionSyntax access,
        IOperation receiverOperation,
        SemanticModel semanticModel,
        System.Threading.CancellationToken cancellationToken)
    {
        var operation = Unwrap(semanticModel.GetOperation(access, cancellationToken));
        return operation switch
        {
            IFieldReferenceOperation field => AreEquivalentReceivers(receiverOperation, field.Instance),
            IPropertyReferenceOperation property => AreEquivalentReceivers(receiverOperation, property.Instance),
            IInvocationOperation invocation => invocation.TargetMethod.ReducedFrom is null &&
                !invocation.TargetMethod.IsStatic &&
                AreEquivalentReceivers(receiverOperation, invocation.Instance),
            _ => false,
        };
    }

    public static ExpressionSyntax CreateReplacement(ExpressionSyntax access)
    {
        access = WalkDownParentheses(access);
        if (access is InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax memberAccess,
            } invocation)
        {
            var binding = SyntaxFactory.MemberBindingExpression(
                memberAccess.OperatorToken,
                memberAccess.Name);
            return SyntaxFactory.ConditionalAccessExpression(
                memberAccess.Expression,
                invocation.WithExpression(binding));
        }

        var member = (MemberAccessExpressionSyntax)access;
        return SyntaxFactory.ConditionalAccessExpression(
            member.Expression,
            SyntaxFactory.MemberBindingExpression(member.OperatorToken, member.Name));
    }

    private static bool IsStableReceiver(IOperation? operation)
    {
        operation = Unwrap(operation);
        return operation switch
        {
            ILocalReferenceOperation => true,
            IParameterReferenceOperation => true,
            IFieldReferenceOperation field when field.Field.IsReadOnly && !field.Field.IsVolatile =>
                field.Instance is null || IsStableReceiver(field.Instance),
            IInstanceReferenceOperation => true,
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

    private static bool IsWithinExpressionTree(
        SyntaxNode node,
        SemanticModel semanticModel,
        INamedTypeSymbol? expressionType,
        System.Threading.CancellationToken cancellationToken)
    {
        if (expressionType is null)
        {
            return false;
        }
        for (var ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor is AnonymousFunctionExpressionSyntax anonymousFunction &&
                semanticModel.GetTypeInfo(anonymousFunction, cancellationToken).ConvertedType is
                    INamedTypeSymbol convertedType &&
                SymbolEqualityComparer.Default.Equals(convertedType.OriginalDefinition, expressionType))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasCandidateShape(ConditionalExpressionSyntax conditional)
    {
        var condition = WalkDownParentheses(conditional.Condition);
        if (condition is not IsPatternExpressionSyntax &&
            condition is not BinaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.EqualsExpression or (int)SyntaxKind.NotEqualsExpression,
            })
        {
            return false;
        }

        return IsNullLiteral(conditional.WhenTrue) && TryGetAccessReceiver(conditional.WhenFalse, out _) ||
            IsNullLiteral(conditional.WhenFalse) && TryGetAccessReceiver(conditional.WhenTrue, out _);
    }

    private static bool IsNullLiteral(ExpressionSyntax expression) =>
        WalkDownParentheses(expression).IsKind(SyntaxKind.NullLiteralExpression);

    private static ExpressionSyntax WalkDownParentheses(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression;
    }

    private static bool HasDirective(ConditionalExpressionSyntax conditional)
    {
        foreach (var trivia in conditional.DescendantTrivia(descendIntoTrivia: true))
        {
            if (trivia.IsDirective)
            {
                return true;
            }
        }

        return false;
    }
}
