using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HooSharper.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HooSharper.CodeFixes;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(UseNotPatternCodeFixProvider)), Shared]
public sealed class UseNotPatternCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(UseNotPatternAnalyzer.DiagnosticId);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var diagnostic = context.Diagnostics[0];
        var logicalNot = root?.FindToken(diagnostic.Location.SourceSpan.Start).Parent?
            .AncestorsAndSelf().OfType<PrefixUnaryExpressionSyntax>()
            .FirstOrDefault(expression => expression.IsKind(SyntaxKind.LogicalNotExpression));

        if (!TryGetParts(logicalNot, out _, out _, out _))
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                "Use not pattern",
                cancellationToken => ApplyFixAsync(context.Document, logicalNot!, cancellationToken),
                nameof(UseNotPatternCodeFixProvider)),
            diagnostic);
    }

    private static async Task<Document> ApplyFixAsync(
        Document document,
        PrefixUnaryExpressionSyntax logicalNot,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || !TryGetParts(logicalNot, out var target, out var isKeyword, out var pattern))
        {
            return document;
        }

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (semanticModel is null)
        {
            return document;
        }

        var rewrittenTarget = (ExpressionSyntax)new NestedNotPatternRewriter(semanticModel, cancellationToken).Visit(target)!;
        var replacement = CreatePatternReplacement(logicalNot, rewrittenTarget, isKeyword, pattern);
        return document.WithSyntaxRoot(root.ReplaceNode(logicalNot, replacement));
    }

    private static ExpressionSyntax CreatePatternReplacement(
        PrefixUnaryExpressionSyntax logicalNot,
        ExpressionSyntax target,
        SyntaxToken isKeyword,
        PatternSyntax pattern)
    {
        var parenthesized = (ParenthesizedExpressionSyntax)logicalNot.Operand;
        var removedTokenTrivia = logicalNot.OperatorToken.TrailingTrivia
            .AddRange(parenthesized.OpenParenToken.LeadingTrivia)
            .AddRange(parenthesized.OpenParenToken.TrailingTrivia)
            .AddRange(target.GetLeadingTrivia());
        var notKeyword = SyntaxFactory.Token(SyntaxKind.NotKeyword)
            .WithLeadingTrivia(pattern.GetLeadingTrivia());
        pattern = pattern.WithoutLeadingTrivia().WithoutTrailingTrivia();
        if (pattern is BinaryPatternSyntax or UnaryPatternSyntax)
        {
            pattern = SyntaxFactory.ParenthesizedPattern(pattern);
        }

        var negatedPattern = SyntaxFactory.UnaryPattern(notKeyword, pattern.WithLeadingTrivia(SyntaxFactory.Space));
        var trailingTrivia = EnsureCommentLineBreaks(
            GetOriginalPattern(logicalNot).GetTrailingTrivia()
                .AddRange(parenthesized.CloseParenToken.LeadingTrivia)
                .AddRange(logicalNot.GetTrailingTrivia()));
        return SyntaxFactory.IsPatternExpression(target.WithoutLeadingTrivia(), isKeyword, negatedPattern)
            .WithLeadingTrivia(logicalNot.GetLeadingTrivia().AddRange(removedTokenTrivia))
            .WithTrailingTrivia(trailingTrivia);
    }

    private sealed class NestedNotPatternRewriter(
        SemanticModel semanticModel,
        CancellationToken cancellationToken) : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
        {
            // Check the original tree before visiting: rewritten nodes cannot be
            // queried through the original semantic model.
            var canRewrite = TryGetParts(node, out var originalTarget, out _, out _) &&
                !IsWithinExpressionTree(node) && IsSupportedTypeTest(node, originalTarget);
            var visited = (PrefixUnaryExpressionSyntax)base.VisitPrefixUnaryExpression(node)!;
            return canRewrite && TryGetParts(visited, out var target, out var isKeyword, out var pattern)
                ? CreatePatternReplacement(visited, target, isKeyword, pattern)
                : visited;
        }

        private bool IsWithinExpressionTree(SyntaxNode node)
        {
            var expressionType = semanticModel.Compilation.GetTypeByMetadataName("System.Linq.Expressions.Expression`1");
            var queryableType = semanticModel.Compilation.GetTypeByMetadataName("System.Linq.IQueryable");
            for (var ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                if (expressionType is not null && ancestor is AnonymousFunctionExpressionSyntax lambda &&
                    semanticModel.GetTypeInfo(lambda, cancellationToken).ConvertedType is INamedTypeSymbol lambdaType &&
                    SymbolEqualityComparer.Default.Equals(lambdaType.OriginalDefinition, expressionType))
                {
                    return true;
                }

                if (queryableType is not null && ancestor is QueryExpressionSyntax query &&
                    semanticModel.GetTypeInfo(query, cancellationToken).Type is INamedTypeSymbol queryType &&
                    (SymbolEqualityComparer.Default.Equals(queryType.OriginalDefinition, queryableType) ||
                     queryType.AllInterfaces.Any(type => SymbolEqualityComparer.Default.Equals(
                         type.OriginalDefinition, queryableType))))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsSupportedTypeTest(PrefixUnaryExpressionSyntax node, ExpressionSyntax target)
        {
            var expression = ((ParenthesizedExpressionSyntax)node.Operand).Expression;
            var type = expression switch
            {
                IsPatternExpressionSyntax { Pattern: TypePatternSyntax pattern } => pattern.Type,
                BinaryExpressionSyntax { Right: TypeSyntax testedType } => testedType,
                _ => null,
            };
            if (type is null)
            {
                return true;
            }

            var targetType = semanticModel.GetTypeInfo(target, cancellationToken).Type;
            var matchedType = semanticModel.GetTypeInfo(type, cancellationToken).Type;
            return targetType is null || matchedType is null || !targetType.IsValueType ||
                targetType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T ||
                !SymbolEqualityComparer.Default.Equals(targetType, matchedType);
        }
    }

    private static bool TryGetParts(
        PrefixUnaryExpressionSyntax? logicalNot,
        out ExpressionSyntax target,
        out SyntaxToken isKeyword,
        out PatternSyntax pattern)
    {
        if (logicalNot?.Operand is not ParenthesizedExpressionSyntax { Expression: var expression } ||
            logicalNot.ContainsDirectives)
        {
            target = null!;
            isKeyword = default;
            pattern = null!;
            return false;
        }

        switch (expression)
        {
            case IsPatternExpressionSyntax isPattern when !ContainsDesignation(isPattern.Pattern):
                target = isPattern.Expression;
                isKeyword = isPattern.IsKeyword;
                pattern = isPattern.Pattern;
                return true;
            case BinaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.IsExpression,
                Left: var left,
                OperatorToken: var operatorToken,
                Right: TypeSyntax type,
            }:
                target = left;
                isKeyword = SyntaxFactory.Token(
                    operatorToken.LeadingTrivia,
                    SyntaxKind.IsKeyword,
                    operatorToken.TrailingTrivia);
                pattern = SyntaxFactory.TypePattern(type);
                return true;
            default:
                target = null!;
                isKeyword = default;
                pattern = null!;
                return false;
        }
    }

    private static SyntaxNode GetOriginalPattern(PrefixUnaryExpressionSyntax logicalNot) =>
        ((ParenthesizedExpressionSyntax)logicalNot.Operand).Expression switch
        {
            IsPatternExpressionSyntax isPattern => isPattern.Pattern,
            BinaryExpressionSyntax binary => binary.Right,
            _ => logicalNot,
        };



    private static SyntaxTriviaList EnsureCommentLineBreaks(SyntaxTriviaList trivia)
    {
        var result = new List<SyntaxTrivia>();
        foreach (var item in trivia)
        {
            result.Add(item);
            if (item.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
                item.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia))
            {
                result.Add(SyntaxFactory.ElasticCarriageReturnLineFeed);
            }
        }

        return SyntaxFactory.TriviaList(result);
    }

    private static bool ContainsDesignation(PatternSyntax pattern) =>
        pattern.DescendantNodesAndSelf().Any(node => node is VariableDesignationSyntax);
}
