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
    public const int MaximumConfiguredWidth = 4096;

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
        var chain = GetOutermostChain(memberAccess);
        if (GetLastMemberAccess(chain) != memberAccess ||
            !HasStatementOrArrowAncestor(chain, out var isInInterpolation) ||
            HasDirective(chain) ||
            HasBoundaryOnLeftSpine(chain) ||
            !HasInvocation(chain, context) ||
            !IsSupportedLanguageVersion(chain, isInInterpolation, context) ||
            !TryGetChainDotCount(chain, out var dotCount) ||
            dotCount < 2)
        {
            return;
        }

        var lineSpan = chain.GetLocation().GetLineSpan();
        if (lineSpan.StartLinePosition.Line != lineSpan.EndLinePosition.Line)
        {
            return;
        }

        var options = context.Options.AnalyzerConfigOptionsProvider.GetOptions(context.Node.SyntaxTree);
        var maximumLineLength = GetMaximumLineLength(options);
        if (GetVisualEndColumn(chain, GetTabWidth(options)) <= maximumLineLength)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, chain.GetLocation()));
    }

    private static bool HasStatementOrArrowAncestor(SyntaxNode node, out bool isInInterpolation)
    {
        isInInterpolation = false;
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (current is StatementSyntax or ArrowExpressionClauseSyntax)
            {
                return true;
            }

            isInInterpolation |= current is InterpolationSyntax;
        }

        return false;
    }

    private static int GetMaximumLineLength(AnalyzerConfigOptions options)
    {
        if (TryGetPositiveInteger(options, MaximumLineLengthOption, out var maximumLineLength) ||
            TryGetPositiveInteger(options, StandardMaximumLineLengthOption, out maximumLineLength))
        {
            return maximumLineLength;
        }

        return DefaultMaximumLineLength;
    }

    private static int GetTabWidth(AnalyzerConfigOptions options)
    {
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
                var increment = tabWidth - column % tabWidth;
                column = column > int.MaxValue - increment ? int.MaxValue : column + increment;
            }
            else if ((!char.IsLowSurrogate(character) || position == line.Start ||
                      !char.IsHighSurrogate(text[position - 1])) &&
                     column < int.MaxValue)
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
               value > 0 &&
               value <= MaximumConfiguredWidth;
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

    private static bool TryGetChainDotCount(ExpressionSyntax chain, out int count)
    {
        count = 0;
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
                    count++;
                    current = memberAccess.Expression;
                    break;
                case ConditionalAccessExpressionSyntax:
                    count = 0;
                    return false;
                default:
                    current = null;
                    break;
            }
        }

        return true;
    }
    private static bool HasInvocation(ExpressionSyntax chain, SyntaxNodeAnalysisContext context)
    {
        ExpressionSyntax? current = chain;
        while (current is not null)
        {
            switch (current)
            {
                case InvocationExpressionSyntax invocation:
                    if (invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                        context.SemanticModel.GetSymbolInfo(memberAccess, context.CancellationToken).Symbol is IMethodSymbol)
                    {
                        return !IsStaticOrTypeRoot(GetChainBase(chain), context);
                    }

                    current = invocation.Expression;
                    break;
                case MemberAccessExpressionSyntax chainedMemberAccess
                    when chainedMemberAccess.IsKind(SyntaxKind.SimpleMemberAccessExpression):
                    current = chainedMemberAccess.Expression;
                    break;
                case ElementAccessExpressionSyntax elementAccess:
                    current = elementAccess.Expression;
                    break;
                case ParenthesizedExpressionSyntax parenthesized:
                    current = parenthesized.Expression;
                    break;
                default:
                    return false;
            }
        }

        return false;
    }

    private static ExpressionSyntax GetChainBase(ExpressionSyntax chain)
    {
        ExpressionSyntax current = chain;
        while (true)
        {
            var next = current switch
            {
                InvocationExpressionSyntax invocation => invocation.Expression,
                MemberAccessExpressionSyntax memberAccess
                    when memberAccess.IsKind(SyntaxKind.SimpleMemberAccessExpression) => memberAccess.Expression,
                ParenthesizedExpressionSyntax parenthesized => parenthesized.Expression,
                ElementAccessExpressionSyntax elementAccess => elementAccess.Expression,
                _ => current,
            };

            if (next == current)
            {
                return current;
            }

            current = next;
        }
    }

    private static bool IsStaticOrTypeRoot(
        ExpressionSyntax root,
        SyntaxNodeAnalysisContext context)
    {
        var symbol = context.SemanticModel.GetSymbolInfo(root, context.CancellationToken).Symbol;
        return symbol is INamespaceSymbol or INamedTypeSymbol ||
            symbol is IFieldSymbol { IsStatic: true } ||
            symbol is IPropertySymbol { IsStatic: true };
    }

    private static bool HasBoundaryOnLeftSpine(ExpressionSyntax chain)
    {
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
                    current = memberAccess.Expression;
                    break;
                case ParenthesizedExpressionSyntax parenthesized:
                    return HasAtLeastTwoDots(parenthesized.Expression);
                case ElementAccessExpressionSyntax elementAccess:
                    return HasAtLeastTwoDots(elementAccess.Expression);
                default:
                    return false;
            }
        }

        return false;
    }

    private static bool HasAtLeastTwoDots(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return TryGetChainDotCount(expression, out var count) && count >= 2;
    }

    private static bool IsSupportedLanguageVersion(
        ExpressionSyntax chain,
        bool isInInterpolation,
        SyntaxNodeAnalysisContext context)
    {
        if (context.Node.SyntaxTree.Options is not CSharpParseOptions { LanguageVersion: var languageVersion } ||
            languageVersion == LanguageVersion.Default ||
            languageVersion >= LanguageVersion.CSharp11)
        {
            return true;
        }

        if (isInInterpolation)
        {
            return false;
        }

        ExpressionSyntax? current = chain;
        while (current is not null)
        {
            switch (current)
            {
                case InvocationExpressionSyntax invocation:
                    current = invocation.Expression;
                    break;
                case MemberAccessExpressionSyntax memberAccess:
                    current = memberAccess.Expression;
                    break;
                default:
                    return current is not InterpolatedStringExpressionSyntax;
            }
        }

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
