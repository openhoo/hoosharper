using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace HooSharper.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UseHashSetAddResultAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HOO1012";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Use the result of HashSet.Add",
        "Use the result of HashSet.Add instead of calling Contains first",
        "HooSharper.CodeStyle",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "HashSet.Add reports whether the value was newly added, avoiding a separate lookup.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static compilationContext =>
        {
            var hashSetDefinition = compilationContext.Compilation.GetTypeByMetadataName(
                "System.Collections.Generic.HashSet`1");
            if (hashSetDefinition is null)
            {
                return;
            }

            compilationContext.RegisterSyntaxNodeAction(
                nodeContext => AnalyzeIfStatement(nodeContext, hashSetDefinition),
                SyntaxKind.IfStatement);
        });
    }

    private static void AnalyzeIfStatement(
        SyntaxNodeAnalysisContext context,
        INamedTypeSymbol hashSetDefinition)
    {
        var ifStatement = (IfStatementSyntax)context.Node;
        if (ifStatement.Else is not null ||
            ifStatement.Statement is not BlockSyntax { Statements.Count: > 0 } block ||
            block.Statements[0] is not ExpressionStatementSyntax
            {
                Expression: InvocationExpressionSyntax addInvocation,
            } ||
            HasDirective(ifStatement) ||
            !TryGetNegatedContains(ifStatement.Condition, out var containsInvocation, out var containsMember) ||
            containsInvocation.ArgumentList.Arguments.Count != 1 ||
            addInvocation.Expression is not MemberAccessExpressionSyntax
            {
                RawKind: (int)SyntaxKind.SimpleMemberAccessExpression,
                Name.Identifier.ValueText: "Add",
            } addMember ||
            addInvocation.ArgumentList.Arguments.Count != 1)
        {
            return;
        }

        var containsValue = containsInvocation.ArgumentList.Arguments[0].Expression;
        var addValue = addInvocation.ArgumentList.Arguments[0].Expression;
        if (!SyntaxFactory.AreEquivalent(containsMember.Expression, addMember.Expression) ||
            !SyntaxFactory.AreEquivalent(containsValue, addValue))
        {
            return;
        }

        var receiverOperation = context.SemanticModel.GetOperation(containsMember.Expression, context.CancellationToken);
        var valueOperation = context.SemanticModel.GetOperation(containsValue, context.CancellationToken);
        if (!IsCallbackStableOperation(receiverOperation) ||
            !IsCallbackStableOperation(valueOperation) ||
            receiverOperation?.Type is not INamedTypeSymbol namedReceiver ||
            !SymbolEqualityComparer.Default.Equals(namedReceiver.OriginalDefinition, hashSetDefinition))
        {
            return;
        }

        var containsOperation = context.SemanticModel.GetOperation(containsInvocation, context.CancellationToken) as IInvocationOperation;
        var addOperation = context.SemanticModel.GetOperation(addInvocation, context.CancellationToken) as IInvocationOperation;
        if (!IsHashSetMethod(containsOperation?.TargetMethod, hashSetDefinition, "Contains") ||
            !IsHashSetMethod(addOperation?.TargetMethod, hashSetDefinition, "Add"))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, containsMember.Name.GetLocation()));
    }

    private static bool TryGetNegatedContains(
        ExpressionSyntax condition,
        out InvocationExpressionSyntax invocation,
        out MemberAccessExpressionSyntax memberAccess)
    {
        condition = WalkDownParentheses(condition);
        if (condition is PrefixUnaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.LogicalNotExpression,
                Operand: var operand,
            })
        {
            operand = WalkDownParentheses(operand);
            if (operand is InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax
                    {
                        RawKind: (int)SyntaxKind.SimpleMemberAccessExpression,
                        Name.Identifier.ValueText: "Contains",
                    } containsMember,
                } containsInvocation)
            {
                invocation = containsInvocation;
                memberAccess = containsMember;
                return true;
            }
        }

        invocation = null!;
        memberAccess = null!;
        return false;
    }

    private static bool IsHashSetMethod(IMethodSymbol? method, INamedTypeSymbol hashSetDefinition, string name) =>
        method is { IsStatic: false, Parameters.Length: 1 } &&
        method.Name == name &&
        SymbolEqualityComparer.Default.Equals(method.OriginalDefinition.ContainingType.OriginalDefinition, hashSetDefinition);


    private static bool IsCallbackStableOperation(IOperation? operation)
    {
        operation = Unwrap(operation);
        return operation switch
        {
            ILiteralOperation => true,
            IDefaultValueOperation => true,
            ITypeOfOperation => true,
            IParameterReferenceOperation => true,
            IInstanceReferenceOperation => true,
            IFieldReferenceOperation field when field.Field.IsReadOnly && !field.Field.IsVolatile =>
                IsCallbackStableOperation(field.Instance),
            _ => false,
        };
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
}
