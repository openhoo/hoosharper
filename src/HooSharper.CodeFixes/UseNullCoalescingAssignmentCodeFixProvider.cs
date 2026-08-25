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

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(UseNullCoalescingAssignmentCodeFixProvider)), Shared]
public sealed class UseNullCoalescingAssignmentCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => [UseNullCoalescingAssignmentAnalyzer.DiagnosticId];

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var diagnostic = context.Diagnostics[0];
        var ifStatement = root?.FindToken(diagnostic.Location.SourceSpan.Start).Parent?
            .AncestorsAndSelf().OfType<IfStatementSyntax>().FirstOrDefault();

        if (ifStatement is null)
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
        if (root is null ||
            ifStatement.Statement is not BlockSyntax { Statements.Count: 1 } block ||
            block.Statements[0] is not ExpressionStatementSyntax
            {
                Expression: AssignmentExpressionSyntax assignment,
            } assignmentStatement)
        {
            return document;
        }

        var leadingTrivia = ifStatement.GetLeadingTrivia()
            .AddRange(CommentLines(ifStatement.IfKeyword.TrailingTrivia))
            .AddRange(CommentLines(ifStatement.OpenParenToken.LeadingTrivia))
            .AddRange(CommentLines(ifStatement.OpenParenToken.TrailingTrivia))
            .AddRange(CommentLines(ifStatement.Condition.GetLeadingTrivia()))
            .AddRange(CommentLines(ifStatement.Condition.GetTrailingTrivia()))
            .AddRange(CommentLines(ifStatement.CloseParenToken.LeadingTrivia))
            .AddRange(CommentLines(ifStatement.CloseParenToken.TrailingTrivia))
            .AddRange(CommentLines(block.OpenBraceToken.LeadingTrivia))
            .AddRange(CommentLines(block.OpenBraceToken.TrailingTrivia))
            .AddRange(CommentLines(assignmentStatement.GetLeadingTrivia()))
            .AddRange(CommentLines(block.CloseBraceToken.LeadingTrivia))
            .AddRange(CommentLines(block.CloseBraceToken.TrailingTrivia));
        var trailingTrivia = InlineComments(assignmentStatement.GetTrailingTrivia())
            .AddRange(ifStatement.GetTrailingTrivia().Where(trivia =>
                !trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) &&
                !trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)));

        var replacement = SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.CoalesceAssignmentExpression,
                    assignment.Left,
                    SyntaxFactory.Token(SyntaxKind.QuestionQuestionEqualsToken),
                    assignment.Right))
            .WithLeadingTrivia(leadingTrivia)
            .WithTrailingTrivia(trailingTrivia)
            .WithAdditionalAnnotations(Formatter.Annotation);

        return document.WithSyntaxRoot(root.ReplaceNode(ifStatement, replacement));
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
