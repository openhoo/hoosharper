using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

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
        if (!IsStable(memberAccess.Expression) || !IsStable(key))
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
            if (indexer is not null && IsDictionaryMember(indexer.OriginalDefinition.ContainingType, dictionaryDefinition, dictionaryInterface))
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, memberAccess.Name.GetLocation()));
                return;
            }
        }
    }

    private static bool IsDictionaryMember(
        INamedTypeSymbol containingType,
        INamedTypeSymbol? dictionaryDefinition,
        INamedTypeSymbol? dictionaryInterface) =>
        SymbolEqualityComparer.Default.Equals(containingType.OriginalDefinition, dictionaryDefinition) ||
        SymbolEqualityComparer.Default.Equals(containingType.OriginalDefinition, dictionaryInterface);

    private static bool IsStable(ExpressionSyntax expression)
    {
        expression = WalkDownParentheses(expression);
        return expression switch
        {
            IdentifierNameSyntax or ThisExpressionSyntax or BaseExpressionSyntax or LiteralExpressionSyntax or
                TypeOfExpressionSyntax or DefaultExpressionSyntax => true,
            MemberAccessExpressionSyntax memberAccess when memberAccess.IsKind(SyntaxKind.SimpleMemberAccessExpression) =>
                IsStable(memberAccess.Expression),
            _ => false,
        };
    }

    private static ExpressionSyntax WalkDownParentheses(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression;
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
