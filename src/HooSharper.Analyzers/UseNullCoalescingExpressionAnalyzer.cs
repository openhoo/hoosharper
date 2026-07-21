using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace HooSharper.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UseNullCoalescingExpressionAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HOO1014";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Use a null-coalescing expression",
        "Use a null-coalescing expression",
        "HooSharper.CodeStyle",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Replace a conditional null check with the null-coalescing operator.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeConditionalExpression, SyntaxKind.ConditionalExpression);
    }

    private static void AnalyzeConditionalExpression(SyntaxNodeAnalysisContext context)
    {
        var conditional = (ConditionalExpressionSyntax)context.Node;
        if (conditional.ContainsDirectives ||
            !TryGetNullCheck(
                conditional.Condition,
                context.SemanticModel,
                context.CancellationToken,
                out var checkedTarget,
                out var nullWhenTrue))
        {
            return;
        }

        var repeatedTarget = nullWhenTrue ? conditional.WhenFalse : conditional.WhenTrue;
        var fallback = nullWhenTrue ? conditional.WhenTrue : conditional.WhenFalse;
        var checkedOperation = context.SemanticModel.GetOperation(checkedTarget, context.CancellationToken);
        var repeatedOperation = context.SemanticModel.GetOperation(repeatedTarget, context.CancellationToken);
        if (!IsStableSupportedTarget(checkedOperation) ||
            repeatedOperation is null ||
            !AreEquivalentReferences(checkedOperation!, repeatedOperation))
        {
            return;
        }

        var replacement = SyntaxFactory.BinaryExpression(
            SyntaxKind.CoalesceExpression,
            checkedTarget.WithoutTrivia(),
            fallback.WithoutTrivia());
        var originalType = context.SemanticModel.GetTypeInfo(conditional, context.CancellationToken);
        var replacementType = context.SemanticModel.GetSpeculativeTypeInfo(
            conditional.SpanStart,
            replacement,
            SpeculativeBindingOption.BindAsExpression);
        if (!SameType(originalType.Type, replacementType.Type))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, conditional.QuestionToken.GetLocation()));
    }

    private static bool TryGetNullCheck(
        ExpressionSyntax condition,
        SemanticModel semanticModel,
        System.Threading.CancellationToken cancellationToken,
        out ExpressionSyntax target,
        out bool nullWhenTrue)
    {
        condition = WalkDownParentheses(condition);
        if (condition is IsPatternExpressionSyntax isPattern)
        {
            if (isPattern.Pattern is ConstantPatternSyntax
                {
                    Expression.RawKind: (int)SyntaxKind.NullLiteralExpression,
                })
            {
                target = isPattern.Expression;
                nullWhenTrue = true;
                return true;
            }

            if (isPattern.Pattern is UnaryPatternSyntax
                {
                    RawKind: (int)SyntaxKind.NotPattern,
                    Pattern: ConstantPatternSyntax
                    {
                        Expression.RawKind: (int)SyntaxKind.NullLiteralExpression,
                    },
                })
            {
                target = isPattern.Expression;
                nullWhenTrue = false;
                return true;
            }
        }

        if (condition is BinaryExpressionSyntax binary &&
            (binary.IsKind(SyntaxKind.EqualsExpression) || binary.IsKind(SyntaxKind.NotEqualsExpression)) &&
            semanticModel.GetOperation(binary, cancellationToken) is IBinaryOperation { OperatorMethod: null })
        {
            if (binary.Right.IsKind(SyntaxKind.NullLiteralExpression))
            {
                target = binary.Left;
                nullWhenTrue = binary.IsKind(SyntaxKind.EqualsExpression);
                return true;
            }

            if (binary.Left.IsKind(SyntaxKind.NullLiteralExpression))
            {
                target = binary.Right;
                nullWhenTrue = binary.IsKind(SyntaxKind.EqualsExpression);
                return true;
            }
        }

        target = null!;
        nullWhenTrue = false;
        return false;
    }

    private static bool IsStableSupportedTarget(IOperation? operation)
    {
        operation = Unwrap(operation);
        if (operation?.Type is not { TypeKind: not TypeKind.Dynamic } type ||
            !SupportsNullCoalescing(type))
        {
            return false;
        }

        return operation switch
        {
            ILocalReferenceOperation => true,
            IParameterReferenceOperation => true,
            IInstanceReferenceOperation => true,
            IFieldReferenceOperation field =>
                field.Field.IsReadOnly &&
                !field.Field.IsVolatile &&
                IsStableReceiver(field.Instance),
            _ => false,
        };
    }

    private static bool IsStableReceiver(IOperation? operation)
    {
        operation = Unwrap(operation);
        return operation switch
        {
            null => true,
            IInstanceReferenceOperation => true,
            ILocalReferenceOperation => true,
            IParameterReferenceOperation => true,
            IFieldReferenceOperation field =>
                field.Field.IsReadOnly &&
                !field.Field.IsVolatile &&
                IsStableReceiver(field.Instance),
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
        while (true)
        {
            operation = operation switch
            {
                IConversionOperation { IsImplicit: true } conversion => conversion.Operand,
                IParenthesizedOperation parenthesized => parenthesized.Operand,
                _ => operation,
            };

            if (operation is not (IConversionOperation { IsImplicit: true } or IParenthesizedOperation))
            {
                return operation;
            }
        }
    }

    private static bool SupportsNullCoalescing(ITypeSymbol type) =>
        type.IsReferenceType ||
        type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };

    private static bool SameType(ITypeSymbol? first, ITypeSymbol? second) =>
        SymbolEqualityComparer.IncludeNullability.Equals(first, second);

    private static ExpressionSyntax WalkDownParentheses(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression;
    }
}
