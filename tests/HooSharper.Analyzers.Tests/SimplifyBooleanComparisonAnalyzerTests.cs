using HooSharper.CodeFixes;
using VerifyCS = HooSharper.Analyzers.Tests.AnalyzerVerifier<
    HooSharper.Analyzers.SimplifyBooleanComparisonAnalyzer,
    HooSharper.CodeFixes.SimplifyBooleanComparisonCodeFixProvider>;

namespace HooSharper.Analyzers.Tests;

public sealed class SimplifyBooleanComparisonAnalyzerTests
{
    [Theory]
    [InlineData("value {|#0:==|} true", "value")]
    [InlineData("true {|#0:==|} value", "value")]
    [InlineData("value {|#0:!=|} false", "value")]
    [InlineData("false {|#0:!=|} value", "value")]
    [InlineData("value {|#0:==|} false", "!value")]
    [InlineData("false {|#0:==|} value", "!value")]
    [InlineData("value {|#0:!=|} true", "!value")]
    [InlineData("true {|#0:!=|} value", "!value")]
    public Task SimplifiesEveryComparisonAndOperandOrder(string comparison, string replacement)
    {
        var source = $$"""
            class Example
            {
                bool Run(bool value) => {{comparison}};
            }
            """;
        var fixedSource = $$"""
            class Example
            {
                bool Run(bool value) => {{replacement}};
            }
            """;

        var expected = VerifyCS.Diagnostic(SimplifyBooleanComparisonAnalyzer.DiagnosticId)
            .WithLocation(0)
            .WithMessage("Simplify this boolean comparison");
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task AddsParenthesesWhenNegatingComplexExpression()
    {
        const string source = """
            class Example
            {
                bool Run(bool left, bool right) => (left && right) {|#0:==|} false;
            }
            """;
        const string fixedSource = """
            class Example
            {
                bool Run(bool left, bool right) => !(left && right);
            }
            """;

        return VerifyCS.VerifyCodeFixAsync(
            source,
            VerifyCS.Diagnostic(SimplifyBooleanComparisonAnalyzer.DiagnosticId).WithLocation(0),
            fixedSource);
    }

    [Theory]
    [InlineData("!value {|#0:==|} false")]
    [InlineData("!value {|#0:!=|} true")]
    public Task RemovesExistingLogicalNegation(string comparison)
    {
        var source = $$"""
            class Example
            {
                bool Run(bool value) => {{comparison}};
            }
            """;
        const string fixedSource = """
            class Example
            {
                bool Run(bool value) => value;
            }
            """;

        return VerifyCS.VerifyCodeFixAsync(
            source,
            VerifyCS.Diagnostic(SimplifyBooleanComparisonAnalyzer.DiagnosticId).WithLocation(0),
            fixedSource);
    }

    [Fact]
    public Task PreservesCommentsAroundRemovedOperatorAndLiteral()
    {
        const string source = """
            class Example
            {
                bool Run(bool value) => value /* before */ {|#0:==|} /* after */ true;
            }
            """;
        const string fixedSource = """
            class Example
            {
                bool Run(bool value) => value/* before *//* after */;
            }
            """;

        return VerifyCS.VerifyCodeFixAsync(
            source,
            VerifyCS.Diagnostic(SimplifyBooleanComparisonAnalyzer.DiagnosticId).WithLocation(0),
            fixedSource);
    }

    [Fact]
    public Task DoesNotReportNullableDynamicNonBooleanOrUserDefinedOperators()
    {
        const string source = """
            struct Flag
            {
                public static bool operator ==(Flag left, bool right) => true;
                public static bool operator !=(Flag left, bool right) => false;
                public override bool Equals(object? value) => false;
                public override int GetHashCode() => 0;
            }

            class Example
            {
                bool? Nullable(bool? value) => value == true;
                bool Dynamic(dynamic value) => value == true;
                bool Other(bool value) => value;
                bool Overloaded(Flag value) => value == true;
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task FixAllSimplifiesEveryComparison()
    {
        const string source = """
            class Example
            {
                bool Run(bool first, bool second, bool third) =>
                    (first {|#0:==|} true) && (false {|#1:!=|} second) && (third {|#2:==|} false);
            }
            """;
        const string fixedSource = """
            class Example
            {
                bool Run(bool first, bool second, bool third) =>
                    (first) && (second) && (!third);
            }
            """;

        var expected = new[]
        {
            VerifyCS.Diagnostic(SimplifyBooleanComparisonAnalyzer.DiagnosticId).WithLocation(0),
            VerifyCS.Diagnostic(SimplifyBooleanComparisonAnalyzer.DiagnosticId).WithLocation(1),
            VerifyCS.Diagnostic(SimplifyBooleanComparisonAnalyzer.DiagnosticId).WithLocation(2),
        };
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }
}
