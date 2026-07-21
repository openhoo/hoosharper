using System.Collections.Generic;
using System.Linq;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace HooSharper.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class OmitBracesForSingleLineIfAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HOO1002";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Omit braces for a single-statement if",
        "Remove braces from this single-statement if",
        "HooSharper.CodeStyle",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Single-statement if branches do not need braces.");

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
        ReportIfSafe(context, ifStatement.Statement, ifStatement.Else is not null);

        if (ifStatement.Else is { Statement: not IfStatementSyntax } elseClause)
        {
            ReportIfSafe(context, elseClause.Statement, hasFollowingElse: false);
        }
    }

    private static void ReportIfSafe(SyntaxNodeAnalysisContext context, StatementSyntax statement, bool hasFollowingElse)
    {
        if (statement is not BlockSyntax { Statements.Count: 1 } block)
        {
            return;
        }

        var nestedStatement = block.Statements[0];
        if (nestedStatement is LocalDeclarationStatementSyntax or LocalFunctionStatementSyntax ||
            hasFollowingElse && nestedStatement is IfStatementSyntax { Else: null } ||
            HasDirective(block) ||
            HasExpandedScopeCollision(block, nestedStatement))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, block.OpenBraceToken.GetLocation()));
    }

    private static bool HasExpandedScopeCollision(BlockSyntax block, StatementSyntax nestedStatement)
    {
        var introducedNames = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var node in nestedStatement.DescendantNodesAndSelf())
        {
            if (node is SingleVariableDesignationSyntax designation &&
                !designation.Identifier.IsKind(SyntaxKind.UnderscoreToken))
            {
                introducedNames.Add(designation.Identifier.ValueText);
            }
        }

        if (introducedNames.Count == 0)
        {
            return false;
        }

        StatementSyntax? containingStatement = null;
        for (var current = block.Parent; current is not null; current = current.Parent)
        {
            if (current is StatementSyntax statement)
            {
                containingStatement = statement;
                break;
            }
        }

        if (containingStatement?.Parent is not BlockSyntax parentBlock)
        {
            return true;
        }

        var statementIndex = parentBlock.Statements.IndexOf(containingStatement);
        for (var index = statementIndex + 1; index < parentBlock.Statements.Count; index++)
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

    private static bool HasDirective(BlockSyntax block)
    {
        foreach (var trivia in block.DescendantTrivia(descendIntoTrivia: true))
        {
            if (trivia.IsDirective)
            {
                return true;
            }
        }

        return false;
    }
}
