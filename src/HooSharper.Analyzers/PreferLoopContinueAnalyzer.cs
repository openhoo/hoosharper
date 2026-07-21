using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace HooSharper.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferLoopContinueAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HOO1004";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Prefer an early continue",
        "Invert this condition and continue early",
        "HooSharper.CodeStyle",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Prefer a continue guard when a final if statement wraps the remaining work in a loop body.");

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
        if (ifStatement.Else is not null ||
            ifStatement.Statement is not BlockSyntax block ||
            block.Statements.Count == 0 ||
            ifStatement.ContainsDirectives)
        {
            return;
        }

        if (ifStatement.Parent is not BlockSyntax loopBody ||
            loopBody.Statements.LastOrDefault() != ifStatement ||
            !IsLoopBody(loopBody) ||
            HasBindingCollision(ifStatement, loopBody, block))
        {
            return;
        }

        if (context.SemanticModel.GetTypeInfo(ifStatement.Condition, context.CancellationToken).Type?.SpecialType !=
            SpecialType.System_Boolean)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, ifStatement.IfKeyword.GetLocation()));
    }

    internal static bool HasBindingCollision(
        IfStatementSyntax ifStatement,
        BlockSyntax loopBody,
        BlockSyntax body)
    {
        var introducedNames = CollectDeclaredNames(body);
        if (introducedNames.Count == 0)
        {
            return false;
        }

        var ifIndex = loopBody.Statements.IndexOf(ifStatement);
        for (var index = 0; index < ifIndex; index++)
        {
            foreach (var node in loopBody.Statements[index].DescendantNodes())
            {
                var name = GetDeclaredName(node);
                if (name is not null && introducedNames.Contains(name))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static HashSet<string> CollectDeclaredNames(SyntaxNode scope)
    {
        var names = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var node in scope.DescendantNodes())
        {
            var name = GetDeclaredName(node);
            if (name is not null)
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static string? GetDeclaredName(SyntaxNode node) => node switch
    {
        VariableDeclaratorSyntax declarator => declarator.Identifier.ValueText,
        SingleVariableDesignationSyntax designation => designation.Identifier.ValueText,
        ForEachStatementSyntax forEachStatement => forEachStatement.Identifier.ValueText,
        CatchDeclarationSyntax catchDeclaration => catchDeclaration.Identifier.ValueText,
        LocalFunctionStatementSyntax localFunction => localFunction.Identifier.ValueText,
        _ => null,
    };

    private static bool IsLoopBody(BlockSyntax block) => block.Parent switch
    {
        ForStatementSyntax forStatement => forStatement.Statement == block,
        ForEachStatementSyntax forEachStatement => forEachStatement.Statement == block,
        ForEachVariableStatementSyntax forEachVariableStatement => forEachVariableStatement.Statement == block,
        WhileStatementSyntax whileStatement => whileStatement.Statement == block,
        DoStatementSyntax doStatement => doStatement.Statement == block,
        _ => false,
    };
}
