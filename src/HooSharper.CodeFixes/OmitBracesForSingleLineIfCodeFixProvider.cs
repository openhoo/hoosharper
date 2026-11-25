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
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(OmitBracesForSingleLineIfAnalyzer.DiagnosticId);

    private static readonly FixAllProvider DocumentFixer = FixAllProvider.Create(FixDocumentAsync);

    public override FixAllProvider GetFixAllProvider() => DocumentFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var diagnostic = context.Diagnostics[0];
        var block = FindEligibleBlock(root, diagnostic, context.Document, context.CancellationToken);
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

    private static BlockSyntax? FindEligibleBlock(SyntaxNode? root, Diagnostic diagnostic, Document document, CancellationToken cancellationToken)
    {
        var span = diagnostic.Location.SourceSpan;
        if (root is null || !root.FullSpan.Contains(span))
            return null;
        var token = root.FindToken(span.Start);
        return token.IsKind(SyntaxKind.OpenBraceToken) && token.Span == span &&
            token.Parent is BlockSyntax block && block.FirstAncestorOrSelf<IfStatementSyntax>()?.ContainsDiagnostics != true &&
            document.Project.CompilationOptions is { } options &&
            OmitBracesForSingleLineIfAnalyzer.CanRemoveBraces(block, options, cancellationToken) ? block : null;
    }

    private static async Task<Document?> FixDocumentAsync(FixAllContext context, Document document, ImmutableArray<Diagnostic> diagnostics)
    {
        var cancellationToken = context.CancellationToken;
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
            return null;

        // Nested and neighboring blocks can share formatting edits. Replacing
        // them in one tree composes child edits before unwrapping their parents.
        var blocks = diagnostics.Select(diagnostic => FindEligibleBlock(root, diagnostic, document, cancellationToken)).OfType<BlockSyntax>().Distinct();
        var rewritten = root.ReplaceNodes(blocks, (original, current) =>
            Unwrap(current, original, SyntaxFactory.EndOfLine(CodeFixTrivia.GetNewLine(document, original.SyntaxTree, original.SpanStart, cancellationToken)))
                .WithAdditionalAnnotations(Formatter.Annotation));
        return await CodeFixTrivia.FormatChangesAsync(document, rewritten, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Document> ApplyFixAsync(
        Document document,
        BlockSyntax block,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || block.FirstAncestorOrSelf<IfStatementSyntax>()?.ContainsDiagnostics == true ||
            document.Project.CompilationOptions is not { } options ||
            !OmitBracesForSingleLineIfAnalyzer.CanRemoveBraces(block, options, cancellationToken))
        {
            return document;
        }

        var endOfLine = SyntaxFactory.EndOfLine(CodeFixTrivia.GetNewLine(document, block.SyntaxTree, block.SpanStart, cancellationToken));
        return await CodeFixTrivia.ReplaceAndFormatAsync(document, root, block, Unwrap(block, block, endOfLine), cancellationToken).ConfigureAwait(false);
    }

    private static StatementSyntax Unwrap(BlockSyntax block, BlockSyntax original, SyntaxTrivia endOfLine)
    {
        var originalStatement = block.Statements[0];
        var leadingTrivia = block.GetLeadingTrivia()
            .AddRange(CommentLines(block.OpenBraceToken.TrailingTrivia, endOfLine))
            .AddRange(originalStatement.GetLeadingTrivia());
        // Keep an inner if/else together when the removed opening brace shared
        // the outer condition's line. Otherwise its else appears to belong to
        // the outer if, even though the parser still binds it to the inner one.
        if (original.Parent is IfStatementSyntax outer && originalStatement is IfStatementSyntax { Else: not null } &&
            !outer.CloseParenToken.TrailingTrivia.Any(t => t.IsKind(SyntaxKind.EndOfLineTrivia)) &&
            !leadingTrivia.Any(t => t.IsKind(SyntaxKind.EndOfLineTrivia)))
            leadingTrivia = leadingTrivia.Insert(0, endOfLine);
        var trailingTrivia = originalStatement.GetTrailingTrivia()
            .AddRange(CommentLines(block.CloseBraceToken.LeadingTrivia, endOfLine))
            .AddRange(InlineComments(block.CloseBraceToken.TrailingTrivia, endOfLine));
        return originalStatement
            .WithLeadingTrivia(leadingTrivia)
            .WithTrailingTrivia(trailingTrivia);
    }

    private static SyntaxTriviaList CommentLines(
        SyntaxTriviaList trivia,
        SyntaxTrivia endOfLine,
        bool addInitialLineBreak = false)
    {
        var result = SyntaxFactory.TriviaList();
        var foundComment = false;
        foreach (var item in trivia)
        {
            if (IsComment(item))
            {
                if (addInitialLineBreak && !foundComment)
                {
                    result = result.Add(endOfLine);
                }

                result = result.Add(item).Add(endOfLine);
                foundComment = true;
            }
        }

        return result;
    }

    private static SyntaxTriviaList InlineComments(SyntaxTriviaList trivia, SyntaxTrivia endOfLine)
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
                    result = result.Add(endOfLine);
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
