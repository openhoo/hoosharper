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

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(OmitBracesForSingleLineIfCodeFixProvider)), Shared]
public sealed class OmitBracesForSingleLineIfCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => [OmitBracesForSingleLineIfAnalyzer.DiagnosticId];

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var diagnostic = context.Diagnostics[0];
        var block = root?.FindToken(diagnostic.Location.SourceSpan.Start).Parent?
            .AncestorsAndSelf().OfType<BlockSyntax>().FirstOrDefault();

        if (block is null)
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                "Remove braces",
                cancellationToken => ApplyFixAsync(context.Document, block, cancellationToken),
                nameof(OmitBracesForSingleLineIfCodeFixProvider)),
            diagnostic);
    }

    private static async Task<Document> ApplyFixAsync(
        Document document,
        BlockSyntax block,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || block.Statements.Count != 1 ||
            HasDirective(block) ||
            block.Statements[0] is LabeledStatementSyntax ||
            block.Parent is IfStatementSyntax { Statement: var thenStatement, Else: not null } &&
            thenStatement == block &&
            CanCaptureFollowingElse(block.Statements[0]) ||
            HasExpandedScopeCollision(block, block.Statements[0]))
        {
            return document;
        }

        var sourceText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var endOfLine = SyntaxFactory.EndOfLine(DetectEndOfLine(sourceText));
        var originalStatement = block.Statements[0];
        var leadingTrivia = block.GetLeadingTrivia()
            .AddRange(CommentLines(block.OpenBraceToken.TrailingTrivia, endOfLine))
            .AddRange(originalStatement.GetLeadingTrivia());
        var trailingTrivia = originalStatement.GetTrailingTrivia()
            .AddRange(CommentLines(block.CloseBraceToken.LeadingTrivia, endOfLine))
            .AddRange(InlineComments(block.CloseBraceToken.TrailingTrivia, endOfLine));
        var statement = originalStatement
            .WithLeadingTrivia(leadingTrivia)
            .WithTrailingTrivia(trailingTrivia)
            .WithAdditionalAnnotations(Formatter.Annotation);

        return document.WithSyntaxRoot(root.ReplaceNode(block, statement));
    }

    private static bool CanCaptureFollowingElse(StatementSyntax statement) =>
        statement switch
        {
            IfStatementSyntax { Else: null } => true,
            IfStatementSyntax { Else: { Statement: var elseStatement } } =>
                CanCaptureFollowingElse(elseStatement),
            WhileStatementSyntax whileStatement => CanCaptureFollowingElse(whileStatement.Statement),
            ForStatementSyntax forStatement => CanCaptureFollowingElse(forStatement.Statement),
            ForEachStatementSyntax forEachStatement => CanCaptureFollowingElse(forEachStatement.Statement),
            ForEachVariableStatementSyntax forEachVariableStatement =>
                CanCaptureFollowingElse(forEachVariableStatement.Statement),
            DoStatementSyntax doStatement => CanCaptureFollowingElse(doStatement.Statement),
            UsingStatementSyntax usingStatement => CanCaptureFollowingElse(usingStatement.Statement),
            FixedStatementSyntax fixedStatement => CanCaptureFollowingElse(fixedStatement.Statement),
            LockStatementSyntax lockStatement => CanCaptureFollowingElse(lockStatement.Statement),
            LabeledStatementSyntax labeledStatement => CanCaptureFollowingElse(labeledStatement.Statement),
            _ => false,
        };
    private static bool HasExpandedScopeCollision(BlockSyntax block, StatementSyntax nestedStatement)
    {
        var introducedNames = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var node in nestedStatement.DescendantNodesAndSelf())
        {
            if (node is SingleVariableDesignationSyntax designation &&
                !designation.Identifier.IsKind(SyntaxKind.UnderscoreToken))
            {
                introducedNames.Add(designation.Identifier.ValueText);
            }
        }

        if (introducedNames.Count == 0)
        {
            return false;
        }

        StatementSyntax? containingStatement = null;
        for (var current = block.Parent; current is not null; current = current.Parent)
        {
            if (current is StatementSyntax statement)
            {
                containingStatement = statement;
                break;
            }
        }

        if (containingStatement?.Parent is not BlockSyntax parentBlock)
        {
            return true;
        }

        var statementIndex = parentBlock.Statements.IndexOf(containingStatement);
        for (var index = 0; index < parentBlock.Statements.Count; index++)
        {
            if (index == statementIndex)
            {
                continue;
            }

            foreach (var token in parentBlock.Statements[index].DescendantTokens())
            {
                if (token.IsKind(SyntaxKind.IdentifierToken) && introducedNames.Contains(token.ValueText))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasDirective(BlockSyntax block) =>
        block.DescendantTrivia(descendIntoTrivia: true).Any(trivia => trivia.IsDirective);
    private static SyntaxTriviaList CommentLines(
        SyntaxTriviaList trivia,
        SyntaxTrivia endOfLine,
        bool addInitialLineBreak = false)
    {
        var result = SyntaxFactory.TriviaList();
        var foundComment = false;
        foreach (var item in trivia)
        {
            if (IsComment(item))
            {
                if (addInitialLineBreak && !foundComment)
                {
                    result = result.Add(endOfLine);
                }

                result = result.Add(item).Add(endOfLine);
                foundComment = true;
            }
        }

        return result;
    }

    private static SyntaxTriviaList InlineComments(SyntaxTriviaList trivia, SyntaxTrivia endOfLine)
    {
        var result = SyntaxFactory.TriviaList();
        foreach (var item in trivia)
        {
            if (IsComment(item))
            {
                result = result.Add(SyntaxFactory.Space).Add(item);
                if (item.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
                    item.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia))
                {
                    result = result.Add(endOfLine);
                }
            }
        }

        return result;
    }

    private static string DetectEndOfLine(Microsoft.CodeAnalysis.Text.SourceText sourceText)
    {
        foreach (var line in sourceText.Lines)
        {
            var lineBreakLength = line.EndIncludingLineBreak - line.End;
            if (lineBreakLength > 0)
            {
                return sourceText.ToString(new Microsoft.CodeAnalysis.Text.TextSpan(line.End, lineBreakLength));
            }
        }

        return "\n";
    }

    private static bool IsComment(SyntaxTrivia trivia) =>
        trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
        trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) ||
        trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
        trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia);

}
