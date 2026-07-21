using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace HooSharper.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferEarlyReturnAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HOO1001";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Prefer an early return",
        "Invert this condition and return early",
        "HooSharper.CodeStyle",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Prefer a guard clause when an if statement wraps the remaining statements of a void method.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeIfStatement, SyntaxKind.IfStatement);
    }

    private static void AnalyzeIfStatement(SyntaxNodeAnalysisContext context)
    {
        var ifStatement = (IfStatementSyntax)context.Node;
        if (ifStatement.Else is not null || ifStatement.Statement is not BlockSyntax block || block.Statements.Count == 0)
        {
            return;
        }

        if (context.SemanticModel.GetTypeInfo(ifStatement.Condition, context.CancellationToken).Type?.SpecialType !=
            SpecialType.System_Boolean)
        {
            return;
        }

        if (ifStatement.Parent is not BlockSyntax parentBlock || parentBlock.Statements.LastOrDefault() != ifStatement)
        {
            return;
        }

        if (parentBlock.Parent is not MethodDeclarationSyntax method || !method.ReturnType.IsKind(SyntaxKind.PredefinedType) ||
            !((PredefinedTypeSyntax)method.ReturnType).Keyword.IsKind(SyntaxKind.VoidKeyword) ||
            HasScopeCollision(ifStatement, parentBlock, block))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, ifStatement.IfKeyword.GetLocation()));
    }

    private static bool HasScopeCollision(
        IfStatementSyntax ifStatement,
        BlockSyntax parentBlock,
        BlockSyntax body)
    {
        var movedNames = new HashSet<string>(StringComparer.Ordinal);
        var movedLabels = new HashSet<string>(StringComparer.Ordinal);

        foreach (var statement in body.Statements)
        {
            switch (statement)
            {
                case LocalDeclarationStatementSyntax declaration:
                    foreach (var variable in declaration.Declaration.Variables)
                    {
                        movedNames.Add(variable.Identifier.ValueText);
                    }

                    break;
                case LocalFunctionStatementSyntax localFunction:
                    movedNames.Add(localFunction.Identifier.ValueText);
                    break;
                case LabeledStatementSyntax labeledStatement:
                    movedLabels.Add(labeledStatement.Identifier.ValueText);
                    break;
            }

            foreach (var designation in statement.DescendantNodes(ShouldDescendInto)
                         .OfType<SingleVariableDesignationSyntax>()
                         .Where(designation => designation.Ancestors().OfType<BlockSyntax>().FirstOrDefault() == body))
            {
                movedNames.Add(designation.Identifier.ValueText);
            }
        }

        if (movedNames.Count == 0 && movedLabels.Count == 0)
        {
            return false;
        }

        foreach (var statement in parentBlock.Statements)
        {
            if (statement == ifStatement)
            {
                continue;
            }

            foreach (var node in statement.DescendantNodesAndSelf(ShouldDescendInto))
            {
                var name = node switch
                {
                    VariableDeclaratorSyntax variable => variable.Identifier.ValueText,
                    SingleVariableDesignationSyntax designation => designation.Identifier.ValueText,
                    ForEachStatementSyntax forEachStatement => forEachStatement.Identifier.ValueText,
                    CatchDeclarationSyntax catchDeclaration => catchDeclaration.Identifier.ValueText,
                    LocalFunctionStatementSyntax localFunction => localFunction.Identifier.ValueText,
                    _ => null,
                };

                if (name is not null && movedNames.Contains(name))
                {
                    return true;
                }

                if (node is LabeledStatementSyntax label && movedLabels.Contains(label.Identifier.ValueText))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ShouldDescendInto(SyntaxNode node) =>
        node is not AnonymousFunctionExpressionSyntax and
        not LocalFunctionStatementSyntax and
        not TypeDeclarationSyntax;

}
