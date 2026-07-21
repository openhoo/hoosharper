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

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(UseNotPatternCodeFixProvider)), Shared]
public sealed class UseNotPatternCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => [UseNotPatternAnalyzer.DiagnosticId];

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var diagnostic = context.Diagnostics[0];
        var logicalNot = root?.FindToken(diagnostic.Location.SourceSpan.Start).Parent?
            .AncestorsAndSelf().OfType<PrefixUnaryExpressionSyntax>()
            .FirstOrDefault(expression => expression.IsKind(SyntaxKind.LogicalNotExpression));

        if (!TryGetParts(logicalNot, out _, out _, out _))
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                "Use not pattern",
                cancellationToken => ApplyFixAsync(context.Document, logicalNot!, cancellationToken),
                nameof(UseNotPatternCodeFixProvider)),
            diagnostic);
    }

    private static async Task<Document> ApplyFixAsync(
        Document document,
        PrefixUnaryExpressionSyntax logicalNot,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || !TryGetParts(logicalNot, out var target, out var isKeyword, out var pattern))
        {
            return document;
        }

        var parenthesized = (ParenthesizedExpressionSyntax)logicalNot.Operand;
        var removedTokenTrivia = logicalNot.OperatorToken.TrailingTrivia
            .AddRange(parenthesized.OpenParenToken.LeadingTrivia)
            .AddRange(parenthesized.OpenParenToken.TrailingTrivia)
            .AddRange(target.GetLeadingTrivia());
        var notKeyword = SyntaxFactory.Token(SyntaxKind.NotKeyword)
            .WithLeadingTrivia(pattern.GetLeadingTrivia());
        pattern = pattern.WithoutLeadingTrivia().WithoutTrailingTrivia();
        if (pattern is BinaryPatternSyntax or UnaryPatternSyntax)
        {
            pattern = SyntaxFactory.ParenthesizedPattern(pattern);
        }

        var negatedPattern = SyntaxFactory.UnaryPattern(notKeyword, pattern.WithLeadingTrivia(SyntaxFactory.Space));
        var trailingTrivia = GetOriginalPattern(logicalNot).GetTrailingTrivia()
            .AddRange(CommentTrivia(parenthesized.CloseParenToken.LeadingTrivia))
            .AddRange(CommentTrivia(parenthesized.CloseParenToken.TrailingTrivia))
            .AddRange(logicalNot.GetTrailingTrivia());
        var replacement = SyntaxFactory.IsPatternExpression(target.WithoutLeadingTrivia(), isKeyword, negatedPattern)
            .WithLeadingTrivia(logicalNot.GetLeadingTrivia().AddRange(removedTokenTrivia))
            .WithTrailingTrivia(trailingTrivia);

        return document.WithSyntaxRoot(root.ReplaceNode(logicalNot, replacement));
    }

    private static bool TryGetParts(
        PrefixUnaryExpressionSyntax? logicalNot,
        out ExpressionSyntax target,
        out SyntaxToken isKeyword,
        out PatternSyntax pattern)
    {
        if (logicalNot?.Operand is not ParenthesizedExpressionSyntax { Expression: var expression } ||
            logicalNot.ContainsDirectives)
        {
            target = null!;
            isKeyword = default;
            pattern = null!;
            return false;
        }

        switch (expression)
        {
            case IsPatternExpressionSyntax isPattern when !ContainsDesignation(isPattern.Pattern):
                target = isPattern.Expression;
                isKeyword = isPattern.IsKeyword;
                pattern = isPattern.Pattern;
                return true;
            case BinaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.IsExpression,
                Left: var left,
                OperatorToken: var operatorToken,
                Right: TypeSyntax type,
            }:
                target = left;
                isKeyword = SyntaxFactory.Token(
                    operatorToken.LeadingTrivia,
                    SyntaxKind.IsKeyword,
                    operatorToken.TrailingTrivia);
                pattern = SyntaxFactory.TypePattern(type);
                return true;
            default:
                target = null!;
                isKeyword = default;
                pattern = null!;
                return false;
        }
    }

    private static SyntaxNode GetOriginalPattern(PrefixUnaryExpressionSyntax logicalNot) =>
        ((ParenthesizedExpressionSyntax)logicalNot.Operand).Expression switch
        {
            IsPatternExpressionSyntax isPattern => isPattern.Pattern,
            BinaryExpressionSyntax binary => binary.Right,
            _ => logicalNot,
        };


    private static SyntaxTriviaList CommentTrivia(SyntaxTriviaList trivia) =>
        SyntaxFactory.TriviaList(trivia.Where(item =>
            item.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
            item.IsKind(SyntaxKind.MultiLineCommentTrivia)));

    private static bool ContainsDesignation(PatternSyntax pattern) =>
        pattern.DescendantNodesAndSelf().Any(node => node is VariableDesignationSyntax);
}
