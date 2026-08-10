using HooSharper.CodeFixes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using VerifyCS = HooSharper.Analyzers.Tests.AnalyzerVerifier<
    HooSharper.Analyzers.UseNotPatternAnalyzer,
    HooSharper.CodeFixes.UseNotPatternCodeFixProvider>;

namespace HooSharper.Analyzers.Tests;

public sealed class UseNotPatternAnalyzerTests
{
    [Fact]
    public Task ConvertsTypePattern()
    {
        const string source = """
            class Example
            {
                bool Run(object value) => {|#0:!|}(value is string);
            }
            """;
        const string fixedSource = """
            class Example
            {
                bool Run(object value) => value is not string;
            }
            """;

        var expected = VerifyCS.Diagnostic(UseNotPatternAnalyzer.DiagnosticId)
            .WithLocation(0)
            .WithMessage("Use a not pattern");
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task ConvertsNullPattern()
    {
        const string source = """
            class Example
            {
                bool Run(object? value) => {|#0:!|}(value is null);
            }
            """;
        const string fixedSource = """
            class Example
            {
                bool Run(object? value) => value is not null;
            }
            """;

        var expected = VerifyCS.Diagnostic(UseNotPatternAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task ParenthesizesCompoundPatternToPreserveMeaning()
    {
        const string source = """
            class Example
            {
                bool Run(object value) => {|#0:!|}(value is string or int);
            }
            """;
        const string fixedSource = """
            class Example
            {
                bool Run(object value) => value is not (string or int);
            }
            """;

        var expected = VerifyCS.Diagnostic(UseNotPatternAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task PreservesCommentsAroundPattern()
    {
        const string source = """
            class Example
            {
                bool Run(object value) => {|#0:!|}(value is /* pattern comment */ string);
            }
            """;
        const string fixedSource = """
            class Example
            {
                bool Run(object value) => value is /* pattern comment */ not string;
            }
            """;

        var expected = VerifyCS.Diagnostic(UseNotPatternAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task ParenthesizesAlreadyNegatedPattern()
    {
        const string source = """
            class Example
            {
                bool Run(object value) => {|#0:!|}(value is not string);
            }
            """;
        const string fixedSource = """
            class Example
            {
                bool Run(object value) => value is not (not string);
            }
            """;

        var expected = VerifyCS.Diagnostic(UseNotPatternAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task PreservesCommentsAttachedAroundTargetAndPattern()
    {
        const string source = """
            class Example
            {
                bool Run(object value) => {|#0:!|}(/* target leading */ value /* target trailing */ is /* pattern leading */ string /* pattern trailing */);
            }
            """;
        const string fixedSource = """
            class Example
            {
                bool Run(object value) => /* target leading */ value /* target trailing */ is /* pattern leading */ not string /* pattern trailing */;
            }
            """;

        var expected = VerifyCS.Diagnostic(UseNotPatternAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task DoesNotReportPatternWithDesignation()
    {
        const string source = """
            class Example
            {
                bool Run(object value) => !(value is string text);
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task DoesNotReportAcrossDirective()
    {
        const string source = """
            class Example
            {
                bool Run(object value) => !(
            #if DEBUG
                    value is string
            #else
                    value is int
            #endif
                );
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task DoesNotReportBeforeCSharp9()
    {
        const string source = """
            class Example
            {
                bool Run(object value) => !(value is string);
            }
            """;

        var test = new CSharpCodeFixTest<UseNotPatternAnalyzer, UseNotPatternCodeFixProvider, DefaultVerifier>
        {
            ReferenceAssemblies = ReferenceAssemblies.Net.Net100,
            TestCode = source,
        };
        test.SolutionTransforms.Add((solution, projectId) =>
        {
            var project = solution.GetProject(projectId)!;
            return solution.WithProjectParseOptions(
                projectId,
                ((CSharpParseOptions)project.ParseOptions!).WithLanguageVersion(LanguageVersion.CSharp8));
        });

        return test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public Task DoesNotReportInsideExpressionTree()
    {
        const string source = """
            using System;
            using System.Linq.Expressions;

            class Example
            {
                Expression<Func<object, bool>> Build() => value => !(value is string);
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task DoesNotReportImplicitQueryableExpressionTree()
    {
        const string source = """
            using System.Linq;

            class Example
            {
                IQueryable<object> Run(IQueryable<object> query) =>
                    from value in query
                    where !(value is string)
                    select value;
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task FixAllConvertsNestedPatternsInOnePass()
    {
        const string source = """
            class Example
            {
                bool Run(object value) =>
                !(({|#0:!|}(value is string)) is bool);
            }
            """;
        const string fixedSource = """
            class Example
            {
                bool Run(object value) =>
                !((value is not string) is bool);
            }
            """;

        var expected = new[]
        {
            VerifyCS.Diagnostic(UseNotPatternAnalyzer.DiagnosticId).WithLocation(0),
        };
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }
    [Fact]
    public Task FixAllConvertsEveryEligibleExpression()
    {
        const string source = """
            class Example
            {
                bool Run(object first, object? second) =>
                    {|#0:!|}(first is string) && {|#1:!|}(second is null);
            }
            """;
        const string fixedSource = """
            class Example
            {
                bool Run(object first, object? second) =>
                    first is not string && second is not null;
            }
            """;

        var expected = new[]
        {
            VerifyCS.Diagnostic(UseNotPatternAnalyzer.DiagnosticId).WithLocation(0),
            VerifyCS.Diagnostic(UseNotPatternAnalyzer.DiagnosticId).WithLocation(1),
        };
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }
}
