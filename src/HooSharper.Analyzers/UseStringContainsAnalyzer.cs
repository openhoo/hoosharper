using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace HooSharper.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UseStringContainsAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HOO1016";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Use string.Contains",
        "Use string.Contains instead of testing the result of IndexOf",
        "HooSharper.CodeStyle",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Contains expresses a string presence test directly when the exact IndexOf position is not used.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(
            AnalyzeComparison,
            SyntaxKind.EqualsExpression,
            SyntaxKind.NotEqualsExpression,
            SyntaxKind.LessThanExpression,
            SyntaxKind.LessThanOrEqualExpression,
            SyntaxKind.GreaterThanExpression,
            SyntaxKind.GreaterThanOrEqualExpression);
    }

    private static void AnalyzeComparison(SyntaxNodeAnalysisContext context)
    {
        var comparison = (BinaryExpressionSyntax)context.Node;
        if (HasDirective(comparison) ||
            !TryGetIndexOfInvocation(comparison, context.SemanticModel, context.CancellationToken, out var invocation) ||
            context.SemanticModel.GetOperation(comparison, context.CancellationToken) is not IBinaryOperation
            {
                OperatorMethod: null,
                Type.SpecialType: SpecialType.System_Boolean,
            } binaryOperation ||
            (comparison.Left == invocation ? binaryOperation.LeftOperand : binaryOperation.RightOperand) is not IInvocationOperation
            {
                TargetMethod:
                {
                    Name: "IndexOf",
                    IsStatic: false,
                    ContainingType.SpecialType: SpecialType.System_String,
                } indexOfMethod,
            } ||
            !HasMatchingContainsOverload(indexOfMethod))
        {
            return;
        }

        var name = ((MemberAccessExpressionSyntax)invocation.Expression).Name;
        context.ReportDiagnostic(Diagnostic.Create(Rule, name.GetLocation()));
    }

    private static bool TryGetIndexOfInvocation(
        BinaryExpressionSyntax comparison,
        SemanticModel semanticModel,
        System.Threading.CancellationToken cancellationToken,
        out InvocationExpressionSyntax invocation)
    {
        if (comparison.Left is InvocationExpressionSyntax leftInvocation &&
            TryGetIntConstant(comparison.Right, semanticModel, cancellationToken, out var rightValue) &&
            IsPresenceTest(comparison.Kind(), rightValue, invocationOnLeft: true))
        {
            invocation = leftInvocation;
            return IsIndexOfSyntax(invocation);
        }

        if (comparison.Right is InvocationExpressionSyntax rightInvocation &&
            TryGetIntConstant(comparison.Left, semanticModel, cancellationToken, out var leftValue) &&
            IsPresenceTest(comparison.Kind(), leftValue, invocationOnLeft: false))
        {
            invocation = rightInvocation;
            return IsIndexOfSyntax(invocation);
        }

        invocation = null!;
        return false;
    }

    private static bool TryGetIntConstant(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        System.Threading.CancellationToken cancellationToken,
        out int value)
    {
        var constant = semanticModel.GetConstantValue(expression, cancellationToken);
        if (constant.HasValue && constant.Value is int intValue)
        {
            value = intValue;
            return true;
        }

        value = default;
        return false;
    }

    private static bool IsIndexOfSyntax(InvocationExpressionSyntax invocation) =>
        invocation.Expression is MemberAccessExpressionSyntax
        {
            RawKind: (int)SyntaxKind.SimpleMemberAccessExpression,
            Name.Identifier.ValueText: "IndexOf",
        };

    private static bool IsPresenceTest(SyntaxKind kind, int constant, bool invocationOnLeft) =>
        (invocationOnLeft, kind, constant) switch
        {
            (true, SyntaxKind.GreaterThanOrEqualExpression, 0) => true,
            (true, SyntaxKind.GreaterThanExpression, -1) => true,
            (true, SyntaxKind.NotEqualsExpression, -1) => true,
            (true, SyntaxKind.LessThanExpression, 0) => true,
            (true, SyntaxKind.LessThanOrEqualExpression, -1) => true,
            (true, SyntaxKind.EqualsExpression, -1) => true,
            (false, SyntaxKind.LessThanOrEqualExpression, 0) => true,
            (false, SyntaxKind.LessThanExpression, -1) => true,
            (false, SyntaxKind.NotEqualsExpression, -1) => true,
            (false, SyntaxKind.GreaterThanExpression, 0) => true,
            (false, SyntaxKind.GreaterThanOrEqualExpression, -1) => true,
            (false, SyntaxKind.EqualsExpression, -1) => true,
            _ => false,
        };

    private static bool HasMatchingContainsOverload(IMethodSymbol indexOfMethod)
    {
        foreach (var member in indexOfMethod.ContainingType.GetMembers("Contains"))
        {
            if (member is not IMethodSymbol { IsStatic: false } containsMethod ||
                containsMethod.Parameters.Length != indexOfMethod.Parameters.Length)
            {
                continue;
            }

            var matches = true;
            for (var index = 0; index < indexOfMethod.Parameters.Length; index++)
            {
                if (indexOfMethod.Parameters[index].RefKind != containsMethod.Parameters[index].RefKind ||
                    !SymbolEqualityComparer.Default.Equals(
                        indexOfMethod.Parameters[index].Type,
                        containsMethod.Parameters[index].Type))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasDirective(BinaryExpressionSyntax comparison)
    {
        foreach (var trivia in comparison.DescendantTrivia(descendIntoTrivia: true))
        {
            if (trivia.IsDirective)
            {
                return true;
            }
        }

        return false;
    }
}
