using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HooSharper.Internal;

internal static class ControlFlowScopeSafety
{
    internal static bool CanRemoveElse(
        IfStatementSyntax ifStatement,
        SemanticModel semanticModel,
        CancellationToken cancellationToken) =>
        !ifStatement.ContainsDiagnostics &&
        ifStatement.Else is { Statement: not IfStatementSyntax } &&
        ifStatement.Parent is not ElseClauseSyntax &&
        !ifStatement.DescendantTrivia(descendIntoTrivia: true).Any(trivia => trivia.IsDirective) &&
        DefinitelyTerminates(ifStatement.Statement, semanticModel, cancellationToken);

    private static bool DefinitelyTerminates(
        StatementSyntax statement,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (statement is ReturnStatementSyntax or ThrowStatementSyntax or ContinueStatementSyntax or BreakStatementSyntax)
        {
            return true;
        }

        if (statement is not BlockSyntax block)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var controlFlow = semanticModel.AnalyzeControlFlow(statement);
            return controlFlow is { Succeeded: true, StartPointIsReachable: true, EndPointIsReachable: false };
        }

        // Keep direct terminal branches eligible even when their enclosing
        // condition is constant false and flow analysis marks them unreachable.
        if (block.Statements.LastOrDefault() is ReturnStatementSyntax or ThrowStatementSyntax or
            ContinueStatementSyntax or BreakStatementSyntax)
        {
            return true;
        }

        // Ignore unreachable tails while checking the last reachable statement.
        // Its endpoint also covers nested if/else and try/catch/finally exits.
        for (var index = block.Statements.Count - 1; index >= 0; index--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = block.Statements[index];
            var controlFlow = semanticModel.AnalyzeControlFlow(candidate);
            if (controlFlow?.StartPointIsReachable == true)
            {
                return controlFlow.Succeeded && !controlFlow.EndPointIsReachable;
            }
        }

        return false;
    }

    internal static bool CanConvertUsingStatement(
        UsingStatementSyntax usingStatement,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (usingStatement.SyntaxTree.Options is not CSharpParseOptions parseOptions ||
            parseOptions.LanguageVersion != LanguageVersion.Default &&
            parseOptions.LanguageVersion < LanguageVersion.CSharp8 ||
            usingStatement.ContainsDiagnostics ||
            usingStatement.Parent is not BlockSyntax parentBlock ||
            parentBlock.Statements.LastOrDefault() != usingStatement ||
            usingStatement.Declaration is not { Variables.Count: 1 } declaration ||
            declaration.Variables[0].Initializer is null ||
            usingStatement.Expression is not null ||
            usingStatement.Statement is not BlockSyntax { Statements.Count: > 0 } body ||
            usingStatement.ContainsDirectives)
        {
            return false;
        }

        return !HasUsingBindingCollision(usingStatement, parentBlock, body) &&
            !HasBackwardGotoToContainingBlock(body, parentBlock, semanticModel, cancellationToken);
    }

    internal static bool HoistingBodyCrossesUsingDeclaration(
        BlockSyntax body,
        BlockSyntax parentBlock,
        SemanticModel semanticModel,
        CancellationToken cancellationToken) =>
        body.Statements.OfType<LocalDeclarationStatementSyntax>().Any(declaration =>
            declaration.UsingKeyword != default) &&
        HasBackwardGotoToContainingBlock(body, parentBlock, semanticModel, cancellationToken);

    private static bool HasBackwardGotoToContainingBlock(
        BlockSyntax body,
        BlockSyntax parentBlock,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        // A using declaration forbids jumping back above it in its containing
        // block. A using statement or a using declaration inside the old body
        // allowed the same jump to exit that narrower disposal scope.
        foreach (var jump in body.DescendantNodes(ShouldDescendInto).OfType<GotoStatementSyntax>())
        {
            if (!jump.IsKind(SyntaxKind.GotoStatement) || jump.Expression is null ||
                semanticModel.GetSymbolInfo(jump.Expression, cancellationToken).Symbol is not ILabelSymbol label)
            {
                continue;
            }

            foreach (var reference in label.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax(cancellationToken) is LabeledStatementSyntax target &&
                    target.SpanStart < body.SpanStart &&
                    target.FirstAncestorOrSelf<BlockSyntax>() == parentBlock)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ShouldDescendInto(SyntaxNode node) =>
        node is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax;

    private static bool HasUsingBindingCollision(
        UsingStatementSyntax usingStatement,
        BlockSyntax parentBlock,
        BlockSyntax body)
    {
        var introducedNames = new HashSet<string>(System.StringComparer.Ordinal)
        {
            usingStatement.Declaration!.Variables[0].Identifier.ValueText,
        };
        var introducedLabels = new HashSet<string>(System.StringComparer.Ordinal);

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
                case LabeledStatementSyntax label when label.FirstAncestorOrSelf<BlockSyntax>() == body:
                    // Labels inside nested blocks keep their original scope.
                    // Only labels in the flattened body become parent labels.
                    introducedLabels.Add(label.Identifier.ValueText);
                    break;
            }
        }

        var initializer = usingStatement.Declaration!.Variables[0].Initializer!.Value;
        // Flattening the body extends its declaration space to the initializer.
        // A moved local can shadow a member that the resource originally used.
        foreach (var identifier in initializer.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            if (introducedNames.Contains(identifier.Identifier.ValueText))
            {
                return true;
            }
        }

        // Resource initializer designations already bind throughout that
        // initializer, but converting the using also exposes them to earlier
        // statements in the containing block.
        foreach (var designation in initializer.DescendantNodesAndSelf(ShouldDescendInto)
                     .OfType<SingleVariableDesignationSyntax>())
        {
            introducedNames.Add(designation.Identifier.ValueText);
        }

        var usingIndex = parentBlock.Statements.IndexOf(usingStatement);
        for (var index = 0; index < usingIndex; index++)
        {
            foreach (var token in parentBlock.Statements[index].DescendantTokens())
            {
                if (token.IsKind(SyntaxKind.IdentifierToken) &&
                    (introducedNames.Contains(token.ValueText) || introducedLabels.Contains(token.ValueText)))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
