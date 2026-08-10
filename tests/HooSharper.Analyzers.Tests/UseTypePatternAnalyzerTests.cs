using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

using HooSharper.CodeFixes;
using VerifyCS = HooSharper.Analyzers.Tests.AnalyzerVerifier<
    HooSharper.Analyzers.UseTypePatternAnalyzer,
    HooSharper.CodeFixes.UseTypePatternCodeFixProvider>;

namespace HooSharper.Analyzers.Tests;

public sealed class UseTypePatternAnalyzerTests
{
    [Fact]
    public Task ConvertsIsNotNullCheck()
    {
        const string source = """
            class Example
            {
                void Run(object value)
                {
                    var text = value {|#0:as|} string;
                    if (text is not null)
                    {
                        System.Console.WriteLine(text.Length);
                    }
                }
            }
            """;
        const string fixedSource = """
            class Example
            {
                void Run(object value)
                {
                    if (value is string text)
                    {
                        System.Console.WriteLine(text.Length);
                    }
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(UseTypePatternAnalyzer.DiagnosticId)
            .WithLocation(0)
            .WithMessage("Replace the as cast and null check with a type pattern");

        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task ConvertsNotEqualsNullCheckAndPreservesComments()
    {
        const string source = """
            class Example
            {
                void Run(object value)
                {
                    // Cast once.
                    var text = value {|#0:as|} string; // Keep this explanation.
                    // Use the successful cast.
                    if (text != null) // Keep the condition comment.
                    {
                        System.Console.WriteLine(text);
                    }
                }
            }
            """;
        const string fixedSource = """
            class Example
            {
                void Run(object value)
                {
                    // Cast once.
                    // Keep this explanation.
                    // Use the successful cast.
                    if (value is string text) // Keep the condition comment.
                    {
                        System.Console.WriteLine(text);
                    }
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(UseTypePatternAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task DoesNotReportDifferentSymbol()
    {
        const string source = """
            class Example
            {
                private string? text;

                void Run(object value)
                {
                    var other = value as string;
                    if (text is not null)
                    {
                        System.Console.WriteLine(text);
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task DoesNotReportWhenLocalIsUsedAfterIf()
    {
        const string source = """
            class Example
            {
                void Run(object value)
                {
                    var text = value as string;
                    if (text != null)
                    {
                        System.Console.WriteLine(text.Length);
                    }

                    System.Console.WriteLine(text);
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task DoesNotReportMultipleDeclarators()
    {
        const string source = """
            class Example
            {
                void Run(object first, object second)
                {
                    string? left = first as string, right = second as string;
                    if (left is not null)
                    {
                        System.Console.WriteLine(left);
                    }
                }
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
                void Run(object value)
                {
                    var text = value as string;
            #if DEBUG
                    if (text is not null)
                    {
                        System.Console.WriteLine(text);
                    }
            #endif
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task DoesNotReportBeforeCSharp7()
    {
        const string source = """
            class Example
            {
                void Run(object value)
                {
                    var text = value as string;
                    if (text != null)
                    {
                        System.Console.WriteLine(text.Length);
                    }
                }
            }
            """;

        var test = new CSharpCodeFixTest<
            UseTypePatternAnalyzer,
            UseTypePatternCodeFixProvider,
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
                ((CSharpParseOptions)project.ParseOptions!).WithLanguageVersion(LanguageVersion.CSharp6));
        });

        return test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public Task AcceptsDefaultLanguageVersion()
    {
        const string source = """
            class Example
            {
                void Run(object value)
                {
                    var text = value {|#0:as|} string;
                    if (text != null)
                    {
                        System.Console.WriteLine(text.Length);
                    }
                }
            }
            """;
        const string fixedSource = """
            class Example
            {
                void Run(object value)
                {
                    if (value is string text)
                    {
                        System.Console.WriteLine(text.Length);
                    }
                }
            }
            """;

        var test = new CSharpCodeFixTest<
            UseTypePatternAnalyzer,
            UseTypePatternCodeFixProvider,
            DefaultVerifier>
        {
            ReferenceAssemblies = ReferenceAssemblies.Net.Net100,
            TestCode = source,
            FixedCode = fixedSource,
        };
        test.ExpectedDiagnostics.Add(VerifyCS.Diagnostic(UseTypePatternAnalyzer.DiagnosticId).WithLocation(0));
        test.SolutionTransforms.Add((solution, projectId) =>
            solution.WithProjectParseOptions(
                projectId,
                ((CSharpParseOptions)solution.GetProject(projectId)!.ParseOptions!)
                    .WithLanguageVersion(LanguageVersion.Default)));
        return test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public Task DoesNotReportExplicitTypeUsingOrDiscardDeclarations()
    {
        const string source = """
            using System;
            using System.Threading.Tasks;

            class Example : IDisposable
            {
                public void Dispose() { }

                async Task Run(object value)
                {
                    string text = value as string;
                    if (text != null)
                    {
                        System.Console.WriteLine(text.Length);
                    }

                    await using var resource = value as System.IO.MemoryStream;
                    if (resource != null)
                    {
                        resource.Dispose();
                    }

                    var _ = value as string;
                    if (value != null)
                    {
                        System.Console.WriteLine(value);
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task DoesNotReportEscapedUnderscoreDeclaration()
    {
        const string source = """
            class Example
            {
                void Run(object value)
                {
                    var @_ = value as string;
                    if (@_ != null)
                    {
                        System.Console.WriteLine(@_.Length);
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task DoesNotReportOverloadedInequality()
    {
        const string source = """
            class Value
            {
                public static bool operator ==(Value? left, Value? right) => true;
                public static bool operator !=(Value? left, Value? right) => false;
            }

            class Example
            {
                void Run(object value)
                {
                    var typed = value as Value;
                    if (typed != null)
                    {
                        System.Console.WriteLine(typed);
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task FixAllConvertsEveryEligiblePair()
    {
        const string source = """
            class Example
            {
                void Run(object first, object second)
                {
                    var left = first {|#0:as|} string;
                    if (left is not null)
                    {
                        System.Console.WriteLine(left);
                    }

                    var right = second {|#1:as|} string;
                    if (right != null)
                    {
                        System.Console.WriteLine(right);
                    }
                }
            }
            """;
        const string fixedSource = """
            class Example
            {
                void Run(object first, object second)
                {
                    if (first is string left)
                    {
                        System.Console.WriteLine(left);
                    }

                    if (second is string right)
                    {
                        System.Console.WriteLine(right);
                    }
                }
            }
            """;

        var expected = new[]
        {
            VerifyCS.Diagnostic(UseTypePatternAnalyzer.DiagnosticId).WithLocation(0),
            VerifyCS.Diagnostic(UseTypePatternAnalyzer.DiagnosticId).WithLocation(1),
        };

        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }
}
