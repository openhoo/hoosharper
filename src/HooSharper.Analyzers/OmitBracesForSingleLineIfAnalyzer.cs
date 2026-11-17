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

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeIfStatement, SyntaxKind.IfStatement);
    }

    private static void AnalyzeIfStatement(SyntaxNodeAnalysisContext context)
    {
        var ifStatement = (IfStatementSyntax)context.Node;
        if (ifStatement.ContainsDiagnostics)
        {
            return;
        }

        ReportIfSafe(context, ifStatement.Statement);

        if (ifStatement.Else is { Statement: not IfStatementSyntax } elseClause)
        {
            ReportIfSafe(context, elseClause.Statement);
        }
    }

    private static void ReportIfSafe(SyntaxNodeAnalysisContext context, StatementSyntax statement)
    {
        if (statement is BlockSyntax block && CanRemoveBraces(block))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, block.OpenBraceToken.GetLocation()));
        }
    }

    internal static bool CanRemoveBraces(BlockSyntax block) =>
        block.Parent is IfStatementSyntax or ElseClauseSyntax &&
        block.Statements.Count == 1 && !block.ContainsDiagnostics && !block.ContainsDirectives &&
        block.Statements[0] is not (LocalDeclarationStatementSyntax or LocalFunctionStatementSyntax or LabeledStatementSyntax) &&
        !(block.CloseBraceToken.GetNextToken().IsKind(SyntaxKind.ElseKeyword) &&
          CanCaptureFollowingElse(block.Statements[0])) &&
        !HooSharper.Internal.ControlFlowScopeSafety.HasExpandedScopeCollision(block, block.Statements[0]);

    private static bool CanCaptureFollowingElse(StatementSyntax statement) =>
        statement switch
        {
            // Fix All may also remove a single-statement block on this branch.
            BlockSyntax { Statements.Count: 1, Parent: IfStatementSyntax or ElseClauseSyntax } block =>
                CanCaptureFollowingElse(block.Statements[0]),
            IfStatementSyntax { Else: null } => true,
            IfStatementSyntax { Else: { Statement: var elseStatement } } =>
                CanCaptureFollowingElse(elseStatement),
            WhileStatementSyntax whileStatement => CanCaptureFollowingElse(whileStatement.Statement),
            ForStatementSyntax forStatement => CanCaptureFollowingElse(forStatement.Statement),
            ForEachStatementSyntax forEachStatement => CanCaptureFollowingElse(forEachStatement.Statement),
            ForEachVariableStatementSyntax forEachVariableStatement =>
                CanCaptureFollowingElse(forEachVariableStatement.Statement),
            DoStatementSyntax doStatement => CanCaptureFollowingElse(doStatement.Statement),
            UsingStatementSyntax usingStatement => CanCaptureFollowingElse(usingStatement.Statement),
            FixedStatementSyntax fixedStatement => CanCaptureFollowingElse(fixedStatement.Statement),
            LockStatementSyntax lockStatement => CanCaptureFollowingElse(lockStatement.Statement),
            LabeledStatementSyntax labeledStatement => CanCaptureFollowingElse(labeledStatement.Statement),
            _ => false,
        };
}
