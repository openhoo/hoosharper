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

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(UseDictionaryTryAddCodeFixProvider)), Shared]
public sealed class UseDictionaryTryAddCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => [UseDictionaryTryAddAnalyzer.DiagnosticId];

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
                "Use Dictionary.TryAdd",
                cancellationToken => ApplyFixAsync(context.Document, ifStatement, cancellationToken),
                nameof(UseDictionaryTryAddCodeFixProvider)),
            diagnostic);
    }

    private static async Task<Document> ApplyFixAsync(
        Document document,
        IfStatementSyntax ifStatement,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null ||
            ifStatement.Condition is not PrefixUnaryExpressionSyntax
            {
                OperatorToken: var notToken,
                Operand: InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax containsMember,
                } containsInvocation,
            } ||
            ifStatement.Statement is not BlockSyntax { Statements.Count: > 0 } block ||
            block.Statements[0] is not ExpressionStatementSyntax
            {
                Expression: InvocationExpressionSyntax addInvocation,
            } addStatement ||
            addInvocation.ArgumentList.Arguments.Count != 2)
        {
            return document;
        }

        var tryAddInvocation = containsInvocation
            .WithExpression(containsMember.WithName(
                SyntaxFactory.IdentifierName("TryAdd").WithTriviaFrom(containsMember.Name)))
            .WithArgumentList(addInvocation.ArgumentList)
            .WithTriviaFrom(containsInvocation)
            .WithAdditionalAnnotations(Formatter.Annotation);

        if (block.Statements.Count == 1)
        {
            var leadingTrivia = ifStatement.GetLeadingTrivia()
                .AddRange(CommentLines(ifStatement.IfKeyword.TrailingTrivia))
                .AddRange(CommentLines(ifStatement.OpenParenToken.LeadingTrivia))
                .AddRange(CommentLines(ifStatement.OpenParenToken.TrailingTrivia))
                .AddRange(CommentLines(notToken.LeadingTrivia))
                .AddRange(CommentLines(notToken.TrailingTrivia))
                .AddRange(CommentLines(ifStatement.CloseParenToken.LeadingTrivia))
                .AddRange(CommentLines(ifStatement.CloseParenToken.TrailingTrivia))
                .AddRange(CommentLines(ifStatement.Condition.DescendantTrivia(descendIntoTrivia: true)))
                .AddRange(CommentLines(block.OpenBraceToken.LeadingTrivia))
                .AddRange(CommentLines(block.OpenBraceToken.TrailingTrivia))
                .AddRange(addStatement.GetLeadingTrivia())
                .AddRange(CommentLines(addInvocation.Expression.DescendantTrivia(descendIntoTrivia: true)));
            var trailingTrivia = InlineComments(addStatement.GetTrailingTrivia())
                .Add(SyntaxFactory.LineFeed)
                .AddRange(CommentLines(block.CloseBraceToken.LeadingTrivia))
                .AddRange(InlineComments(block.CloseBraceToken.TrailingTrivia));
            var replacement = addStatement.WithoutTrivia()
                .WithExpression(tryAddInvocation.WithoutTrivia())
                .WithLeadingTrivia(leadingTrivia)
                .WithTrailingTrivia(trailingTrivia)
                .WithAdditionalAnnotations(Formatter.Annotation);
            return document.WithSyntaxRoot(root.ReplaceNode(ifStatement, replacement));
        }

        var conditionLeadingTrivia = InlineComments(notToken.LeadingTrivia)
            .AddRange(InlineComments(notToken.TrailingTrivia));
        var updatedCondition = tryAddInvocation.WithoutTrivia().WithLeadingTrivia(conditionLeadingTrivia);
        var remainingStatements = block.Statements.RemoveAt(0);
        var firstRemaining = remainingStatements[0];
        var removedAddTrivia = CommentLines(addInvocation.Expression.DescendantTrivia(descendIntoTrivia: true))
            .AddRange(CommentLines(addStatement.SemicolonToken.LeadingTrivia))
            .AddRange(CommentLines(addStatement.GetTrailingTrivia()))
            .AddRange(firstRemaining.GetLeadingTrivia());
        remainingStatements = remainingStatements.Replace(
            firstRemaining,
            firstRemaining.WithLeadingTrivia(removedAddTrivia));

        var updatedBlock = block.WithStatements(remainingStatements);
        var updatedIf = ifStatement
            .WithCondition(updatedCondition)
            .WithStatement(updatedBlock)
            .WithAdditionalAnnotations(Formatter.Annotation);
        return document.WithSyntaxRoot(root.ReplaceNode(ifStatement, updatedIf));
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

    private static SyntaxTriviaList InlineComments(SyntaxTriviaList trivia)
    {
        var result = SyntaxFactory.TriviaList();
        foreach (var item in trivia)
        {
            if (item.IsKind(SyntaxKind.SingleLineCommentTrivia) || item.IsKind(SyntaxKind.MultiLineCommentTrivia))
            {
                result = result.Add(SyntaxFactory.Space).Add(item);
            }
        }

        return result;
    }

    private static SyntaxTriviaList CommentLines(System.Collections.Generic.IEnumerable<SyntaxTrivia> trivia) =>
        CommentLines(SyntaxFactory.TriviaList(trivia));
}
