using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace HooSharper.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UseUsingDeclarationAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HOO1013";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Use a using declaration",
        "Convert this using statement to a using declaration",
        "HooSharper.CodeStyle",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Use a using declaration when a using statement is the final statement in its block.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeUsingStatement, SyntaxKind.UsingStatement);
    }

    private static void AnalyzeUsingStatement(SyntaxNodeAnalysisContext context)
    {
        if (context.Node.SyntaxTree.Options is not CSharpParseOptions { LanguageVersion: >= LanguageVersion.CSharp8 })
        {
            return;
        }

        var usingStatement = (UsingStatementSyntax)context.Node;
        if (usingStatement.Parent is not BlockSyntax parentBlock ||
            parentBlock.Statements.LastOrDefault() != usingStatement ||
            usingStatement.Declaration is not { Variables.Count: 1 } declaration ||
            declaration.Variables[0].Initializer is null ||
            usingStatement.Expression is not null ||
            usingStatement.Statement is not BlockSyntax { Statements.Count: > 0 } body ||
            usingStatement.ContainsDirectives ||
            HasBindingCollision(usingStatement, parentBlock, body))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, usingStatement.UsingKeyword.GetLocation()));
    }

    private static bool HasBindingCollision(
        UsingStatementSyntax usingStatement,
        BlockSyntax parentBlock,
        BlockSyntax body)
    {
        var introducedNames = new HashSet<string>(System.StringComparer.Ordinal)
        {
            usingStatement.Declaration!.Variables[0].Identifier.ValueText,
        };

        foreach (var node in body.DescendantNodes())
        {
            switch (node)
            {
                case VariableDeclaratorSyntax declarator:
                    introducedNames.Add(declarator.Identifier.ValueText);
                    break;
                case SingleVariableDesignationSyntax designation:
                    introducedNames.Add(designation.Identifier.ValueText);
                    break;
                case ForEachStatementSyntax forEachStatement:
                    introducedNames.Add(forEachStatement.Identifier.ValueText);
                    break;
                case CatchDeclarationSyntax catchDeclaration:
                    introducedNames.Add(catchDeclaration.Identifier.ValueText);
                    break;
                case LocalFunctionStatementSyntax localFunction:
                    introducedNames.Add(localFunction.Identifier.ValueText);
                    break;
            }
        }

        var usingIndex = parentBlock.Statements.IndexOf(usingStatement);
        for (var index = 0; index < usingIndex; index++)
        {
            foreach (var token in parentBlock.Statements[index].DescendantTokens())
            {
                if (token.IsKind(SyntaxKind.IdentifierToken) && introducedNames.Contains(token.ValueText))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
