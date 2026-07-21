using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace HooSharper.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UseNotPatternAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HOO1019";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Use a not pattern",
        "Use a not pattern",
        "HooSharper.CodeStyle",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Replace logical negation of an is-pattern expression with a not pattern.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeLogicalNot, SyntaxKind.LogicalNotExpression);
    }

    private static void AnalyzeLogicalNot(SyntaxNodeAnalysisContext context)
    {
        if (context.Node.SyntaxTree.Options is not CSharpParseOptions { LanguageVersion: >= LanguageVersion.CSharp9 } ||
            IsWithinExpressionTree(context.Node, context.SemanticModel, context.CancellationToken) ||
            context.Node is not PrefixUnaryExpressionSyntax
            {
                Operand: ParenthesizedExpressionSyntax { Expression: var expression },
            } logicalNot ||
            logicalNot.ContainsDirectives ||
            !IsSupportedIsExpression(expression))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, logicalNot.OperatorToken.GetLocation()));
    }

    private static bool IsWithinExpressionTree(
        SyntaxNode node,
        SemanticModel semanticModel,
        System.Threading.CancellationToken cancellationToken)
    {
        var expressionType = semanticModel.Compilation.GetTypeByMetadataName(
            "System.Linq.Expressions.Expression`1");
        if (expressionType is null)
        {
            return false;
        }

        return node.Ancestors()
            .OfType<AnonymousFunctionExpressionSyntax>()
            .Any(anonymousFunction =>
                semanticModel.GetTypeInfo(anonymousFunction, cancellationToken).ConvertedType is
                    INamedTypeSymbol convertedType &&
                SymbolEqualityComparer.Default.Equals(convertedType.OriginalDefinition, expressionType));
    }

    private static bool IsSupportedIsExpression(ExpressionSyntax expression) => expression switch
    {
        IsPatternExpressionSyntax isPattern => !ContainsDesignation(isPattern.Pattern),
        BinaryExpressionSyntax { RawKind: (int)SyntaxKind.IsExpression, Right: TypeSyntax } => true,
        _ => false,
    };

    internal static bool ContainsDesignation(PatternSyntax pattern) =>
        pattern.DescendantNodesAndSelf().Any(node => node is VariableDesignationSyntax);
}
