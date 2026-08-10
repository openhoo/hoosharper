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
using Microsoft.CodeAnalysis.Operations;

namespace HooSharper.CodeFixes;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(SimplifyBooleanReturnCodeFixProvider)), Shared]
public sealed class SimplifyBooleanReturnCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => [SimplifyBooleanReturnAnalyzer.DiagnosticId];

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
        if (root is null || semanticModel is null || ifStatement.Parent is not BlockSyntax parentBlock ||
            !TryGetReturnedLiteral(ifStatement.Statement, out var branchValue) ||
            ContainsUserDefinedNot(semanticModel.GetOperation(ifStatement.Condition, cancellationToken)))
        {
            return document;
        }

        var index = parentBlock.Statements.IndexOf(ifStatement);
        if (index < 0 || index + 1 >= parentBlock.Statements.Count)
        {
            return document;
        }

        if (parentBlock.Statements[index + 1] is not ReturnStatementSyntax nextReturn ||
            !TryGetReturnedLiteral(nextReturn, out var nextValue) || branchValue == nextValue)
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

        var statements = parentBlock.Statements.RemoveAt(index).Insert(index, replacement).RemoveAt(index + 1);
        var newParentBlock = parentBlock.WithStatements(statements);
        return document.WithSyntaxRoot(root.ReplaceNode(parentBlock, newParentBlock));
    }

    private static SyntaxTriviaList CollectSignificantTrivia(
        IfStatementSyntax ifStatement,
        ReturnStatementSyntax nextReturn)
    {
        var trivia = new List<SyntaxTrivia>();
        AddSignificantTrivia(trivia, ifStatement.DescendantTrivia().Where(item =>
            !ifStatement.Condition.FullSpan.Contains(item.Span)));
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

    private static bool TryGetReturnedLiteral(StatementSyntax statement, out bool value)
    {
        var returnStatement = statement switch
        {
            ReturnStatementSyntax directReturn => directReturn,
            BlockSyntax { Statements.Count: 1 } block when block.Statements[0] is ReturnStatementSyntax blockReturn =>
                blockReturn,
            _ => null,
        };

        if (returnStatement?.Expression is LiteralExpressionSyntax literal)
        {
            if (literal.IsKind(SyntaxKind.TrueLiteralExpression))
            {
                value = true;
                return true;
            }

            if (literal.IsKind(SyntaxKind.FalseLiteralExpression))
            {
                value = false;
                return true;
            }
        }

        value = false;
        return false;
    }

    private static ExpressionSyntax Negate(ExpressionSyntax expression)
    {
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
