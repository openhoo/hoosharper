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

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(RemoveRedundantElseCodeFixProvider)), Shared]
public sealed class RemoveRedundantElseCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(RemoveRedundantElseAnalyzer.DiagnosticId);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var diagnostic = context.Diagnostics[0];
        var ifStatement = root?.FindToken(diagnostic.Location.SourceSpan.Start).Parent?
            .AncestorsAndSelf().OfType<IfStatementSyntax>().FirstOrDefault();

        var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        if (ifStatement is null || semanticModel is null ||
            !HooSharper.Internal.ControlFlowScopeSafety.CanRemoveElse(ifStatement, semanticModel, context.CancellationToken))
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                "Remove redundant else",
                cancellationToken => ApplyFixAsync(context.Document, ifStatement, cancellationToken),
                nameof(RemoveRedundantElseCodeFixProvider)),
            diagnostic);
    }

    private static async Task<Document> ApplyFixAsync(
        Document document,
        IfStatementSyntax ifStatement,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || semanticModel is null || ifStatement.Else is not { } elseClause ||
            !HooSharper.Internal.ControlFlowScopeSafety.CanRemoveElse(ifStatement, semanticModel, cancellationToken))
        {
            return document;
        }

        var separateBlocks = RequiresBlankLineAfterBlock(document, semanticModel, cancellationToken);
        var replacementStatements = CreateReplacementStatements(ifStatement, elseClause, semanticModel, cancellationToken, separateBlocks);
        SyntaxNode newRoot;

        if (ifStatement.Parent is BlockSyntax parentBlock)
        {
            var index = parentBlock.Statements.IndexOf(ifStatement);
            var newParentBlock = CodeFixTrivia.ReplaceWithStatements(parentBlock, index, replacementStatements);
            newRoot = root.ReplaceNode(parentBlock, newParentBlock);
        }
        else
        {
            // The statements keep the original boundary comments. Only the
            // line ending needs to move past the new closing brace.
            var replacementBlock = SyntaxFactory.Block(replacementStatements)
                .WithTrailingTrivia(ifStatement.GetTrailingTrivia().Where(trivia => trivia.IsKind(SyntaxKind.EndOfLineTrivia)))
                .WithAdditionalAnnotations(Formatter.Annotation);
            newRoot = root.ReplaceNode(ifStatement, replacementBlock);
        }

        return await CodeFixTrivia.FormatChangesAsync(document, newRoot, cancellationToken).ConfigureAwait(false);
    }

    private static List<StatementSyntax> CreateReplacementStatements(
        IfStatementSyntax ifStatement,
        ElseClauseSyntax elseClause,
        SemanticModel semanticModel,
        CancellationToken cancellationToken,
        bool separateBlocks)
    {
        var replacements = new List<StatementSyntax>();
        SyntaxTriviaList? movedLeadingTrivia = null;
        while (true)
        {
            var split = SplitElse(ifStatement, elseClause);
            if (separateBlocks && split.Count > 1 && split[0].GetLastToken().IsKind(SyntaxKind.CloseBraceToken))
                split[1] = CodeFixTrivia.WithBlankLineBefore(split[1], split[0], ifStatement);
            replacements.Add(movedLeadingTrivia is { } moved
                ? split[0].WithLeadingTrivia(moved)
                : split[0]);
            // The outer condition already passed the directive and syntax checks.
            // Only split consecutive arms whose endpoint is unreachable.
            if (elseClause.Statement is not IfStatementSyntax { Else: { } nextElse } nextIf ||
                !HooSharper.Internal.ControlFlowScopeSafety.DefinitelyTerminates(nextIf.Statement, semanticModel, cancellationToken) ||
                (ProjectStylePolicy.IsEnabled(semanticModel.Compilation.Options, ifStatement.SyntaxTree, "S1199", cancellationToken) &&
                 HooSharper.Internal.ControlFlowScopeSafety.ElseNeedsStandaloneBlock(nextElse)))
            {
                replacements.AddRange(split.Skip(1));
                return replacements;
            }

            if (split[1] is BlockSyntax scope)
            {
                // Keep pattern/out variables inside the scope chosen by SplitElse.
                var scopedStatements = CreateReplacementStatements(nextIf, nextElse, semanticModel, cancellationToken, separateBlocks);
                replacements.Add(scope.WithStatements(SyntaxFactory.List(scopedStatements)));
                return replacements;
            }

            movedLeadingTrivia = split[1].GetLeadingTrivia();
            ifStatement = nextIf;
            elseClause = nextElse;
        }
    }

    private static bool RequiresBlankLineAfterBlock(Document document, SemanticModel model, CancellationToken token)
    {
        var tree = model.SyntaxTree;
        var config = document.Project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GetOptions(tree);
        if (config.TryGetValue("csharp_style_allow_statement_immediately_after_block_experimental", out var style) &&
            bool.TryParse(style.Split(':')[0].Trim(), out var allow) && !allow)
            return true;

        return ProjectStylePolicy.IsEnabled(model.Compilation.Options, tree, "SA1513", token);
    }

    private static List<StatementSyntax> SplitElse(
        IfStatementSyntax ifStatement,
        ElseClauseSyntax elseClause)
    {
        var statementWithoutElse = ifStatement.WithElse(null)
            .WithAdditionalAnnotations(Formatter.Annotation);

        var preserveScope = HooSharper.Internal.ControlFlowScopeSafety.ElseNeedsStandaloneBlock(elseClause);
        if (elseClause.Statement is BlockSyntax scopedBlock && preserveScope)
        {
            var elseTrivia = SignificantTrivia(elseClause.ElseKeyword.LeadingTrivia)
                .AddRange(SignificantTrivia(elseClause.ElseKeyword.TrailingTrivia));
            var preservedBlock = scopedBlock
                .WithLeadingTrivia(WithLineBreaks(elseTrivia, addInitialLineBreak: false)
                    .AddRange(scopedBlock.GetLeadingTrivia()))
                .WithAdditionalAnnotations(Formatter.Annotation);

            return [statementWithoutElse, preservedBlock];
        }

        // An unbraced embedded statement also has its own declaration scope.
        // Keep it when moving an out or pattern variable into the parent block.
        if (elseClause.Statement is not BlockSyntax && preserveScope)
        {
            var trivia = SignificantTrivia(elseClause.ElseKeyword.LeadingTrivia)
                .AddRange(SignificantTrivia(elseClause.ElseKeyword.TrailingTrivia));
            var scope = SyntaxFactory.Block(elseClause.Statement)
                .WithLeadingTrivia(WithLineBreaks(trivia, addInitialLineBreak: false))
                .WithAdditionalAnnotations(Formatter.Annotation);
            return [statementWithoutElse, scope];
        }

        var movedStatements = elseClause.Statement is BlockSyntax block
            ? block.Statements.ToList()
            : [elseClause.Statement];

        if (movedStatements.Count > 0)
        {
            var firstStatement = movedStatements[0];
            var leadingTrivia = SignificantTrivia(elseClause.ElseKeyword.LeadingTrivia)
                .AddRange(SignificantTrivia(elseClause.ElseKeyword.TrailingTrivia));

            if (elseClause.Statement is BlockSyntax elseBlock)
            {
                leadingTrivia = leadingTrivia
                    .AddRange(SignificantTrivia(elseBlock.OpenBraceToken.LeadingTrivia))
                    .AddRange(SignificantTrivia(elseBlock.OpenBraceToken.TrailingTrivia));
            }

            leadingTrivia = leadingTrivia.AddRange(SignificantTrivia(firstStatement.GetLeadingTrivia()));
            movedStatements[0] = firstStatement
                .WithLeadingTrivia(WithLineBreaks(leadingTrivia, addInitialLineBreak: false))
                .WithAdditionalAnnotations(Formatter.Annotation);

            if (elseClause.Statement is BlockSyntax body)
            {
                var lastIndex = movedStatements.Count - 1;
                var trailingTrivia = movedStatements[lastIndex].GetTrailingTrivia()
                    .AddRange(WithLineBreaks(SignificantTrivia(body.CloseBraceToken.LeadingTrivia)))
                    .AddRange(WithLineBreaks(SignificantTrivia(body.CloseBraceToken.TrailingTrivia), addInitialLineBreak: false));
                movedStatements[lastIndex] = movedStatements[lastIndex]
                    .WithTrailingTrivia(trailingTrivia);
            }
        }

        var replacements = new List<StatementSyntax>(movedStatements.Count + 1) { statementWithoutElse };
        replacements.AddRange(movedStatements);
        return replacements;
    }

    private static SyntaxTriviaList SignificantTrivia(SyntaxTriviaList trivia) =>
        SyntaxFactory.TriviaList(trivia.Where(item =>
            !item.IsKind(SyntaxKind.WhitespaceTrivia) && !item.IsKind(SyntaxKind.EndOfLineTrivia)));

    private static SyntaxTriviaList WithLineBreaks(SyntaxTriviaList trivia, bool addInitialLineBreak = true)
    {
        var result = new List<SyntaxTrivia>(trivia.Count * 2 + 1);
        if (addInitialLineBreak && trivia.Count > 0)
        {
            result.Add(SyntaxFactory.ElasticCarriageReturnLineFeed);
        }

        foreach (var item in trivia)
        {
            result.Add(item);
            if (item.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
                item.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia))
            {
                result.Add(SyntaxFactory.ElasticCarriageReturnLineFeed);
            }
        }

        return SyntaxFactory.TriviaList(result);
    }

}
