using HooSharper.CodeFixes;
using VerifyCS = HooSharper.Analyzers.Tests.AnalyzerVerifier<
    HooSharper.Analyzers.PreferLoopContinueAnalyzer,
    HooSharper.CodeFixes.PreferLoopContinueCodeFixProvider>;

namespace HooSharper.Analyzers.Tests;

public sealed class PreferLoopContinueAnalyzerTests
{
    [Fact]
    public Task ConvertsFinalForeachIfIntoContinueGuard()
    {
        const string source = """
            using System.Collections.Generic;

            class Example
            {
                void Run(IEnumerable<int> values)
                {
                    foreach (var value in values)
                    {
                        Prepare(value);
                        {|#0:if|} (value > 0)
                        {
                            Execute(value);
                            Finish(value);
                        }
                    }
                }

                void Prepare(int value) { }
                void Execute(int value) { }
                void Finish(int value) { }
            }
            """;
        const string fixedSource = """
            using System.Collections.Generic;

            class Example
            {
                void Run(IEnumerable<int> values)
                {
                    foreach (var value in values)
                    {
                        Prepare(value);
                        if (!(value > 0))
                            continue;
                        Execute(value);
                        Finish(value);
                    }
                }

                void Prepare(int value) { }
                void Execute(int value) { }
                void Finish(int value) { }
            }
            """;

        var expected = VerifyCS.Diagnostic(PreferLoopContinueAnalyzer.DiagnosticId)
            .WithLocation(0)
            .WithMessage("Invert this condition and continue early");

        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task ConvertsFinalWhileIfAndSimplifiesNegation()
    {
        const string source = """
            class Example
            {
                void Run(bool disabled)
                {
                    while (TryNext())
                    {
                        {|#0:if|} (!disabled)
                        {
                            Execute();
                        }
                    }
                }

                bool TryNext() => false;
                void Execute() { }
            }
            """;
        const string fixedSource = """
            class Example
            {
                void Run(bool disabled)
                {
                    while (TryNext())
                    {
                        if (disabled)
                            continue;
                        Execute();
                    }
                }

                bool TryNext() => false;
                void Execute() { }
            }
            """;

        var expected = VerifyCS.Diagnostic(PreferLoopContinueAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task PreservesCommentsAroundMovedStatements()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled)
                {
                    for (;;)
                    {
                        // Guarded work follows.
                        {|#0:if|} (enabled)
                        {
                            // Keep this comment.
                            Execute(); // Keep trailing comment.
                            // Keep the closing comment.
                        } // Keep the if comment.
                    }
                }

                void Execute() { }
            }
            """;
        const string fixedSource = """
            class Example
            {
                void Run(bool enabled)
                {
                    for (;;)
                    {
                        // Guarded work follows.
                        if (!enabled)
                            continue;
                        // Keep this comment.
                        Execute(); // Keep trailing comment.
                                   // Keep the closing comment.
                                   // Keep the if comment.
                    }
                }

                void Execute() { }
            }
            """;

        var expected = VerifyCS.Diagnostic(PreferLoopContinueAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task DoesNotReportNonFinalIf()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled)
                {
                    for (;;)
                    {
                        if (enabled)
                        {
                            Execute();
                        }

                        Finish();
                    }
                }

                void Execute() { }
                void Finish() { }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task DoesNotReportIfOutsideLoop()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled)
                {
                    if (enabled)
                    {
                        Execute();
                    }
                }

                void Execute() { }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task DoesNotReportBlockContainingDirectives()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled)
                {
                    do
                    {
                        if (enabled)
                        {
            #if DEBUG
                            Execute();
            #endif
                        }
                    }
                    while (enabled);
                }

                void Execute() { }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task FixAllConvertsEveryEligibleLoop()
    {
        const string source = """
            using System.Collections.Generic;

            class Example
            {
                void Run(IEnumerable<int> values)
                {
                    foreach (var value in values)
                    {
                        {|#0:if|} (value == 0)
                        {
                            Execute(value);
                        }
                    }

                    for (var index = 0; index < 10; index++)
                    {
                        {|#1:if|} (index != 5)
                        {
                            Finish(index);
                        }
                    }
                }

                void Execute(int value) { }
                void Finish(int value) { }
            }
            """;
        const string fixedSource = """
            using System.Collections.Generic;

            class Example
            {
                void Run(IEnumerable<int> values)
                {
                    foreach (var value in values)
                    {
                        if (value != 0)
                            continue;
                        Execute(value);
                    }

                    for (var index = 0; index < 10; index++)
                    {
                        if (index == 5)
                            continue;
                        Finish(index);
                    }
                }

                void Execute(int value) { }
                void Finish(int value) { }
            }
            """;

        var expected = new[]
        {
            VerifyCS.Diagnostic(PreferLoopContinueAnalyzer.DiagnosticId).WithLocation(0),
            VerifyCS.Diagnostic(PreferLoopContinueAnalyzer.DiagnosticId).WithLocation(1),
        };

        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }
    [Fact]
    public Task PreservesFloatingPointNaNSemantics()
    {
        const string source = """
            class Example
            {
                void Run(double[] values)
                {
                    foreach (var value in values)
                    {
                        {|#0:if|} (value > 0)
                        {
                            Execute(value);
                        }
                    }
                }

                void Execute(double value) { }
            }
            """;
        const string fixedSource = """
            class Example
            {
                void Run(double[] values)
                {
                    foreach (var value in values)
                    {
                        if (!(value > 0))
                            continue;
                        Execute(value);
                    }
                }

                void Execute(double value) { }
            }
            """;

        var expected = VerifyCS.Diagnostic(PreferLoopContinueAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

}
