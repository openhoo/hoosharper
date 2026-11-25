using System.Collections.Immutable;
using System.Threading;
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

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeLocalDeclaration, SyntaxKind.LocalDeclarationStatement);
    }

    private static void AnalyzeLocalDeclaration(SyntaxNodeAnalysisContext context)
    {
        if (FindCandidate((LocalDeclarationStatementSyntax)context.Node,
                context.SemanticModel, context.CancellationToken) is { } candidate &&
            !CallerArgumentExpressionSafety.ContainsFormattableCapture(candidate.Cast, context.SemanticModel, context.CancellationToken))
        {
            context.ReportIfUncaptured(Rule, candidate.Cast.OperatorToken.GetLocation(), candidate.Branch);
        }
    }

    internal static (BinaryExpressionSyntax Cast, IfStatementSyntax Branch)? FindCandidate(
        LocalDeclarationStatementSyntax declarationStatement,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (declarationStatement.SyntaxTree.Options is not CSharpParseOptions parseOptions ||
            !SupportsTypePatterns(parseOptions.LanguageVersion))
        {
            return null;
        }

        if (!declarationStatement.Declaration.Type.IsVar ||
            !declarationStatement.UsingKeyword.IsKind(SyntaxKind.None) ||
            !declarationStatement.AwaitKeyword.IsKind(SyntaxKind.None) ||
            declarationStatement.Declaration.Variables.Count != 1 ||
            declarationStatement.Parent is not BlockSyntax block ||
            declarationStatement.ContainsDirectives)
        {
            return null;
        }

        var declarator = declarationStatement.Declaration.Variables[0];
        if (declarator.Identifier.ValueText == "_" ||
            declarator.Initializer?.Value is not BinaryExpressionSyntax asExpression ||
            !asExpression.IsKind(SyntaxKind.AsExpression) ||
            asExpression.Right is NullableTypeSyntax)
        {
            return null;
        }

        var statementIndex = block.Statements.IndexOf(declarationStatement);
        if (statementIndex < 0 || statementIndex + 1 >= block.Statements.Count ||
            block.Statements[statementIndex + 1] is not IfStatementSyntax ifStatement ||
            ifStatement.ContainsDirectives)
        {
            return null;
        }

        var checkedIdentifier = GetNullCheckedIdentifier(ifStatement.Condition);
        var nullCheck = WalkDownParentheses(ifStatement.Condition);
        if (checkedIdentifier is null ||
            (nullCheck is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.NotEqualsExpression } &&
             !HooSharper.Internal.NullComparisonSafety.IsSafeBinary(semanticModel.GetOperation(nullCheck, cancellationToken))))
        {
            return null;
        }

        var local = semanticModel.GetDeclaredSymbol(declarator, cancellationToken) as ILocalSymbol;
        var checkedSymbol = semanticModel.GetSymbolInfo(checkedIdentifier, cancellationToken).Symbol;
        if (local is null || !SymbolEqualityComparer.Default.Equals(local, checkedSymbol))
        {
            return null;
        }

        if (!PreservesNullableDeclaration(local, ifStatement.Statement, semanticModel))
        {
            return null;
        }

        var patternType = semanticModel.GetTypeInfo(asExpression.Right, cancellationToken).Type;
        // A type or alias named var may have converted the as-result. A null
        // literal is legal on the left of as, but cannot be a pattern input.
        if (patternType is null || patternType.TypeKind is TypeKind.Dynamic or TypeKind.Error ||
            IsNullableValueType(patternType) || !SymbolEqualityComparer.Default.Equals(local.Type, patternType) ||
            semanticModel.GetTypeInfo(asExpression.Left, cancellationToken).Type is not { TypeKind: not TypeKind.Error } inputType)
        {
            return null;
        }

        if (parseOptions.LanguageVersion == LanguageVersion.CSharp7 &&
            semanticModel.Compilation.ClassifyConversion(inputType, patternType) is
                { IsIdentity: false, IsReference: false, IsBoxing: false, IsUnboxing: false })
        {
            return null;
        }

        if (ifStatement.Else is not null && ContainsReference(
                ifStatement.Else.Statement, local, declarator.Identifier.ValueText, semanticModel, cancellationToken))
        {
            return null;
        }

        for (var index = statementIndex + 2; index < block.Statements.Count; index++)
        {
            if (ContainsReference(
                    block.Statements[index], local, declarator.Identifier.ValueText, semanticModel, cancellationToken))
            {
                return null;
            }
        }

        return (asExpression, ifStatement);
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


    private static bool PreservesNullableDeclaration(
        ILocalSymbol local,
        StatementSyntax body,
        SemanticModel semanticModel)
    {
        if (local.Type.NullableAnnotation != NullableAnnotation.Annotated)
        {
            return true;
        }

        // The as-inferred local can accept null, while a declaration-pattern
        // local has a non-nullable declaration type. Writes (including ref/out)
        // would therefore introduce nullable warnings after the rewrite.
        var dataFlow = semanticModel.AnalyzeDataFlow(body);
        if (dataFlow is not { Succeeded: true })
        {
            return false;
        }

        foreach (var written in dataFlow.WrittenInside)
        {
            if (SymbolEqualityComparer.Default.Equals(local, written))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsNullableValueType(ITypeSymbol type) =>
        type is INamedTypeSymbol namedType &&
        namedType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
}
