using System.Collections.Generic;
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

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(MergeNestedIfCodeFixProvider)), Shared]
public sealed class MergeNestedIfCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => [MergeNestedIfAnalyzer.DiagnosticId];

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken)
            .ConfigureAwait(false);
        var diagnostic = context.Diagnostics[0];
        var outerIf = root?.FindToken(diagnostic.Location.SourceSpan.Start).Parent?
            .AncestorsAndSelf().OfType<IfStatementSyntax>().FirstOrDefault();

        if (!TryGetInnerIf(outerIf, out _) ||
            semanticModel is null ||
            !AreAllConditionsOrdinaryBoolean(semanticModel, outerIf!, context.CancellationToken))
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                "Merge nested if statements",
                cancellationToken => ApplyFixAsync(context.Document, outerIf!, cancellationToken),
                nameof(MergeNestedIfCodeFixProvider)),
            diagnostic);
    }

    private static async Task<Document> ApplyFixAsync(
        Document document,
        IfStatementSyntax outerIf,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || outerIf.ContainsDirectives || !TryGetInnerIf(outerIf, out var innerIf))
        {
            return document;
        }

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (semanticModel is null ||
            !AreAllConditionsOrdinaryBoolean(semanticModel, outerIf, cancellationToken))
        {
            return document;
        }

        ExpressionSyntax combinedCondition = PrepareOperand(outerIf.Condition);
        var deepestIf = innerIf;
        while (true)
        {
            combinedCondition = SyntaxFactory.BinaryExpression(
                SyntaxKind.LogicalAndExpression,
                combinedCondition,
                SyntaxFactory.Token(SyntaxKind.AmpersandAmpersandToken),
                PrepareOperand(deepestIf.Condition));

            if (!TryGetInnerIf(deepestIf, out var nextIf))
            {
                break;
            }

            deepestIf = nextIf;
        }

        var mergedStatement = deepestIf.Statement
            .WithLeadingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);
        var replacement = outerIf
            .WithCondition(combinedCondition)
            .WithCloseParenToken(outerIf.CloseParenToken.WithTrailingTrivia())
            .WithStatement(mergedStatement)
            .WithElse(null)
            .WithLeadingTrivia(PreserveRemovedComments(outerIf, innerIf))
            .WithTrailingTrivia(outerIf.GetTrailingTrivia())
            .WithAdditionalAnnotations(Formatter.Annotation);

        return document.WithSyntaxRoot(root.ReplaceNode(outerIf, replacement));
    }

    private static ExpressionSyntax PrepareOperand(
        ExpressionSyntax expression,
        bool isRightOperand = false)
    {
        var operand = expression.WithoutLeadingTrivia().WithoutTrailingTrivia();
        return NeedsParentheses(operand) ||
            isRightOperand && operand.IsKind(SyntaxKind.LogicalAndExpression)
            ? SyntaxFactory.ParenthesizedExpression(operand)
            : operand;
    }

    private static bool NeedsParentheses(ExpressionSyntax expression)
    {
        if (expression is BinaryExpressionSyntax binary)
        {
            return binary.IsKind(SyntaxKind.LogicalOrExpression) ||
                binary.IsKind(SyntaxKind.CoalesceExpression);
        }

        return expression is ConditionalExpressionSyntax or AssignmentExpressionSyntax or
            LambdaExpressionSyntax or AnonymousMethodExpressionSyntax or QueryExpressionSyntax;
    }

    private static SyntaxTriviaList PreserveRemovedComments(IfStatementSyntax outerIf, IfStatementSyntax innerIf)
    {
        var leading = outerIf.GetLeadingTrivia();
        var indentation = SyntaxFactory.TriviaList(
            leading.Where(static trivia => trivia.IsKind(SyntaxKind.WhitespaceTrivia)));
        var comments = outerIf.DescendantTrivia(descendIntoTrivia: true)
            .Where(trivia => outerIf.Span.Contains(trivia.Span) &&
                IsComment(trivia) &&
                !outerIf.Condition.Span.Contains(trivia.Span) &&
                !innerIf.Condition.Span.Contains(trivia.Span) &&
                !innerIf.Statement.FullSpan.Contains(trivia.Span));

        foreach (var comment in comments)
        {
            leading = leading.Add(comment);
            leading = leading.Add(SyntaxFactory.ElasticCarriageReturnLineFeed);
            leading = leading.AddRange(indentation);
        }

        return leading;
    }

    private static bool IsComment(SyntaxTrivia trivia) =>
        trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
        trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) ||
        trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
        trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia);

    private static bool AreAllConditionsOrdinaryBoolean(
        SemanticModel semanticModel,
        IfStatementSyntax outerIf,
        CancellationToken cancellationToken)
    {
        var current = outerIf;
        while (true)
        {
            if (semanticModel.GetTypeInfo(current.Condition, cancellationToken).Type?.SpecialType !=
                SpecialType.System_Boolean)
            {
                return false;
            }

            if (!TryGetInnerIf(current, out var innerIf))
            {
                return true;
            }

            current = innerIf;
        }
    }

    private static bool TryGetInnerIf(IfStatementSyntax? outerIf, out IfStatementSyntax innerIf)
    {
        if (outerIf?.Else is null &&
            outerIf?.Statement is BlockSyntax { Statements.Count: 1 } block &&
            block.Statements[0] is IfStatementSyntax { Else: null } candidate)
        {
            innerIf = candidate;
            return true;
        }

        innerIf = null!;
        return false;
    }
}
