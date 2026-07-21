using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Immutable;
using System.Composition;
using HooSharper.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace HooSharper.CodeFixes;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(OmitBracesForSingleLineIfCodeFixProvider)), Shared]
public sealed class OmitBracesForSingleLineIfCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => [OmitBracesForSingleLineIfAnalyzer.DiagnosticId];

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var diagnostic = context.Diagnostics[0];
        var block = root?.FindToken(diagnostic.Location.SourceSpan.Start).Parent?
            .AncestorsAndSelf().OfType<BlockSyntax>().FirstOrDefault();

        if (block is null)
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                "Remove braces",
                cancellationToken => ApplyFixAsync(context.Document, block, cancellationToken),
                nameof(OmitBracesForSingleLineIfCodeFixProvider)),
            diagnostic);
    }

    private static async Task<Document> ApplyFixAsync(
        Document document,
        BlockSyntax block,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || block.Statements.Count != 1 ||
            block.Parent is IfStatementSyntax { Statement: var thenStatement, Else: not null } &&
            thenStatement == block && block.Statements[0] is IfStatementSyntax { Else: null })
        {
            return document;
        }

        var originalStatement = block.Statements[0];
        var leadingTrivia = block.GetLeadingTrivia()
            .AddRange(CommentLines(block.OpenBraceToken.TrailingTrivia))
            .AddRange(originalStatement.GetLeadingTrivia());
        var trailingTrivia = originalStatement.GetTrailingTrivia()
            .AddRange(CommentLines(block.CloseBraceToken.LeadingTrivia))
            .AddRange(InlineComments(block.CloseBraceToken.TrailingTrivia));
        var statement = originalStatement
            .WithLeadingTrivia(leadingTrivia)
            .WithTrailingTrivia(trailingTrivia)
            .WithAdditionalAnnotations(Formatter.Annotation);

        return document.WithSyntaxRoot(root.ReplaceNode(block, statement));
    }
    private static SyntaxTriviaList CommentLines(SyntaxTriviaList trivia, bool addInitialLineBreak = false)
    {
        var result = SyntaxFactory.TriviaList();
        var foundComment = false;
        foreach (var item in trivia)
        {
            if (IsComment(item))
            {
                if (addInitialLineBreak && !foundComment)
                {
                    result = result.Add(SyntaxFactory.CarriageReturnLineFeed);
                }

                result = result.Add(item).Add(SyntaxFactory.CarriageReturnLineFeed);
                foundComment = true;
            }
        }

        return result;
    }

    private static SyntaxTriviaList InlineComments(SyntaxTriviaList trivia)
    {
        var result = SyntaxFactory.TriviaList();
        foreach (var item in trivia)
        {
            if (IsComment(item))
            {
                result = result.Add(SyntaxFactory.Space).Add(item);
                if (item.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
                    item.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia))
                {
                    result = result.Add(SyntaxFactory.CarriageReturnLineFeed);
                }
            }
        }

        return result;
    }

    private static bool IsComment(SyntaxTrivia trivia) =>
        trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
        trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) ||
        trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
        trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia);

}
