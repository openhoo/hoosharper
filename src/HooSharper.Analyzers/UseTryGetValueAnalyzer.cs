using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace HooSharper.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UseTryGetValueAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HOO1007";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Use TryGetValue",
        "Use TryGetValue instead of ContainsKey followed by an index access",
        "HooSharper.CodeStyle",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "TryGetValue performs a single dictionary lookup and provides the matching value.");

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
            ifStatement.Condition is not InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax
                {
                    RawKind: (int)SyntaxKind.SimpleMemberAccessExpression,
                    Name.Identifier.ValueText: "ContainsKey",
                } memberAccess,
            } invocation ||
            invocation.ArgumentList.Arguments.Count != 1)
        {
            return;
        }

        var key = invocation.ArgumentList.Arguments[0].Expression;
        if (!IsCallbackStable(memberAccess.Expression, context.SemanticModel, context.CancellationToken) ||
            !IsCallbackStable(key, context.SemanticModel, context.CancellationToken))
        {
            return;
        }

        var dictionaryType = context.SemanticModel.GetTypeInfo(memberAccess.Expression, context.CancellationToken).Type;
        var dictionaryDefinition = context.SemanticModel.Compilation.GetTypeByMetadataName(
            "System.Collections.Generic.Dictionary`2");
        var dictionaryInterface = context.SemanticModel.Compilation.GetTypeByMetadataName(
            "System.Collections.Generic.IDictionary`2");
        if (dictionaryType is not INamedTypeSymbol namedType ||
            (!SymbolEqualityComparer.Default.Equals(namedType.OriginalDefinition, dictionaryDefinition) &&
             !SymbolEqualityComparer.Default.Equals(namedType.OriginalDefinition, dictionaryInterface)))
        {
            return;
        }

        var method = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol as IMethodSymbol;
        if (method is null || !IsDictionaryMember(method.OriginalDefinition.ContainingType, dictionaryDefinition, dictionaryInterface))
        {
            return;
        }

        if (MayMutateLookup(ifStatement.Statement, memberAccess.Expression, key))
        {
            return;
        }

        var foundValueRead = false;
        foreach (var elementAccess in DescendantElementAccesses(ifStatement.Statement))
        {
            if (elementAccess.ArgumentList.Arguments.Count != 1)
            {
                continue;
            }

            var indexedKey = elementAccess.ArgumentList.Arguments[0].Expression;
            if (!SyntaxFactory.AreEquivalent(memberAccess.Expression, elementAccess.Expression) ||
                !SyntaxFactory.AreEquivalent(key, indexedKey))
            {
                continue;
            }

            var indexer = context.SemanticModel.GetSymbolInfo(elementAccess, context.CancellationToken).Symbol as IPropertySymbol;
            if (indexer is null || !IsDictionaryMember(
                    indexer.OriginalDefinition.ContainingType,
                    dictionaryDefinition,
                    dictionaryInterface))
            {
                continue;
            }

            if (!IsValueRead(elementAccess, context.SemanticModel, context.CancellationToken))
            {
                continue;
            }

            foundValueRead = true;
        }

        if (foundValueRead)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, memberAccess.Name.GetLocation()));
        }
    }

    private static bool IsDictionaryMember(
        INamedTypeSymbol containingType,
        INamedTypeSymbol? dictionaryDefinition,
        INamedTypeSymbol? dictionaryInterface) =>
        SymbolEqualityComparer.Default.Equals(containingType.OriginalDefinition, dictionaryDefinition) ||
        SymbolEqualityComparer.Default.Equals(containingType.OriginalDefinition, dictionaryInterface);

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
            ILocalReferenceOperation local when local.Local.IsConst => true,
            IInstanceReferenceOperation => true,
            IFieldReferenceOperation field when
                (field.Field.IsConst || field.Field.IsReadOnly) && !field.Field.IsVolatile =>
                field.Instance is null || IsCallbackStableOperation(field.Instance),
            _ => false,
        };
    }

    private static bool IsValueRead(
        ElementAccessExpressionSyntax elementAccess,
        SemanticModel semanticModel,
        System.Threading.CancellationToken cancellationToken)
    {
        var operation = Unwrap(semanticModel.GetOperation(elementAccess, cancellationToken));
        var parent = operation?.Parent;
        while (parent is IConversionOperation { IsImplicit: true } or IParenthesizedOperation)
        {
            parent = parent.Parent;
        }

        if (parent is ISimpleAssignmentOperation simpleAssignment &&
            ReferenceEquals(Unwrap(simpleAssignment.Target), operation) ||
            parent is ICompoundAssignmentOperation compoundAssignment &&
            ReferenceEquals(Unwrap(compoundAssignment.Target), operation) ||
            parent is IIncrementOrDecrementOperation increment &&
            ReferenceEquals(Unwrap(increment.Target), operation) ||
            parent is IArgumentOperation argument && argument.Parameter?.RefKind != RefKind.None)
        {
            return false;
        }

        for (SyntaxNode? node = elementAccess.Parent; node is not null; node = node.Parent)
        {
            if (node is RefExpressionSyntax)
            {
                return false;
            }

            if (node is ArgumentSyntax argumentSyntax && !argumentSyntax.RefKindKeyword.IsKind(SyntaxKind.None))
            {
                return false;
            }

            if (node is StatementSyntax or ArrowExpressionClauseSyntax)
            {
                break;
            }
        }

        return true;
    }


    private static bool MayMutateLookup(
        StatementSyntax statement,
        ExpressionSyntax dictionary,
        ExpressionSyntax key)
    {
        foreach (var node in statement.DescendantNodes(ShouldDescendInto))
        {
            if (node is AssignmentExpressionSyntax assignment &&
                (IsSameExpression(assignment.Left, dictionary) ||
                 IsSameExpression(assignment.Left, key) ||
                 IsDictionaryIndexer(assignment.Left, dictionary)))
            {
                return true;
            }

            var mutatedOperand = node switch
            {
                PrefixUnaryExpressionSyntax prefix when
                    prefix.IsKind(SyntaxKind.PreIncrementExpression) ||
                    prefix.IsKind(SyntaxKind.PreDecrementExpression) => prefix.Operand,
                PostfixUnaryExpressionSyntax postfix when
                    postfix.IsKind(SyntaxKind.PostIncrementExpression) ||
                    postfix.IsKind(SyntaxKind.PostDecrementExpression) => postfix.Operand,
                _ => null,
            };
            if (mutatedOperand is not null &&
                (IsSameExpression(mutatedOperand, dictionary) ||
                 IsSameExpression(mutatedOperand, key) ||
                 IsDictionaryIndexer(mutatedOperand, dictionary)))
            {
                return true;
            }

            if (node is InvocationExpressionSyntax)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSameExpression(ExpressionSyntax first, ExpressionSyntax second) =>
        SyntaxFactory.AreEquivalent(first, second);

    private static bool IsDictionaryIndexer(
        ExpressionSyntax expression,
        ExpressionSyntax dictionary) =>
        expression is ElementAccessExpressionSyntax elementAccess &&
        IsSameExpression(elementAccess.Expression, dictionary);

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

            if (operation is not IConversionOperation { IsImplicit: true } and not IParenthesizedOperation)
            {
                return operation;
            }
        }
    }

    private static ImmutableArray<ElementAccessExpressionSyntax> DescendantElementAccesses(StatementSyntax statement)
    {
        var builder = ImmutableArray.CreateBuilder<ElementAccessExpressionSyntax>();
        foreach (var node in statement.DescendantNodes(ShouldDescendInto))
        {
            if (node is ElementAccessExpressionSyntax elementAccess)
            {
                builder.Add(elementAccess);
            }
        }

        return builder.ToImmutable();
    }

    private static bool ShouldDescendInto(SyntaxNode node) =>
        node is not AnonymousFunctionExpressionSyntax and
        not LocalFunctionStatementSyntax and
        not TypeDeclarationSyntax;

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
