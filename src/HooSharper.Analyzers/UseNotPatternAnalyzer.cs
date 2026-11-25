using System.Collections.Immutable;
using System.Threading;
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

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeLogicalNot, SyntaxKind.LogicalNotExpression);
    }

    private static void AnalyzeLogicalNot(SyntaxNodeAnalysisContext context)
    {
        var logicalNot = (PrefixUnaryExpressionSyntax)context.Node;
        if (!CanRewrite(logicalNot, context.SemanticModel, context.CancellationToken))
            return;

        for (var ancestor = logicalNot.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor is PrefixUnaryExpressionSyntax parent && CanRewrite(parent, context.SemanticModel, context.CancellationToken))
                return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, logicalNot.OperatorToken.GetLocation()));
    }

    internal static bool CanRewrite(PrefixUnaryExpressionSyntax node, SemanticModel model, CancellationToken token) =>
        node.IsKind(SyntaxKind.LogicalNotExpression) &&
        node.Operand is ParenthesizedExpressionSyntax { Expression: var expression } &&
        node.SyntaxTree.Options is CSharpParseOptions parseOptions &&
        (parseOptions.LanguageVersion == LanguageVersion.Default || parseOptions.LanguageVersion >= LanguageVersion.CSharp9) &&
        !node.ContainsDiagnostics && !node.ContainsDirectives &&
        IsSupportedIsExpression(expression, model, token) && !ExpressionTreeSafety.IsQuoted(node, model, token) &&
        !CallerArgumentExpressionSafety.IsCaptured(node, model, token);

    private static bool IsSupportedIsExpression(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        System.Threading.CancellationToken cancellationToken)
    {
        ExpressionSyntax target;
        TypeSyntax? patternType;
        switch (expression)
        {
            case IsPatternExpressionSyntax isPattern when IsSupportedPattern(isPattern.Pattern):
                target = isPattern.Expression;
                patternType = isPattern.Pattern switch
                {
                    TypePatternSyntax typePattern => typePattern.Type,
                    DeclarationPatternSyntax declarationPattern => declarationPattern.Type,
                    _ => null,
                };
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
        if (targetType is null || matchedType is null ||
            targetType.TypeKind == TypeKind.Error || matchedType.TypeKind == TypeKind.Error)
        {
            return false;
        }

        var nullableTarget = targetType is INamedTypeSymbol
        {
            OriginalDefinition.SpecialType: SpecialType.System_Nullable_T,
        };
        var effectiveTarget = nullableTarget
            ? ((INamedTypeSymbol)targetType).TypeArguments[0]
            : targetType;
        if (effectiveTarget.TypeKind == TypeKind.Dynamic)
        {
            effectiveTarget = semanticModel.Compilation.GetSpecialType(SpecialType.System_Object);
        }
        var conversion = semanticModel.Compilation.ClassifyConversion(effectiveTarget, matchedType);
        // Negating an always-matching value-type test creates a prohibited pattern.
        if (!nullableTarget && targetType.IsValueType &&
            (conversion.IsIdentity || conversion.IsBoxing && conversion.IsImplicit))
        {
            return false;
        }

        // Legacy is-type expressions allow impossible tests as warnings; not patterns
        // require compatible types and turn these expressions into compiler errors.
        return conversion.IsIdentity || conversion.IsReference || conversion.IsBoxing || conversion.IsUnboxing ||
            effectiveTarget.TypeKind == TypeKind.TypeParameter || matchedType.TypeKind == TypeKind.TypeParameter;
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

    internal static bool IsSupportedPattern(PatternSyntax pattern) =>
        pattern is DeclarationPatternSyntax
        {
            Designation: SingleVariableDesignationSyntax { Identifier.IsMissing: false } or DiscardDesignationSyntax,
        } || !ContainsDesignation(pattern);
}
