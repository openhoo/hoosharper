using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Composition;
using HooSharper.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace HooSharper.CodeFixes;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(UseUsingDeclarationCodeFixProvider)), Shared]
public sealed class UseUsingDeclarationCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => [UseUsingDeclarationAnalyzer.DiagnosticId];

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var diagnostic = context.Diagnostics[0];
        var usingStatement = root?.FindToken(diagnostic.Location.SourceSpan.Start).Parent?
            .AncestorsAndSelf().OfType<UsingStatementSyntax>().FirstOrDefault();

        if (usingStatement is null)
        {
            return;
        }


        context.RegisterCodeFix(
            CodeAction.Create(
                "Use using declaration",
                createChangedSolution: cancellationToken => ApplyFixToSolutionAsync(context.Document, usingStatement, cancellationToken),
                equivalenceKey: nameof(UseUsingDeclarationCodeFixProvider)),
            diagnostic);
    }

    private static async Task<Solution> ApplyFixToSolutionAsync(
        Document document,
        UsingStatementSyntax usingStatement,
        CancellationToken cancellationToken)
    {
        var changedDocument = await ApplyFixAsync(document, usingStatement, cancellationToken).ConfigureAwait(false);
        return changedDocument.Project.Solution;
    }

    private static async Task<Document> ApplyFixAsync(
        Document document,
        UsingStatementSyntax usingStatement,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var sourceText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (root is null ||
            usingStatement.Parent is not BlockSyntax parentBlock ||
            usingStatement.Declaration is not { } declaration ||
            usingStatement.Statement is not BlockSyntax body)
        {
            return document;
        }

        var endOfLineText = DetectEndOfLine(sourceText);
        var endOfLine = SyntaxFactory.EndOfLine(endOfLineText);
        var declarationLeadingTrivia = usingStatement.GetLeadingTrivia()
            .AddRange(CommentLines(usingStatement.AwaitKeyword.TrailingTrivia, endOfLine))
            .AddRange(CommentLines(usingStatement.UsingKeyword.TrailingTrivia, endOfLine))
            .AddRange(CommentLines(usingStatement.OpenParenToken.LeadingTrivia, endOfLine))
            .AddRange(CommentLines(usingStatement.OpenParenToken.TrailingTrivia, endOfLine));
        var declarationTrailingTrivia = InlineComments(usingStatement.CloseParenToken.LeadingTrivia)
            .AddRange(InlineComments(usingStatement.CloseParenToken.TrailingTrivia))
            .Add(endOfLine);

        var usingDeclaration = SyntaxFactory.LocalDeclarationStatement(declaration)
            .WithAwaitKeyword(usingStatement.AwaitKeyword.WithoutTrivia())
            .WithUsingKeyword(usingStatement.UsingKeyword.WithoutTrivia())
            .WithLeadingTrivia(declarationLeadingTrivia)
            .WithTrailingTrivia(declarationTrailingTrivia);

        var movedStatements = body.Statements.ToList();
        var firstStatement = movedStatements[0];
        var firstLeadingTrivia = CommentLines(body.OpenBraceToken.LeadingTrivia, endOfLine, addInitialLineBreak: true)
            .AddRange(CommentLines(body.OpenBraceToken.TrailingTrivia, endOfLine, addInitialLineBreak: true))
            .AddRange(firstStatement.GetLeadingTrivia());
        movedStatements[0] = firstStatement.WithLeadingTrivia(firstLeadingTrivia);

        var lastIndex = movedStatements.Count - 1;
        var lastStatement = movedStatements[lastIndex];
        var lastTrailingTrivia = lastStatement.GetTrailingTrivia()
            .AddRange(CommentLines(body.CloseBraceToken.LeadingTrivia, endOfLine, addInitialLineBreak: true))
            .AddRange(CommentLines(usingStatement.GetTrailingTrivia(), endOfLine, addInitialLineBreak: true));
        movedStatements[lastIndex] = lastStatement.WithTrailingTrivia(lastTrailingTrivia);

        var replacementAnnotation = new SyntaxAnnotation();
        var replacements = new List<StatementSyntax>(movedStatements.Count + 1) { usingDeclaration };
        replacements.AddRange(movedStatements);
        for (var replacementIndex = 0; replacementIndex < replacements.Count; replacementIndex++)
        {
            replacements[replacementIndex] = replacements[replacementIndex]
                .WithAdditionalAnnotations(replacementAnnotation);
        }

        var index = parentBlock.Statements.IndexOf(usingStatement);
        var replacementBlock = parentBlock.WithStatements(
            parentBlock.Statements.RemoveAt(index).InsertRange(index, replacements));
        var changedDocument = document.WithSyntaxRoot(root.ReplaceNode(parentBlock, replacementBlock));
        var changedRoot = await changedDocument.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (changedRoot is null)
        {
            return changedDocument;
        }

        var affectedSpans = CollectAffectedLineBreakSpans(changedRoot, replacementAnnotation);
        var formattedOptions = changedDocument.Project.Solution.Options.WithChangedOption(
            FormattingOptions.NewLine,
            LanguageNames.CSharp,
            endOfLineText);
        var formattedDocument = await Formatter.FormatAsync(
                changedDocument,
                affectedSpans,
                formattedOptions,
                cancellationToken)
            .ConfigureAwait(false);

        var normalizedDocument = await NormalizeFormattedLineEndingsAsync(
            formattedDocument, replacementAnnotation, endOfLineText, cancellationToken).ConfigureAwait(false);

        // Pin the exact final text so downstream consumers cannot re-derive divergent
        // line endings from the syntax tree under ambient formatting options.
        var normalizedText = await normalizedDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
        return normalizedDocument.WithText(Microsoft.CodeAnalysis.Text.SourceText.From(
            normalizedText.ToString(), normalizedText.Encoding, normalizedText.ChecksumAlgorithm));
    }
    private static string DetectEndOfLine(Microsoft.CodeAnalysis.Text.SourceText sourceText)
    {
        for (var position = 0; position < sourceText.Length; position++)
        {
            switch (sourceText[position])
            {
                case '\n':
                    return "\n";
                case '\r':
                    return position + 1 < sourceText.Length && sourceText[position + 1] == '\n'
                        ? "\r\n"
                        : "\r";
            }
        }

        return "\r\n";
    }

    private static List<Microsoft.CodeAnalysis.Text.TextSpan> CollectAffectedLineBreakSpans(
        SyntaxNode root,
        SyntaxAnnotation replacementAnnotation)
    {
        var spans = new List<Microsoft.CodeAnalysis.Text.TextSpan>();
        var textEnd = root.FullSpan.End;
        foreach (var annotatedNodeOrToken in root.GetAnnotatedNodesAndTokens(replacementAnnotation))
        {
            if (annotatedNodeOrToken.IsToken)
            {
                continue;
            }

            var fullSpan = annotatedNodeOrToken.FullSpan;
            var start = fullSpan.Start;
            var end = fullSpan.End;
            while (start > 0 && TryGetEndOfLineTriviaAt(root, start - 1, out var leadingBreak))
            {
                start = leadingBreak.Span.Start;
            }

            while (end < textEnd && TryGetEndOfLineTriviaAt(root, end, out var trailingBreak))
            {
                end = trailingBreak.Span.End;
            }

            spans.Add(new Microsoft.CodeAnalysis.Text.TextSpan(start, end - start));
        }

        if (spans.Count == 0)
        {
            return spans;
        }

        spans.Sort(static (left, right) => left.Start != right.Start
            ? left.Start.CompareTo(right.Start)
            : left.End.CompareTo(right.End));
        var merged = new List<Microsoft.CodeAnalysis.Text.TextSpan>(spans.Count) { spans[0] };
        foreach (var span in spans.Skip(1))
        {
            var last = merged[merged.Count - 1];
            if (span.Start <= last.End)
            {
                merged[merged.Count - 1] = span.End > last.End
                    ? new Microsoft.CodeAnalysis.Text.TextSpan(last.Start, span.End - last.Start)
                    : last;
            }
            else
            {
                merged.Add(span);
            }
        }

        return merged;
    }

    private static bool TryGetEndOfLineTriviaAt(SyntaxNode root, int position, out SyntaxTrivia endOfLine)
    {
        var trivia = root.FindTrivia(position, findInsideTrivia: true);
        if (trivia != default && trivia.IsKind(SyntaxKind.EndOfLineTrivia))
        {
            endOfLine = trivia;
            return true;
        }

        endOfLine = default;
        return false;
    }

    private static async Task<Document> NormalizeFormattedLineEndingsAsync(
        Document document,
        SyntaxAnnotation replacementAnnotation,
        string endOfLineText,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return document;
        }

        var annotatedSpans = root.GetAnnotatedNodesAndTokens(replacementAnnotation)
            .Select(annotatedNodeOrToken => annotatedNodeOrToken.Span)
            .ToList();
        if (annotatedSpans.Count == 0)
        {
            return document;
        }

        var replacedRange = Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(
            annotatedSpans.Min(span => span.Start),
            annotatedSpans.Max(span => span.End));
        var foreignEndOfLines = new List<SyntaxTrivia>();
        foreach (var trivia in root.DescendantTrivia(replacedRange, descendIntoTrivia: true))
        {
            if (IsForeignEndOfLine(trivia, endOfLineText))
            {
                foreignEndOfLines.Add(trivia);
            }
        }

        if (foreignEndOfLines.Count == 0)
        {
            return document;
        }

        return document.WithSyntaxRoot(root.ReplaceTrivia(
            foreignEndOfLines,
            (_, rewritten) => IsForeignEndOfLine(rewritten, endOfLineText)
                ? SyntaxFactory.EndOfLine(endOfLineText)
                : rewritten));
    }

    private static bool IsForeignEndOfLine(SyntaxTrivia trivia, string endOfLineText) =>
        trivia.IsKind(SyntaxKind.EndOfLineTrivia) && !trivia.ToFullString().Equals(endOfLineText);

    private static SyntaxTriviaList CommentLines(
        SyntaxTriviaList trivia,
        SyntaxTrivia endOfLine,
        bool addInitialLineBreak = false)
    {
        var result = SyntaxFactory.TriviaList();
        var foundComment = false;
        foreach (var item in trivia)
        {
            if (item.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
                item.IsKind(SyntaxKind.MultiLineCommentTrivia) ||
                item.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                item.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
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

    private static SyntaxTriviaList InlineComments(SyntaxTriviaList trivia)
    {
        var result = SyntaxFactory.TriviaList();
        foreach (var item in trivia)
        {
            if (item.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
                item.IsKind(SyntaxKind.MultiLineCommentTrivia) ||
                item.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                item.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            {
                result = result.Count == 0 ? result.Add(SyntaxFactory.Space).Add(item) : result.Add(item);
            }
        }

        return result;
    }
}
