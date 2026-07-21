using HooSharper.CodeFixes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using VerifyCS = HooSharper.Analyzers.Tests.AnalyzerVerifier<
    HooSharper.Analyzers.UseNullConditionalAccessAnalyzer,
    HooSharper.CodeFixes.UseNullConditionalAccessCodeFixProvider>;

namespace HooSharper.Analyzers.Tests;

public sealed class UseNullConditionalAccessAnalyzerTests
{
    [Fact]
    public Task ReplacesNullPatternWithMemberAccess()
    {
        const string source = """
            class Example
            {
                int? GetLength(string? value) => value is null {|#0:?|} null : value.Length;
            }
            """;
        const string fixedSource = """
            class Example
            {
                int? GetLength(string? value) => value?.Length;
            }
            """;

        var expected = VerifyCS.Diagnostic(UseNullConditionalAccessAnalyzer.DiagnosticId)
            .WithLocation(0)
            .WithMessage("Use null-conditional access");
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task ReplacesInverseNonNullPatternWithInvocation()
    {
        const string source = """
            class Example
            {
                string? Format(object? value) => value is not null {|#0:?|} value.ToString() : null;
            }
            """;
        const string fixedSource = """
            class Example
            {
                string? Format(object? value) => value?.ToString();
            }
            """;

        var expected = VerifyCS.Diagnostic(UseNullConditionalAccessAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task ReplacesSafeEqualityOrientations()
    {
        const string source = """
            class Example
            {
                int? First(string? value) => null == value {|#0:?|} null : value.Length;
                int? Second(string? value) => value != null {|#1:?|} value.Length : null;
            }
            """;
        const string fixedSource = """
            class Example
            {
                int? First(string? value) => value?.Length;
                int? Second(string? value) => value?.Length;
            }
            """;

        var expected = new[]
        {
            VerifyCS.Diagnostic(UseNullConditionalAccessAnalyzer.DiagnosticId).WithLocation(0),
            VerifyCS.Diagnostic(UseNullConditionalAccessAnalyzer.DiagnosticId).WithLocation(1),
        };
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }

    [Fact]
    public Task PreservesCommentsOnAccess()
    {
        const string source = """
            class Example
            {
                int? GetLength(string? value) => value is null {|#0:?|} null : value /* receiver */.Length /* result */;
            }
            """;
        const string fixedSource = """
            class Example
            {
                int? GetLength(string? value) => value /* receiver */?.Length /* result */;
            }
            """;

        var expected = VerifyCS.Diagnostic(UseNullConditionalAccessAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task FixAllReplacesEverySafeConditional()
    {
        const string source = """
            class Example
            {
                int? Length(string? value) => value is null {|#0:?|} null : value.Length;
                string? Text(object? value) => value != null {|#1:?|} value.ToString() : null;
            }
            """;
        const string fixedSource = """
            class Example
            {
                int? Length(string? value) => value?.Length;
                string? Text(object? value) => value?.ToString();
            }
            """;

        var expected = new[]
        {
            VerifyCS.Diagnostic(UseNullConditionalAccessAnalyzer.DiagnosticId).WithLocation(0),
            VerifyCS.Diagnostic(UseNullConditionalAccessAnalyzer.DiagnosticId).WithLocation(1),
        };
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }

    [Fact]
    public Task DoesNotReportBeforeCSharp6()
    {
        const string source = """
            class Example
            {
                int? GetLength(string value)
                {
                    return value == null ? (int?)null : value.Length;
                }
            }
            """;

        var test = new CSharpCodeFixTest<UseNullConditionalAccessAnalyzer, UseNullConditionalAccessCodeFixProvider, DefaultVerifier>
        {
            ReferenceAssemblies = ReferenceAssemblies.Net.Net100,
            TestCode = source,
        };
        test.SolutionTransforms.Add((solution, projectId) =>
        {
            var project = solution.GetProject(projectId)!;
            return solution.WithProjectParseOptions(
                projectId,
                ((CSharpParseOptions)project.ParseOptions!).WithLanguageVersion(LanguageVersion.CSharp5));
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
                Expression<Func<string?, int?>> Build() =>
                    value => value == null ? null : value.Length;
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresMutableFieldReceiversRecursively()
    {
        const string source = """
            class State
            {
                public string? Value;
            }

            class Example
            {
                private string? _value;
                private State _state = new();

                int? Direct() => _value is null ? null : _value.Length;
                int? Nested() => _state.Value is null ? null : _state.Value.Length;
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task ReportsReadonlyFieldReceiverChain()
    {
        const string source = """
            class State
            {
                public readonly string? Value;
            }

            class Example
            {
                private readonly State _state = new();

                int? GetLength() => _state.Value is null {|#0:?|} null : _state.Value.Length;
            }
            """;
        const string fixedSource = """
            class State
            {
                public readonly string? Value;
            }

            class Example
            {
                private readonly State _state = new();

                int? GetLength() => _state.Value?.Length;
            }
            """;

        var expected = VerifyCS.Diagnostic(UseNullConditionalAccessAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task ReplacesLocalReceiverFieldAccess()
    {
        const string source = """
            class Value
            {
                public int Number;
            }

            class Example
            {
                int? GetNumber(Value? input)
                {
                    var value = input;
                    return value is null {|#0:?|} null : value.Number;
                }
            }
            """;
        const string fixedSource = """
            class Value
            {
                public int Number;
            }

            class Example
            {
                int? GetNumber(Value? input)
                {
                    var value = input;
                    return value?.Number;
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(UseNullConditionalAccessAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task ReplacesParenthesizedReceiver()
    {
        const string source = """
            class Example
            {
                int? GetLength(string? value) => ((value is null)) {|#0:?|} ((null)) : ((value.Length));
            }
            """;
        const string fixedSource = """
            class Example
            {
                int? GetLength(string? value) => value?.Length;
            }
            """;

        var expected = VerifyCS.Diagnostic(UseNullConditionalAccessAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task IgnoresExtensionMethodNonNullComparisonAndOrdinaryConditional()
    {
        const string source = """
            static class TextExtensions
            {
                public static int CountCharacters(this string value) => value.Length;
            }

            class Example
            {
                int? Extension(string? value) => value is null ? null : value.CountCharacters();
                int? Comparison(string? first, string? second) => first == second ? null : first.Length;
                int Ordinary(bool enabled, string value) => enabled ? value.Length : 0;
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresMismatchedAndUnstableReceivers()
    {
        const string source = """
            class Example
            {
                string? Get() => "value";

                int? Mismatched(string? first, string? second) => first is null ? null : second.Length;
                int? Unstable() => Get() is null ? null : Get().Length;
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresVolatileReceiversRecursively()
    {
        const string source = """
            class State
            {
                public string? Value;
            }

            class Example
            {
                private volatile string? _value;
                private volatile State _state = new();

                int? Direct() => _value is null ? null : _value.Length;
                int? Nested() => _state.Value is null ? null : _state.Value.Length;
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresDynamicAndOverloadedEquality()
    {
        const string source = """
            class Value
            {
                public int Member => 1;
                public static bool operator ==(Value? left, Value? right) => false;
                public static bool operator !=(Value? left, Value? right) => true;
                public override bool Equals(object? obj) => false;
                public override int GetHashCode() => 0;
            }

            class Example
            {
                object? Dynamic(dynamic value) => value is null ? null : value.Member;
                int? Overloaded(Value? value) => value == null ? null : value.Member;
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresChangedResultTypeAndNonImmediateAccess()
    {
        const string source = """
            class Child
            {
                public int Value => 1;
            }

            class Parent
            {
                public Child Child => new();
            }

            class Example
            {
                object Box(string? value) => value is null ? null : value.Length;
                int? Chain(Parent? value) => value is null ? null : value.Child.Value;
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresDirectives()
    {
        const string source = """
            class Example
            {
                int? GetLength(string? value) => value is null
            #if DEBUG
                    ? null
            #else
                    ? null
            #endif
                    : value.Length;
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }
}
