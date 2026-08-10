using HooSharper.CodeFixes;
using Microsoft.CodeAnalysis.Testing;
using VerifyCS = HooSharper.Analyzers.Tests.AnalyzerVerifier<
    HooSharper.Analyzers.WrapFluentChainAnalyzer,
    HooSharper.CodeFixes.WrapFluentChainCodeFixProvider>;

namespace HooSharper.Analyzers.Tests;

public sealed class WrapFluentChainAnalyzerTests
{
    [Fact]
    public Task WrapsAtDefault140WithDotLeadingIndentation()
    {
        const string source = """
            using System.Linq;
            class Example
            {
                int[] Run(int[] source)
                {
                    return {|#0:source.Where(value => value > 0).Select(value => value * 2).Where(value => value < 100).Select(value => value + 1).Where(value => value != 42).ToArray()|};
                }
            }
            """;
        const string fixedSource = """
            using System.Linq;
            class Example
            {
                int[] Run(int[] source)
                {
                    return source
                        .Where(value => value > 0)
                        .Select(value => value * 2)
                        .Where(value => value < 100)
                        .Select(value => value + 1)
                        .Where(value => value != 42)
                        .ToArray();
                }
            }
            """;

        return VerifyCS.VerifyCodeFixAsync(source,
            VerifyCS.Diagnostic(WrapFluentChainAnalyzer.DiagnosticId).WithLocation(0), fixedSource);
    }

    [Fact]
    public Task HonorsConfiguredLengthAndSpecificKeyPrecedence()
    {
        const string source = """
            using System.Linq;
            class Example
            {
                int[] Run(int[] source) => {|#0:source.Where(value => value > 0).Select(value => value * 2).ToArray()|};
            }
            """;
        const string fixedSource = """
            using System.Linq;
            class Example
            {
                int[] Run(int[] source) => source
                    .Where(value => value > 0)
                    .Select(value => value * 2)
                    .ToArray();
            }
            """;

        return VerifyWithConfigAsync(source, fixedSource,
            VerifyCS.Diagnostic(WrapFluentChainAnalyzer.DiagnosticId).WithLocation(0),
            "max_line_length = 200\nhoosharper_max_line_length = 40");
    }

    [Fact]
    public async Task IgnoresUnsafeAndAlreadyMultilineText()
    {
        const string source = """
            using System.Linq;
            class Example
            {
                object? Run(string? text, int[] values)
                {
                    var conditional = text?.Trim().ToUpperInvariant().PadLeft(100).PadRight(100);
                    var range = values[1..^1];
                    var number = 1234567890.1234567890m;
                    var literal = "........................................................................................................................................";
                    // ......................................................................................................................................
                    return values
                        .Where(x => x > 0)
                        .Select(x => x + 1)
                        .ToArray();
                }
            }
            """;
        const string directives = """
            using System.Linq;
            class Example
            {
                int[] Run(int[] values)
                {
            #if DEBUG
                    return values.Where(x => x > 0).Select(x => x + 1).Where(x => x < 10).Select(x => x * 2).Where(x => x != 4).ToArray();
            #else
                    return values;
            #endif
                }
            }
            """;

        await VerifyCS.VerifyAnalyzerAsync(source);
        await VerifyCS.VerifyAnalyzerAsync(directives);
    }

    [Fact]
    public Task IgnoresShortChainBeforeLongUnrelatedTrailingContent()
    {
        const string source = """
            using System.Linq;
            class Example
            {
                int[] BeforeComment(int[] values) => values.Where(x => x > 0).Select(x => x + 1).ToArray(); // This deliberately long unrelated comment pushes the physical line beyond the configured maximum without making the fluent chain itself too long.
                (int[], string) BeforeString(int[] values) => (values.Where(x => x > 0).Select(x => x + 1).ToArray(), "This deliberately long unrelated string pushes the physical line beyond the configured maximum without making the fluent chain itself too long.");
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task PreservesCommentsBetweenSegments()
    {
        const string source = """
            using System.Linq;
            class Example
            {
                int[] Run(int[] source)
                {
                    return {|#0:source.Where(value => value > 0) /* keep */.Select(value => value * 2).Where(value => value < 100).Select(value => value + 1).Where(value => value != 42).ToArray()|};
                }
            }
            """;
        const string fixedSource = """
            using System.Linq;
            class Example
            {
                int[] Run(int[] source)
                {
                    return source
                        .Where(value => value > 0) /* keep */
                        .Select(value => value * 2)
                        .Where(value => value < 100)
                        .Select(value => value + 1)
                        .Where(value => value != 42)
                        .ToArray();
                }
            }
            """;

        return VerifyCS.VerifyCodeFixAsync(source,
            VerifyCS.Diagnostic(WrapFluentChainAnalyzer.DiagnosticId).WithLocation(0), fixedSource);
    }

    [Fact]
    public Task FixAllWrapsEachOutermostChain()
    {
        const string source = """
            using System.Linq;
            class Example
            {
                int[] First(int[] source) => {|#0:source.Where(value => value > 0).Select(value => value * 2).Where(value => value < 100).Select(value => value + 1).Where(value => value != 42).ToArray()|};
                int[] Second(int[] source) => {|#1:source.Where(value => value < 0).Select(value => value * 3).Where(value => value > -100).Select(value => value - 1).Where(value => value != -42).ToArray()|};
            }
            """;
        const string fixedSource = """
            using System.Linq;
            class Example
            {
                int[] First(int[] source) => source
                    .Where(value => value > 0)
                    .Select(value => value * 2)
                    .Where(value => value < 100)
                    .Select(value => value + 1)
                    .Where(value => value != 42)
                    .ToArray();
                int[] Second(int[] source) => source
                    .Where(value => value < 0)
                    .Select(value => value * 3)
                    .Where(value => value > -100)
                    .Select(value => value - 1)
                    .Where(value => value != -42)
                    .ToArray();
            }
            """;
        const string batchFixedSource = """
            using System.Linq;
            class Example
            {
                int[] First(int[] source) => source
                    .Where(value => value > 0)
                    .Select(value => value * 2)
                    .Where(value => value < 100)
                    .Select(value => value + 1)
                    .Where(value => value != 42)
                    .ToArray();
                int[] Second(int[] source) => source
                    .Where(value => value < 0)
                    .Select(value => value * 3)
                    .Where(value => value > -100)
                    .Select(value => value - 1)
                    .Where(value => value != -42)
                    .ToArray();
            }
            """;

        var expected = new[]
        {
            VerifyCS.Diagnostic(WrapFluentChainAnalyzer.DiagnosticId).WithLocation(0),
            VerifyCS.Diagnostic(WrapFluentChainAnalyzer.DiagnosticId).WithLocation(1),
        };
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, batchFixedSource);
    }

    [Fact]
    public async Task UsesExactVisual140CharacterBoundary()
    {
        const string chain = "source.Where(x => x > 0).Select(x => x + 1).ToArray()";
        const string prefix = "    int[] Run(int[] source) => ";
        var paddingAt140 = new string(' ', 140 - prefix.Length - chain.Length);
        var sourceAt140 = "using System.Linq;\nclass Example\n{\n" + prefix + paddingAt140 + chain + ";\n}\n";
        var sourceAt141 = "using System.Linq;\nclass Example\n{\n" + prefix + paddingAt140 + " " +
            "{|#0:" + chain + "|};\n}\n";

        await VerifyCS.VerifyAnalyzerAsync(sourceAt140);
        await VerifyAnalyzerWithConfigAsync(
            sourceAt141,
            VerifyCS.Diagnostic(WrapFluentChainAnalyzer.DiagnosticId).WithLocation(0),
            string.Empty);
    }

    [Fact]
    public Task CountsTabsByVisualTabStops()
    {
        const string source = "using System.Linq;\nclass Example\n{\n\tint[] Run(int[] source) => {|#0:source.Where(x => x > 0).Select(x => x + 1).ToArray()|};\n}\n";

        return VerifyAnalyzerWithConfigAsync(
            source,
            VerifyCS.Diagnostic(WrapFluentChainAnalyzer.DiagnosticId).WithLocation(0),
            "hoosharper_max_line_length = 87\ntab_width = 8");
    }

    [Fact]
    public Task HonorsTwoSpaceContinuationIndentAndPreservesCrLf()
    {
        const string source = "using System.Linq;\r\nclass Example\r\n{\r\n  int[] Run(int[] source) => {|#0:source.Where(x => x > 0).Select(x => x + 1).ToArray()|};\r\n}\r\n";
        const string fixedSource = "using System.Linq;\r\nclass Example\r\n{\r\n  int[] Run(int[] source) => source\r\n    .Where(x => x > 0)\r\n    .Select(x => x + 1)\r\n    .ToArray();\r\n}\r\n";

        return VerifyWithConfigAsync(
            source,
            fixedSource,
            VerifyCS.Diagnostic(WrapFluentChainAnalyzer.DiagnosticId).WithLocation(0),
            "hoosharper_max_line_length = 40\nindent_style = space\nindent_size = 2\ntab_width = 8");
    }

    [Fact]
    public Task HonorsTabContinuationIndent()
    {
        const string source = "using System.Linq;\nclass Example\n{\n\tint[] Run(int[] source) => {|#0:source.Where(x => x > 0).Select(x => x + 1).ToArray()|};\n}\n";
        const string fixedSource = "using System.Linq;\nclass Example\n{\n\tint[] Run(int[] source) => source\n\t\t.Where(x => x > 0)\n\t\t.Select(x => x + 1)\n\t\t.ToArray();\n}\n";

        return VerifyWithConfigAsync(
            source,
            fixedSource,
            VerifyCS.Diagnostic(WrapFluentChainAnalyzer.DiagnosticId).WithLocation(0),
            "hoosharper_max_line_length = 40\nindent_style = tab\nindent_size = 2\ntab_width = 8");
    }

    [Theory]
    [InlineData("hoosharper_max_line_length = invalid\nmax_line_length = invalid")]
    [InlineData("hoosharper_max_line_length = 0\nmax_line_length = 0")]
    [InlineData("hoosharper_max_line_length = 999999999999999999999\nmax_line_length = 999999999999999999999")]
    public Task FallsBackToDefaultForInvalidMaximumLineLength(string options)
    {
        const string source = """
            using System.Linq;
            class Example
            {
                int[] Run(int[] source) => {|#0:source.Where(value => value > 0).Select(value => value * 2).Where(value => value < 100).Select(value => value + 1).Where(value => value != 42).ToArray()|};
            }
            """;

        return VerifyAnalyzerWithConfigAsync(
            source,
            VerifyCS.Diagnostic(WrapFluentChainAnalyzer.DiagnosticId).WithLocation(0),
            options);
    }

    [Fact]
    public Task InvalidIndentOptionsFallBackToFourSpaces()
    {
        const string source = "using System.Linq;\nclass Example\n{\n    int[] Run(int[] source) => {|#0:source.Where(x => x > 0).Select(x => x + 1).ToArray()|};\n}\n";
        const string fixedSource = "using System.Linq;\nclass Example\n{\n    int[] Run(int[] source) => source\n        .Where(x => x > 0)\n        .Select(x => x + 1)\n        .ToArray();\n}\n";

        return VerifyWithConfigAsync(
            source,
            fixedSource,
            VerifyCS.Diagnostic(WrapFluentChainAnalyzer.DiagnosticId).WithLocation(0),
            "hoosharper_max_line_length = 40\nindent_style = nonsense\nindent_size = 0\ntab_width = 999999999999999999999");
    }

    private static Task VerifyAnalyzerWithConfigAsync(string source, DiagnosticResult expected, string options)
    {
        var test = new ConfigTest { TestCode = source, ExpectedDiagnostics = { expected } };
        test.TestState.AnalyzerConfigFiles.Add(("/.editorconfig", "root = true\n\n[*.cs]\n" + options + "\n"));
        return test.RunAsync(TestContext.Current.CancellationToken);
    }
    [Theory]
    [InlineData("invalid")]
    [InlineData("0")]
    [InlineData("999999999999999999999")]
    public Task InvalidSpecificLengthFallsBackToValidStandardLength(string invalidValue)
    {
        const string source = "using System.Linq;\nclass Example\n{\n    int[] Run(int[] source) => {|#0:source.Where(x => x > 0).Select(x => x + 1).ToArray()|};\n}\n";

        return VerifyAnalyzerWithConfigAsync(
            source,
            VerifyCS.Diagnostic(WrapFluentChainAnalyzer.DiagnosticId).WithLocation(0),
            "hoosharper_max_line_length = " + invalidValue + "\nmax_line_length = 40");
    }


    private static Task VerifyWithConfigAsync(string source, string fixedSource, DiagnosticResult expected, string options)
    {
        var test = new ConfigTest { TestCode = source, FixedCode = fixedSource, ExpectedDiagnostics = { expected } };
        test.TestState.AnalyzerConfigFiles.Add(("/.editorconfig", "root = true\n\n[*.cs]\n" + options + "\n"));
        return test.RunAsync(TestContext.Current.CancellationToken);
    }

    private sealed class ConfigTest : Microsoft.CodeAnalysis.CSharp.Testing.CSharpCodeFixTest<
        WrapFluentChainAnalyzer, WrapFluentChainCodeFixProvider, DefaultVerifier>
    {
        public ConfigTest() => ReferenceAssemblies = ReferenceAssemblies.Net.Net100;
    }

    [Fact]
    public Task WrapsChainStartingAfterElementAccess()
    {
        const string source = """
            using System.Linq;
            class Example
            {
                int[] Run(int[][] source) => {|#0:source[0].Where(value => value > 0).Select(value => value + 1).ToArray()|};
            }
            """;
        const string fixedSource = """
            using System.Linq;
            class Example
            {
                int[] Run(int[][] source) => source[0]
                    .Where(value => value > 0)
                    .Select(value => value + 1)
                    .ToArray();
            }
            """;

        return VerifyWithConfigAsync(
            source,
            fixedSource,
            VerifyCS.Diagnostic(WrapFluentChainAnalyzer.DiagnosticId).WithLocation(0),
            "hoosharper_max_line_length = 40");
    }

    [Fact]
    public Task IgnoresPropertyOnlyAccessChains()
    {
        const string source = """
            class Example
            {
                int Run(Holder holder) => holder.First.Second.Third.Fourth;
            }

            class Holder
            {
                public Holder First => this;
                public Holder Second => this;
                public Holder Third => this;
                public int Fourth => 0;
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }
}
