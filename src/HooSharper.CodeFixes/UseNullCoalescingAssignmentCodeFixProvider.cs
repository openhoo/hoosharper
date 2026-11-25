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

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(UseNullCoalescingAssignmentCodeFixProvider)), Shared]
public sealed class UseNullCoalescingAssignmentCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(UseNullCoalescingAssignmentAnalyzer.DiagnosticId);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var diagnostic = context.Diagnostics[0];
        var ifStatement = root?.FindToken(diagnostic.Location.SourceSpan.Start).Parent?
            .AncestorsAndSelf().OfType<IfStatementSyntax>().FirstOrDefault();

        var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        if (ifStatement is null || semanticModel is null ||
            !UseNullCoalescingAssignmentAnalyzer.TryGetCandidate(
                ifStatement, semanticModel, context.CancellationToken, out _))
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                "Use ??= assignment",
                cancellationToken => ApplyFixAsync(context.Document, ifStatement, cancellationToken),
                nameof(UseNullCoalescingAssignmentCodeFixProvider)),
            diagnostic);
    }

    private static async Task<Document> ApplyFixAsync(
        Document document,
        IfStatementSyntax ifStatement,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (semanticModel is null ||
            !UseNullCoalescingAssignmentAnalyzer.TryGetCandidate(
                ifStatement, semanticModel, cancellationToken, out _))
        {
            return document;
        }
        if (root is null || UseNullCoalescingAssignmentAnalyzer.GetAssignmentStatement(ifStatement) is not
            {
                Expression: AssignmentExpressionSyntax assignment,
            } assignmentStatement)
        {
            return document;
        }

        var replacement = assignmentStatement.WithExpression(
            SyntaxFactory.AssignmentExpression(SyntaxKind.CoalesceAssignmentExpression, assignment.Left,
                SyntaxFactory.Token(SyntaxKind.QuestionQuestionEqualsToken).WithTriviaFrom(assignment.OperatorToken),
                assignment.Right));
        if (ifStatement.Statement is not BlockSyntax block)
        {
            return document.WithSyntaxRoot(root.ReplaceNode(ifStatement,
                CodeFixTrivia.UnwrapEmbeddedStatement(document, ifStatement, replacement, cancellationToken)));
        }

        var leadingTrivia = ifStatement.GetLeadingTrivia();
        var guardTrivia = ifStatement.IfKeyword.TrailingTrivia
            .AddRange(ifStatement.OpenParenToken.LeadingTrivia).AddRange(ifStatement.OpenParenToken.TrailingTrivia)
            .AddRange(ifStatement.Condition.GetLeadingTrivia()).AddRange(ifStatement.Condition.GetTrailingTrivia())
            .AddRange(ifStatement.CloseParenToken.LeadingTrivia).AddRange(ifStatement.CloseParenToken.TrailingTrivia);

        leadingTrivia = leadingTrivia.AddRange(CommentLines(guardTrivia))
            .AddRange(CommentLines(block.OpenBraceToken.LeadingTrivia))
            .AddRange(CommentLines(block.OpenBraceToken.TrailingTrivia))
            .AddRange(CommentLines(assignmentStatement.GetLeadingTrivia()))
            .AddRange(CommentLines(block.CloseBraceToken.LeadingTrivia))
            .AddRange(CommentLines(block.CloseBraceToken.TrailingTrivia));
        var trailingTrivia = InlineComments(assignmentStatement.GetTrailingTrivia())
            .AddRange(ifStatement.GetTrailingTrivia().Where(trivia => !CodeFixTrivia.IsComment(trivia)));
        replacement = replacement
            .WithLeadingTrivia(leadingTrivia)
            .WithTrailingTrivia(trailingTrivia);

        return await CodeFixTrivia.ReplaceAndFormatAsync(document, root, ifStatement, replacement, cancellationToken)
            .ConfigureAwait(false);
    }
    private static SyntaxTriviaList CommentLines(SyntaxTriviaList trivia)
    {
        var result = SyntaxFactory.TriviaList();
        foreach (var item in trivia)
        {
            if (item.IsKind(SyntaxKind.SingleLineCommentTrivia) || item.IsKind(SyntaxKind.MultiLineCommentTrivia))
            {
                result = result.Add(SyntaxFactory.ElasticMarker).Add(item).Add(SyntaxFactory.ElasticLineFeed);
            }
        }

        return result;
    }


    private static SyntaxTriviaList InlineComments(SyntaxTriviaList trivia)
    {
        var result = SyntaxFactory.TriviaList();
        foreach (var item in trivia)
        {
            if (item.IsKind(SyntaxKind.SingleLineCommentTrivia) || item.IsKind(SyntaxKind.MultiLineCommentTrivia))
            {
                result = result.Count == 0
                    ? result.Add(SyntaxFactory.Space).Add(item)
                    : result.Add(item);
            }
        }

        return result;
    }
}
