using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HooSharper.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Formatting;

namespace HooSharper.CodeFixes;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(UseTypePatternCodeFixProvider)), Shared]
public sealed class UseTypePatternCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => [UseTypePatternAnalyzer.DiagnosticId];

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var diagnostic = context.Diagnostics[0];
        var declaration = root?.FindToken(diagnostic.Location.SourceSpan.Start).Parent?
            .AncestorsAndSelf().OfType<LocalDeclarationStatementSyntax>().FirstOrDefault();

        if (declaration is null ||
            !await IsEligibleAsync(context.Document, declaration, context.CancellationToken).ConfigureAwait(false))
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                "Use type pattern",
                cancellationToken => ApplyFixAsync(context.Document, declaration, cancellationToken),
                nameof(UseTypePatternCodeFixProvider)),
            diagnostic);
    }

    private static async Task<Document> ApplyFixAsync(
        Document document,
        LocalDeclarationStatementSyntax declaration,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null ||
            declaration.Parent is not BlockSyntax block ||
            !await IsEligibleAsync(document, declaration, cancellationToken).ConfigureAwait(false))
        {
            return document;
        }

        var declarator = declaration.Declaration.Variables[0];
        if (declarator.Initializer?.Value is not BinaryExpressionSyntax asExpression ||
            !asExpression.IsKind(SyntaxKind.AsExpression))
        {
            return document;
        }

        var declarationIndex = block.Statements.IndexOf(declaration);
        if (declarationIndex < 0 || declarationIndex + 1 >= block.Statements.Count ||
            block.Statements[declarationIndex + 1] is not IfStatementSyntax ifStatement)
        {
            return document;
        }

        var isKeyword = SyntaxFactory.Token(
            asExpression.OperatorToken.LeadingTrivia,
            SyntaxKind.IsKeyword,
            asExpression.OperatorToken.TrailingTrivia);
        var pattern = SyntaxFactory.DeclarationPattern(
            (TypeSyntax)asExpression.Right,
            SyntaxFactory.SingleVariableDesignation(declarator.Identifier.WithoutTrivia()));
        var condition = SyntaxFactory.IsPatternExpression(
                ParenthesizeIfNeeded(asExpression.Left),
                isKeyword,
                pattern)
            .WithLeadingTrivia(ifStatement.Condition.GetLeadingTrivia())
            .WithTrailingTrivia(ifStatement.Condition.GetTrailingTrivia());

        var replacementIf = ifStatement
            .WithCondition(condition)
            .WithLeadingTrivia(
                declaration.GetLeadingTrivia()
                    .AddRange(KeepComments(declaration.GetTrailingTrivia()))
                    .AddRange(ifStatement.GetLeadingTrivia()))
            .WithAdditionalAnnotations(Formatter.Annotation);

        var replacementBlock = block.WithStatements(
                block.Statements.RemoveAt(declarationIndex).RemoveAt(declarationIndex).Insert(declarationIndex, replacementIf))
            .WithAdditionalAnnotations(Formatter.Annotation);

        return document.WithSyntaxRoot(root.ReplaceNode(block, replacementBlock));
    }

    private static ExpressionSyntax ParenthesizeIfNeeded(ExpressionSyntax expression) =>
        expression is AssignmentExpressionSyntax or BinaryExpressionSyntax or ConditionalExpressionSyntax or LambdaExpressionSyntax or
            QueryExpressionSyntax or SwitchExpressionSyntax
            ? SyntaxFactory.ParenthesizedExpression(expression.WithoutTrivia()).WithTriviaFrom(expression)
            : expression;

    private static SyntaxTriviaList KeepComments(SyntaxTriviaList trivia)
    {
        var result = default(SyntaxTriviaList);
        var keptComment = false;
        foreach (var item in trivia)
        {
            if (item.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
                item.IsKind(SyntaxKind.MultiLineCommentTrivia) ||
                item.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                item.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            {
                result = result.Add(item);
                keptComment = true;
            }
            else if (keptComment && item.IsKind(SyntaxKind.EndOfLineTrivia))
            {
                result = result.Add(item);
                keptComment = false;
            }
        }

        return result;
    }

    private static async Task<bool> IsEligibleAsync(
        Document document,
        LocalDeclarationStatementSyntax declaration,
        CancellationToken cancellationToken)
    {
        var parseOptions = (await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false))?.Options
            as CSharpParseOptions;
        if (parseOptions is null ||
            (parseOptions.LanguageVersion != LanguageVersion.Default &&
             parseOptions.LanguageVersion < LanguageVersion.CSharp7) ||
            !declaration.Declaration.Type.IsVar ||
            !declaration.UsingKeyword.IsKind(SyntaxKind.None) ||
            !declaration.AwaitKeyword.IsKind(SyntaxKind.None) ||
            declaration.Declaration.Variables.Count != 1 ||
            declaration.ContainsDirectives)
        {
            return false;
        }

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (semanticModel is null ||
            declaration.Parent is not BlockSyntax block)
        {
            return false;
        }

        var declarator = declaration.Declaration.Variables[0];
        if (declarator.Identifier.ValueText == "_" ||
            declarator.Initializer?.Value is not BinaryExpressionSyntax asExpression ||
            !asExpression.IsKind(SyntaxKind.AsExpression) ||
            asExpression.Right is NullableTypeSyntax)
        {
            return false;
        }

        var index = block.Statements.IndexOf(declaration);
        if (index < 0 || index + 1 >= block.Statements.Count ||
            block.Statements[index + 1] is not IfStatementSyntax ifStatement ||
            ifStatement.ContainsDirectives)
        {
            return false;
        }

        var checkedIdentifier = GetNullCheckedIdentifier(ifStatement.Condition);
        var nullCheck = WalkDownParentheses(ifStatement.Condition);
        if (checkedIdentifier is null ||
            (nullCheck is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.NotEqualsExpression } &&
             semanticModel.GetOperation(nullCheck, cancellationToken) is not IBinaryOperation
             {
                 OperatorMethod: null,
             }))
        {
            return false;
        }

        var local = semanticModel.GetDeclaredSymbol(declarator, cancellationToken) as ILocalSymbol;
        var checkedSymbol = semanticModel.GetSymbolInfo(checkedIdentifier, cancellationToken).Symbol;
        if (local is null ||
            !SymbolEqualityComparer.Default.Equals(local, checkedSymbol))
        {
            return false;
        }

        var patternType = semanticModel.GetTypeInfo(asExpression.Right, cancellationToken).Type;
        if (patternType is null ||
            patternType.TypeKind is TypeKind.Dynamic or TypeKind.Error ||
            IsNullableValueType(patternType))
        {
            return false;
        }

        if (ifStatement.Else is not null &&
            ContainsReference(ifStatement.Else.Statement, local, declarator.Identifier.ValueText, semanticModel, cancellationToken))
        {
            return false;
        }

        for (var statementIndex = index + 2; statementIndex < block.Statements.Count; statementIndex++)
        {
            if (ContainsReference(
                    block.Statements[statementIndex],
                    local,
                    declarator.Identifier.ValueText,
                    semanticModel,
                    cancellationToken))
            {
                return false;
            }
        }

        return true;
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

        return condition is BinaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.NotEqualsExpression,
                Left: IdentifierNameSyntax identifierName,
                Right.RawKind: (int)SyntaxKind.NullLiteralExpression,
            }
            ? identifierName
            : null;
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
        CancellationToken cancellationToken)
    {
        foreach (var identifier in node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            if (identifier.Identifier.ValueText == localName &&
                SymbolEqualityComparer.Default.Equals(
                    local,
                    semanticModel.GetSymbolInfo(identifier, cancellationToken).Symbol))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNullableValueType(ITypeSymbol type) =>
        type is INamedTypeSymbol namedType &&
        namedType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
}
