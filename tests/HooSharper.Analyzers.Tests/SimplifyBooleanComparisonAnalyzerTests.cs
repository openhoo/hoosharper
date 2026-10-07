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
    [Fact]
    public Task DoesNotRewriteUserDefinedLogicalNot()
    {
        const string source = """
            struct Flag
            {
                public static bool operator !(Flag value) => false;
                public static implicit operator bool(Flag value) => true;
            }

            class Example
            {
                bool Run(Flag value) => (!value) == false;
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task FixAllSimplifiesNestedComparisons()
    {
        const string source = """
            class Example
            {
                bool Run(bool value) => ((value == true) {|#0:==|} false);
            }
            """;
        const string fixedSource = """
            class Example
            {
                bool Run(bool value) => (!value);
            }
            """;

        var expected = new[]
        {
            VerifyCS.Diagnostic(SimplifyBooleanComparisonAnalyzer.DiagnosticId).WithLocation(0),
        };
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }
    [Fact]
    public Task DoesNotReportComparisonWithoutBooleanLiteral()
    {
        const string source = """
            class Example
            {
                bool Run(bool left, bool right) => left == right;
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Theory]
    [InlineData("&&")]
    [InlineData("||")]
    [InlineData("&")]
    [InlineData("|")]
    [InlineData("^")]
    public Task DoesNotRewriteLogicalOperatorsInsideComparison(string operation)
    {
        var source = $$"""
            class Example
            {
                bool Run(bool value) => (value {{operation}} true) {|#0:==|} true;
            }
            """;
        var fixedSource = $$"""
            class Example
            {
                bool Run(bool value) => (value {{operation}} true);
            }
            """;
        return VerifyCS.VerifyCodeFixAsync(source,
            VerifyCS.Diagnostic(SimplifyBooleanComparisonAnalyzer.DiagnosticId).WithLocation(0), fixedSource);
    }

    [Fact]
    public Task ReportsEqualityInsideLiteralLogicalParent()
    {
        const string source = """
            class Example
            {
                bool Run(bool value) => (value {|#0:==|} false) && true;
            }
            """;
        const string fixedSource = """
            class Example
            {
                bool Run(bool value) => (!value) && true;
            }
            """;
        return VerifyCS.VerifyCodeFixAsync(source,
            VerifyCS.Diagnostic(SimplifyBooleanComparisonAnalyzer.DiagnosticId).WithLocation(0), fixedSource);
    }

    [Fact]
    public Task RetainsGroupingAfterRemovingNegation()
    {
        const string source = """
            class Example
            {
                bool Run(bool left, bool right, bool ready) => !(left || right) {|#0:==|} false && ready;
            }
            """;
        const string fixedSource = """
            class Example
            {
                bool Run(bool left, bool right, bool ready) => (left || right) && ready;
            }
            """;
        return VerifyCS.VerifyCodeFixAsync(source,
            VerifyCS.Diagnostic(SimplifyBooleanComparisonAnalyzer.DiagnosticId).WithLocation(0), fixedSource);
    }

    [Fact]
    public Task PreservesCommentInsideNegatedParentheses()
    {
        const string source = """
            class Example
            {
                bool Run(bool value) => (/* audit */ value) {|#0:==|} false;
            }
            """;
        const string fixedSource = """
            class Example
            {
                bool Run(bool value) => !((/* audit */ value));
            }
            """;
        return VerifyCS.VerifyCodeFixAsync(source,
            VerifyCS.Diagnostic(SimplifyBooleanComparisonAnalyzer.DiagnosticId).WithLocation(0), fixedSource);
    }


    [Theory]
    [InlineData("true {|#0:==|} false", "!(true)")]
    [InlineData("true {|#0:!=|} false", "true")]
    [InlineData("false {|#0:==|} true", "false")]
    [InlineData("false {|#0:!=|} true", "!(false)")]
    public Task SimplifiesComparisonsOfTwoLiteralsCorrectly(string comparison, string replacement)
    {
        var source = $$"""
            class Example
            {
                bool Run() => {{comparison}};
            }
            """;
        var fixedSource = $$"""
            class Example
            {
                bool Run() => {{replacement}};
            }
            """;
        return VerifyCS.VerifyCodeFixAsync(source,
            VerifyCS.Diagnostic(SimplifyBooleanComparisonAnalyzer.DiagnosticId).WithLocation(0), fixedSource);
    }

    [Fact]
    public Task PreservesExteriorCommentsExactlyOnce()
    {
        const string source = """
            class Example
            {
                bool Run(bool value) => /* before */ value {|#0:==|} true /* after */;
            }
            """;
        const string fixedSource = """
            class Example
            {
                bool Run(bool value) => /* before */ value /* after */;
            }
            """;
        return VerifyCS.VerifyCodeFixAsync(source,
            VerifyCS.Diagnostic(SimplifyBooleanComparisonAnalyzer.DiagnosticId).WithLocation(0), fixedSource);
    }

}
