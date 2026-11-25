using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Composition;
using HooSharper.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Operations;

namespace HooSharper.CodeFixes;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(UseThrowIfNullCodeFixProvider)), Shared]
public sealed class UseThrowIfNullCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(UseThrowIfNullAnalyzer.DiagnosticId);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var diagnostic = context.Diagnostics[0];
        var ifStatement = root?.FindToken(diagnostic.Location.SourceSpan.Start).Parent?
            .AncestorsAndSelf().OfType<IfStatementSyntax>().FirstOrDefault();

        var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        if (ifStatement is null || model is null ||
            UseThrowIfNullAnalyzer.GetAvailableExceptionType(model.Compilation) is not { } exceptionType ||
            UseThrowIfNullAnalyzer.FindGuard(ifStatement, model, exceptionType, context.CancellationToken) is not { } guard ||
            guard.Location.SourceSpan != diagnostic.Location.SourceSpan ||
            CallerArgumentExpressionSafety.IsCaptured(ifStatement, model, context.CancellationToken))
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                "Use ArgumentNullException.ThrowIfNull",
                cancellationToken => ApplyFixAsync(context.Document, ifStatement, guard.Checked, guard.Creation, guard.Throw,
                    model, exceptionType, cancellationToken),
                nameof(UseThrowIfNullCodeFixProvider)),
            diagnostic);
    }

    private static async Task<Document> ApplyFixAsync(
        Document document,
        IfStatementSyntax ifStatement,
        ExpressionSyntax checkedExpression,
        ObjectCreationExpressionSyntax creation,
        ThrowStatementSyntax throwStatement,
        SemanticModel semanticModel,
        INamedTypeSymbol expectedType,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return document;
        }

        var typeExpression = creation.Type;
        var arguments = creation.ArgumentList!.Arguments;
        var invocation = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                typeExpression,
                SyntaxFactory.IdentifierName("ThrowIfNull")),
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SeparatedList(
                [
                    SyntaxFactory.Argument(checkedExpression.WithoutTrivia()),
                    SyntaxFactory.Argument(arguments[0].Expression.WithoutTrivia()),
                ])));

        // A constructor name is bound as a type, but the receiver of a method
        // call may instead bind to a local or field with the same name.
        IMethodSymbol? FrameworkGuard(InvocationExpressionSyntax call) =>
            semanticModel.GetSpeculativeSymbolInfo(ifStatement.SpanStart,
                SyntaxFactory.ParseExpression(call.ToString()), SpeculativeBindingOption.BindAsExpression).Symbol
                is IMethodSymbol { IsStatic: true, ReturnsVoid: true, Arity: 0, Parameters.Length: 2 } method &&
            method.Parameters[0] is { RefKind: RefKind.None, Type.SpecialType: SpecialType.System_Object } &&
            method.Parameters[1] is { RefKind: RefKind.None, Type.SpecialType: SpecialType.System_String } &&
            SymbolEqualityComparer.Default.Equals(method.ContainingType, expectedType) ? method : null;

        var target = FrameworkGuard(invocation);
        var qualify = target is null;
        if (target is null)
        {
            var member = (MemberAccessExpressionSyntax)invocation.Expression;
            invocation = invocation.WithExpression(member.WithExpression(
                SyntaxFactory.ParseExpression("global::System.ArgumentNullException").WithTriviaFrom(typeExpression)));
            target = FrameworkGuard(invocation);
            if (target is null)
                return document;
        }

        var shorter = invocation.WithArgumentList(invocation.ArgumentList.WithArguments(
            SyntaxFactory.SingletonSeparatedList(invocation.ArgumentList.Arguments[0])));
        if (CanOmitName(shorter, target, arguments[0].Expression, ifStatement.SpanStart, semanticModel, cancellationToken))
        {
            invocation = shorter;
        }

        var replacement = SyntaxFactory.ExpressionStatement(invocation)
            .WithLeadingTrivia(ifStatement.GetLeadingTrivia())
            .WithTrailingTrivia(CodeFixTrivia.TrailingAfterUnwrapping(throwStatement, ifStatement))
            .WithAdditionalAnnotations(Formatter.Annotation);

        var comments = ifStatement.DescendantTrivia(descendIntoTrivia: true)
            .Where(trivia => trivia.SpanStart >= ifStatement.SpanStart &&
                             trivia.Span.End <= ifStatement.Span.End &&
                             (!creation.Type.FullSpan.Contains(trivia.Span) ||
                              qualify && creation.Type.Span.Contains(trivia.Span)) &&
                             !checkedExpression.Span.Contains(trivia.Span) &&
                             !arguments[0].Expression.Span.Contains(trivia.Span) &&
                             !throwStatement.SemicolonToken.FullSpan.Contains(trivia.Span) &&
                             (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
                              trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)))
            .ToArray();
        {
            var indentation = ifStatement.GetLeadingTrivia()
                .Where(static trivia => trivia.IsKind(SyntaxKind.WhitespaceTrivia));
            var leading = replacement.GetLeadingTrivia();
            foreach (var comment in comments)
            {
                leading = leading.Add(comment);
                leading = leading.Add(SyntaxFactory.ElasticCarriageReturnLineFeed);
                leading = leading.AddRange(indentation);
            }

            replacement = replacement.WithLeadingTrivia(leading);
        }

        return await CodeFixTrivia.FormatChangesAsync(document, root.ReplaceNode(ifStatement, replacement), cancellationToken).ConfigureAwait(false);
    }

    private static bool CanOmitName(InvocationExpressionSyntax shorter, IMethodSymbol target, ExpressionSyntax originalName,
        int position, SemanticModel model, CancellationToken token)
    {
        // A name expression may carry a handwritten comment. Keep it in place.
        var nameParameter = target.Parameters[1];
        if (!nameParameter.IsOptional || originalName.DescendantTrivia().Any(CodeFixTrivia.IsComment) ||
            !nameParameter.GetAttributes().Any(a =>
                a.AttributeClass?.ToDisplayString() == "System.Runtime.CompilerServices.CallerArgumentExpressionAttribute" &&
                a.ConstructorArguments.Length == 1 && a.ConstructorArguments[0].Value is string parameter &&
                parameter == target.Parameters[0].Name) ||
            model.GetConstantValue(originalName, token) is not { HasValue: true, Value: string expected })
        {
            return false;
        }

        // Bind the actual shortened call. An overload, an escaped identifier, or a
        // qualified member can change the selected method or compiler-supplied name.
        var proposed = SyntaxFactory.ExpressionStatement(SyntaxFactory.ParseExpression(shorter.ToString()));
        return model.TryGetSpeculativeSemanticModel(position, proposed, out var speculative) &&
            speculative.GetOperation(proposed.Expression, token) is IInvocationOperation call &&
            SymbolEqualityComparer.Default.Equals(call.TargetMethod, target) &&
            call.Arguments.Any(a => a.ArgumentKind == ArgumentKind.DefaultValue && a.Parameter?.Ordinal == 1 &&
                a.Value.ConstantValue is { HasValue: true, Value: string captured } && captured == expected);
    }
}
