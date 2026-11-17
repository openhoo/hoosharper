using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HooSharper.CodeFixes;

internal static class CodeFixTrivia
{
    internal static bool IsComment(SyntaxTrivia trivia) =>
        trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) ||
        trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia);

    internal static SyntaxTriviaList TrailingAfterUnwrapping(StatementSyntax statement, IfStatementSyntax wrapper)
    {
        var outer = wrapper.GetTrailingTrivia();
        var inner = statement.GetTrailingTrivia();
        if (statement.GetLastToken() == wrapper.GetLastToken() || !inner.Any(IsComment))
        {
            return outer;
        }

        return outer.Any(IsComment) ? inner.AddRange(outer) : inner;
    }
}
