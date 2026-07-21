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
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null ||
            semanticModel is null ||
            semanticModel.GetTypeInfo(ifStatement.Condition, cancellationToken).Type?.SpecialType !=
                SpecialType.System_Boolean ||
            ifStatement.Statement is not BlockSyntax body ||
            ifStatement.Parent is not BlockSyntax parentBlock ||
            HasScopeCollision(ifStatement, parentBlock, body))
        {
            return document;
        }

        var negatedCondition = Negate(ifStatement.Condition.WithoutTrivia())
            .WithTriviaFrom(ifStatement.Condition);
        var guard = SyntaxFactory.IfStatement(
                negatedCondition,
                SyntaxFactory.ReturnStatement())
            .WithLeadingTrivia(ifStatement.GetLeadingTrivia());

        var movedStatements = body.Statements.ToList();
        PreserveSignificantTrivia(body, ifStatement, movedStatements);

        var replacementStatements = new List<StatementSyntax> { guard };
        replacementStatements.AddRange(movedStatements);

        var index = parentBlock.Statements.IndexOf(ifStatement);
        if (index < 0)
        {
            return document;
        }
        var newParentBlock = parentBlock.WithStatements(
                parentBlock.Statements.RemoveAt(index).InsertRange(index, replacementStatements))
            .WithAdditionalAnnotations(Formatter.Annotation);

        return document.WithSyntaxRoot(root.ReplaceNode(parentBlock, newParentBlock));
    }

    private static bool HasScopeCollision(
        IfStatementSyntax ifStatement,
        BlockSyntax parentBlock,
        BlockSyntax body)
    {
        var movedNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var statement in body.Statements)
        {
            switch (statement)
            {
                case LocalDeclarationStatementSyntax declaration:
                    foreach (var variable in declaration.Declaration.Variables)
                    {
                        movedNames.Add(variable.Identifier.ValueText);
                    }

                    break;
                case LocalFunctionStatementSyntax localFunction:
                    movedNames.Add(localFunction.Identifier.ValueText);
                    break;
            }

            foreach (var designation in statement.DescendantNodes(ShouldDescendInto)
                         .OfType<SingleVariableDesignationSyntax>()
                         .Where(designation => designation.Ancestors().OfType<BlockSyntax>().FirstOrDefault() == body))
            {
                movedNames.Add(designation.Identifier.ValueText);
            }
        }

        if (movedNames.Count == 0)
        {
            return false;
        }

        foreach (var statement in parentBlock.Statements)
        {
            if (statement == ifStatement)
            {
                continue;
            }

            foreach (var node in statement.DescendantNodesAndSelf(ShouldDescendInto))
            {
                var name = node switch
                {
                    VariableDeclaratorSyntax variable => variable.Identifier.ValueText,
                    SingleVariableDesignationSyntax designation => designation.Identifier.ValueText,
                    ForEachStatementSyntax forEachStatement => forEachStatement.Identifier.ValueText,
                    CatchDeclarationSyntax catchDeclaration => catchDeclaration.Identifier.ValueText,
                    LocalFunctionStatementSyntax localFunction => localFunction.Identifier.ValueText,
                    _ => null,
                };

                if (name is not null && movedNames.Contains(name))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ShouldDescendInto(SyntaxNode node) =>
        node is not AnonymousFunctionExpressionSyntax and
        not LocalFunctionStatementSyntax and
        not TypeDeclarationSyntax;

    private static void PreserveSignificantTrivia(
        BlockSyntax body,
        IfStatementSyntax ifStatement,
        List<StatementSyntax> statements)
    {
        var openingTrivia = body.OpenBraceToken.LeadingTrivia.AddRange(body.OpenBraceToken.TrailingTrivia);
        if (HasSignificantTrivia(openingTrivia))
        {
            statements[0] = statements[0].WithLeadingTrivia(openingTrivia.AddRange(statements[0].GetLeadingTrivia()));
        }

        var closingTrivia = body.CloseBraceToken.LeadingTrivia;
        if (HasSignificantTrivia(closingTrivia))
        {
            var last = statements.Count - 1;
            statements[last] = statements[last].WithTrailingTrivia(
                statements[last].GetTrailingTrivia().AddRange(closingTrivia));
        }

        var trailingTrivia = ifStatement.GetTrailingTrivia();
        if (HasSignificantTrivia(trailingTrivia))
        {
            var last = statements.Count - 1;
            statements[last] = statements[last].WithTrailingTrivia(
                statements[last].GetTrailingTrivia().AddRange(trailingTrivia));
        }
    }

    private static bool HasSignificantTrivia(SyntaxTriviaList trivia) =>
        trivia.Any(item => !item.IsKind(SyntaxKind.WhitespaceTrivia) && !item.IsKind(SyntaxKind.EndOfLineTrivia));

    private static ExpressionSyntax Negate(ExpressionSyntax condition)
    {
        condition = WalkDownParentheses(condition);
        return condition switch
        {
            PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalNotExpression } logicalNot =>
                WalkDownParentheses(logicalNot.Operand).WithoutTrivia(),
            BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.EqualsExpression) =>
                binary.WithOperatorToken(SyntaxFactory.Token(SyntaxKind.ExclamationEqualsToken)),
            BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.NotEqualsExpression) =>
                binary.WithOperatorToken(SyntaxFactory.Token(SyntaxKind.EqualsEqualsToken)),
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
