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
