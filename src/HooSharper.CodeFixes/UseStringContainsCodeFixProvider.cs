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

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(UseStringContainsCodeFixProvider)), Shared]
public sealed class UseStringContainsCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(UseStringContainsAnalyzer.DiagnosticId);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var diagnostic = context.Diagnostics[0];
        var comparison = root?.FindToken(diagnostic.Location.SourceSpan.Start).Parent?
            .AncestorsAndSelf().OfType<BinaryExpressionSyntax>().FirstOrDefault();
        if (comparison is null)
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                "Use string.Contains",
                cancellationToken => ApplyFixAsync(context.Document, comparison, cancellationToken),
                nameof(UseStringContainsCodeFixProvider)),
            diagnostic);
    }

    private static async Task<Document> ApplyFixAsync(
        Document document,
        BinaryExpressionSyntax comparison,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || semanticModel is null ||
            !TryGetInvocationAndResult(comparison, semanticModel, cancellationToken, out var invocation, out var found))
        {
            return document;
        }

        var memberAccess = (MemberAccessExpressionSyntax)invocation.Expression;
        var containsMember = memberAccess.WithName(
            SyntaxFactory.IdentifierName("Contains").WithTriviaFrom(memberAccess.Name));
        ExpressionSyntax replacement = invocation.WithExpression(containsMember)
            .WithoutLeadingTrivia()
            .WithoutTrailingTrivia();
        if (!found)
        {
            replacement = SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, replacement);
        }

        var removedTrivia = comparison.DescendantTrivia()
            .Where(trivia => comparison.Span.Contains(trivia.Span) &&
                             !invocation.Span.Contains(trivia.Span)).ToArray();
        var hasLineComment = removedTrivia.Any(trivia => trivia.IsKind(SyntaxKind.SingleLineCommentTrivia));
        var interstitialTrivia = SyntaxFactory.TriviaList();
        for (var index = 0; index < removedTrivia.Length; index++)
        {
            var trivia = removedTrivia[index];
            if (trivia.IsKind(SyntaxKind.WhitespaceTrivia))
            {
                // Preserve comment separation and continuation indentation,
                // while dropping spaces belonging to the removed comparison.
                if (hasLineComment &&
                    (index > 0 && removedTrivia[index - 1].IsKind(SyntaxKind.EndOfLineTrivia) ||
                     index + 1 < removedTrivia.Length && removedTrivia[index + 1].IsKind(SyntaxKind.SingleLineCommentTrivia)))
                {
                    interstitialTrivia = interstitialTrivia.Add(trivia);
                }
            }
            else if (!trivia.IsKind(SyntaxKind.EndOfLineTrivia) || hasLineComment)
            {
                interstitialTrivia = interstitialTrivia.Add(trivia);
            }
        }

        var trailingTrivia = comparison.GetTrailingTrivia();
        if (hasLineComment && interstitialTrivia.Count > 0 &&
            interstitialTrivia[interstitialTrivia.Count - 1].IsKind(SyntaxKind.WhitespaceTrivia))
        {
            trailingTrivia = SyntaxFactory.TriviaList(trailingTrivia
                .SkipWhile(trivia => trivia.IsKind(SyntaxKind.WhitespaceTrivia)));
        }

        replacement = replacement
            .WithLeadingTrivia(comparison.GetLeadingTrivia())
            .WithTrailingTrivia(interstitialTrivia.AddRange(trailingTrivia));

        return document.WithSyntaxRoot(root.ReplaceNode(comparison, replacement));
    }

    private static bool TryGetInvocationAndResult(
        BinaryExpressionSyntax comparison,
        SemanticModel semanticModel,
        CancellationToken cancellationToken,
        out InvocationExpressionSyntax invocation,
        out bool found)
    {
        if (comparison.Left is InvocationExpressionSyntax leftInvocation &&
            TryGetConstantValue(comparison.Right, semanticModel, cancellationToken, out var rightValue) &&
            TryGetFoundResult(comparison.Kind(), rightValue, invocationOnLeft: true, out found))
        {
            invocation = leftInvocation;
            return IsIndexOfSyntax(invocation);
        }

        if (comparison.Right is InvocationExpressionSyntax rightInvocation &&
            TryGetConstantValue(comparison.Left, semanticModel, cancellationToken, out var leftValue) &&
            TryGetFoundResult(comparison.Kind(), leftValue, invocationOnLeft: false, out found))
        {
            invocation = rightInvocation;
            return IsIndexOfSyntax(invocation);
        }

        invocation = null!;
        found = default;
        return false;
    }

    private static bool TryGetConstantValue(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        CancellationToken cancellationToken,
        out int value)
    {
        var constant = semanticModel.GetConstantValue(expression, cancellationToken);
        if (constant.HasValue && constant.Value is int intValue)
        {
            value = intValue;
            return true;
        }

        value = default;
        return false;
    }

    private static bool IsIndexOfSyntax(InvocationExpressionSyntax invocation) =>
        invocation.Expression is MemberAccessExpressionSyntax
        {
            RawKind: (int)SyntaxKind.SimpleMemberAccessExpression,
            Name.Identifier.ValueText: "IndexOf",
        };

    private static bool TryGetFoundResult(
        SyntaxKind kind,
        int constant,
        bool invocationOnLeft,
        out bool found)
    {
        bool? result = (invocationOnLeft, kind, constant) switch
        {
            (true, SyntaxKind.GreaterThanOrEqualExpression, 0) => true,
            (true, SyntaxKind.GreaterThanExpression, -1) => true,
            (true, SyntaxKind.NotEqualsExpression, -1) => true,
            (true, SyntaxKind.LessThanExpression, 0) => false,
            (true, SyntaxKind.LessThanOrEqualExpression, -1) => false,
            (true, SyntaxKind.EqualsExpression, -1) => false,
            (false, SyntaxKind.LessThanOrEqualExpression, 0) => true,
            (false, SyntaxKind.LessThanExpression, -1) => true,
            (false, SyntaxKind.NotEqualsExpression, -1) => true,
            (false, SyntaxKind.GreaterThanExpression, 0) => false,
            (false, SyntaxKind.GreaterThanOrEqualExpression, -1) => false,
            (false, SyntaxKind.EqualsExpression, -1) => false,
            _ => null,
        };

        if (result is bool value)
        {
            found = value;
            return true;
        }

        found = default;
        return false;
    }
}
