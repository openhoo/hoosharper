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
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(PreferLoopContinueAnalyzer.DiagnosticId);

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
            PreferLoopContinueAnalyzer.HasBindingCollision(ifStatement, parentBlock, body) ||
            HooSharper.Internal.ControlFlowScopeSafety.HoistingBodyCrossesUsingDeclaration(
                body, parentBlock, semanticModel, cancellationToken))
        {
            return document;
        }

        var negatedCondition = GuardCondition.Negate(
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
        if (movedStatements.Count != 0 && CodeFixTrivia.StartsWithLineComment(movedStatements[0]) &&
            ProjectStylePolicy.IsEnabled(semanticModel.Compilation.Options, ifStatement.SyntaxTree, "SA1515", cancellationToken))
            movedStatements[0] = CodeFixTrivia.WithBlankLineBefore(movedStatements[0], guard, ifStatement);

        var replacementStatements = new List<StatementSyntax> { guard };
        replacementStatements.AddRange(movedStatements);

        var index = parentBlock.Statements.IndexOf(ifStatement);
        if (index < 0)
        {
            return document;
        }

        var newParentBlock = CodeFixTrivia.ReplaceWithStatements(parentBlock, index, replacementStatements);

        return await CodeFixTrivia.FormatChangesAsync(document, root.ReplaceNode(parentBlock, newParentBlock), cancellationToken).ConfigureAwait(false);
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

    private static bool HasSignificantTrivia(IEnumerable<SyntaxTrivia> trivia) =>
        trivia.Any(item => !item.IsKind(SyntaxKind.WhitespaceTrivia) && !item.IsKind(SyntaxKind.EndOfLineTrivia));
}
