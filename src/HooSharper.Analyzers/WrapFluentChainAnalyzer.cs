using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace HooSharper.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class WrapFluentChainAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HOO1020";
    public const string MaximumLineLengthOption = "hoosharper_max_line_length";
    public const string StandardMaximumLineLengthOption = "max_line_length";
    public const string IndentStyleOption = "indent_style";
    public const string IndentSizeOption = "indent_size";
    public const string TabWidthOption = "tab_width";

    private const int DefaultMaximumLineLength = 140;

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Wrap fluent chain",
        "Wrap fluent chain to fit the maximum line length",
        "HooSharper.CodeStyle",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Long single-line fluent chains are easier to read when every continuation starts on its own line.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeMemberAccess, SyntaxKind.SimpleMemberAccessExpression);
    }

    private static void AnalyzeMemberAccess(SyntaxNodeAnalysisContext context)
    {
        var memberAccess = (MemberAccessExpressionSyntax)context.Node;
        if (memberAccess.Parent is MemberAccessExpressionSyntax
            {
                RawKind: (int)SyntaxKind.SimpleMemberAccessExpression,
                Expression: var parentExpression,
            } && parentExpression == memberAccess ||
            memberAccess.Parent is InvocationExpressionSyntax { Expression: var invocationExpression } invocation &&
            invocationExpression == memberAccess &&
            invocation.Parent is MemberAccessExpressionSyntax
            {
                RawKind: (int)SyntaxKind.SimpleMemberAccessExpression,
                Expression: var invocationParentExpression,
            } && invocationParentExpression == invocation)
        {
            return;
        }

        var chain = GetOutermostChain(memberAccess);
        if (GetLastMemberAccess(chain) != memberAccess ||
            !HasStatementOrArrowAncestor(chain) ||
            HasDirective(chain) ||
            !TryGetChainDots(chain, out var dots) ||
            dots.Count < 2)
        {
            return;
        }

        var lineSpan = chain.GetLocation().GetLineSpan();
        if (lineSpan.StartLinePosition.Line != lineSpan.EndLinePosition.Line)
        {
            return;
        }

        var maximumLineLength = GetMaximumLineLength(context);
        if (GetVisualEndColumn(chain, GetTabWidth(context)) <= maximumLineLength)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, chain.GetLocation()));
    }

    private static bool HasStatementOrArrowAncestor(SyntaxNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (current is StatementSyntax or ArrowExpressionClauseSyntax)
            {
                return true;
            }
        }

        return false;
    }

    private static int GetMaximumLineLength(SyntaxNodeAnalysisContext context)
    {
        var options = context.Options.AnalyzerConfigOptionsProvider.GetOptions(context.Node.SyntaxTree);
        if (TryGetPositiveInteger(options, MaximumLineLengthOption, out var maximumLineLength) ||
            TryGetPositiveInteger(options, StandardMaximumLineLengthOption, out maximumLineLength))
        {
            return maximumLineLength;
        }

        return DefaultMaximumLineLength;
    }

    private static int GetTabWidth(SyntaxNodeAnalysisContext context)
    {
        var options = context.Options.AnalyzerConfigOptionsProvider.GetOptions(context.Node.SyntaxTree);
        if (TryGetPositiveInteger(options, TabWidthOption, out var tabWidth))
        {
            return tabWidth;
        }

        if (TryGetPositiveInteger(options, IndentSizeOption, out var indentSize))
        {
            return indentSize;
        }

        return 4;
    }

    private static int GetVisualEndColumn(ExpressionSyntax chain, int tabWidth)
    {
        var text = chain.SyntaxTree.GetText();
        var line = text.Lines.GetLineFromPosition(chain.Span.End);
        var column = 0;
        for (var position = line.Start; position < chain.Span.End; position++)
        {
            var character = text[position];
            if (character == '\t')
            {
                column += tabWidth - column % tabWidth;
            }
            else if (!char.IsLowSurrogate(character) || position == line.Start ||
                     !char.IsHighSurrogate(text[position - 1]))
            {
                column++;
            }
        }

        return column;
     }

    private static bool TryGetPositiveInteger(
        AnalyzerConfigOptions options,
        string key,
        out int value)
    {
        value = 0;
        return options.TryGetValue(key, out var configuredValue) &&
               int.TryParse(configuredValue, out value) &&
               value > 0;
    }

    internal static ExpressionSyntax GetOutermostChain(ExpressionSyntax expression)
    {
        ExpressionSyntax current = expression;
        while (true)
        {
            if (current.Parent is InvocationExpressionSyntax invocation && invocation.Expression == current)
            {
                current = invocation;
                continue;
            }

            if (current.Parent is MemberAccessExpressionSyntax memberAccess && memberAccess.Expression == current &&
                memberAccess.IsKind(SyntaxKind.SimpleMemberAccessExpression))
            {
                current = memberAccess;
                continue;
            }

            return current;
        }
    }

    internal static bool TryGetChainDots(ExpressionSyntax chain, out IReadOnlyList<SyntaxToken> dots)
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

    private static MemberAccessExpressionSyntax? GetLastMemberAccess(ExpressionSyntax chain) =>
        chain switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess,
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax memberAccess } => memberAccess,
            _ => null,
        };

    private static bool HasDirective(ExpressionSyntax chain)
    {
        foreach (var trivia in chain.DescendantTrivia(descendIntoTrivia: true))
        {
            if (trivia.IsDirective)
            {
                return true;
            }
        }

        return false;
    }
}
