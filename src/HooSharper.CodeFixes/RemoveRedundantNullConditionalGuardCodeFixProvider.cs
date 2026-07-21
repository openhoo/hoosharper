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

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(RemoveRedundantNullConditionalGuardCodeFixProvider)), Shared]
public sealed class RemoveRedundantNullConditionalGuardCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => [RemoveRedundantNullConditionalGuardAnalyzer.DiagnosticId];

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
                "Remove redundant null guard",
                cancellationToken => ApplyFixAsync(context.Document, ifStatement, cancellationToken),
                nameof(RemoveRedundantNullConditionalGuardCodeFixProvider)),
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
            block.Statements[0] is not ExpressionStatementSyntax expressionStatement)
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
            .AddRange(CommentLines(expressionStatement.GetLeadingTrivia()))
            .AddRange(CommentLines(block.CloseBraceToken.LeadingTrivia));
        var trailingTrivia = InlineComments(expressionStatement.GetTrailingTrivia())
            .AddRange(InlineComments(block.CloseBraceToken.TrailingTrivia));

        trailingTrivia = trailingTrivia
            .AddRange(ifStatement.GetTrailingTrivia().Where(static trivia =>
                !trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) &&
                !trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) &&
                !trivia.IsKind(SyntaxKind.WhitespaceTrivia)));

        var replacement = expressionStatement
            .WithLeadingTrivia(leadingTrivia)
            .WithTrailingTrivia(trailingTrivia);

        return document.WithSyntaxRoot(root.ReplaceNode(ifStatement, replacement));
    }

    private static SyntaxTriviaList CommentLines(SyntaxTriviaList trivia)
    {
        var result = SyntaxFactory.TriviaList();
        foreach (var item in trivia)
        {
            if (item.IsKind(SyntaxKind.SingleLineCommentTrivia) || item.IsKind(SyntaxKind.MultiLineCommentTrivia))
            {
                result = result.Add(SyntaxFactory.ElasticMarker).Add(item).Add(SyntaxFactory.ElasticCarriageReturnLineFeed);
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
