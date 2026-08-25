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

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeIfStatement, SyntaxKind.IfStatement);
    }

    private static void AnalyzeIfStatement(SyntaxNodeAnalysisContext context)
    {
        var outerIf = (IfStatementSyntax)context.Node;
        if (!TryGetInnerIf(outerIf, out var innerIf) ||
            outerIf.ContainsDirectives ||
            IsNestedEligibleIf(outerIf) ||
            !AreAllConditionsOrdinaryBoolean(context, outerIf) ||
            IntroducesDeclarationCollision(outerIf))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, outerIf.IfKeyword.GetLocation()));
    }

    private static bool TryGetInnerIf(IfStatementSyntax outerIf, out IfStatementSyntax innerIf)
    {
        if (outerIf.Else is null &&
            outerIf.Statement is BlockSyntax { Statements.Count: 1 } block &&
            block.Statements[0] is IfStatementSyntax { Else: null } candidate)
        {
            innerIf = candidate;
            return true;
        }

        innerIf = null!;
        return false;
    }

    private static bool IsNestedEligibleIf(IfStatementSyntax statement) =>
        statement.Parent is BlockSyntax { Statements.Count: 1 } block &&
        block.Parent is IfStatementSyntax { Else: null };

    private static bool IntroducesDeclarationCollision(IfStatementSyntax outerIf)
    {
        var introducedNames = new HashSet<string>();
        var current = outerIf;
        while (TryGetInnerIf(current, out var innerIf))
        {
            foreach (var node in innerIf.Condition.DescendantNodesAndSelf())
            {
                if (node is SingleVariableDesignationSyntax designation)
                {
                    introducedNames.Add(designation.Identifier.ValueText);
                }
            }

            current = innerIf;
        }

        if (introducedNames.Count == 0)
        {
            return false;
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
                    var name = node switch
                    {
                        VariableDeclaratorSyntax declaration => declaration.Identifier.ValueText,
                        SingleVariableDesignationSyntax designation => designation.Identifier.ValueText,
                        ForEachStatementSyntax forEachStatement => forEachStatement.Identifier.ValueText,
                        CatchDeclarationSyntax catchDeclaration => catchDeclaration.Identifier.ValueText,
                        LocalFunctionStatementSyntax localFunction => localFunction.Identifier.ValueText,
                        _ => null,
                    };

                    if (name is not null && introducedNames.Contains(name))
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

                var name = node switch
                {
                    VariableDeclaratorSyntax declaration => declaration.Identifier.ValueText,
                    SingleVariableDesignationSyntax designation => designation.Identifier.ValueText,
                    ForEachStatementSyntax forEachStatement => forEachStatement.Identifier.ValueText,
                    CatchDeclarationSyntax catchDeclaration => catchDeclaration.Identifier.ValueText,
                    LocalFunctionStatementSyntax localFunction => localFunction.Identifier.ValueText,
                    _ => null,
                };

                if (name is not null && introducedNames.Contains(name))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool AreAllConditionsOrdinaryBoolean(
        SyntaxNodeAnalysisContext context,
        IfStatementSyntax outerIf)
    {
        var current = outerIf;
        while (true)
        {
            if (context.SemanticModel.GetTypeInfo(current.Condition, context.CancellationToken).Type?.SpecialType !=
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
