using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using HooSharper.CodeFixes;
using VerifyCS = HooSharper.Analyzers.Tests.AnalyzerVerifier<
    HooSharper.Analyzers.UseNullCoalescingExpressionAnalyzer,
    HooSharper.CodeFixes.UseNullCoalescingExpressionCodeFixProvider>;

namespace HooSharper.Analyzers.Tests;

public sealed class UseNullCoalescingExpressionAnalyzerTests
{
    [Theory]
    [InlineData("value is null {|#0:?|} fallback : value")]
    [InlineData("value is not null {|#0:?|} value : fallback")]
    [InlineData("value == null {|#0:?|} fallback : value")]
    [InlineData("null == value {|#0:?|} fallback : value")]
    [InlineData("value != null {|#0:?|} value : fallback")]
    [InlineData("null != value {|#0:?|} value : fallback")]
    public Task ReplacesBothOrientationsAndOrdinaryEquality(string expression)
    {
        var source = $$"""
            class Example
            {
                string Get(string? value, string fallback) => {{expression}};
            }
            """;
        const string fixedSource = """
            class Example
            {
                string Get(string? value, string fallback) => value ?? fallback;
            }
            """;

        var expected = VerifyCS.Diagnostic(UseNullCoalescingExpressionAnalyzer.DiagnosticId)
            .WithLocation(0)
            .WithMessage("Use a null-coalescing expression");
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task IgnoresNullableValueAccessInsteadOfRepeatedTarget()
    {
        const string source = """
            class Example
            {
                int Get(int? value, int fallback) => value is null ? fallback : value.Value;
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task ReplacesNullableValueTypeWhenTargetIsRepeated()
    {
        const string source = """
            class Example
            {
                int? Get(int? value, int? fallback) => value is null {|#0:?|} fallback : value;
            }
            """;
        const string fixedSource = """
            class Example
            {
                int? Get(int? value, int? fallback) => value ?? fallback;
            }
            """;

        return VerifyCS.VerifyCodeFixAsync(
            source,
            VerifyCS.Diagnostic(UseNullCoalescingExpressionAnalyzer.DiagnosticId).WithLocation(0),
            fixedSource);
    }

    [Fact]
    public Task PreservesParenthesesAndComments()
    {
        const string source = """
            class Example
            {
                string Get(string? value, string fallback) =>
                    (value is null /* condition */ {|#0:?|} /* fallback */ fallback /* separator */ : /* repeated */ value);
            }
            """;
        const string fixedSource = """
            class Example
            {
                string Get(string? value, string fallback) =>
                    (value ?? /* condition */ /* fallback */ /* separator */ /* repeated */ fallback);
            }
            """;

        return VerifyCS.VerifyCodeFixAsync(
            source,
            VerifyCS.Diagnostic(UseNullCoalescingExpressionAnalyzer.DiagnosticId).WithLocation(0),
            fixedSource);
    }

    [Fact]
    public Task IgnoresUnstableAndMismatchedTargets()
    {
        const string source = """
            class Holder
            {
                public string? Value { get; set; }
            }

            class Example
            {
                Holder GetHolder() => new();

                string Unstable(string fallback) =>
                    GetHolder().Value is null ? fallback : GetHolder().Value;

                string? Mismatched(string? first, string? second, string fallback) =>
                    first is null ? fallback : second;
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresPropertiesBecauseRepeatedEvaluationMayDiffer()
    {
        const string source = """
            class Example
            {
                string? Value => null;

                string Get(string fallback) => Value is null ? fallback : Value;
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresMutableVolatileAndUnstableReceiverFields()
    {
        const string source = """
            class Holder
            {
                public readonly string? Value;
            }

            class Example
            {
                private string? mutable;
                private volatile string? volatileField;
                private Holder mutableHolder = new();

                string Mutable(string fallback) =>
                    mutable is null ? fallback : mutable;

                string Volatile(string fallback) =>
                    volatileField is null ? fallback : volatileField;

                string UnstableReceiver(string fallback) =>
                    mutableHolder.Value is null ? fallback : mutableHolder.Value;
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task DiagnosesReadonlyFieldsWithStableReceivers()
    {
        const string source = """
            class Holder
            {
                public readonly string? Value;
            }

            class Example
            {
                private readonly string? value;
                private readonly Holder holder = new();

                string Direct(string fallback) =>
                    value is null {|#0:?|} fallback : value;

                string Nested(string fallback) =>
                    holder.Value is null {|#1:?|} fallback : holder.Value;

                string Parameter(Holder parameter, string fallback) =>
                    parameter.Value is null {|#2:?|} fallback : parameter.Value;
            }
            """;
        const string fixedSource = """
            class Holder
            {
                public readonly string? Value;
            }

            class Example
            {
                private readonly string? value;
                private readonly Holder holder = new();

                string Direct(string fallback) =>
                    value ?? fallback;

                string Nested(string fallback) =>
                    holder.Value ?? fallback;

                string Parameter(Holder parameter, string fallback) =>
                    parameter.Value ?? fallback;
            }
            """;

        var expected = new[]
        {
            VerifyCS.Diagnostic(UseNullCoalescingExpressionAnalyzer.DiagnosticId).WithLocation(0),
            VerifyCS.Diagnostic(UseNullCoalescingExpressionAnalyzer.DiagnosticId).WithLocation(1),
            VerifyCS.Diagnostic(UseNullCoalescingExpressionAnalyzer.DiagnosticId).WithLocation(2),
        };
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }

    [Fact]
    public Task IgnoresEventsBecauseRepeatedEvaluationMayDiffer()
    {
        const string source = """
            using System;

            class Example
            {
                event Action? Changed;

                Action Get(Action fallback) =>
                    Changed is null ? fallback : Changed;
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresOverloadedEquality()
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
                Value Get(Value? value, Value fallback) =>
                    value == null ? fallback : value;
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresDynamicEqualityAndPatternTargets()
    {
        const string source = """
            class Example
            {
                dynamic Equality(dynamic value, dynamic fallback) =>
                    value == null ? fallback : value;

                dynamic Pattern(dynamic value, dynamic fallback) =>
                    value is null ? fallback : value;
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task PreservesCommentsWithinTargetAndFallbackOperands()
    {
        const string source = """
            class Example
            {
                string Get(string? value, string fallback) =>
                    value /* target */ is null {|#0:?|} (fallback /* fallback */) : value;
            }
            """;
        const string fixedSource = """
            class Example
            {
                string Get(string? value, string fallback) =>
                    value /* target */ ?? (fallback /* fallback */);
            }
            """;

        return VerifyCS.VerifyCodeFixAsync(
            source,
            VerifyCS.Diagnostic(UseNullCoalescingExpressionAnalyzer.DiagnosticId).WithLocation(0),
            fixedSource);
    }

    [Fact]
    public Task PreservesResultingBaseTypeConversion()
    {
        const string source = """
            class Base { }
            class Derived : Base { }

            class Example
            {
                Base Get(Derived? value, Base fallback) =>
                    value is null {|#0:?|} fallback : value;
            }
            """;
        const string fixedSource = """
            class Base { }
            class Derived : Base { }

            class Example
            {
                Base Get(Derived? value, Base fallback) =>
                    value ?? fallback;
            }
            """;

        return VerifyCS.VerifyCodeFixAsync(
            source,
            VerifyCS.Diagnostic(UseNullCoalescingExpressionAnalyzer.DiagnosticId).WithLocation(0),
            fixedSource);
    }

    [Fact]
    public Task IgnoresUnsupportedTypesAndDirectives()
    {
        const string source = """
            class Example
            {
                int Value(int value, int fallback) => value == 0 ? fallback : value;

                string Directive(string? value, string fallback) =>
                    value is null
            #if DEBUG
                        ? fallback
            #else
                        ? fallback
            #endif
                        : value;
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task ParenthesizesLowPrecedenceFallbackExpressions()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using System.Linq;

            class Example
            {
                string Assignment(string? value, string fallback) =>
                    value is null {|#0:?|} fallback /* destination */ = "assigned" : value;

                Func<int> Lambda(Func<int>? value) =>
                    value is null {|#1:?|} () => 1 : value;

                Func<int> AnonymousMethod(Func<int>? value) =>
                    value is null {|#2:?|} delegate { return 1; } : value;

                string Conditional(string? value, bool condition, string first, string second) =>
                    value is null {|#3:?|} condition ? first : second : value;

                string Switch(string? value, int number) =>
                    value is null {|#4:?|} number switch { 0 => "zero", _ => "other" } : value;

                IEnumerable<int> Query(IEnumerable<int>? value, IEnumerable<int> items) =>
                    value is null {|#5:?|} from item in items select item : value;
            }
            """;
        const string fixedSource = """
            using System;
            using System.Collections.Generic;
            using System.Linq;

            class Example
            {
                string Assignment(string? value, string fallback) =>
                    value ?? (fallback /* destination */ = "assigned");

                Func<int> Lambda(Func<int>? value) =>
                    value ?? (() => 1);

                Func<int> AnonymousMethod(Func<int>? value) =>
                    value ?? (delegate { return 1; });

                string Conditional(string? value, bool condition, string first, string second) =>
                    value ?? (condition ? first : second);

                string Switch(string? value, int number) =>
                    value ?? (number switch { 0 => "zero", _ => "other" });

                IEnumerable<int> Query(IEnumerable<int>? value, IEnumerable<int> items) =>
                    value ?? (from item in items select item);
            }
            """;

        var expected = new[]
        {
            VerifyCS.Diagnostic(UseNullCoalescingExpressionAnalyzer.DiagnosticId).WithLocation(0),
            VerifyCS.Diagnostic(UseNullCoalescingExpressionAnalyzer.DiagnosticId).WithLocation(1),
            VerifyCS.Diagnostic(UseNullCoalescingExpressionAnalyzer.DiagnosticId).WithLocation(2),
            VerifyCS.Diagnostic(UseNullCoalescingExpressionAnalyzer.DiagnosticId).WithLocation(3),
            VerifyCS.Diagnostic(UseNullCoalescingExpressionAnalyzer.DiagnosticId).WithLocation(4),
            VerifyCS.Diagnostic(UseNullCoalescingExpressionAnalyzer.DiagnosticId).WithLocation(5),
        };
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }

    [Fact]
    public Task FixAllReplacesEverySafeConditional()
    {
        const string source = """
            class Example
            {
                private readonly string? field;

                string Get(string? value, string fallback) =>
                    (value is null {|#0:?|} fallback : value) +
                    (field is not null {|#1:?|} field : fallback);
            }
            """;
        const string fixedSource = """
            class Example
            {
                private readonly string? field;

                string Get(string? value, string fallback) =>
                    (value ?? fallback) +
                    (field ?? fallback);
            }
            """;

        var expected = new[]
        {
            VerifyCS.Diagnostic(UseNullCoalescingExpressionAnalyzer.DiagnosticId).WithLocation(0),
            VerifyCS.Diagnostic(UseNullCoalescingExpressionAnalyzer.DiagnosticId).WithLocation(1),
        };
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }

    [Fact]
    public Task DoesNotReportBeforeCSharp2()
    {
        const string source = """
            class Example
            {
                string Get(string value, string fallback)
                {
                    return value == null ? fallback : value;
                }
            }
            """;

        var test = new CSharpCodeFixTest<
            UseNullCoalescingExpressionAnalyzer,
            UseNullCoalescingExpressionCodeFixProvider,
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
                ((CSharpParseOptions)project.ParseOptions!).WithLanguageVersion(LanguageVersion.CSharp1));
        });

        return test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public Task AcceptsDefaultLanguageVersion()
    {
        const string source = """
            class Example
            {
                string Get(string? value, string fallback) =>
                    value is null {|#0:?|} fallback : value;
            }
            """;
        const string fixedSource = """
            class Example
            {
                string Get(string? value, string fallback) =>
                    value ?? fallback;
            }
            """;

        var test = new CSharpCodeFixTest<
            UseNullCoalescingExpressionAnalyzer,
            UseNullCoalescingExpressionCodeFixProvider,
            DefaultVerifier>
        {
            ReferenceAssemblies = ReferenceAssemblies.Net.Net100,
            TestCode = source,
            FixedCode = fixedSource,
        };
        test.ExpectedDiagnostics.Add(
            VerifyCS.Diagnostic(UseNullCoalescingExpressionAnalyzer.DiagnosticId).WithLocation(0));
        test.SolutionTransforms.Add((solution, projectId) =>
        {
            var project = solution.GetProject(projectId)!;
            return solution.WithProjectParseOptions(
                projectId,
                ((CSharpParseOptions)project.ParseOptions!).WithLanguageVersion(LanguageVersion.Default));
        });

        return test.RunAsync(TestContext.Current.CancellationToken);
    }
}
