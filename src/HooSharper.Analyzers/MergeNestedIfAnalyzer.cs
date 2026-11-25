using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace HooSharper.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MergeNestedIfAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HOO1010";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Merge nested if statements",
        "Merge these nested if statements",
        "HooSharper.CodeStyle",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Combine nested if statements without else branches into a single condition.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeIfStatement, SyntaxKind.IfStatement);
    }

    private static void AnalyzeIfStatement(SyntaxNodeAnalysisContext context)
    {
        var outerIf = (IfStatementSyntax)context.Node;
        if (!CanMerge(outerIf, context.SemanticModel, context.CancellationToken) ||
            HasMergingParent(outerIf, context.SemanticModel, context.CancellationToken))
        {
            return;
        }

        context.ReportIfUncaptured(Rule, outerIf.IfKeyword.GetLocation(), outerIf);
    }

    internal static bool CanMerge(IfStatementSyntax outerIf, SemanticModel model, System.Threading.CancellationToken token) =>
        TryGetInnerIf(outerIf, out _) && !outerIf.ContainsDiagnostics && !outerIf.ContainsDirectives &&
        AreAllConditionsOrdinaryBoolean(outerIf, model, token) && !IntroducesDeclarationCollision(outerIf);

    internal static bool TryGetInnerIf(IfStatementSyntax outerIf, out IfStatementSyntax innerIf)
    {
        var statement = outerIf.Statement is BlockSyntax { Statements.Count: 1 } block
            ? block.Statements[0]
            : outerIf.Statement;
        if (outerIf.Else is null && statement is IfStatementSyntax { Else: null } candidate)
        {
            innerIf = candidate;
            return true;
        }

        innerIf = null!;
        return false;
    }

    private static bool HasMergingParent(IfStatementSyntax statement, SemanticModel model, System.Threading.CancellationToken token)
    {
        var parent = statement.Parent is BlockSyntax block ? block.Parent : statement.Parent;
        return parent is IfStatementSyntax parentIf && TryGetInnerIf(parentIf, out var child) &&
            child == statement && CanMerge(parentIf, model, token);
    }

    internal static bool IntroducesDeclarationCollision(IfStatementSyntax outerIf)
    {
        var introducedNames = new HashSet<string>();
        var current = outerIf;
        var precedingConditions = new List<ExpressionSyntax> { outerIf.Condition };
        while (TryGetInnerIf(current, out var innerIf))
        {
            foreach (var node in innerIf.Condition.DescendantNodesAndSelf())
            {
                if (node is SingleVariableDesignationSyntax designation)
                {
                    var name = designation.Identifier.ValueText;
                    if (precedingConditions.Any(condition => condition.DescendantTokens().Any(token =>
                        token.IsKind(SyntaxKind.IdentifierToken) && token.ValueText == name)))
                    {
                        return true;
                    }

                    introducedNames.Add(name);
                }
            }

            precedingConditions.Add(innerIf.Condition);
            current = innerIf;
        }

        if (introducedNames.Count == 0)
        {
            return false;
        }

        if (outerIf.Condition.DescendantTokens().Any(token =>
            token.IsKind(SyntaxKind.IdentifierToken) && introducedNames.Contains(token.ValueText)))
        {
            return true;
        }

        if (outerIf.Parent is BlockSyntax containingBlock)
        {
            var outerIndex = containingBlock.Statements.IndexOf(outerIf);
            if (outerIndex < 0)
            {
                return false;
            }

            for (var statementIndex = 0; statementIndex < containingBlock.Statements.Count; statementIndex++)
            {
                if (statementIndex == outerIndex)
                {
                    continue;
                }

                foreach (var node in containingBlock.Statements[statementIndex].DescendantNodesAndSelf())
                {
                    var name = HooSharper.Internal.ControlFlowScopeSafety.DeclaredName(node);

                    if (node is IdentifierNameSyntax identifier && introducedNames.Contains(identifier.Identifier.ValueText) ||
                        node is GenericNameSyntax generic && introducedNames.Contains(generic.Identifier.ValueText) ||
                        name is not null && introducedNames.Contains(name))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        var enclosingBlock = outerIf.FirstAncestorOrSelf<BlockSyntax>();
        if (enclosingBlock is null)
        {
            return true;
        }

        foreach (var statement in enclosingBlock.Statements)
        {
            foreach (var node in statement.DescendantNodesAndSelf())
            {
                if (outerIf.FullSpan.Contains(node.Span))
                {
                    continue;
                }

                var name = HooSharper.Internal.ControlFlowScopeSafety.DeclaredName(node);

                if (node is IdentifierNameSyntax identifier && introducedNames.Contains(identifier.Identifier.ValueText) ||
                    node is GenericNameSyntax generic && introducedNames.Contains(generic.Identifier.ValueText) ||
                    name is not null && introducedNames.Contains(name))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool AreAllConditionsOrdinaryBoolean(
        IfStatementSyntax outerIf, SemanticModel model, System.Threading.CancellationToken token)
    {
        var current = outerIf;
        while (true)
        {
            if (model.GetTypeInfo(current.Condition, token).Type?.SpecialType !=
                SpecialType.System_Boolean)
            {
                return false;
            }

            if (!TryGetInnerIf(current, out var innerIf))
            {
                return true;
            }

            current = innerIf;
        }
    }
}
