using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp.Formatting;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Options;

namespace HooSharper.CodeFixes;

internal static class CodeFixTrivia
{
    internal static bool StartsWithLineComment(StatementSyntax statement) => statement.GetLeadingTrivia()
        .FirstOrDefault(t => !t.IsKind(SyntaxKind.WhitespaceTrivia) && !t.IsKind(SyntaxKind.EndOfLineTrivia))
        .IsKind(SyntaxKind.SingleLineCommentTrivia);

    internal static StatementSyntax WithBlankLineBefore(StatementSyntax next, StatementSyntax preceding, SyntaxNode original)
    {
        var trailing = preceding.GetTrailingTrivia();
        var leading = next.GetLeadingTrivia();
        // Formatting a newly constructed guard supplies its first line break.
        var breaks = Math.Max(1, trailing.Count(t => t.IsKind(SyntaxKind.EndOfLineTrivia))) +
            leading.TakeWhile(t => t.IsKind(SyntaxKind.WhitespaceTrivia) || t.IsKind(SyntaxKind.EndOfLineTrivia))
                .Count(t => t.IsKind(SyntaxKind.EndOfLineTrivia));
        var newline = trailing.FirstOrDefault(t => t.IsKind(SyntaxKind.EndOfLineTrivia));
        if (newline.RawKind == 0)
            newline = original.DescendantTrivia().FirstOrDefault(t => t.IsKind(SyntaxKind.EndOfLineTrivia));
        if (newline.RawKind == 0)
            newline = SyntaxFactory.ElasticCarriageReturnLineFeed;
        for (; breaks < 2; breaks++)
            leading = leading.Insert(0, newline);
        return next.WithLeadingTrivia(leading);
    }

    // Formatting the parent block can rewrite untouched siblings, including
    // expressions whose source text the compiler passes to user code.
    internal static BlockSyntax ReplaceWithStatements(BlockSyntax block, int index, IEnumerable<StatementSyntax> replacements) =>
        block.WithStatements(block.Statements.RemoveAt(index).InsertRange(index,
            replacements.Select(statement => statement.WithAdditionalAnnotations(Formatter.Annotation))));

    internal static Task<Document> ReplaceAndFormatAsync(Document document, SyntaxNode root,
        SyntaxNode original, SyntaxNode replacement, CancellationToken cancellationToken) =>
        FormatChangesAsync(document, root.ReplaceNode(original, replacement.WithAdditionalAnnotations(Formatter.Annotation)), cancellationToken);

    internal static async Task<Document> FormatChangesAsync(Document document, SyntaxNode newRoot, CancellationToken cancellationToken)
    {
        var tree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
        var tabIndent = UsesTabIndentSize(document, tree);
        var firstEdit = newRoot.GetAnnotatedNodesAndTokens(Formatter.Annotation).FirstOrDefault();
        var options = await document.GetOptionsAsync(cancellationToken).ConfigureAwait(false);
        var defaultNewline = options.GetOption(FormattingOptions.NewLine);
        var newline = tree is null ? defaultNewline : GetNewLine(document, tree, firstEdit.SpanStart, cancellationToken);
        // Without an explicit setting, keep the existing switch layout. The
        // default formatter otherwise moves the untouched closing brace while
        // leaving the opening brace aligned with the case label.
        var aligned = tree is not null && !document.Project.AnalyzerOptions.AnalyzerConfigOptionsProvider
            .GetOptions(tree).TryGetValue("csharp_indent_case_contents_when_block", out _)
            ? newRoot.GetAnnotatedNodes(Formatter.Annotation).Where(IsInAlignedCaseBlock).ToArray()
            : Array.Empty<SyntaxNode>();
        if (!tabIndent && aligned.Length == 0 && newline == defaultNewline)
            return document.WithSyntaxRoot(newRoot);

        options = options.WithChangedOption(FormattingOptions.NewLine, newline);
        if (tabIndent)
            options = WithTabIndentSize(options);
        var caseLayout = new SyntaxAnnotation();
        newRoot = newRoot.ReplaceNodes(aligned, (_, updated) =>
            updated.WithoutAnnotations(Formatter.Annotation).WithAdditionalAnnotations(caseLayout));
        var formatted = document.WithSyntaxRoot(newRoot);
        if (newRoot.GetAnnotatedNodesAndTokens(Formatter.Annotation).Any())
            formatted = await Formatter.FormatAsync(formatted, Formatter.Annotation, options, cancellationToken).ConfigureAwait(false);
        if (aligned.Length != 0)
            formatted = await Formatter.FormatAsync(formatted, caseLayout,
                options.WithChangedOption(CSharpFormattingOptions.IndentSwitchCaseSectionWhenBlock, false),
                cancellationToken).ConfigureAwait(false);
        return tree is null ? formatted : await WithTrackedFormattingAsync(document, tree, formatted, cancellationToken).ConfigureAwait(false);
    }

    // Give the host plain text so it cannot format elastic trivia a second time.
    // Base that text on the original change ranges, so Fix All can merge separate
    // edits without treating each one as a replacement of the entire file.
    internal static async Task<Document> WithTrackedFormattingAsync(Document original, SyntaxTree originalTree,
        Document formatted, CancellationToken cancellationToken)
    {
        var formattedTree = (await formatted.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false))!;
        var changes = formattedTree.GetChanges(originalTree);
        var originalText = await original.GetTextAsync(cancellationToken).ConfigureAwait(false);
        return original.WithText(originalText.WithChanges(changes));
    }

    // Choose new separators at the edit, without normalizing existing file text.
    internal static string GetNewLine(Document document, SyntaxTree tree, int position, CancellationToken cancellationToken)
    {
        if (document.Project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GetOptions(tree).TryGetValue("end_of_line", out var configured))
        {
            switch (configured.Trim().ToLowerInvariant())
            {
                case "lf": return "\n";
                case "crlf": return "\r\n";
                case "cr": return "\r";
            }
        }

        var text = tree.GetText(cancellationToken);
        for (var i = text.Lines.GetLineFromPosition(Math.Min(position, text.Length)).LineNumber; i >= 0; i--)
        {
            var line = text.Lines[i];
            if (line.EndIncludingLineBreak > line.End)
                return text.ToString(Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(line.End, line.EndIncludingLineBreak));
        }
        return document.Project.Solution.Options.GetOption(FormattingOptions.NewLine, document.Project.Language);
    }

    private static bool IsInAlignedCaseBlock(SyntaxNode node)
    {
        foreach (var block in node.AncestorsAndSelf().OfType<BlockSyntax>())
        {
            if (block.Parent is not SwitchSectionSyntax { Labels.Count: > 0 } section)
                continue;
            var label = section.Labels.Last().GetFirstToken();
            var brace = block.OpenBraceToken;
            var labelLine = label.GetLocation().GetLineSpan().StartLinePosition;
            var braceLine = brace.GetLocation().GetLineSpan().StartLinePosition;
            return labelLine.Line < braceLine.Line && labelLine.Character == braceLine.Character;
        }
        return false;
    }

    internal static async Task<DocumentOptionSet> GetFormattingOptionsAsync(Document document, CancellationToken cancellationToken)
    {
        var options = await document.GetOptionsAsync(cancellationToken).ConfigureAwait(false);
        return UsesTabIndentSize(document, await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false))
            ? WithTabIndentSize(options) : options;
    }

    // Roslyn reads tab_width but falls back to four columns for "indent_size = tab".
    // In that case EditorConfig defines one indent as one tab stop.
    private static bool UsesTabIndentSize(Document document, SyntaxTree? tree) =>
        tree != null && document.Project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GetOptions(tree)
            .TryGetValue("indent_size", out var indent) && string.Equals(indent, "tab", StringComparison.OrdinalIgnoreCase);

    private static DocumentOptionSet WithTabIndentSize(DocumentOptionSet options) =>
        options.WithChangedOption(FormattingOptions.IndentationSize, options.GetOption(FormattingOptions.TabSize));

    internal static bool IsComment(SyntaxTrivia trivia) =>
        trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) ||
        trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia);

    // The embedded statement supplies its own semicolon and trailing trivia.
    // Move only comments from the removed guard/body boundary to its new indent.
    internal static StatementSyntax UnwrapEmbeddedStatement(Document document, IfStatementSyntax guard,
        StatementSyntax replacement, CancellationToken cancellationToken)
    {
        var leading = guard.GetLeadingTrivia();
        var indentation = SyntaxFactory.TriviaList(leading.Reverse()
            .TakeWhile(t => t.IsKind(SyntaxKind.WhitespaceTrivia)).Reverse());
        var comments = guard.DescendantTrivia().Where(t => IsComment(t) &&
            t.SpanStart >= guard.SpanStart && t.Span.End <= guard.Statement.SpanStart);
        SyntaxTrivia eol = default;
        foreach (var comment in comments)
        {
            if (eol.RawKind == 0)
                eol = SyntaxFactory.EndOfLine(GetNewLine(document, guard.SyntaxTree, guard.SpanStart, cancellationToken));
            leading = leading.Add(comment).Add(eol).AddRange(indentation);
        }
        return replacement.WithLeadingTrivia(leading);
    }

    internal static SyntaxTriviaList TrailingAfterUnwrapping(StatementSyntax statement, IfStatementSyntax wrapper)
    {
        var outer = wrapper.GetTrailingTrivia();
        var inner = statement.GetTrailingTrivia();
        if (statement.GetLastToken() == wrapper.GetLastToken() || !inner.Any(IsComment))
        {
            return outer;
        }

        return outer.Any(IsComment) ? inner.AddRange(outer) : inner;
    }
}
