using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace HooSharper.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UseThrowIfNullAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HOO1009";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Use ArgumentNullException.ThrowIfNull",
        "Use ArgumentNullException.ThrowIfNull",
        "HooSharper.CodeStyle",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Replace a classic argument null guard with ArgumentNullException.ThrowIfNull.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static compilationContext =>
        {
            var exceptionType = compilationContext.Compilation.GetTypeByMetadataName("System.ArgumentNullException");
            if (exceptionType is null || !HasThrowIfNull(exceptionType))
            {
                return;
            }

            compilationContext.RegisterSyntaxNodeAction(
                nodeContext => AnalyzeIfStatement(nodeContext, exceptionType),
                SyntaxKind.IfStatement);
        });
    }

    private static bool HasThrowIfNull(INamedTypeSymbol exceptionType) =>
        exceptionType.GetMembers("ThrowIfNull")
            .OfType<IMethodSymbol>()
            .Any(static method =>
                method.IsStatic &&
                method.DeclaredAccessibility == Accessibility.Public &&
                method.ReturnsVoid &&
                method.Arity == 0 &&
                method.Parameters.Length == 2 &&
                method.Parameters[0].RefKind == RefKind.None &&
                method.Parameters[0].Type.SpecialType == SpecialType.System_Object &&
                method.Parameters[1].RefKind == RefKind.None &&
                method.Parameters[1].Type.SpecialType == SpecialType.System_String);

    private static void AnalyzeIfStatement(SyntaxNodeAnalysisContext context, INamedTypeSymbol exceptionType)
    {
        var ifStatement = (IfStatementSyntax)context.Node;
        if (ifStatement.Else is not null || !TryGetThrowStatement(ifStatement.Statement, out var throwStatement) ||
            HasDirective(ifStatement) ||
            throwStatement.Expression is not ObjectCreationExpressionSyntax { Initializer: null } creation ||
            creation.ArgumentList is not { Arguments.Count: 1 } argumentList ||
            argumentList.Arguments[0].NameColon is not null ||
            !SymbolEqualityComparer.Default.Equals(context.SemanticModel.GetTypeInfo(creation, context.CancellationToken).Type, exceptionType) ||
            !TryGetCheckedExpression(ifStatement.Condition, context, out var checkedExpression, out var location) ||
            !IsMatchingNameOf(argumentList.Arguments[0].Expression, checkedExpression, context))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, location));
    }

    private static bool TryGetThrowStatement(StatementSyntax statement, out ThrowStatementSyntax throwStatement)
    {
        if (statement is ThrowStatementSyntax directThrow)
        {
            throwStatement = directThrow;
            return true;
        }

        if (statement is BlockSyntax { Statements.Count: 1 } block && block.Statements[0] is ThrowStatementSyntax blockThrow)
        {
            throwStatement = blockThrow;
            return true;
        }

        throwStatement = null!;
        return false;
    }

    private static bool TryGetCheckedExpression(
        ExpressionSyntax condition,
        SyntaxNodeAnalysisContext context,
        out ExpressionSyntax checkedExpression,
        out Location location)
    {
        condition = WalkDownParentheses(condition);

        if (condition is IsPatternExpressionSyntax
            {
                IsKeyword: var isKeyword,
                Expression: var expression,
                Pattern: ConstantPatternSyntax { Expression.RawKind: (int)SyntaxKind.NullLiteralExpression },
            })
        {
            checkedExpression = WalkDownParentheses(expression);
            location = isKeyword.GetLocation();
            return !IsPointer(checkedExpression, context);
        }

        if (condition is BinaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.EqualsExpression,
                OperatorToken: var operatorToken,
            } equality &&
            context.SemanticModel.GetOperation(equality, context.CancellationToken) is IBinaryOperation
            {
                OperatorMethod: null,
                Type.TypeKind: not TypeKind.Dynamic,
            })
        {
            if (equality.Left.IsKind(SyntaxKind.NullLiteralExpression))
            {
                checkedExpression = WalkDownParentheses(equality.Right);
                location = operatorToken.GetLocation();
                return !IsPointer(checkedExpression, context);
            }

            if (equality.Right.IsKind(SyntaxKind.NullLiteralExpression))
            {
                checkedExpression = WalkDownParentheses(equality.Left);
                location = operatorToken.GetLocation();
                return !IsPointer(checkedExpression, context);
            }
        }

        checkedExpression = null!;
        location = null!;
        return false;
    }
    private static bool IsPointer(ExpressionSyntax expression, SyntaxNodeAnalysisContext context) =>
        context.SemanticModel.GetTypeInfo(expression, context.CancellationToken).Type?.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer;


    private static bool IsMatchingNameOf(
        ExpressionSyntax expression,
        ExpressionSyntax checkedExpression,
        SyntaxNodeAnalysisContext context)
    {
        if (context.SemanticModel.GetOperation(expression, context.CancellationToken) is not INameOfOperation nameOf)
        {
            return false;
        }

        var checkedSymbol = context.SemanticModel.GetSymbolInfo(checkedExpression, context.CancellationToken).Symbol;
        var namedSymbol = context.SemanticModel.GetSymbolInfo(nameOf.Argument.Syntax, context.CancellationToken).Symbol;
        return checkedSymbol is not null && SymbolEqualityComparer.Default.Equals(checkedSymbol, namedSymbol);
    }

    private static ExpressionSyntax WalkDownParentheses(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression;
    }

    private static bool HasDirective(IfStatementSyntax ifStatement) =>
        ifStatement.DescendantTrivia(descendIntoTrivia: true).Any(static trivia => trivia.IsDirective);
}
