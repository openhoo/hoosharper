using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Immutable;
using System.Composition;
using HooSharper.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace HooSharper.CodeFixes;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(PreferEarlyReturnCodeFixProvider)), Shared]
public sealed class PreferEarlyReturnCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => [PreferEarlyReturnAnalyzer.DiagnosticId];

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
                "Invert condition and return early",
                cancellationToken => ApplyFixAsync(context.Document, ifStatement, cancellationToken),
                nameof(PreferEarlyReturnCodeFixProvider)),
            diagnostic);
    }

    private static async Task<Document> ApplyFixAsync(
        Document document,
        IfStatementSyntax ifStatement,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || ifStatement.Statement is not BlockSyntax body || ifStatement.Parent is not BlockSyntax parentBlock)
        {
            return document;
        }

        var negatedCondition = Negate(ifStatement.Condition.WithoutTrivia())
            .WithTriviaFrom(ifStatement.Condition);
        var guard = SyntaxFactory.IfStatement(
                negatedCondition,
                SyntaxFactory.ReturnStatement())
            .WithLeadingTrivia(ifStatement.GetLeadingTrivia());

        var replacementStatements = new List<StatementSyntax> { guard };
        replacementStatements.AddRange(body.Statements);

        var index = parentBlock.Statements.IndexOf(ifStatement);
        var newParentBlock = parentBlock.WithStatements(
                parentBlock.Statements.RemoveAt(index).InsertRange(index, replacementStatements))
            .WithAdditionalAnnotations(Formatter.Annotation);

        return document.WithSyntaxRoot(root.ReplaceNode(parentBlock, newParentBlock));
    }

    private static ExpressionSyntax Negate(ExpressionSyntax condition)
    {
        condition = WalkDownParentheses(condition);
        return condition switch
        {
            PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalNotExpression } logicalNot =>
                WalkDownParentheses(logicalNot.Operand).WithoutTrivia(),
            BinaryExpressionSyntax binary => binary.Kind() switch
            {
                SyntaxKind.EqualsExpression => binary.WithOperatorToken(SyntaxFactory.Token(SyntaxKind.ExclamationEqualsToken)),
                SyntaxKind.NotEqualsExpression => binary.WithOperatorToken(SyntaxFactory.Token(SyntaxKind.EqualsEqualsToken)),
                SyntaxKind.LessThanExpression => binary.WithOperatorToken(SyntaxFactory.Token(SyntaxKind.GreaterThanEqualsToken)),
                SyntaxKind.LessThanOrEqualExpression => binary.WithOperatorToken(SyntaxFactory.Token(SyntaxKind.GreaterThanToken)),
                SyntaxKind.GreaterThanExpression => binary.WithOperatorToken(SyntaxFactory.Token(SyntaxKind.LessThanEqualsToken)),
                SyntaxKind.GreaterThanOrEqualExpression => binary.WithOperatorToken(SyntaxFactory.Token(SyntaxKind.LessThanToken)),
                _ => ParenthesizedNegation(condition),
            },
            IdentifierNameSyntax or MemberAccessExpressionSyntax or InvocationExpressionSyntax =>
                SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, condition),
            _ => ParenthesizedNegation(condition),
        };
    }

    private static ExpressionSyntax WalkDownParentheses(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression;
    }

    private static PrefixUnaryExpressionSyntax ParenthesizedNegation(ExpressionSyntax condition) =>
        SyntaxFactory.PrefixUnaryExpression(
            SyntaxKind.LogicalNotExpression,
            SyntaxFactory.ParenthesizedExpression(condition));
}
