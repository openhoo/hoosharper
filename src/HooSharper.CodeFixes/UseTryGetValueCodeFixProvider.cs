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
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(UseTryGetValueAnalyzer.DiagnosticId);

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
        var matchingAccesses = new List<ElementAccessExpressionSyntax>();
        if (root is null || semanticModel is null ||
            !UseTryGetValueAnalyzer.TryGetMatchingAccesses(ifStatement, semanticModel, cancellationToken,
                semanticModel.Compilation.GetTypeByMetadataName("System.Collections.Generic.Dictionary`2"),
                semanticModel.Compilation.GetTypeByMetadataName("System.Collections.Generic.IDictionary`2"),
                matchingAccesses))
        {
            return document;
        }

        var invocation = (InvocationExpressionSyntax)ifStatement.Condition;
        var memberAccess = (MemberAccessExpressionSyntax)invocation.Expression;
        var valueName = CreateUniqueName(semanticModel, ifStatement);
        var valueIdentifier = SyntaxFactory.IdentifierName(valueName);
        var updatedBody = ifStatement.Statement.ReplaceNodes(
            matchingAccesses,
            (original, _) => RewriteAccess(original, valueIdentifier));

        var tryGetValueMember = memberAccess.WithName(
            SyntaxFactory.IdentifierName("TryGetValue").WithTriviaFrom(memberAccess.Name));
        var outArgument = SyntaxFactory.Argument(
                SyntaxFactory.DeclarationExpression(
                    SyntaxFactory.IdentifierName("var"),
                    SyntaxFactory.SingleVariableDesignation(SyntaxFactory.Identifier(valueName))))
            .WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.OutKeyword));
        if (invocation.ArgumentList.Arguments[0].NameColon is not null)
        {
            // C# 7.0/7.1 do not allow a positional out argument after a named
            // key argument. Keep both arguments named on every language level.
            outArgument = outArgument.WithNameColon(SyntaxFactory.NameColon("value"));
        }

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

    private static ExpressionSyntax RewriteAccess(
        ElementAccessExpressionSyntax access,
        IdentifierNameSyntax valueIdentifier)
    {
        var rewritten = valueIdentifier.WithTriviaFrom(access);
        var internalTrivia = NormalizeInternalTrivia(CollectInternalTrivia(access));
        if (internalTrivia.Count == 0)
        {
            return rewritten;
        }

        // Exactly one space separates migrated internal trivia from the value
        // identifier unless an end-of-line or the surrounding whitespace already
        // provides that separation.
        var leading = rewritten.GetLeadingTrivia();
        var updatedLeading = internalTrivia.AddRange(leading);
        if (!internalTrivia[internalTrivia.Count - 1].IsKind(SyntaxKind.EndOfLineTrivia) &&
            (leading.Count == 0 ||
             (!leading[0].IsKind(SyntaxKind.WhitespaceTrivia) &&
              !leading[0].IsKind(SyntaxKind.EndOfLineTrivia))))
        {
            updatedLeading = updatedLeading.Insert(internalTrivia.Count, SyntaxFactory.Space);
        }

        return rewritten.WithLeadingTrivia(updatedLeading);
    }

    private static SyntaxTriviaList CollectInternalTrivia(ElementAccessExpressionSyntax access)
    {
        var internalTrivia = SyntaxFactory.TriviaList();
        var tokens = access.DescendantTokens().ToList();
        if (tokens.Count < 2)
        {
            return internalTrivia;
        }

        // The first token's leading trivia is the access's own leading trivia and the last
        // token's trailing trivia its own trailing trivia; both are migrated by WithTriviaFrom.
        for (var index = 0; index < tokens.Count - 1; index++)
        {
            internalTrivia = internalTrivia.AddRange(tokens[index].TrailingTrivia);
            internalTrivia = internalTrivia.AddRange(tokens[index + 1].LeadingTrivia);
        }

        return internalTrivia;
    }

    private static SyntaxTriviaList NormalizeInternalTrivia(SyntaxTriviaList internalTrivia)
    {
        var startIndex = 0;
        var endIndex = internalTrivia.Count - 1;
        while (startIndex <= endIndex && internalTrivia[startIndex].IsKind(SyntaxKind.WhitespaceTrivia))
        {
            startIndex++;
        }

        while (endIndex >= startIndex && internalTrivia[endIndex].IsKind(SyntaxKind.WhitespaceTrivia))
        {
            endIndex--;
        }

        if (startIndex > endIndex)
        {
            return SyntaxFactory.TriviaList();
        }

        var normalized = SyntaxFactory.TriviaList();
        for (var index = startIndex; index <= endIndex; index++)
        {
            normalized = normalized.Add(internalTrivia[index]);
        }

        return normalized;
    }

    private static string CreateUniqueName(
        SemanticModel semanticModel,
        IfStatementSyntax ifStatement)
    {
        var unavailableNames = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var symbol in semanticModel.LookupSymbols(ifStatement.SpanStart))
        {
            unavailableNames.Add(symbol.Name);
        }

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
            foreach (var statement in block.Statements)
            {
                if (statement == ifStatement)
                {
                    break;
                }

                if (statement is IfStatementSyntax
                    {
                        Condition: InvocationExpressionSyntax
                        {
                            Expression: MemberAccessExpressionSyntax
                            {
                                Name.Identifier.ValueText: "ContainsKey",
                            },
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
}
