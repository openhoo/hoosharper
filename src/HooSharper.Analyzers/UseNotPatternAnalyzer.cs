using System.Collections.Immutable;
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
        context.RegisterCompilationStartAction(static compilationContext =>
        {
            var expressionType = compilationContext.Compilation.GetTypeByMetadataName(
                "System.Linq.Expressions.Expression`1");
            compilationContext.RegisterSyntaxNodeAction(
                nodeContext => AnalyzeLogicalNot(nodeContext, expressionType),
                SyntaxKind.LogicalNotExpression);
        });
    }

    private static void AnalyzeLogicalNot(
        SyntaxNodeAnalysisContext context,
        INamedTypeSymbol? expressionType)
    {
        if (context.Node.SyntaxTree.Options is not CSharpParseOptions { LanguageVersion: >= LanguageVersion.CSharp9 } ||
            context.Node is not PrefixUnaryExpressionSyntax
            {
                Operand: ParenthesizedExpressionSyntax { Expression: var expression },
            } logicalNot ||
            logicalNot.ContainsDirectives ||
            !IsSupportedIsExpression(expression) ||
            IsWithinExpressionTree(context.Node, context.SemanticModel, expressionType, context.CancellationToken))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, logicalNot.OperatorToken.GetLocation()));
    }

    private static bool IsWithinExpressionTree(
        SyntaxNode node,
        SemanticModel semanticModel,
        INamedTypeSymbol? expressionType,
        System.Threading.CancellationToken cancellationToken)
    {
        if (expressionType is null)
        {
            return false;
        }
        for (var ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor is AnonymousFunctionExpressionSyntax anonymousFunction &&
                semanticModel.GetTypeInfo(anonymousFunction, cancellationToken).ConvertedType is
                    INamedTypeSymbol convertedType &&
                SymbolEqualityComparer.Default.Equals(convertedType.OriginalDefinition, expressionType))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSupportedIsExpression(ExpressionSyntax expression) => expression switch
    {
        IsPatternExpressionSyntax isPattern => !ContainsDesignation(isPattern.Pattern),
        BinaryExpressionSyntax { RawKind: (int)SyntaxKind.IsExpression, Right: TypeSyntax } => true,
        _ => false,
    };

    internal static bool ContainsDesignation(PatternSyntax pattern)
    {
        foreach (var node in pattern.DescendantNodesAndSelf())
        {
            if (node is VariableDesignationSyntax)
            {
                return true;
            }
        }

        return false;
    }
}
