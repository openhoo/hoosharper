using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace HooSharper.Analyzers;

internal static class CallerArgumentExpressionSafety
{
    internal static void ReportIfUncaptured(this SyntaxNodeAnalysisContext context, DiagnosticDescriptor rule, Location location,
        SyntaxNode? formattedRegion = null)
    {
        if (!IsCaptured(context.Node, context.SemanticModel, context.CancellationToken) &&
            (formattedRegion is null || !ContainsFormattableCapture(formattedRegion, context.SemanticModel, context.CancellationToken)))
        {
            context.ReportDiagnostic(Diagnostic.Create(rule, location));
        }
    }

    // A method, constructor or indexer can receive the *source text* of another
    // argument, including an entire lambda or the receiver of an extension call.
    internal static bool IsCaptured(SyntaxNode node, SemanticModel model, CancellationToken token)
    {
        if (node is StatementSyntax && node.FirstAncestorOrSelf<AnonymousFunctionExpressionSyntax>() is null)
        {
            return false;
        }

        // Some syntax (a method's member access, for example) has no operation
        // of its own. Start at its first bound ancestor and follow the actual
        // calls, including implicit Add calls in collection initializers.
        IOperation? operation = null;
        for (var syntax = node; syntax is not null && syntax is not MemberDeclarationSyntax;
             syntax = syntax.Parent)
        {
            operation = model.GetOperation(syntax, token);
            if (operation is not null)
                break;
        }

        for (; operation is not null; operation = operation.Parent)
        {
            var arguments = GetArguments(operation);
            if (!arguments.IsDefault && HasCapturedArgument(arguments, node))
            {
                return true;
            }
        }

        return false;
    }

    // Roslyn formats every token in an annotated subtree, including unchanged
    // operands. Even whitespace is observable in a captured expression.
    internal static bool ContainsFormattableCapture(SyntaxNode region, SemanticModel model, CancellationToken token)
    {
        if (model.GetOperation(region, token) is not { } root)
            return false;

        foreach (var operation in root.DescendantsAndSelf())
        {
            var arguments = GetArguments(operation);
            if (!arguments.IsDefault && HasCapturedArgument(arguments, changed: null))
                return true;
        }
        return false;
    }

    private static ImmutableArray<IArgumentOperation> GetArguments(IOperation operation) => operation switch
    {
        IInvocationOperation invocation => invocation.Arguments,
        IObjectCreationOperation creation => creation.Arguments,
        IPropertyReferenceOperation property => property.Arguments,
        _ => default,
    };

    private static bool HasCapturedArgument(ImmutableArray<IArgumentOperation> arguments, SyntaxNode? changed)
    {
        foreach (var omitted in arguments)
        {
            if (omitted.ArgumentKind != ArgumentKind.DefaultValue || omitted.Parameter is null ||
                omitted.Value.ConstantValue is not { HasValue: true, Value: string text })
            {
                continue;
            }

            foreach (var attribute in omitted.Parameter.GetAttributes())
            {
                if (attribute.AttributeClass?.ToDisplayString() != "System.Runtime.CompilerServices.CallerArgumentExpressionAttribute" ||
                    attribute.ConstructorArguments.Length != 1 ||
                    attribute.ConstructorArguments[0].Value is not string parameterName)
                {
                    continue;
                }

                foreach (var supplied in arguments)
                {
                    if (supplied.Parameter?.Name != parameterName)
                        continue;

                    var source = supplied.Value.Syntax;
                    if (supplied.ArgumentKind == ArgumentKind.Explicit)
                    {
                        if (TouchesCapture(source, text, changed))
                            return true;
                    }
                    else if (supplied.Parameter.IsParams && supplied.IsImplicit && text.Length != 0)
                    {
                        // SDK 10 captures the implicit array's invocation syntax; SDK 8
                        // captures the element expression. Use the compiler's actual text.
                        if (text == source.ToString())
                        {
                            if (TouchesCapture(source, text, changed))
                                return true;
                        }
                        else if (supplied.Value is IArrayCreationOperation { Initializer: { } initializer })
                        {
                            foreach (var element in initializer.ElementValues)
                            {
                                if (TouchesCapture(element.Syntax, text, changed))
                                    return true;
                            }
                        }
                    }
                }
            }
        }

        return false;
    }

    private static bool TouchesCapture(SyntaxNode source, string capturedText, SyntaxNode? changed) =>
        changed is null ? capturedText != source.GetFirstToken().Text : source.Span.Contains(changed.Span);
}
