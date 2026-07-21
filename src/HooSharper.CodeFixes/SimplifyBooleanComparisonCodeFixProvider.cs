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

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(SimplifyBooleanComparisonCodeFixProvider)), Shared]
public sealed class SimplifyBooleanComparisonCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => [SimplifyBooleanComparisonAnalyzer.DiagnosticId];

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var diagnostic = context.Diagnostics[0];
        var comparison = root?.FindToken(diagnostic.Location.SourceSpan.Start).Parent?
            .AncestorsAndSelf().OfType<BinaryExpressionSyntax>().FirstOrDefault();

        if (comparison is null || !TryGetBooleanLiteralOperand(comparison, out _))
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                "Simplify boolean comparison",
                cancellationToken => ApplyFixAsync(context.Document, comparison, cancellationToken),
                nameof(SimplifyBooleanComparisonCodeFixProvider)),
            diagnostic);
    }

    private static async Task<Document> ApplyFixAsync(
        Document document,
        BinaryExpressionSyntax comparison,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || !TryGetBooleanLiteralOperand(comparison, out var expression))
        {
            return document;
        }

        var literal = comparison.Left.IsKind(SyntaxKind.TrueLiteralExpression)
            ? comparison.Left
            : comparison.Right;
        var literalValue = literal.IsKind(SyntaxKind.TrueLiteralExpression);
        var preserveValue = comparison.IsKind(SyntaxKind.EqualsExpression) == literalValue;

        var interstitialTrivia = SyntaxFactory.TriviaList(comparison.DescendantTrivia()
            .Where(trivia =>
                !expression.Span.Contains(trivia.Span) &&
                !trivia.IsKind(SyntaxKind.WhitespaceTrivia) &&
                !trivia.IsKind(SyntaxKind.EndOfLineTrivia)));
        var expressionWithoutOuterTrivia = expression.WithoutLeadingTrivia().WithoutTrailingTrivia();
        var replacement = preserveValue
            ? expressionWithoutOuterTrivia
            : Negate(expressionWithoutOuterTrivia);
        replacement = replacement.WithLeadingTrivia(comparison.GetLeadingTrivia())
            .WithTrailingTrivia(interstitialTrivia.AddRange(comparison.GetTrailingTrivia()));

        return document.WithSyntaxRoot(root.ReplaceNode(comparison, replacement));
    }

    private static ExpressionSyntax Negate(ExpressionSyntax expression)
    {
        var unparenthesized = WalkDownParentheses(expression);
        if (unparenthesized is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalNotExpression } logicalNot)
        {
            return WalkDownParentheses(logicalNot.Operand).WithoutLeadingTrivia().WithoutTrailingTrivia();
        }

        return SyntaxFactory.PrefixUnaryExpression(
            SyntaxKind.LogicalNotExpression,
            NeedsParentheses(unparenthesized)
                ? SyntaxFactory.ParenthesizedExpression(unparenthesized.WithoutTrivia())
                : unparenthesized.WithoutTrivia());
    }

    private static ExpressionSyntax WalkDownParentheses(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression;
    }

    private static bool TryGetBooleanLiteralOperand(
        BinaryExpressionSyntax comparison,
        out ExpressionSyntax expression)
    {
        if (IsBooleanLiteral(comparison.Right))
        {
            expression = comparison.Left;
            return true;
        }

        if (IsBooleanLiteral(comparison.Left))
        {
            expression = comparison.Right;
            return true;
        }

        expression = null!;
        return false;
    }

    private static bool IsBooleanLiteral(ExpressionSyntax expression) =>
        expression.IsKind(SyntaxKind.TrueLiteralExpression) ||
        expression.IsKind(SyntaxKind.FalseLiteralExpression);

    private static bool NeedsParentheses(ExpressionSyntax expression) => expression is not (
        IdentifierNameSyntax or
        GenericNameSyntax or
        MemberAccessExpressionSyntax or
        MemberBindingExpressionSyntax or
        InvocationExpressionSyntax or
        ElementAccessExpressionSyntax or
        ElementBindingExpressionSyntax or
        ThisExpressionSyntax or
        BaseExpressionSyntax or
        ObjectCreationExpressionSyntax or
        ImplicitObjectCreationExpressionSyntax or
        ParenthesizedExpressionSyntax or
        PrefixUnaryExpressionSyntax);
}
