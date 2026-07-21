using System.Collections.Generic;
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

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(PreferLoopContinueCodeFixProvider)), Shared]
public sealed class PreferLoopContinueCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => [PreferLoopContinueAnalyzer.DiagnosticId];

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
                "Invert condition and continue early",
                cancellationToken => ApplyFixAsync(context.Document, ifStatement, cancellationToken),
                nameof(PreferLoopContinueCodeFixProvider)),
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
            body.Statements.Count == 0 ||
            ifStatement.ContainsDirectives ||
            ifStatement.Parent is not BlockSyntax parentBlock ||
            HasBindingCollision(ifStatement, parentBlock, body))
        {
            return document;
        }

        var negatedCondition = Negate(ifStatement.Condition.WithoutTrivia())
            .WithTriviaFrom(ifStatement.Condition);
        var guard = SyntaxFactory.IfStatement(
                negatedCondition,
                SyntaxFactory.ContinueStatement())
            .WithIfKeyword(ifStatement.IfKeyword)
            .WithOpenParenToken(ifStatement.OpenParenToken)
            .WithCloseParenToken(ifStatement.CloseParenToken);

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
    private static bool HasBindingCollision(
        IfStatementSyntax ifStatement,
        BlockSyntax parentBlock,
        BlockSyntax body)
    {
        var introducedNames = CollectDeclaredNames(body);
        if (introducedNames.Count == 0)
        {
            return false;
        }

        var ifIndex = parentBlock.Statements.IndexOf(ifStatement);
        for (var index = 0; index < ifIndex; index++)
        {
            foreach (var name in CollectDeclaredNames(parentBlock.Statements[index]))
            {
                if (introducedNames.Contains(name))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static HashSet<string> CollectDeclaredNames(SyntaxNode scope)
    {
        var names = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var node in scope.DescendantNodes())
        {
            switch (node)
            {
                case VariableDeclaratorSyntax declarator:
                    names.Add(declarator.Identifier.ValueText);
                    break;
                case SingleVariableDesignationSyntax designation:
                    names.Add(designation.Identifier.ValueText);
                    break;
                case ForEachStatementSyntax forEachStatement:
                    names.Add(forEachStatement.Identifier.ValueText);
                    break;
                case CatchDeclarationSyntax catchDeclaration:
                    names.Add(catchDeclaration.Identifier.ValueText);
                    break;
                case LocalFunctionStatementSyntax localFunction:
                    names.Add(localFunction.Identifier.ValueText);
                    break;
            }
        }

        return names;
    }


    private static void PreserveSignificantTrivia(
        BlockSyntax body,
        IfStatementSyntax ifStatement,
        List<StatementSyntax> statements)
    {
        var openingTrivia = body.OpenBraceToken.LeadingTrivia
            .AddRange(body.OpenBraceToken.TrailingTrivia);
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
                ReplaceOperator(binary, SyntaxKind.NotEqualsExpression, SyntaxKind.ExclamationEqualsToken),
            BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.NotEqualsExpression) =>
                ReplaceOperator(binary, SyntaxKind.EqualsExpression, SyntaxKind.EqualsEqualsToken),
            IdentifierNameSyntax or MemberAccessExpressionSyntax or InvocationExpressionSyntax =>
                SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, condition),
            _ => ParenthesizedNegation(condition),
        };
    }

    private static BinaryExpressionSyntax ReplaceOperator(
        BinaryExpressionSyntax expression,
        SyntaxKind expressionKind,
        SyntaxKind tokenKind) =>
        SyntaxFactory.BinaryExpression(
            expressionKind,
            expression.Left,
            SyntaxFactory.Token(tokenKind).WithTriviaFrom(expression.OperatorToken),
            expression.Right);

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
