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
using Microsoft.CodeAnalysis.Formatting;

namespace HooSharper.CodeFixes;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(UseNullCoalescingExpressionCodeFixProvider)), Shared]
public sealed class UseNullCoalescingExpressionCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(UseNullCoalescingExpressionAnalyzer.DiagnosticId);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var diagnostic = context.Diagnostics[0];
        var conditional = root?.FindToken(diagnostic.Location.SourceSpan.Start).Parent?
            .AncestorsAndSelf().OfType<ConditionalExpressionSyntax>().FirstOrDefault();
        if (conditional is null)
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                "Use ?? expression",
                cancellationToken => ApplyFixAsync(context.Document, conditional, cancellationToken),
                nameof(UseNullCoalescingExpressionCodeFixProvider)),
            diagnostic);
    }

    private static async Task<Document> ApplyFixAsync(
        Document document,
        ConditionalExpressionSyntax conditional,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || semanticModel is null ||
            !UseNullCoalescingExpressionAnalyzer.TryGetReplacementOperands(
                conditional, semanticModel, cancellationToken, out var target, out var fallback))
        {
            return document;
        }

        var comments = conditional.DescendantTrivia(descendIntoTrivia: true)
            .Where(trivia =>
                CodeFixTrivia.IsComment(trivia) &&
                !target.FullSpan.Contains(trivia.Span) &&
                !fallback.Span.Contains(trivia.Span))
            .ToArray();
        var operatorToken = SyntaxFactory.Token(SyntaxKind.QuestionQuestionToken);
        if (comments.Length > 0)
        {
            var trailing = SyntaxFactory.TriviaList(SyntaxFactory.Space);
            foreach (var comment in comments)
            {
                trailing = trailing.Add(comment).Add(SyntaxFactory.Space);
            }

            operatorToken = operatorToken.WithTrailingTrivia(trailing);
        }
        var replacement = SyntaxFactory.BinaryExpression(
                SyntaxKind.CoalesceExpression,
                target,
                operatorToken,
                PrepareFallback(fallback))
            .WithLeadingTrivia(conditional.GetLeadingTrivia())
            .WithTrailingTrivia(conditional.GetTrailingTrivia())
            .WithAdditionalAnnotations(Formatter.Annotation);

        return document.WithSyntaxRoot(root.ReplaceNode(conditional, replacement));
    }

    private static ExpressionSyntax PrepareFallback(ExpressionSyntax fallback)
    {
        var operand = WithoutOuterTrivia(fallback);
        return NeedsParentheses(operand)
            ? SyntaxFactory.ParenthesizedExpression(operand)
            : operand;
    }

    private static ExpressionSyntax WithoutOuterTrivia(ExpressionSyntax expression) =>
        expression.WithLeadingTrivia(default(SyntaxTriviaList)).WithTrailingTrivia(default(SyntaxTriviaList));

    private static bool NeedsParentheses(ExpressionSyntax expression) =>
        expression is AssignmentExpressionSyntax or
            LambdaExpressionSyntax or
            AnonymousMethodExpressionSyntax or
            ConditionalExpressionSyntax or
            SwitchExpressionSyntax or
            QueryExpressionSyntax;
}
