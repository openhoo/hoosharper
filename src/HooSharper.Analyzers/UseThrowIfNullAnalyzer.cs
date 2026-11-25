using System.Collections.Immutable;
using System.Linq;
using System.Threading;
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

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static compilationContext =>
        {
            var exceptionType = GetAvailableExceptionType(compilationContext.Compilation);
            if (exceptionType is null)
            {
                return;
            }

            compilationContext.RegisterSyntaxNodeAction(
                nodeContext => AnalyzeIfStatement(nodeContext, exceptionType),
                SyntaxKind.IfStatement);
        });
    }

    internal static INamedTypeSymbol? GetAvailableExceptionType(Compilation compilation)
    {
        var type = compilation.GetTypeByMetadataName("System.ArgumentNullException");
        return type is not null && HasThrowIfNull(type) ? type : null;
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
        if (FindGuard(ifStatement, context.SemanticModel, exceptionType, context.CancellationToken) is { } guard)
        {
            context.ReportIfUncaptured(Rule, guard.Location, ifStatement);
        }
    }

    internal static (ExpressionSyntax Checked, ObjectCreationExpressionSyntax Creation, ThrowStatementSyntax Throw, Location Location)?
        FindGuard(IfStatementSyntax ifStatement, SemanticModel model, INamedTypeSymbol exceptionType, CancellationToken token)
    {
        if (ifStatement.ContainsDiagnostics || ifStatement.Else is not null ||
            !TryGetThrowStatement(ifStatement.Statement, out var throwStatement) || HasDirective(ifStatement) ||
            throwStatement.Expression is not ObjectCreationExpressionSyntax { Initializer: null } creation ||
            creation.ArgumentList is not { Arguments.Count: 1 } argumentList ||
            argumentList.Arguments[0].NameColon is not null ||
            !SymbolEqualityComparer.Default.Equals(model.GetTypeInfo(creation, token).Type, exceptionType) ||
            !TryGetCheckedExpression(ifStatement.Condition, model, token, out var checkedExpression, out var location) ||
            !MatchesArgumentName(argumentList.Arguments[0].Expression, checkedExpression, model, token))
        {
            return null;
        }

        return (checkedExpression, creation, throwStatement, location);
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
        SemanticModel model,
        CancellationToken token,
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
            return CanPassToThrowIfNull(checkedExpression, model, token);
        }

        if (condition is BinaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.EqualsExpression,
                OperatorToken: var operatorToken,
            } equality &&
            HooSharper.Internal.NullComparisonSafety.IsSafeBinary(
                model.GetOperation(equality, token)))
        {
            if (equality.Left.IsKind(SyntaxKind.NullLiteralExpression))
            {
                checkedExpression = WalkDownParentheses(equality.Right);
                location = operatorToken.GetLocation();
                return CanPassToThrowIfNull(checkedExpression, model, token);
            }

            if (equality.Right.IsKind(SyntaxKind.NullLiteralExpression))
            {
                checkedExpression = WalkDownParentheses(equality.Left);
                location = operatorToken.GetLocation();
                return CanPassToThrowIfNull(checkedExpression, model, token);
            }
        }

        checkedExpression = null!;
        location = null!;
        return false;
    }

    private static bool CanPassToThrowIfNull(ExpressionSyntax expression, SemanticModel model, CancellationToken token)
    {
        var type = model.GetTypeInfo(expression, token).Type;
        return type is { TypeKind: not (TypeKind.Pointer or TypeKind.FunctionPointer or TypeKind.Error or TypeKind.Dynamic) } &&
            model.Compilation.ClassifyConversion(
                type,
                model.Compilation.GetSpecialType(SpecialType.System_Object)).IsImplicit;
    }

    private static bool MatchesArgumentName(ExpressionSyntax expression, ExpressionSyntax checkedExpression,
        SemanticModel model, CancellationToken token)
    {
        var checkedSymbol = model.GetSymbolInfo(checkedExpression, token).Symbol;
        if (checkedSymbol is null)
        {
            return false;
        }

        // Legacy parameter guards often spell the parameter name as a literal.
        // Keep that literal and require the exact parameter name (including case).
        if (expression.IsKind(SyntaxKind.StringLiteralExpression))
        {
            return checkedExpression is IdentifierNameSyntax && checkedSymbol is IParameterSymbol parameter &&
                ((LiteralExpressionSyntax)expression).Token.ValueText == parameter.Name;
        }

        return model.GetOperation(expression, token) is INameOfOperation nameOf &&
            SymbolEqualityComparer.Default.Equals(checkedSymbol, model.GetSymbolInfo(nameOf.Argument.Syntax, token).Symbol);
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
