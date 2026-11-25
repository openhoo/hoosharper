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
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Formatting;

namespace HooSharper.CodeFixes;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(SimplifyBooleanReturnCodeFixProvider)), Shared]
public sealed class SimplifyBooleanReturnCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(SimplifyBooleanReturnAnalyzer.DiagnosticId);

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
                "Simplify boolean return",
                cancellationToken => ApplyFixAsync(context.Document, ifStatement, cancellationToken),
                nameof(SimplifyBooleanReturnCodeFixProvider)),
            diagnostic);
    }

    private static async Task<Document> ApplyFixAsync(
        Document document,
        IfStatementSyntax ifStatement,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || semanticModel is null ||
            !SimplifyBooleanReturnAnalyzer.TryGetReturns(ifStatement, out var statements, out var index,
                out var branchValue, out var nextReturn) ||
            ContainsUserDefinedNot(semanticModel.GetOperation(ifStatement.Condition, cancellationToken)))
        {
            return document;
        }

        var condition = ifStatement.Condition.WithoutLeadingTrivia().WithoutTrailingTrivia();
        var expression = branchValue ? condition : Negate(condition);
        var significantTrivia = CollectSignificantTrivia(ifStatement, nextReturn);
        var replacement = SyntaxFactory.ReturnStatement(expression)
            .WithLeadingTrivia(ifStatement.GetLeadingTrivia())
            .WithTrailingTrivia(significantTrivia)
            .WithAdditionalAnnotations(Formatter.Annotation);

        statements = statements.Replace(ifStatement, replacement).RemoveAt(index + 1);
        SyntaxNode updated = ifStatement.Parent switch
        {
            SwitchSectionSyntax section => section.WithStatements(statements),
            BlockSyntax block => block.WithStatements(statements),
            _ => throw new System.InvalidOperationException(),
        };
        return await CodeFixTrivia.FormatChangesAsync(document, root.ReplaceNode(ifStatement.Parent!, updated), cancellationToken).ConfigureAwait(false);
    }

    private static SyntaxTriviaList CollectSignificantTrivia(
        IfStatementSyntax ifStatement,
        ReturnStatementSyntax nextReturn)
    {
        var trivia = new List<SyntaxTrivia>();
        AddSignificantTrivia(trivia, ifStatement.DescendantTrivia().Where(item =>
            item.SpanStart >= ifStatement.SpanStart &&
            !ifStatement.Condition.Span.Contains(item.Span)));
        AddSignificantTrivia(trivia, nextReturn.DescendantTrivia());
        return SyntaxFactory.TriviaList(WithLineBreaks(trivia));
    }

    private static void AddSignificantTrivia(List<SyntaxTrivia> target, IEnumerable<SyntaxTrivia> source) =>
        target.AddRange(source.Where(item =>
            !item.IsKind(SyntaxKind.WhitespaceTrivia) &&
            !item.IsKind(SyntaxKind.EndOfLineTrivia)));

    private static IEnumerable<SyntaxTrivia> WithLineBreaks(IEnumerable<SyntaxTrivia> trivia)
    {
        foreach (var item in trivia)
        {
            yield return SyntaxFactory.ElasticCarriageReturnLineFeed;
            yield return item;
            if (item.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
                item.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia))
            {
                yield return SyntaxFactory.ElasticCarriageReturnLineFeed;
            }
        }
    }

    private static ExpressionSyntax Negate(ExpressionSyntax expression)
    {
        if (expression.DescendantTrivia().Any(item =>
            !item.IsKind(SyntaxKind.WhitespaceTrivia) && !item.IsKind(SyntaxKind.EndOfLineTrivia)))
        {
            return SyntaxFactory.PrefixUnaryExpression(
                SyntaxKind.LogicalNotExpression,
                SyntaxFactory.ParenthesizedExpression(expression));
        }

        var unparenthesized = WalkDownParentheses(expression);
        if (unparenthesized is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalNotExpression } logicalNot &&
            logicalNot.GetLeadingTrivia().Count == 0 &&
            logicalNot.GetTrailingTrivia().Count == 0)
        {
            return WalkDownParentheses(logicalNot.Operand).WithoutTrivia();
        }

        return SyntaxFactory.PrefixUnaryExpression(
            SyntaxKind.LogicalNotExpression,
            NeedsParentheses(unparenthesized)
                ? SyntaxFactory.ParenthesizedExpression(unparenthesized.WithoutTrivia())
                : unparenthesized.WithoutTrivia());
    }

    private static bool ContainsUserDefinedNot(IOperation? operation) =>
        operation is not null &&
        operation.DescendantsAndSelf().OfType<IUnaryOperation>().Any(unary =>
            unary.OperatorKind == UnaryOperatorKind.Not && unary.OperatorMethod is not null);

    private static ExpressionSyntax WalkDownParentheses(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression;
    }

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
