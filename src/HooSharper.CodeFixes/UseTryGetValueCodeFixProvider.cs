using System.Collections.Generic;
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

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(UseTryGetValueCodeFixProvider)), Shared]
public sealed class UseTryGetValueCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => [UseTryGetValueAnalyzer.DiagnosticId];

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
                "Use TryGetValue",
                cancellationToken => ApplyFixAsync(context.Document, ifStatement, cancellationToken),
                nameof(UseTryGetValueCodeFixProvider)),
            diagnostic);
    }

    private static async Task<Document> ApplyFixAsync(
        Document document,
        IfStatementSyntax ifStatement,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || semanticModel is null ||
            ifStatement.Condition is not InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax memberAccess,
            } invocation ||
            invocation.ArgumentList.Arguments.Count != 1)
        {
            return document;
        }

        var key = invocation.ArgumentList.Arguments[0].Expression;
        var dictionaryDefinition = semanticModel.Compilation.GetTypeByMetadataName(
            "System.Collections.Generic.Dictionary`2");
        var dictionaryInterface = semanticModel.Compilation.GetTypeByMetadataName(
            "System.Collections.Generic.IDictionary`2");
        var matchingAccesses = new List<ElementAccessExpressionSyntax>();
        foreach (var elementAccess in ifStatement.Statement.DescendantNodes(ShouldDescendInto)
                     .OfType<ElementAccessExpressionSyntax>())
        {
            if (elementAccess.ArgumentList.Arguments.Count != 1)
            {
                continue;
            }

            var indexedKey = elementAccess.ArgumentList.Arguments[0].Expression;
            if (!SyntaxFactory.AreEquivalent(memberAccess.Expression, elementAccess.Expression) ||
                !SyntaxFactory.AreEquivalent(key, indexedKey))
            {
                continue;
            }

            var indexer = semanticModel.GetSymbolInfo(elementAccess, cancellationToken).Symbol as IPropertySymbol;
            if (indexer is not null && IsDictionaryMember(
                    indexer.OriginalDefinition.ContainingType,
                    dictionaryDefinition,
                    dictionaryInterface))
            {
                matchingAccesses.Add(elementAccess);
            }
        }

        if (matchingAccesses.Count == 0)
        {
            return document;
        }

        var valueName = CreateUniqueName(semanticModel, ifStatement);
        var valueIdentifier = SyntaxFactory.IdentifierName(valueName);
        var updatedBody = ifStatement.Statement.ReplaceNodes(
            matchingAccesses,
            (original, _) => valueIdentifier.WithTriviaFrom(original));

        var tryGetValueMember = memberAccess.WithName(
            SyntaxFactory.IdentifierName("TryGetValue").WithTriviaFrom(memberAccess.Name));
        var outArgument = SyntaxFactory.Argument(
                SyntaxFactory.DeclarationExpression(
                    SyntaxFactory.IdentifierName("var"),
                    SyntaxFactory.SingleVariableDesignation(SyntaxFactory.Identifier(valueName))))
            .WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.OutKeyword));
        var updatedCondition = invocation
            .WithExpression(tryGetValueMember)
            .WithArgumentList(invocation.ArgumentList.AddArguments(outArgument))
            .WithAdditionalAnnotations(Formatter.Annotation);
        var updatedIf = ifStatement
            .WithCondition(updatedCondition)
            .WithStatement(updatedBody)
            .WithAdditionalAnnotations(Formatter.Annotation);

        return document.WithSyntaxRoot(root.ReplaceNode(ifStatement, updatedIf));
    }

    private static string CreateUniqueName(
        SemanticModel semanticModel,
        IfStatementSyntax ifStatement)
    {
        var unavailableNames = new HashSet<string>(
            semanticModel.LookupSymbols(ifStatement.SpanStart).Select(symbol => symbol.Name),
            System.StringComparer.Ordinal);

        var enclosingScope = ifStatement.Parent ?? ifStatement;
        foreach (var token in enclosingScope.DescendantTokens())
        {
            if (token.IsKind(SyntaxKind.IdentifierToken))
            {
                unavailableNames.Add(token.ValueText);
            }
        }

        const string baseName = "value";
        var suffix = 0;
        while (unavailableNames.Contains(NameForSuffix(baseName, suffix)))
        {
            suffix++;
        }

        if (ifStatement.Parent is BlockSyntax block)
        {
            foreach (var precedingIf in block.Statements.TakeWhile(statement => statement != ifStatement)
                         .OfType<IfStatementSyntax>())
            {
                if (precedingIf.Condition is InvocationExpressionSyntax
                    {
                        Expression: MemberAccessExpressionSyntax
                        {
                            Name.Identifier.ValueText: "ContainsKey",
                        },
                    })
                {
                    suffix++;
                    while (unavailableNames.Contains(NameForSuffix(baseName, suffix)))
                    {
                        suffix++;
                    }
                }
            }
        }

        return NameForSuffix(baseName, suffix);
    }

    private static string NameForSuffix(string baseName, int suffix) =>
        suffix == 0 ? baseName : baseName + suffix;

    private static bool IsDictionaryMember(
        INamedTypeSymbol containingType,
        INamedTypeSymbol? dictionaryDefinition,
        INamedTypeSymbol? dictionaryInterface) =>
        SymbolEqualityComparer.Default.Equals(containingType.OriginalDefinition, dictionaryDefinition) ||
        SymbolEqualityComparer.Default.Equals(containingType.OriginalDefinition, dictionaryInterface);

    private static bool ShouldDescendInto(SyntaxNode node) =>
        node is not AnonymousFunctionExpressionSyntax and
        not LocalFunctionStatementSyntax and
        not TypeDeclarationSyntax;
}
