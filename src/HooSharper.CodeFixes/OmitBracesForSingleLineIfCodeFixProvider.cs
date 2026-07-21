using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Immutable;
using System.Composition;
using HooSharper.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
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
        if (root is null || block.Statements.Count != 1)
        {
            return document;
        }

        var statement = block.Statements[0]
            .WithLeadingTrivia(block.GetLeadingTrivia())
            .WithTrailingTrivia(block.GetTrailingTrivia())
            .WithAdditionalAnnotations(Formatter.Annotation);

        return document.WithSyntaxRoot(root.ReplaceNode(block, statement));
    }
}
