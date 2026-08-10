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

        var negatedCondition = Negate(
                ifStatement.Condition,
                semanticModel,
                cancellationToken)
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
        var introducedLabels = CollectDeclaredLabels(body);
        if (introducedNames.Count == 0 && introducedLabels.Count == 0)
        {
            return false;
        }

        var ifIndex = parentBlock.Statements.IndexOf(ifStatement);
        for (var index = 0; index < parentBlock.Statements.Count; index++)
        {
            if (index == ifIndex)
            {
                continue;
            }

            foreach (var node in parentBlock.Statements[index].DescendantNodes())
            {
                if (index < ifIndex)
                {
                    var name = GetDeclaredName(node);
                    if (name is not null && introducedNames.Contains(name))
                    {
                        return true;
                    }
                }

                if (node is LabeledStatementSyntax label &&
                    introducedLabels.Contains(label.Identifier.ValueText))
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
            var name = GetDeclaredName(node);
            if (name is not null)
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static HashSet<string> CollectDeclaredLabels(SyntaxNode scope)
    {
        var labels = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var node in scope.DescendantNodes())
        {
            if (node is LabeledStatementSyntax label)
            {
                labels.Add(label.Identifier.ValueText);
            }
        }

        return labels;
    }

    private static string? GetDeclaredName(SyntaxNode node) => node switch
    {
        VariableDeclaratorSyntax declarator => declarator.Identifier.ValueText,
        SingleVariableDesignationSyntax designation => designation.Identifier.ValueText,
        ForEachStatementSyntax forEachStatement => forEachStatement.Identifier.ValueText,
        CatchDeclarationSyntax catchDeclaration => catchDeclaration.Identifier.ValueText,
        LocalFunctionStatementSyntax localFunction => localFunction.Identifier.ValueText,
        _ => null,
    };


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

    private static bool HasSignificantTrivia(IEnumerable<SyntaxTrivia> trivia) =>
        trivia.Any(item => !item.IsKind(SyntaxKind.WhitespaceTrivia) && !item.IsKind(SyntaxKind.EndOfLineTrivia));

    private static ExpressionSyntax Negate(
        ExpressionSyntax condition,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var originalCondition = condition;
        condition = WalkDownParentheses(condition);
        return condition switch
        {
            PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalNotExpression } logicalNot
                when condition == originalCondition &&
                    IsBuiltInLogicalNot(logicalNot, semanticModel, cancellationToken) &&
                    !HasSignificantTrivia(logicalNot.DescendantTrivia(descendIntoTrivia: true)) =>
                WalkDownParentheses(logicalNot.Operand).WithoutTrivia(),
            BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.EqualsExpression) &&
                IsBuiltInEquality(binary, semanticModel, cancellationToken) =>
                ReplaceOperator(binary.WithoutTrivia(), SyntaxKind.NotEqualsExpression, SyntaxKind.ExclamationEqualsToken),
            BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.NotEqualsExpression) &&
                IsBuiltInEquality(binary, semanticModel, cancellationToken) =>
                ReplaceOperator(binary.WithoutTrivia(), SyntaxKind.EqualsExpression, SyntaxKind.EqualsEqualsToken),
            IdentifierNameSyntax or MemberAccessExpressionSyntax or InvocationExpressionSyntax =>
                SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, condition.WithoutTrivia()),
            _ => ParenthesizedNegation(originalCondition),
        };
    }

    private static bool IsBuiltInLogicalNot(
        PrefixUnaryExpressionSyntax expression,
        SemanticModel semanticModel,
        CancellationToken cancellationToken) =>
        semanticModel.GetOperation(expression, cancellationToken) is
            Microsoft.CodeAnalysis.Operations.IUnaryOperation { OperatorMethod: null };

    private static bool IsBuiltInEquality(
        BinaryExpressionSyntax expression,
        SemanticModel semanticModel,
        CancellationToken cancellationToken) =>
        semanticModel.GetOperation(expression, cancellationToken) is
            Microsoft.CodeAnalysis.Operations.IBinaryOperation { OperatorMethod: null };

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
