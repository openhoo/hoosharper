using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HooSharper.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HooSharper.CodeFixes;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(UseNullConditionalAccessCodeFixProvider)), Shared]
public sealed class UseNullConditionalAccessCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => [UseNullConditionalAccessAnalyzer.DiagnosticId];

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var diagnostic = context.Diagnostics[0];
        var conditional = root?.FindToken(diagnostic.Location.SourceSpan.Start).Parent?
            .AncestorsAndSelf().OfType<ConditionalExpressionSyntax>().FirstOrDefault();
        if (conditional is null)
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                "Use null-conditional access",
                cancellationToken => ApplyFixAsync(context.Document, conditional, cancellationToken),
                nameof(UseNullConditionalAccessCodeFixProvider)),
            diagnostic);
    }

    private static async Task<Document> ApplyFixAsync(
        Document document,
        ConditionalExpressionSyntax conditional,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || semanticModel is null ||
            !UseNullConditionalAccessAnalyzer.TryGetCandidate(
                conditional,
                semanticModel,
                cancellationToken,
                out _,
                out var access))
        {
            return document;
        }

        var replacement = UseNullConditionalAccessAnalyzer.CreateReplacement(access)
            .WithTriviaFrom(conditional);
        return document.WithSyntaxRoot(root.ReplaceNode(conditional, replacement));
    }
}
