using HooSharper.CodeFixes;
using VerifyCS = HooSharper.Analyzers.Tests.AnalyzerVerifier<
    HooSharper.Analyzers.UseStringContainsAnalyzer,
    HooSharper.CodeFixes.UseStringContainsCodeFixProvider>;

namespace HooSharper.Analyzers.Tests;

public sealed class UseStringContainsAnalyzerTests
{
    [Fact]
    public Task FixesPositiveNegativeReversedAndComparisonOverload()
    {
        const string source = """
            using System;

            class Example
            {
                bool Run(string value, string search) =>
                    value.{|#0:IndexOf|}('x') >= 0 &&
                    value.{|#1:IndexOf|}('x') > -1 &&
                    value.{|#2:IndexOf|}('x') != -1 &&
                    value.{|#3:IndexOf|}('x') < 0 &&
                    value.{|#4:IndexOf|}('x') <= -1 &&
                    value.{|#5:IndexOf|}('x') == -1 &&
                    0 <= value.{|#6:IndexOf|}('x') &&
                    -1 < value.{|#7:IndexOf|}('x') &&
                    0 > value.{|#8:IndexOf|}('x') &&
                    -1 >= value.{|#9:IndexOf|}('x') &&
                    value.{|#10:IndexOf|}(search, StringComparison.Ordinal) >= 0;
            }
            """;
        const string fixedSource = """
            using System;

            class Example
            {
                bool Run(string value, string search) =>
                    value.Contains('x') &&
                    value.Contains('x') &&
                    value.Contains('x') &&
                    !value.Contains('x') &&
                    !value.Contains('x') &&
                    !value.Contains('x') &&
                    value.Contains('x') &&
                    value.Contains('x') &&
                    !value.Contains('x') &&
                    !value.Contains('x') &&
                    value.Contains(search, StringComparison.Ordinal);
            }
            """;

        var expected = Enumerable.Range(0, 11)
            .Select(index => VerifyCS.Diagnostic(UseStringContainsAnalyzer.DiagnosticId).WithLocation(index));
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task PreservesCommentsAndFixesAll()
    {
        const string source = """
            class Example
            {
                bool Run(string value) =>
                    value.{|#0:IndexOf|}('a') /* first */ >= 0 &&
                    value.{|#1:IndexOf|}('b') == /* second */ -1;
            }
            """;
        const string fixedSource = """
            class Example
            {
                bool Run(string value) =>
                    value.Contains('a')/* first */ &&
                    !value.Contains('b')/* second */;
            }
            """;

        var expected = new[]
        {
            VerifyCS.Diagnostic(UseStringContainsAnalyzer.DiagnosticId).WithLocation(0),
            VerifyCS.Diagnostic(UseStringContainsAnalyzer.DiagnosticId).WithLocation(1),
        };
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }

    [Fact]
    public Task IgnoresCustomMethodsOtherThresholdsUnsupportedOverloadsAndDirectives()
    {
        const string source = """
            using System;

            class Custom
            {
                public int IndexOf(string value) => 0;
            }

            class Example
            {
                bool Run(string value, string search, Custom custom)
                {
                    var customResult = custom.IndexOf(search) >= 0;
                    var threshold = value.IndexOf(search) > 0;
                    var startIndex = value.IndexOf(search, 1) >= 0;
                    var count = value.IndexOf(search, 1, 2) >= 0;
                    var directive = value.IndexOf(
            #if DEBUG
                        search
            #else
                        "x"
            #endif
                        ) >= 0;
                    return customResult || threshold || startIndex || count || directive;
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task AcceptsConstantThresholdsWithExactMeaning()
    {
        const string source = """
            class Example
            {
                const int Missing = -1;
                bool Run(string value) => value.{|#0:IndexOf|}('x') != Missing;
            }
            """;
        const string fixedSource = """
            class Example
            {
                const int Missing = -1;
                bool Run(string value) => value.Contains('x');
            }
            """;

        var expected = VerifyCS.Diagnostic(UseStringContainsAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }
    [Fact]
    public Task IgnoresCultureSensitiveStringIndexOf()
    {
        const string source = """
            class Example
            {
                bool Run(string value, string search) =>
                    value.IndexOf(search) >= 0;
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task FixesCharIndexOfWithoutChangingSemantics()
    {
        const string source = """
            class Example
            {
                bool Run(string value) =>
                    value.{|#0:IndexOf|}('x') >= 0;
            }
            """;
        const string fixedSource = """
            class Example
            {
                bool Run(string value) =>
                    value.Contains('x');
            }
            """;

        return VerifyCS.VerifyCodeFixAsync(
            source,
            VerifyCS.Diagnostic(UseStringContainsAnalyzer.DiagnosticId).WithLocation(0),
            fixedSource);
    }

}
