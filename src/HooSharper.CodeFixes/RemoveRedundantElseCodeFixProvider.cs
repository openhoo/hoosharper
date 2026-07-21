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

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(RemoveRedundantElseCodeFixProvider)), Shared]
public sealed class RemoveRedundantElseCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => [RemoveRedundantElseAnalyzer.DiagnosticId];

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var diagnostic = context.Diagnostics[0];
        var ifStatement = root?.FindToken(diagnostic.Location.SourceSpan.Start).Parent?
            .AncestorsAndSelf().OfType<IfStatementSyntax>().FirstOrDefault();

        if (ifStatement?.Else is null)
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                "Remove redundant else",
                cancellationToken => ApplyFixAsync(context.Document, ifStatement, cancellationToken),
                nameof(RemoveRedundantElseCodeFixProvider)),
            diagnostic);
    }

    private static async Task<Document> ApplyFixAsync(
        Document document,
        IfStatementSyntax ifStatement,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || ifStatement.Else is not { } elseClause)
        {
            return document;
        }

        var replacementStatements = CreateReplacementStatements(ifStatement, elseClause);
        SyntaxNode newRoot;

        if (ifStatement.Parent is BlockSyntax parentBlock)
        {
            var index = parentBlock.Statements.IndexOf(ifStatement);
            var newParentBlock = parentBlock.WithStatements(
                    parentBlock.Statements.RemoveAt(index).InsertRange(index, replacementStatements))
                .WithAdditionalAnnotations(Formatter.Annotation);
            newRoot = root.ReplaceNode(parentBlock, newParentBlock);
        }
        else
        {
            var replacementBlock = SyntaxFactory.Block(replacementStatements)
                .WithLeadingTrivia(ifStatement.GetLeadingTrivia())
                .WithTrailingTrivia(ifStatement.GetTrailingTrivia())
                .WithAdditionalAnnotations(Formatter.Annotation);
            newRoot = root.ReplaceNode(ifStatement, replacementBlock);
        }

        return document.WithSyntaxRoot(newRoot);
    }

    private static List<StatementSyntax> CreateReplacementStatements(
        IfStatementSyntax ifStatement,
        ElseClauseSyntax elseClause)
    {
        var statementWithoutElse = ifStatement.WithElse(null)
            .WithAdditionalAnnotations(Formatter.Annotation);

        var movedStatements = elseClause.Statement is BlockSyntax block
            ? block.Statements.ToList()
            : [elseClause.Statement];

        if (movedStatements.Count > 0)
        {
            var firstStatement = movedStatements[0];
            var leadingTrivia = SignificantTrivia(elseClause.ElseKeyword.LeadingTrivia)
                .AddRange(SignificantTrivia(elseClause.ElseKeyword.TrailingTrivia));

            if (elseClause.Statement is BlockSyntax elseBlock)
            {
                leadingTrivia = leadingTrivia
                    .AddRange(SignificantTrivia(elseBlock.OpenBraceToken.LeadingTrivia))
                    .AddRange(SignificantTrivia(elseBlock.OpenBraceToken.TrailingTrivia));
            }

            leadingTrivia = leadingTrivia.AddRange(SignificantTrivia(firstStatement.GetLeadingTrivia()));
            movedStatements[0] = firstStatement
                .WithLeadingTrivia(WithLineBreaks(leadingTrivia, addInitialLineBreak: false))
                .WithAdditionalAnnotations(Formatter.Annotation);

            if (elseClause.Statement is BlockSyntax body)
            {
                var lastIndex = movedStatements.Count - 1;
                var trailingTrivia = movedStatements[lastIndex].GetTrailingTrivia()
                    .AddRange(WithLineBreaks(SignificantTrivia(body.CloseBraceToken.LeadingTrivia)))
                    .AddRange(WithLineBreaks(SignificantTrivia(body.CloseBraceToken.TrailingTrivia), addInitialLineBreak: false));
                movedStatements[lastIndex] = movedStatements[lastIndex]
                    .WithTrailingTrivia(trailingTrivia);
            }
        }

        var replacements = new List<StatementSyntax>(movedStatements.Count + 1) { statementWithoutElse };
        replacements.AddRange(movedStatements);
        return replacements;
    }

    private static SyntaxTriviaList SignificantTrivia(SyntaxTriviaList trivia) =>
        SyntaxFactory.TriviaList(trivia.Where(item =>
            !item.IsKind(SyntaxKind.WhitespaceTrivia) && !item.IsKind(SyntaxKind.EndOfLineTrivia)));

    private static SyntaxTriviaList WithLineBreaks(SyntaxTriviaList trivia, bool addInitialLineBreak = true)
    {
        var result = new List<SyntaxTrivia>(trivia.Count * 2 + 1);
        if (addInitialLineBreak && trivia.Count > 0)
        {
            result.Add(SyntaxFactory.ElasticCarriageReturnLineFeed);
        }

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
}
