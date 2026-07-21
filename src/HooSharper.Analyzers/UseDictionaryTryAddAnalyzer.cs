using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace HooSharper.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UseDictionaryTryAddAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HOO1011";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Use Dictionary.TryAdd",
        "Use TryAdd instead of ContainsKey followed by Add",
        "HooSharper.CodeStyle",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Dictionary.TryAdd performs the existence check and insertion in one operation.");

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
        if (ifStatement.Else is not null || HasDirective(ifStatement) ||
            ifStatement.Condition is not PrefixUnaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.LogicalNotExpression,
                Operand: InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax
                    {
                        RawKind: (int)SyntaxKind.SimpleMemberAccessExpression,
                        Name.Identifier.ValueText: "ContainsKey",
                    } containsMember,
                } containsInvocation,
            } ||
            containsInvocation.ArgumentList.Arguments.Count != 1 ||
            ifStatement.Statement is not BlockSyntax { Statements.Count: > 0 } block ||
            block.Statements[0] is not ExpressionStatementSyntax
            {
                Expression: InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax
                    {
                        RawKind: (int)SyntaxKind.SimpleMemberAccessExpression,
                        Name.Identifier.ValueText: "Add",
                    } addMember,
                } addInvocation,
            } ||
            addInvocation.ArgumentList.Arguments.Count != 2)
        {
            return;
        }

        var containsKey = containsInvocation.ArgumentList.Arguments[0].Expression;
        var addKey = addInvocation.ArgumentList.Arguments[0].Expression;
        var value = addInvocation.ArgumentList.Arguments[1].Expression;
        if (!IsCallbackStable(containsMember.Expression, context.SemanticModel, context.CancellationToken) ||
            !IsCallbackStable(containsKey, context.SemanticModel, context.CancellationToken) ||
            !SyntaxFactory.AreEquivalent(containsMember.Expression, addMember.Expression) ||
            !SyntaxFactory.AreEquivalent(containsKey, addKey) ||
            !IsCallbackStable(value, context.SemanticModel, context.CancellationToken) ||
            !IsSideEffectFree(context.SemanticModel.GetOperation(value, context.CancellationToken)))
        {
            return;
        }

        var dictionaryDefinition = context.SemanticModel.Compilation.GetTypeByMetadataName(
            "System.Collections.Generic.Dictionary`2");
        var receiverType = context.SemanticModel.GetTypeInfo(containsMember.Expression, context.CancellationToken).Type;
        if (dictionaryDefinition is null || receiverType is not INamedTypeSymbol receiverNamedType ||
            !SymbolEqualityComparer.Default.Equals(receiverNamedType.OriginalDefinition, dictionaryDefinition))
        {
            return;
        }

        var containsMethod = context.SemanticModel.GetSymbolInfo(containsInvocation, context.CancellationToken).Symbol as IMethodSymbol;
        var addMethod = context.SemanticModel.GetSymbolInfo(addInvocation, context.CancellationToken).Symbol as IMethodSymbol;
        if (!IsDictionaryMethod(containsMethod, dictionaryDefinition, "ContainsKey", 1) ||
            !IsDictionaryMethod(addMethod, dictionaryDefinition, "Add", 2) ||
            !HasSuitableTryAdd(receiverNamedType))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, containsMember.Name.GetLocation()));
    }

    private static bool IsDictionaryMethod(
        IMethodSymbol? method,
        INamedTypeSymbol dictionaryDefinition,
        string name,
        int parameterCount) =>
        method is { IsStatic: false } &&
        method.Name == name &&
        method.Parameters.Length == parameterCount &&
        SymbolEqualityComparer.Default.Equals(method.OriginalDefinition.ContainingType.OriginalDefinition, dictionaryDefinition);

    private static bool HasSuitableTryAdd(INamedTypeSymbol dictionaryType)
    {
        foreach (var member in dictionaryType.GetMembers("TryAdd"))
        {
            if (member is IMethodSymbol
                {
                    IsStatic: false,
                    DeclaredAccessibility: Accessibility.Public,
                    Parameters.Length: 2,
                    ReturnType.SpecialType: SpecialType.System_Boolean,
                } method &&
                SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, dictionaryType.TypeArguments[0]) &&
                SymbolEqualityComparer.Default.Equals(method.Parameters[1].Type, dictionaryType.TypeArguments[1]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCallbackStable(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        System.Threading.CancellationToken cancellationToken) =>
        IsCallbackStableOperation(semanticModel.GetOperation(expression, cancellationToken));

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

    private static bool IsSideEffectFree(IOperation? operation) => operation switch
    {
        ILiteralOperation => true,
        ILocalReferenceOperation => true,
        IParameterReferenceOperation => true,
        IInstanceReferenceOperation => true,
        IDefaultValueOperation => true,
        ITypeOfOperation => true,
        IFieldReferenceOperation fieldReference when !fieldReference.Field.IsVolatile &&
            (fieldReference.Instance is null or IInstanceReferenceOperation) => true,
        IConversionOperation conversion when conversion.OperatorMethod is null => IsSideEffectFree(conversion.Operand),
        IParenthesizedOperation parenthesized => IsSideEffectFree(parenthesized.Operand),
        _ => false,
    };

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
