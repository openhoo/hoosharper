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

        var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        if (logicalNot is null || semanticModel is null ||
            !new NestedNotPatternRewriter(semanticModel, context.CancellationToken).CanRewrite(logicalNot))
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                "Simplify negated pattern",
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

        var rewriter = new NestedNotPatternRewriter(semanticModel, cancellationToken);
        if (!rewriter.CanRewrite(logicalNot))
        {
            return document;
        }

        var rewrittenTarget = (ExpressionSyntax)rewriter.Visit(target)!;
        var replacement = CreatePatternReplacement(logicalNot, rewrittenTarget, isKeyword, pattern, logicalNot.Parent);
        return document.WithSyntaxRoot(root.ReplaceNode(logicalNot, replacement));
    }

    private static ExpressionSyntax CreatePatternReplacement(
        PrefixUnaryExpressionSyntax logicalNot,
        ExpressionSyntax target,
        SyntaxToken isKeyword,
        PatternSyntax pattern,
        SyntaxNode? parent)
    {
        var parenthesized = (ParenthesizedExpressionSyntax)logicalNot.Operand;
        var removedTokenTrivia = logicalNot.OperatorToken.TrailingTrivia
            .AddRange(parenthesized.OpenParenToken.LeadingTrivia)
            .AddRange(parenthesized.OpenParenToken.TrailingTrivia)
            .AddRange(target.GetLeadingTrivia());
        var trailingTrivia = EnsureCommentLineBreaks(
            GetOriginalPattern(logicalNot).GetTrailingTrivia()
                .AddRange(parenthesized.CloseParenToken.LeadingTrivia)
                .AddRange(logicalNot.GetTrailingTrivia()));
        var negatedPattern = NegatePattern(pattern, ref isKeyword, ref trailingTrivia);
        ExpressionSyntax replacement = SyntaxFactory.IsPatternExpression(target.WithoutLeadingTrivia(), isKeyword, negatedPattern);
        if (negatedPattern is TypePatternSyntax or ConstantPatternSyntax { Expression: NameSyntax or MemberAccessExpressionSyntax })
        {
            // The same bare name can denote a type or a constant. Roslyn parses
            // "x is T" differently from "x is not T"; keep the editor tree in
            // the same shape it will have when the document is reopened.
            var parsed = SyntaxFactory.ParseExpression("x is " + negatedPattern.ToFullString(), options: logicalNot.SyntaxTree.Options);
            if (parsed is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.IsExpression } typeTest)
                replacement = typeTest.WithLeft(target.WithoutLeadingTrivia()).WithOperatorToken(isKeyword);
        }
        // "is" binds less tightly than a cast or unary operator. Preserve the
        // original grouping in expression contexts unless they already delimit it.
        if (parent is ExpressionSyntax and not (ParenthesizedExpressionSyntax or AssignmentExpressionSyntax or
                ConditionalExpressionSyntax or LambdaExpressionSyntax) &&
            parent.Kind() is not (SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression or
                SyntaxKind.BitwiseAndExpression or SyntaxKind.BitwiseOrExpression or SyntaxKind.ExclusiveOrExpression or
                SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression or SyntaxKind.CoalesceExpression))
        {
            replacement = SyntaxFactory.ParenthesizedExpression(replacement);
        }

        return replacement.WithLeadingTrivia(logicalNot.GetLeadingTrivia().AddRange(removedTokenTrivia))
            .WithTrailingTrivia(trailingTrivia);
    }

    private static PatternSyntax NegatePattern(PatternSyntax pattern, ref SyntaxToken isKeyword, ref SyntaxTriviaList trailing)
    {
        var inner = pattern;
        var negations = 0;
        while (true)
        {
            if (inner is ParenthesizedPatternSyntax group)
                inner = group.Pattern;
            else if (inner is UnaryPatternSyntax unary && unary.IsKind(SyntaxKind.NotPattern))
            {
                negations++;
                inner = unary.Pattern;
            }
            else
                break;
        }

        if (negations == 0)
        {
            var keyword = SyntaxFactory.Token(SyntaxKind.NotKeyword).WithLeadingTrivia(pattern.GetLeadingTrivia());
            return AddNot(keyword, pattern.WithoutTrivia());
        }

        // Drop pairs of negations, retaining comments attached to the removed
        // keywords or parentheses on the corresponding side of the pattern.
        var prefix = MergeTrivia(isKeyword.TrailingTrivia,
            pattern.DescendantTrivia().Where(t => t.Span.End <= inner.SpanStart));
        isKeyword = isKeyword.WithTrailingTrivia(prefix.Count == 0 ? SyntaxFactory.TriviaList(SyntaxFactory.Space) : prefix);
        var suffix = pattern.DescendantTrivia().Where(t => t.SpanStart >= inner.Span.End && t.SpanStart < pattern.Span.End);
        trailing = MergeTrivia(SyntaxFactory.TriviaList(suffix), trailing);
        return negations % 2 == 1 ? inner.WithoutTrivia() : AddNot(SyntaxFactory.Token(SyntaxKind.NotKeyword), inner.WithoutTrivia());
    }

    private static UnaryPatternSyntax AddNot(SyntaxToken keyword, PatternSyntax operand)
    {
        if (operand is BinaryPatternSyntax)
            operand = SyntaxFactory.ParenthesizedPattern(operand);
        return SyntaxFactory.UnaryPattern(keyword, operand.WithLeadingTrivia(SyntaxFactory.Space));
    }

    private static SyntaxTriviaList MergeTrivia(SyntaxTriviaList left, IEnumerable<SyntaxTrivia> right)
    {
        foreach (var item in right)
        {
            if (!item.IsKind(SyntaxKind.WhitespaceTrivia) || !left.LastOrDefault().IsKind(SyntaxKind.WhitespaceTrivia))
                left = left.Add(item);
        }
        return left;
    }

    private sealed class NestedNotPatternRewriter(
        SemanticModel semanticModel,
        CancellationToken cancellationToken) : CSharpSyntaxRewriter
    {
        internal bool CanRewrite(PrefixUnaryExpressionSyntax node) =>
            UseNotPatternAnalyzer.CanRewrite(node, semanticModel, cancellationToken);

        public override SyntaxNode? VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
        {
            // Check the original tree before visiting: rewritten nodes cannot be
            // queried through the original semantic model.
            var canRewrite = CanRewrite(node);
            var visited = (PrefixUnaryExpressionSyntax)base.VisitPrefixUnaryExpression(node)!;
            return canRewrite && TryGetParts(visited, out var target, out var isKeyword, out var pattern)
                ? CreatePatternReplacement(visited, target, isKeyword, pattern, node.Parent)
                : visited;
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
            case IsPatternExpressionSyntax isPattern when UseNotPatternAnalyzer.IsSupportedPattern(isPattern.Pattern):
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
}
