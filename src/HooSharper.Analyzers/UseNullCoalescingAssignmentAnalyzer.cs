using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace HooSharper.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UseNullCoalescingAssignmentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HOO1008";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Use a null-coalescing assignment",
        "Use a null-coalescing assignment",
        "HooSharper.CodeStyle",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Replace a null check followed by an assignment with the null-coalescing assignment operator.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeIfStatement, SyntaxKind.IfStatement);
    }

    private static void AnalyzeIfStatement(SyntaxNodeAnalysisContext context)
    {
        if (context.Node.SyntaxTree.Options is not CSharpParseOptions parseOptions ||
            (parseOptions.LanguageVersion != LanguageVersion.Default &&
             parseOptions.LanguageVersion < LanguageVersion.CSharp8))
        {
            return;
        }

        var ifStatement = (IfStatementSyntax)context.Node;
        if (ifStatement.Else is not null ||
            ifStatement.Statement is not BlockSyntax { Statements.Count: 1 } block ||
            block.Statements[0] is not ExpressionStatementSyntax
            {
                Expression: AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.SimpleAssignmentExpression } assignment,
            } ||
            HasDirective(ifStatement) ||
            HasUnpreservedComment(ifStatement.Condition))
        {
            return;
        }

        if (!TryGetNullCheckedTarget(ifStatement.Condition, context.SemanticModel, context.CancellationToken,
                out var checkedTarget, out var diagnosticLocation))
        {
            return;
        }

        if (!TryGetSupportedTargetOperation(checkedTarget, context.SemanticModel, context.CancellationToken,
                out var checkedOperation) ||
            !TryGetSupportedTargetOperation(assignment.Left, context.SemanticModel, context.CancellationToken,
                out var assignedOperation) ||
            !AreEquivalentReferences(checkedOperation, assignedOperation) ||
            !SupportsNullCoalescingAssignment(assignedOperation.Type))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, diagnosticLocation));
    }

    private static bool TryGetNullCheckedTarget(
        ExpressionSyntax condition,
        SemanticModel semanticModel,
        System.Threading.CancellationToken cancellationToken,
        out ExpressionSyntax target,
        out Location diagnosticLocation)
    {
        condition = WalkDownParentheses(condition);

        if (condition is IsPatternExpressionSyntax
            {
                Expression: var patternTarget,
                Pattern: ConstantPatternSyntax { Expression: LiteralExpressionSyntax literal },
            } isPattern && literal.IsKind(SyntaxKind.NullLiteralExpression))
        {
            target = patternTarget;
            diagnosticLocation = isPattern.IsKeyword.GetLocation();
            return true;
        }

        if (condition is BinaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.EqualsExpression,
                Left: var binaryTarget,
                Right: LiteralExpressionSyntax nullLiteral,
            } binary &&
            nullLiteral.IsKind(SyntaxKind.NullLiteralExpression) &&
            semanticModel.GetOperation(binary, cancellationToken) is IBinaryOperation { OperatorMethod: null })
        {
            target = binaryTarget;
            diagnosticLocation = binary.OperatorToken.GetLocation();
            return true;
        }

        target = null!;
        diagnosticLocation = Location.None;
        return false;
    }

    private static bool TryGetSupportedTargetOperation(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        System.Threading.CancellationToken cancellationToken,
        out IOperation operation)
    {
        expression = WalkDownParentheses(expression);
        if (expression is not (IdentifierNameSyntax or MemberAccessExpressionSyntax
            {
                RawKind: (int)SyntaxKind.SimpleMemberAccessExpression,
            }))
        {
            operation = null!;
            return false;
        }

        operation = semanticModel.GetOperation(expression, cancellationToken)!;
        return operation is { Type.TypeKind: not TypeKind.Dynamic } && IsSupportedTargetOperation(operation);
    }

    private static bool IsSupportedTargetOperation(IOperation? operation)
    {
        operation = Unwrap(operation);
        return operation switch
        {
            ILocalReferenceOperation => true,
            IParameterReferenceOperation => true,
            IFieldReferenceOperation field => IsStableReceiver(field.Instance),
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
            (IPropertyReferenceOperation first, IPropertyReferenceOperation second) =>
                SymbolEqualityComparer.Default.Equals(first.Property, second.Property) &&
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

    private static bool SupportsNullCoalescingAssignment(ITypeSymbol? type) =>
        type is { IsReferenceType: true } ||
        type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };

    private static ExpressionSyntax WalkDownParentheses(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression;
    }

    private static bool HasDirective(IfStatementSyntax ifStatement)
    {
        foreach (var trivia in ifStatement.DescendantTrivia(descendIntoTrivia: true))
        {
            if (trivia.IsDirective)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasUnpreservedComment(SyntaxNode node)
    {
        foreach (var trivia in node.DescendantTrivia(descendIntoTrivia: true))
        {
            if (trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            {
                return true;
            }

            if (trivia.SpanStart < node.SpanStart ||
                trivia.Span.End > node.Span.End)
            {
                continue;
            }

            if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
                trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
            {
                return true;
            }
        }

        return false;
    }

}
