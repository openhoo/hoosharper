using System;
using System.Collections.Immutable;
using System.Collections.Generic;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using HooSharper.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HooSharper.CodeFixes;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(WrapFluentChainCodeFixProvider)), Shared]
public sealed class WrapFluentChainCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => [WrapFluentChainAnalyzer.DiagnosticId];

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var diagnostic = context.Diagnostics[0];
        var expression = root?.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true) as ExpressionSyntax;
        while (expression is not null && expression.Span != diagnostic.Location.SourceSpan)
        {
            expression = expression.Parent as ExpressionSyntax;
        }
        if (expression is null)
        {
            return;
        }


        context.RegisterCodeFix(
            CodeAction.Create(
                "Wrap fluent chain",
                cancellationToken => ApplyFixAsync(context.Document, expression, cancellationToken),
                nameof(WrapFluentChainCodeFixProvider)),
            diagnostic);
    }

    private static async Task<Document> ApplyFixAsync(
        Document document,
        ExpressionSyntax expression,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var sourceText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var containingNode = (SyntaxNode?)expression.FirstAncestorOrSelf<StatementSyntax>() ??
            expression.FirstAncestorOrSelf<ArrowExpressionClauseSyntax>();
        if (root is null || containingNode is null || HasDirective(expression) ||
            !TryGetChainDots(expression, out var dots) || dots.Count < 2)
        {
            return document;
        }

        var indentation = GetIndentation(containingNode, sourceText) + GetContinuationIndentation(document, expression.SyntaxTree);
        var endOfLine = DetectEndOfLine(sourceText);
        var endOfLineTrivia = SyntaxFactory.EndOfLine(endOfLine);
        var indentationTrivia = SyntaxFactory.Whitespace(indentation);
        var replacementDots = new SyntaxToken[dots.Count];
        for (var index = 0; index < dots.Count; index++)
        {
            var dot = dots[index];
            var leadingTrivia = dot.LeadingTrivia;
            if (ContainsEndOfLine(leadingTrivia))
            {
                return document;
            }

            replacementDots[index] = dot.WithLeadingTrivia(
                leadingTrivia.Insert(0, indentationTrivia).Insert(0, endOfLineTrivia));
        }

        var dotIndex = 0;
        var updatedExpression = expression.ReplaceTokens(
            dots,
            (_, _) => replacementDots[dotIndex++]);
        return document.WithSyntaxRoot(root.ReplaceNode(expression, updatedExpression));
    }

    private static bool TryGetChainDots(ExpressionSyntax chain, out IReadOnlyList<SyntaxToken> dots)
    {
        var result = new List<SyntaxToken>();
        ExpressionSyntax? current = chain;
        while (current is not null)
        {
            switch (current)
            {
                case InvocationExpressionSyntax invocation:
                    current = invocation.Expression;
                    break;
                case MemberAccessExpressionSyntax memberAccess
                    when memberAccess.IsKind(SyntaxKind.SimpleMemberAccessExpression):
                    result.Add(memberAccess.OperatorToken);
                    current = memberAccess.Expression;
                    break;
                case ConditionalAccessExpressionSyntax:
                    dots = [];
                    return false;
                default:
                    current = null;
                    break;
            }
        }

        result.Reverse();
        dots = result;
        return true;
    }

    private static string GetIndentation(
        SyntaxNode containingNode,
        Microsoft.CodeAnalysis.Text.SourceText sourceText)
    {
        var line = sourceText.Lines.GetLineFromPosition(containingNode.SpanStart);
        var text = line.ToString();
        var indentationLength = 0;
        while (indentationLength < text.Length &&
               text[indentationLength] is ' ' or '\t')
        {
            indentationLength++;
        }

        return text.Substring(0, indentationLength);
    }

    private static string GetContinuationIndentation(Document document, SyntaxTree syntaxTree)
    {
        var options = document.Project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GetOptions(syntaxTree);
        if (options.TryGetValue(WrapFluentChainAnalyzer.IndentStyleOption, out var indentStyle) &&
            string.Equals(indentStyle.Trim(), "tab", StringComparison.OrdinalIgnoreCase))
        {
            return "\t";
        }

        var indentSize = 4;
        if (options.TryGetValue(WrapFluentChainAnalyzer.IndentSizeOption, out var configuredIndentSize))
        {
            if (string.Equals(configuredIndentSize.Trim(), "tab", StringComparison.OrdinalIgnoreCase))
            {
                if (options.TryGetValue(WrapFluentChainAnalyzer.TabWidthOption, out var configuredTabWidth) &&
                    int.TryParse(configuredTabWidth, out var tabWidth) &&
                    tabWidth > 0)
                {
                    indentSize = tabWidth;
                }
            }
            else if (int.TryParse(configuredIndentSize, out var parsedIndentSize) && parsedIndentSize > 0)
            {
                indentSize = parsedIndentSize;
            }
        }

        return new string(' ', indentSize);
    }

    private static string DetectEndOfLine(Microsoft.CodeAnalysis.Text.SourceText sourceText)
    {
        for (var position = 0; position < sourceText.Length; position++)
        {
            switch (sourceText[position])
            {
                case '\n':
                    return "\n";
                case '\r':
                    return position + 1 < sourceText.Length && sourceText[position + 1] == '\n'
                        ? "\r\n"
                        : "\r";
            }
        }

        return "\n";
    }

    private static bool ContainsEndOfLine(SyntaxTriviaList trivia)
    {
        foreach (var item in trivia)
        {
            if (item.IsKind(SyntaxKind.EndOfLineTrivia))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasDirective(ExpressionSyntax expression)
    {
        foreach (var trivia in expression.DescendantTrivia(descendIntoTrivia: true))
        {
            if (trivia.IsDirective)
            {
                return true;
            }
        }

        return false;
    }
}
