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
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(RemoveRedundantNullConditionalGuardAnalyzer.DiagnosticId);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var diagnostic = context.Diagnostics[0];
        var ifStatement = root?.FindToken(diagnostic.Location.SourceSpan.Start).Parent?
            .AncestorsAndSelf().OfType<IfStatementSyntax>().FirstOrDefault();

        var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        if (ifStatement is null || model is null ||
            !RemoveRedundantNullConditionalGuardAnalyzer.TryGetCandidate(ifStatement, model,
                context.CancellationToken, out var body, out _) ||
            CallerArgumentExpressionSafety.IsCaptured(ifStatement, model, context.CancellationToken))
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                "Remove redundant null guard",
                token => Task.FromResult(ApplyFix(context.Document, root!, ifStatement, body, token)),
                nameof(RemoveRedundantNullConditionalGuardCodeFixProvider)),
            diagnostic);
    }

    private static Document ApplyFix(Document document, SyntaxNode root, IfStatementSyntax ifStatement,
        ExpressionStatementSyntax expressionStatement, CancellationToken cancellationToken)
    {
        if (ifStatement.Statement is not BlockSyntax block)
        {
            return document.WithSyntaxRoot(root.ReplaceNode(ifStatement,
                CodeFixTrivia.UnwrapEmbeddedStatement(document, ifStatement, expressionStatement, cancellationToken)));
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
        var replacement = expressionStatement
            .WithLeadingTrivia(leadingTrivia)
            .WithTrailingTrivia(CodeFixTrivia.TrailingAfterUnwrapping(expressionStatement, ifStatement));

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
}
