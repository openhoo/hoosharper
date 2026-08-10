using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Diagnostics;

namespace HooSharper.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UseTypePatternAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HOO1005";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Use a type pattern",
        "Replace the as cast and null check with a type pattern",
        "HooSharper.CodeStyle",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Use a declaration pattern when an as cast is immediately followed by a null check.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeLocalDeclaration, SyntaxKind.LocalDeclarationStatement);
    }

    private static void AnalyzeLocalDeclaration(SyntaxNodeAnalysisContext context)
    {
        if (context.Node.SyntaxTree.Options is not CSharpParseOptions parseOptions ||
            !SupportsTypePatterns(parseOptions.LanguageVersion))
        {
            return;
        }

        var declarationStatement = (LocalDeclarationStatementSyntax)context.Node;
        if (!declarationStatement.Declaration.Type.IsVar ||
            !declarationStatement.UsingKeyword.IsKind(SyntaxKind.None) ||
            !declarationStatement.AwaitKeyword.IsKind(SyntaxKind.None) ||
            declarationStatement.Declaration.Variables.Count != 1 ||
            declarationStatement.Parent is not BlockSyntax block ||
            declarationStatement.ContainsDirectives)
        {
            return;
        }

        var declarator = declarationStatement.Declaration.Variables[0];
        if (declarator.Identifier.ValueText == "_" ||
            declarator.Initializer?.Value is not BinaryExpressionSyntax asExpression ||
            !asExpression.IsKind(SyntaxKind.AsExpression) ||
            asExpression.Right is NullableTypeSyntax)
        {
            return;
        }

        var statementIndex = block.Statements.IndexOf(declarationStatement);
        if (statementIndex < 0 || statementIndex + 1 >= block.Statements.Count ||
            block.Statements[statementIndex + 1] is not IfStatementSyntax ifStatement ||
            ifStatement.ContainsDirectives)
        {
            return;
        }

        var checkedIdentifier = GetNullCheckedIdentifier(ifStatement.Condition);
        var nullCheck = WalkDownParentheses(ifStatement.Condition);
        if (checkedIdentifier is null ||
            (nullCheck is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.NotEqualsExpression } &&
             context.SemanticModel.GetOperation(nullCheck, context.CancellationToken) is not IBinaryOperation
             {
                 OperatorMethod: null,
             }))
        {
            return;
        }

        var local = context.SemanticModel.GetDeclaredSymbol(declarator, context.CancellationToken) as ILocalSymbol;
        var checkedSymbol = context.SemanticModel.GetSymbolInfo(checkedIdentifier, context.CancellationToken).Symbol;
        if (local is null || !SymbolEqualityComparer.Default.Equals(local, checkedSymbol))
        {
            return;
        }

        var patternType = context.SemanticModel.GetTypeInfo(asExpression.Right, context.CancellationToken).Type;
        if (patternType is null || patternType.TypeKind is TypeKind.Dynamic or TypeKind.Error || IsNullableValueType(patternType))
        {
            return;
        }

        if (ifStatement.Else is not null && ContainsReference(
                ifStatement.Else.Statement, local, declarator.Identifier.ValueText, context.SemanticModel, context.CancellationToken))
        {
            return;
        }

        for (var index = statementIndex + 2; index < block.Statements.Count; index++)
        {
            if (ContainsReference(
                    block.Statements[index], local, declarator.Identifier.ValueText, context.SemanticModel, context.CancellationToken))
            {
                return;
            }
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, asExpression.OperatorToken.GetLocation()));
    }

    private static IdentifierNameSyntax? GetNullCheckedIdentifier(ExpressionSyntax condition)
    {
        condition = WalkDownParentheses(condition);

        if (condition is IsPatternExpressionSyntax
            {
                Expression: IdentifierNameSyntax identifier,
                Pattern: UnaryPatternSyntax
                {
                    RawKind: (int)SyntaxKind.NotPattern,
                    Pattern: ConstantPatternSyntax { Expression.RawKind: (int)SyntaxKind.NullLiteralExpression },
                },
            })
        {
            return identifier;
        }

        if (condition is BinaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.NotEqualsExpression,
                Left: IdentifierNameSyntax identifierName,
                Right.RawKind: (int)SyntaxKind.NullLiteralExpression,
            })
        {
            return identifierName;
        }

        return null;
    }

    private static ExpressionSyntax WalkDownParentheses(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression;
    }

    private static bool ContainsReference(
        SyntaxNode node,
        ILocalSymbol local,
        string localName,
        SemanticModel semanticModel,
        System.Threading.CancellationToken cancellationToken)
    {
        foreach (var descendant in node.DescendantNodesAndSelf())
        {
            if (descendant is not IdentifierNameSyntax identifier || identifier.Identifier.ValueText != localName)
            {
                continue;
            }

            if (SymbolEqualityComparer.Default.Equals(
                    local,
                    semanticModel.GetSymbolInfo(identifier, cancellationToken).Symbol))
            {
                return true;
            }
        }

        return false;
    }
    private static bool SupportsTypePatterns(LanguageVersion languageVersion) =>
        languageVersion == LanguageVersion.Default || languageVersion >= LanguageVersion.CSharp7;


    private static bool IsNullableValueType(ITypeSymbol type) =>
        type is INamedTypeSymbol namedType &&
        namedType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
}
