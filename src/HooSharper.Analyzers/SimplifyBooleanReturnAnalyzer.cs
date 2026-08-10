using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace HooSharper.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SimplifyBooleanReturnAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HOO1017";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Simplify boolean return",
        "Simplify these boolean returns",
        "HooSharper.CodeStyle",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Adjacent returns of opposite boolean literals can be replaced with a direct return of the condition.");

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
            ifStatement.ContainsDirectives ||
            !TryGetReturnedLiteral(ifStatement.Statement, out var branchValue) ||
            ifStatement.Parent is not BlockSyntax parentBlock)
        {
            return;
        }

        var index = parentBlock.Statements.IndexOf(ifStatement);
        if (index < 0 || index + 1 >= parentBlock.Statements.Count ||
            parentBlock.Statements[index + 1] is not ReturnStatementSyntax nextReturn ||
            nextReturn.ContainsDirectives ||
            !TryGetReturnedLiteral(nextReturn, out var nextValue) ||
            branchValue == nextValue)
        {
            return;
        }

        var conditionOperation = context.SemanticModel.GetOperation(ifStatement.Condition, context.CancellationToken);
        if (conditionOperation?.Type?.SpecialType != SpecialType.System_Boolean ||
            ContainsUserDefinedNot(conditionOperation))
        {
            return;
        }

        if (context.SemanticModel.GetEnclosingSymbol(ifStatement.SpanStart, context.CancellationToken) is not IMethodSymbol method ||
            method.ReturnType.SpecialType != SpecialType.System_Boolean)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, ifStatement.IfKeyword.GetLocation()));
    }

    private static bool ContainsUserDefinedNot(IOperation? operation) =>
        operation is not null &&
        operation.DescendantsAndSelf().OfType<IUnaryOperation>().Any(unary =>
            unary.OperatorKind == UnaryOperatorKind.Not && unary.OperatorMethod is not null);

    internal static bool TryGetReturnedLiteral(StatementSyntax statement, out bool value)
    {
        var returnStatement = statement switch
        {
            ReturnStatementSyntax directReturn => directReturn,
            BlockSyntax { Statements.Count: 1 } block when block.Statements[0] is ReturnStatementSyntax blockReturn =>
                blockReturn,
            _ => null,
        };

        if (returnStatement?.Expression is LiteralExpressionSyntax literal)
        {
            if (literal.IsKind(SyntaxKind.TrueLiteralExpression))
            {
                value = true;
                return true;
            }

            if (literal.IsKind(SyntaxKind.FalseLiteralExpression))
            {
                value = false;
                return true;
            }
        }

        value = false;
        return false;
    }
}
