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

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(SimplifyBooleanComparisonCodeFixProvider)), Shared]
public sealed class SimplifyBooleanComparisonCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(SimplifyBooleanComparisonAnalyzer.DiagnosticId);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var diagnostic = context.Diagnostics[0];
        var comparison = root?.FindToken(diagnostic.Location.SourceSpan.Start).Parent?
            .AncestorsAndSelf().OfType<BinaryExpressionSyntax>().FirstOrDefault();

        if (comparison is null || !SimplifyBooleanComparisonAnalyzer.TryGetBooleanLiteralOperand(comparison, out _))
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                "Simplify boolean comparison",
                cancellationToken => ApplyFixAsync(context.Document, comparison, cancellationToken),
                nameof(SimplifyBooleanComparisonCodeFixProvider)),
            diagnostic);
    }

    private static async Task<Document> ApplyFixAsync(
        Document document,
        BinaryExpressionSyntax comparison,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || semanticModel is null)
        {
            return document;
        }

        var rewritten = new NestedComparisonRewriter(semanticModel, cancellationToken).Visit(comparison);
        return rewritten is null || rewritten == comparison
            ? document
            : document.WithSyntaxRoot(root.ReplaceNode(comparison, rewritten));
    }

    private sealed class NestedComparisonRewriter : CSharpSyntaxRewriter
    {
        private readonly SemanticModel semanticModel;
        private readonly CancellationToken cancellationToken;

        public NestedComparisonRewriter(SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            this.semanticModel = semanticModel;
            this.cancellationToken = cancellationToken;
        }

        public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
        {
            var visited = (BinaryExpressionSyntax)base.VisitBinaryExpression(node)!;
            if (!SimplifyBooleanComparisonAnalyzer.CanSimplify(node, semanticModel, cancellationToken) ||
                !SimplifyBooleanComparisonAnalyzer.TryGetBooleanLiteralOperand(visited, out var expression))
            {
                return visited;
            }

            return SimplifyComparison(visited, expression, semanticModel, node.SpanStart);
        }
    }

    private static ExpressionSyntax SimplifyComparison(
        BinaryExpressionSyntax comparison,
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        int position)
    {
        var literal = expression == comparison.Left ? comparison.Right : comparison.Left;
        var literalValue = literal.IsKind(SyntaxKind.TrueLiteralExpression);
        var preserveValue = comparison.IsKind(SyntaxKind.EqualsExpression) == literalValue;
        var expressionWithoutOuterTrivia = expression.WithoutLeadingTrivia().WithoutTrailingTrivia();
        var replacement = preserveValue
            ? expressionWithoutOuterTrivia
            : Negate(expressionWithoutOuterTrivia, semanticModel, position);
        return replacement.WithLeadingTrivia(comparison.GetLeadingTrivia())
            .WithTrailingTrivia(
                CollectInterstitialTrivia(comparison, expression)
                    .AddRange(comparison.GetTrailingTrivia()));
    }

    private static SyntaxTriviaList CollectInterstitialTrivia(
        BinaryExpressionSyntax comparison,
        ExpressionSyntax expression)
    {
        var trivia = comparison.DescendantTrivia()
            .Where(item =>
                comparison.Span.Contains(item.Span) &&
                !expression.Span.Contains(item.Span) &&
                !item.IsKind(SyntaxKind.WhitespaceTrivia) &&
                !item.IsKind(SyntaxKind.EndOfLineTrivia));
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

    private static ExpressionSyntax Negate(ExpressionSyntax expression, SemanticModel semanticModel, int position)
    {
        // Preserve parenthesis trivia by retaining the original expression whenever
        // any removed token carries a comment.
        if (expression.DescendantTrivia().Any(item =>
            !item.IsKind(SyntaxKind.WhitespaceTrivia) && !item.IsKind(SyntaxKind.EndOfLineTrivia)))
        {
            return SyntaxFactory.PrefixUnaryExpression(
                SyntaxKind.LogicalNotExpression,
                SyntaxFactory.ParenthesizedExpression(expression));
        }

        var unparenthesized = WalkDownParentheses(expression);
        if (unparenthesized is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalNotExpression } logicalNot &&
            SimplifyBooleanComparisonAnalyzer.HasIndependentBooleanType(logicalNot.Operand, semanticModel, position))
        {
            // The operand's parentheses can be required by the surrounding expression.
            return logicalNot.Operand.WithoutLeadingTrivia().WithoutTrailingTrivia();
        }

        return SyntaxFactory.PrefixUnaryExpression(
            SyntaxKind.LogicalNotExpression,
            NeedsParentheses(unparenthesized)
                ? SyntaxFactory.ParenthesizedExpression(unparenthesized.WithoutTrivia())
                : unparenthesized.WithoutTrivia());
    }

    private static ExpressionSyntax WalkDownParentheses(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression;
    }

    private static bool NeedsParentheses(ExpressionSyntax expression) => expression is not (
        IdentifierNameSyntax or
        GenericNameSyntax or
        MemberAccessExpressionSyntax or
        MemberBindingExpressionSyntax or
        InvocationExpressionSyntax or
        ElementAccessExpressionSyntax or
        ElementBindingExpressionSyntax or
        ThisExpressionSyntax or
        BaseExpressionSyntax or
        ObjectCreationExpressionSyntax or
        ImplicitObjectCreationExpressionSyntax or
        ParenthesizedExpressionSyntax or
        PrefixUnaryExpressionSyntax);
}
