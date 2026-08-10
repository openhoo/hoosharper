using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

using HooSharper.CodeFixes;
using VerifyCS = HooSharper.Analyzers.Tests.AnalyzerVerifier<
    HooSharper.Analyzers.UseNullCoalescingAssignmentAnalyzer,
    HooSharper.CodeFixes.UseNullCoalescingAssignmentCodeFixProvider>;

namespace HooSharper.Analyzers.Tests;

public sealed class UseNullCoalescingAssignmentAnalyzerTests
{
    [Fact]
    public Task ReplacesIdentifierIsNullCheck()
    {
        const string source = """
            class Example
            {
                string Get(string? value, string fallback)
                {
                    if (value {|#0:is|} null)
                    {
                        value = fallback;
                    }

                    return value;
                }
            }
            """;
        const string fixedSource = """
            class Example
            {
                string Get(string? value, string fallback)
                {
                    value ??= fallback;

                    return value;
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(UseNullCoalescingAssignmentAnalyzer.DiagnosticId)
            .WithLocation(0)
            .WithMessage("Use a null-coalescing assignment");

        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task IgnoresPropertyTargets()
    {
        const string source = """
            class Holder
            {
                public string? Value { get; set; }
            }

            class Example
            {
                private readonly Holder holder = new();

                void Set(string fallback)
                {
                    if (holder.Value == null)
                    {
                        holder.Value = fallback;
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task PreservesComments()
    {
        const string source = """
            class Example
            {
                void Set(string? value, string fallback)
                {
                    // before
                    if (value {|#0:is|} null) // condition
                    {
                        // assignment
                        value = fallback; // value
                    } // after
                }
            }
            """;
        const string fixedSource = """
            class Example
            {
                void Set(string? value, string fallback)
                {
                    // before
                    // condition
                    // assignment
                    // after
                    value ??= fallback; // value
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(UseNullCoalescingAssignmentAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task FixAllReplacesEverySafeNullCheck()
    {
        const string source = """
            class Example
            {
                private string? field;

                void Set(string? first, string fallback)
                {
                    if (first {|#0:is|} null)
                    {
                        first = fallback;
                    }

                    if (field {|#1:==|} null)
                    {
                        field = fallback;
                    }
                }
            }
            """;
        const string fixedSource = """
            class Example
            {
                private string? field;

                void Set(string? first, string fallback)
                {
                    first ??= fallback;

                    field ??= fallback;
                }
            }
            """;

        var expected = new[]
        {
            VerifyCS.Diagnostic(UseNullCoalescingAssignmentAnalyzer.DiagnosticId).WithLocation(0),
            VerifyCS.Diagnostic(UseNullCoalescingAssignmentAnalyzer.DiagnosticId).WithLocation(1),
        };

        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }

    [Fact]
    public Task ReplacesLocalAndNullableValueTargets()
    {
        const string source = """
            class Example
            {
                void Set(string? value, int? number, string fallback)
                {
                    string? local = value;
                    if ((local) {|#0:is|} null)
                    {
                        local = fallback;
                    }

                    int? nullable = number;
                    if (nullable {|#1:is|} null)
                    {
                        nullable = 42;
                    }
                }
            }
            """;
        const string fixedSource = """
            class Example
            {
                void Set(string? value, int? number, string fallback)
                {
                    string? local = value;
                    local ??= fallback;

                    int? nullable = number;
                    nullable ??= 42;
                }
            }
            """;

        var expected = new[]
        {
            VerifyCS.Diagnostic(UseNullCoalescingAssignmentAnalyzer.DiagnosticId).WithLocation(0),
            VerifyCS.Diagnostic(UseNullCoalescingAssignmentAnalyzer.DiagnosticId).WithLocation(1),
        };

        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }

    [Fact]
    public Task ReplacesStaticFieldAndIgnoresPropertyTargets()
    {
        const string source = """
            class Holder
            {
                public string? Value { get; set; }
            }

            class Example
            {
                private static string? shared;

                void Set(Holder parameter, string fallback)
                {
                    if (shared {|#0:is|} null)
                    {
                        shared = fallback;
                    }

                    if (parameter.Value is null)
                    {
                        parameter.Value = fallback;
                    }
                }
            }
            """;
        const string fixedSource = """
            class Holder
            {
                public string? Value { get; set; }
            }

            class Example
            {
                private static string? shared;

                void Set(Holder parameter, string fallback)
                {
                    shared ??= fallback;

                    if (parameter.Value is null)
                    {
                        parameter.Value = fallback;
                    }
                }
            }
            """;

        return VerifyCS.VerifyCodeFixAsync(
            source,
            VerifyCS.Diagnostic(UseNullCoalescingAssignmentAnalyzer.DiagnosticId).WithLocation(0),
            fixedSource);
    }

    [Fact]
    public Task IgnoresDynamicEqualityAndRefReturnTargets()
    {
        const string source = """
            class Holder
            {
                private string? value;
                public ref string? Value => ref value;
            }

            class Example
            {
                void Set(dynamic value, dynamic fallback, Holder holder)
                {
                    if (value == null)
                    {
                        value = fallback;
                    }

                    if (holder.Value is null)
                    {
                        holder.Value = "fallback";
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task AcceptsDefaultLanguageVersion()
    {
        const string source = """
            class Example
            {
                void Set(string? value, string fallback)
                {
                    if (value {|#0:is|} null)
                    {
                        value = fallback;
                    }
                }
            }
            """;

        var test = new CSharpCodeFixTest<
            UseNullCoalescingAssignmentAnalyzer,
            UseNullCoalescingAssignmentCodeFixProvider,
            DefaultVerifier>
        {
            ReferenceAssemblies = ReferenceAssemblies.Net.Net100,
            TestCode = source,
            FixedCode = """
                class Example
                {
                    void Set(string? value, string fallback)
                    {
                        value ??= fallback;
                    }
                }
                """,
        };
        test.ExpectedDiagnostics.Add(
            VerifyCS.Diagnostic(UseNullCoalescingAssignmentAnalyzer.DiagnosticId).WithLocation(0));
        test.SolutionTransforms.Add((solution, projectId) =>
        {
            var project = solution.GetProject(projectId)!;
            return solution.WithProjectParseOptions(
                projectId,
                ((CSharpParseOptions)project.ParseOptions!).WithLanguageVersion(LanguageVersion.Default));
        });

        return test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public Task IgnoresElementAccessAndNonNullableTargets()
    {
        const string source = """
            class Example
            {
                void Set(string?[] values, int number)
                {
                    if (values[0] is null)
                    {
                        values[0] = "fallback";
                    }

                    if (number == null)
                    {
                        number = 42;
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task DoesNotReportBeforeCSharp8()
    {
        const string source = """
            class Example
            {
                void Set(string value, string fallback)
                {
                    if (value == null)
                    {
                        value = fallback;
                    }
                }
            }
            """;

        var test = new CSharpCodeFixTest<
            UseNullCoalescingAssignmentAnalyzer,
            UseNullCoalescingAssignmentCodeFixProvider,
            DefaultVerifier>
        {
            ReferenceAssemblies = ReferenceAssemblies.Net.Net100,
            TestCode = source,
        };
        test.SolutionTransforms.Add((solution, projectId) =>
        {
            var project = solution.GetProject(projectId)!;
            return solution.WithProjectParseOptions(
                projectId,
                ((CSharpParseOptions)project.ParseOptions!).WithLanguageVersion(LanguageVersion.CSharp7_3));
        });

        return test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public Task IgnoresMismatchedTarget()
    {
        const string source = """
            class Example
            {
                void Set(string? first, string? second, string fallback)
                {
                    if (first is null)
                    {
                        second = fallback;
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresOverloadedEqualityOperator()
    {
        const string source = """
            class Value
            {
                public static bool operator ==(Value? left, Value? right) => true;
                public static bool operator !=(Value? left, Value? right) => false;
                public override bool Equals(object? obj) => false;
                public override int GetHashCode() => 0;
            }

            class Example
            {
                void Set(Value? value, Value fallback)
                {
                    if (value == null)
                    {
                        value = fallback;
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresUnstableReceiver()
    {
        const string source = """
            class Holder
            {
                public string? Value { get; set; }
            }

            class Example
            {
                Holder GetHolder() => new();

                void Set(string fallback)
                {
                    if (GetHolder().Value is null)
                    {
                        GetHolder().Value = fallback;
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresMultipleStatements()
    {
        const string source = """
            class Example
            {
                void Set(string? value, string fallback)
                {
                    if (value is null)
                    {
                        value = fallback;
                        System.Console.WriteLine(value);
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresElseClauseAndDirectives()
    {
        const string source = """
            class Example
            {
                void Set(string? value, string fallback)
                {
                    if (value is null)
                    {
                        value = fallback;
                    }
                    else
                    {
                        System.Console.WriteLine(value);
                    }

                    if (value is null)
                    {
            #if DEBUG
                        value = fallback;
            #endif
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }
}
