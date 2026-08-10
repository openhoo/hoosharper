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
                cancellationToken => ApplyFixAsync(context.Document, usingStatement, cancellationToken),
                nameof(UseUsingDeclarationCodeFixProvider)),
            diagnostic);
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
                .WithAdditionalAnnotations(Formatter.Annotation, replacementAnnotation);
        }

        var index = parentBlock.Statements.IndexOf(usingStatement);
        var replacementBlock = parentBlock.WithStatements(
            parentBlock.Statements.RemoveAt(index).InsertRange(index, replacements));
        var changedDocument = document.WithSyntaxRoot(root.ReplaceNode(parentBlock, replacementBlock));
        var formattedDocument = await Formatter.FormatAsync(
                changedDocument,
                Formatter.Annotation,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var formattedRoot = await formattedDocument.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (formattedRoot is null)
        {
            return formattedDocument;
        }

        var formattedText = await formattedDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var changes = new List<Microsoft.CodeAnalysis.Text.TextChange>();
        for (var position = 0; position < formattedText.Length; position++)
        {
            var lineBreakLength = formattedText[position] switch
            {
                '\r' when position + 1 < formattedText.Length && formattedText[position + 1] == '\n' => 2,
                '\r' or '\n' => 1,
                _ => 0,
            };
            if (lineBreakLength == 0)
            {
                continue;
            }

            var token = formattedRoot.FindToken(position, findInsideTrivia: true);
            var trivia = formattedRoot.FindTrivia(position, findInsideTrivia: true);
            if (token.Span.Contains(position) ||
                trivia.IsKind(SyntaxKind.DisabledTextTrivia) ||
                trivia.IsDirective ||
                formattedText.ToString(new Microsoft.CodeAnalysis.Text.TextSpan(position, lineBreakLength)) == endOfLineText)
            {
                position += lineBreakLength - 1;
                continue;
            }

            changes.Add(new Microsoft.CodeAnalysis.Text.TextChange(
                new Microsoft.CodeAnalysis.Text.TextSpan(position, lineBreakLength),
                endOfLineText));
            position += lineBreakLength - 1;
        }

        return formattedDocument.WithText(formattedText.WithChanges(changes));
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
