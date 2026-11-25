using HooSharper.CodeFixes;

namespace HooSharper.Analyzers.Tests;

public sealed class ExpressionTreeSafetyTests
{
    // A query provider can take an expression tree and return any type.
    // Inspect the selector's conversion; the result need not implement IQueryable.
    private const string Query = """
        using System;
        using System.Linq.Expressions;
        class Query
        {
            public T Select<T>(Expression<Func<int, T>> projection) => default;
        }
        """;

    [Fact]
    public Task PreserveNullTestInCustomQuery() =>
        AnalyzerVerifier<UseNullConditionalAccessAnalyzer, UseNullConditionalAccessCodeFixProvider>.VerifyAnalyzerAsync(
            Query + " class C { string Run(Query q, string s) => from n in q select s == null ? null : s.Trim(); }");

    [Fact]
    public Task PreserveTypeTestInCustomQuery() =>
        AnalyzerVerifier<UseNotPatternAnalyzer, UseNotPatternCodeFixProvider>.VerifyAnalyzerAsync(
            Query + " class C { bool Run(Query q, object o) => from n in q select !(o is string); }");

    [Fact]
    public Task PreserveBinaryNodeInCapturedPredicate() =>
        AnalyzerVerifier<SimplifyBooleanComparisonAnalyzer, SimplifyBooleanComparisonCodeFixProvider>.VerifyAnalyzerAsync(
            Query + " class C { Expression<Func<bool,bool>> Tree => b => b == true; }");

    [Fact]
    public Task PreserveConditionalNodeInCapturedProjection() =>
        AnalyzerVerifier<UseNullCoalescingExpressionAnalyzer, UseNullCoalescingExpressionCodeFixProvider>.VerifyAnalyzerAsync(
            Query + " class C { Expression<Func<string,string>> Tree => s => s == null ? \"fallback\" : s; }");

    [Fact]
    public Task PreserveIndexOfCallInCustomQuery() =>
        AnalyzerVerifier<UseStringContainsAnalyzer, UseStringContainsCodeFixProvider>.VerifyAnalyzerAsync(
            Query + " class C { bool Run(Query q, string s) => from n in q select s.IndexOf('x') >= 0; }");

    [Fact]
    public Task OrdinaryQuerySelectorCanStillBeFixedWhenItsReturnTypeIsQueryable()
    {
        const string source = """
            using System;
            using System.Linq;
            class Query
            {
                public IQueryable<T> Select<T>(Func<int,T> projection) => default;
                IQueryable<bool> Run(object o) => from n in this select {|#0:!|}(o is string);
            }
            """;
        return AnalyzerVerifier<UseNotPatternAnalyzer, UseNotPatternCodeFixProvider>.VerifyCodeFixAsync(
            source, AnalyzerVerifier<UseNotPatternAnalyzer, UseNotPatternCodeFixProvider>
                .Diagnostic(UseNotPatternAnalyzer.DiagnosticId).WithLocation(0),
            source.Replace("{|#0:!|}(o is string)", "o is not string"));
    }

    [Theory]
    [InlineData("Expression")]
    [InlineData("LambdaExpression")]
    public Task PreserveComparisonInsideNaturallyTypedExpressionTree(string type) =>
        AnalyzerVerifier<SimplifyBooleanComparisonAnalyzer, SimplifyBooleanComparisonCodeFixProvider>.VerifyAnalyzerAsync(
            Query + $" class C {{ {type} Tree => (bool b) => b == true; }}");
}
