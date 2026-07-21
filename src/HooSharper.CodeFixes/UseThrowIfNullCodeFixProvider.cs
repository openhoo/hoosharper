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

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(UseThrowIfNullCodeFixProvider)), Shared]
public sealed class UseThrowIfNullCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => [UseThrowIfNullAnalyzer.DiagnosticId];

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
                "Use ArgumentNullException.ThrowIfNull",
                cancellationToken => ApplyFixAsync(context.Document, ifStatement, cancellationToken),
                nameof(UseThrowIfNullCodeFixProvider)),
            diagnostic);
    }

    private static async Task<Document> ApplyFixAsync(
        Document document,
        IfStatementSyntax ifStatement,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || !TryGetParts(ifStatement, out var checkedExpression, out var creation, out var throwStatement))
        {
            return document;
        }

        var typeExpression = SyntaxFactory.ParseExpression(creation.Type.WithoutTrivia().ToString());
        var invocation = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                typeExpression,
                SyntaxFactory.IdentifierName("ThrowIfNull")),
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(checkedExpression.WithoutTrivia()))));

        var replacement = SyntaxFactory.ExpressionStatement(invocation)
            .WithLeadingTrivia(ifStatement.GetLeadingTrivia())
            .WithTrailingTrivia(GetTrailingTrivia(ifStatement, throwStatement))
            .WithAdditionalAnnotations(Formatter.Annotation);

        var comments = ifStatement.DescendantTrivia(descendIntoTrivia: true)
            .Where(trivia => trivia.SpanStart >= ifStatement.SpanStart &&
                             trivia.Span.End <= ifStatement.Span.End &&
                             (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
                              trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)))
            .ToArray();
        if (comments.Length > 0)
        {
            var indentation = ifStatement.GetLeadingTrivia()
                .Where(static trivia => trivia.IsKind(SyntaxKind.WhitespaceTrivia));
            var leading = replacement.GetLeadingTrivia();
            foreach (var comment in comments)
            {
                leading = leading.Add(comment);
                leading = leading.Add(SyntaxFactory.ElasticCarriageReturnLineFeed);
                leading = leading.AddRange(indentation);
            }

            replacement = replacement.WithLeadingTrivia(leading);
        }

        return document.WithSyntaxRoot(root.ReplaceNode(ifStatement, replacement));
    }

    private static SyntaxTriviaList GetTrailingTrivia(
        IfStatementSyntax ifStatement,
        ThrowStatementSyntax throwStatement)
    {
        var trailing = throwStatement.SemicolonToken.TrailingTrivia;
        return trailing.Any(static trivia => trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
                                             trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
            ? trailing
            : ifStatement.GetTrailingTrivia();
    }

    private static bool TryGetParts(
        IfStatementSyntax ifStatement,
        out ExpressionSyntax checkedExpression,
        out ObjectCreationExpressionSyntax creation,
        out ThrowStatementSyntax throwStatement)
    {
        checkedExpression = GetCheckedExpression(ifStatement.Condition)!;
        throwStatement = ifStatement.Statement switch
        {
            ThrowStatementSyntax directThrow => directThrow,
            BlockSyntax { Statements.Count: 1 } block when block.Statements[0] is ThrowStatementSyntax blockThrow => blockThrow,
            _ => null!,
        };
        creation = throwStatement?.Expression as ObjectCreationExpressionSyntax ?? null!;
        return checkedExpression is not null && creation is not null;
    }

    private static ExpressionSyntax? GetCheckedExpression(ExpressionSyntax condition)
    {
        condition = WalkDownParentheses(condition);
        if (condition is IsPatternExpressionSyntax
            {
                Expression: var expression,
                Pattern: ConstantPatternSyntax { Expression.RawKind: (int)SyntaxKind.NullLiteralExpression },
            })
        {
            return WalkDownParentheses(expression);
        }

        if (condition is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.EqualsExpression } equality)
        {
            return equality.Left.IsKind(SyntaxKind.NullLiteralExpression)
                ? WalkDownParentheses(equality.Right)
                : WalkDownParentheses(equality.Left);
        }

        return null;
    }

    private static ExpressionSyntax WalkDownParentheses(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression;
    }
}
