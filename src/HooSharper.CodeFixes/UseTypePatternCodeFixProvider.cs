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

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(UseTypePatternCodeFixProvider)), Shared]
public sealed class UseTypePatternCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => [UseTypePatternAnalyzer.DiagnosticId];

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var diagnostic = context.Diagnostics[0];
        var declaration = root?.FindToken(diagnostic.Location.SourceSpan.Start).Parent?
            .AncestorsAndSelf().OfType<LocalDeclarationStatementSyntax>().FirstOrDefault();

        if (declaration is null)
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                "Use type pattern",
                cancellationToken => ApplyFixAsync(context.Document, declaration, cancellationToken),
                nameof(UseTypePatternCodeFixProvider)),
            diagnostic);
    }

    private static async Task<Document> ApplyFixAsync(
        Document document,
        LocalDeclarationStatementSyntax declaration,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null ||
            declaration.Parent is not BlockSyntax block ||
            declaration.Declaration.Variables.Count != 1)
        {
            return document;
        }

        var declarator = declaration.Declaration.Variables[0];
        if (declarator.Initializer?.Value is not BinaryExpressionSyntax asExpression ||
            !asExpression.IsKind(SyntaxKind.AsExpression))
        {
            return document;
        }

        var declarationIndex = block.Statements.IndexOf(declaration);
        if (declarationIndex < 0 || declarationIndex + 1 >= block.Statements.Count ||
            block.Statements[declarationIndex + 1] is not IfStatementSyntax ifStatement)
        {
            return document;
        }

        var isKeyword = SyntaxFactory.Token(
            asExpression.OperatorToken.LeadingTrivia,
            SyntaxKind.IsKeyword,
            asExpression.OperatorToken.TrailingTrivia);
        var pattern = SyntaxFactory.DeclarationPattern(
            (TypeSyntax)asExpression.Right,
            SyntaxFactory.SingleVariableDesignation(declarator.Identifier.WithoutTrivia()));
        var condition = SyntaxFactory.IsPatternExpression(
                ParenthesizeIfNeeded(asExpression.Left),
                isKeyword,
                pattern)
            .WithLeadingTrivia(ifStatement.Condition.GetLeadingTrivia())
            .WithTrailingTrivia(ifStatement.Condition.GetTrailingTrivia());

        var replacementIf = ifStatement
            .WithCondition(condition)
            .WithLeadingTrivia(
                declaration.GetLeadingTrivia()
                    .AddRange(KeepComments(declaration.GetTrailingTrivia()))
                    .AddRange(ifStatement.GetLeadingTrivia()))
            .WithAdditionalAnnotations(Formatter.Annotation);

        var replacementBlock = block.WithStatements(
                block.Statements.RemoveAt(declarationIndex).RemoveAt(declarationIndex).Insert(declarationIndex, replacementIf))
            .WithAdditionalAnnotations(Formatter.Annotation);

        return document.WithSyntaxRoot(root.ReplaceNode(block, replacementBlock));
    }

    private static ExpressionSyntax ParenthesizeIfNeeded(ExpressionSyntax expression) =>
        expression is AssignmentExpressionSyntax or BinaryExpressionSyntax or ConditionalExpressionSyntax or LambdaExpressionSyntax or
            QueryExpressionSyntax or SwitchExpressionSyntax
            ? SyntaxFactory.ParenthesizedExpression(expression.WithoutTrivia()).WithTriviaFrom(expression)
            : expression;

    private static SyntaxTriviaList KeepComments(SyntaxTriviaList trivia)
    {
        var result = default(SyntaxTriviaList);
        var keptComment = false;
        foreach (var item in trivia)
        {
            if (item.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
                item.IsKind(SyntaxKind.MultiLineCommentTrivia) ||
                item.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                item.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            {
                result = result.Add(item);
                keptComment = true;
            }
            else if (keptComment && item.IsKind(SyntaxKind.EndOfLineTrivia))
            {
                result = result.Add(item);
                keptComment = false;
            }
        }

        return result;
    }
}
