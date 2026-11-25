using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace HooSharper.Analyzers;

internal static class ExpressionTreeSafety
{
    // Query syntax has synthesized selector lambdas. Their parameter conversion,
    // not the type returned by Select/Where, determines what a provider sees.
    internal static bool IsQuoted(IOperation? operation, Compilation compilation)
    {
        for (; operation is not null; operation = operation.Parent)
        {
            if (operation is IAnonymousFunctionOperation &&
                operation.Parent is IConversionOperation { Type: INamedTypeSymbol { Name: "Expression", Arity: 1 } type } &&
                SymbolEqualityComparer.Default.Equals(type.OriginalDefinition,
                    compilation.GetTypeByMetadataName("System.Linq.Expressions.Expression`1")))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsQuoted(SyntaxNode node, SemanticModel model, CancellationToken token) =>
        IsQuoted(model.GetOperation(node, token), model.Compilation);
}
