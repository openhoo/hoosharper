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

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(UseHashSetAddResultCodeFixProvider)), Shared]
public sealed class UseHashSetAddResultCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => [UseHashSetAddResultAnalyzer.DiagnosticId];

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
                "Use HashSet.Add result",
                cancellationToken => ApplyFixAsync(context.Document, ifStatement, cancellationToken),
                nameof(UseHashSetAddResultCodeFixProvider)),
            diagnostic);
    }

    private static async Task<Document> ApplyFixAsync(
        Document document,
        IfStatementSyntax ifStatement,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null ||
            ifStatement.Statement is not BlockSyntax { Statements.Count: > 0 } block ||
            block.Statements[0] is not ExpressionStatementSyntax
            {
                Expression: InvocationExpressionSyntax addInvocation,
            } addStatement)
        {
            return document;
        }

        SyntaxNode replacement;
        if (block.Statements.Count == 1)
        {
            var leadingTrivia = ifStatement.GetLeadingTrivia()
                .AddRange(CommentLines(ifStatement.IfKeyword.TrailingTrivia))
                .AddRange(CommentLines(ifStatement.OpenParenToken.LeadingTrivia))
                .AddRange(CommentLines(ifStatement.OpenParenToken.TrailingTrivia))
                .AddRange(CommentLines(ifStatement.Condition.DescendantTrivia(descendIntoTrivia: true)))
                .AddRange(CommentLines(ifStatement.CloseParenToken.LeadingTrivia))
                .AddRange(CommentLines(ifStatement.CloseParenToken.TrailingTrivia))
                .AddRange(CommentLines(block.OpenBraceToken.LeadingTrivia))
                .AddRange(CommentLines(block.OpenBraceToken.TrailingTrivia))
                .AddRange(CommentLines(addStatement.GetLeadingTrivia()));
            var trailingTrivia = CommentLines(addStatement.GetTrailingTrivia())
                .AddRange(CommentLines(block.CloseBraceToken.LeadingTrivia))
                .AddRange(ifStatement.GetTrailingTrivia());

            replacement = addStatement.WithoutTrivia()
                .WithLeadingTrivia(leadingTrivia)
                .WithTrailingTrivia(trailingTrivia)
                .WithAdditionalAnnotations(Formatter.Annotation);
        }
        else
        {
            var conditionComments = CommentLines(ifStatement.Condition.DescendantTrivia(descendIntoTrivia: true));
            var updatedCondition = addInvocation
                .WithLeadingTrivia(ifStatement.Condition.GetLeadingTrivia().AddRange(conditionComments))
                .WithTrailingTrivia(ifStatement.Condition.GetTrailingTrivia());

            var remainingStatements = block.Statements.RemoveAt(0);
            if (HasComment(addStatement.GetLeadingTrivia()) || HasComment(addStatement.GetTrailingTrivia()))
            {
                var first = remainingStatements[0];
                var preservedTrivia = CommentLines(addStatement.GetLeadingTrivia())
                    .AddRange(CommentLines(addStatement.GetTrailingTrivia()))
                    .AddRange(first.GetLeadingTrivia());
                remainingStatements = remainingStatements.Replace(first, first.WithLeadingTrivia(preservedTrivia));
            }

            replacement = ifStatement
                .WithCondition(updatedCondition)
                .WithStatement(block.WithStatements(remainingStatements))
                .WithAdditionalAnnotations(Formatter.Annotation);
        }

        return document.WithSyntaxRoot(root.ReplaceNode(ifStatement, replacement));
    }

    private static SyntaxTriviaList CommentLines(SyntaxTriviaList trivia)
    {
        var result = SyntaxFactory.TriviaList();
        foreach (var item in trivia)
        {
            if (item.IsKind(SyntaxKind.SingleLineCommentTrivia) || item.IsKind(SyntaxKind.MultiLineCommentTrivia))
            {
                result = result.Add(item).Add(SyntaxFactory.ElasticCarriageReturnLineFeed);
            }
        }

        return result;
    }

    private static SyntaxTriviaList CommentLines(System.Collections.Generic.IEnumerable<SyntaxTrivia> trivia) =>
        CommentLines(SyntaxFactory.TriviaList(trivia));

    private static bool HasComment(SyntaxTriviaList trivia) =>
        trivia.Any(item => item.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
                           item.IsKind(SyntaxKind.MultiLineCommentTrivia));
}
