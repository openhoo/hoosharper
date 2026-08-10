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
        context.RegisterCompilationStartAction(static compilationContext =>
        {
            var expressionType = compilationContext.Compilation.GetTypeByMetadataName(
                "System.Linq.Expressions.Expression`1");
            var queryableType = compilationContext.Compilation.GetTypeByMetadataName(
                "System.Linq.IQueryable");
            compilationContext.RegisterSyntaxNodeAction(
                nodeContext => AnalyzeLogicalNot(nodeContext, expressionType, queryableType),
                SyntaxKind.LogicalNotExpression);
        });
    }

    private static void AnalyzeLogicalNot(
        SyntaxNodeAnalysisContext context,
        INamedTypeSymbol? expressionType,
        INamedTypeSymbol? queryableType)
    {
        if (context.Node.SyntaxTree.Options is not CSharpParseOptions parseOptions ||
            parseOptions.LanguageVersion != LanguageVersion.Default &&
            parseOptions.LanguageVersion < LanguageVersion.CSharp9 ||
            context.Node is not PrefixUnaryExpressionSyntax
            {
                Operand: ParenthesizedExpressionSyntax { Expression: var expression },
            } logicalNot ||
            logicalNot.ContainsDirectives ||
            !IsSupportedIsExpression(
                expression,
                context.SemanticModel,
                context.CancellationToken) ||
            HasEligibleNotAncestor(
                logicalNot,
                context.SemanticModel,
                context.CancellationToken) ||
            IsWithinExpressionTree(
                context.Node,
                context.SemanticModel,
                expressionType,
                queryableType,
                context.CancellationToken))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, logicalNot.OperatorToken.GetLocation()));
    }

    private static bool IsWithinExpressionTree(
        SyntaxNode node,
        SemanticModel semanticModel,
        INamedTypeSymbol? expressionType,
        INamedTypeSymbol? queryableType,
        System.Threading.CancellationToken cancellationToken)
    {
        for (var ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (expressionType is not null &&
                ancestor is AnonymousFunctionExpressionSyntax anonymousFunction &&
                semanticModel.GetTypeInfo(anonymousFunction, cancellationToken).ConvertedType is
                    INamedTypeSymbol convertedType &&
                SymbolEqualityComparer.Default.Equals(convertedType.OriginalDefinition, expressionType))
            {
                return true;
            }

            if (queryableType is not null &&
                ancestor is QueryExpressionSyntax queryExpression &&
                semanticModel.GetTypeInfo(queryExpression, cancellationToken).Type is INamedTypeSymbol queryType &&
                (SymbolEqualityComparer.Default.Equals(queryType.OriginalDefinition, queryableType) ||
                    queryType.AllInterfaces.Any(
                        interfaceType => SymbolEqualityComparer.Default.Equals(
                            interfaceType.OriginalDefinition,
                            queryableType))))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasEligibleNotAncestor(
        PrefixUnaryExpressionSyntax logicalNot,
        SemanticModel semanticModel,
        System.Threading.CancellationToken cancellationToken)
    {
        for (var ancestor = logicalNot.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor is PrefixUnaryExpressionSyntax
                {
                    Operand: ParenthesizedExpressionSyntax { Expression: var expression },
                } &&
                IsSupportedIsExpression(expression, semanticModel, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSupportedIsExpression(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        System.Threading.CancellationToken cancellationToken)
    {
        ExpressionSyntax target;
        TypeSyntax? patternType;
        switch (expression)
        {
            case IsPatternExpressionSyntax isPattern when !ContainsDesignation(isPattern.Pattern):
                target = isPattern.Expression;
                patternType = isPattern.Pattern is TypePatternSyntax typePattern ? typePattern.Type : null;
                break;
            case BinaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.IsExpression,
                Left: var left,
                Right: TypeSyntax type,
            }:
                target = left;
                patternType = type;
                break;
            default:
                return false;
        }

        if (patternType is null)
        {
            return true;
        }

        var targetType = semanticModel.GetTypeInfo(target, cancellationToken).Type;
        var matchedType = semanticModel.GetTypeInfo(patternType, cancellationToken).Type;
        return targetType is null ||
               matchedType is null ||
               !targetType.IsValueType ||
               targetType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T ||
               !SymbolEqualityComparer.Default.Equals(targetType, matchedType);
    }

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
