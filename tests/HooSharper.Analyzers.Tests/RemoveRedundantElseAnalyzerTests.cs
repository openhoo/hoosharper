using HooSharper.CodeFixes;
using VerifyCS = HooSharper.Analyzers.Tests.AnalyzerVerifier<
    HooSharper.Analyzers.RemoveRedundantElseAnalyzer,
    HooSharper.CodeFixes.RemoveRedundantElseCodeFixProvider>;

namespace HooSharper.Analyzers.Tests;

public sealed class RemoveRedundantElseAnalyzerTests
{
    [Fact]
    public Task RemovesElseAfterReturn()
    {
        const string source = """
            class Example
            {
                int GetValue(bool enabled)
                {
                    if (enabled)
                    {
                        return 1;
                    }
                    {|#0:else|}
                    {
                        return 0;
                    }
                }
            }
            """;
        const string fixedSource = """
            class Example
            {
                int GetValue(bool enabled)
                {
                    if (enabled)
                    {
                        return 1;
                    }
                    return 0;
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(RemoveRedundantElseAnalyzer.DiagnosticId)
            .WithLocation(0)
            .WithMessage("Remove this redundant else");

        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task RemovesElseAfterThrowStatement()
    {
        const string source = """
            using System;

            class Example
            {
                void Run(bool invalid)
                {
                    if (invalid)
                        throw new InvalidOperationException();
                    {|#0:else|}
                        Execute();
                }

                void Execute() { }
            }
            """;
        const string fixedSource = """
            using System;

            class Example
            {
                void Run(bool invalid)
                {
                    if (invalid)
                        throw new InvalidOperationException();
                    Execute();
                }

                void Execute() { }
            }
            """;

        var expected = VerifyCS.Diagnostic(RemoveRedundantElseAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task RemovesElseAfterContinueAndBreak()
    {
        const string source = """
            class Example
            {
                void Run(bool skip, bool stop)
                {
                    while (true)
                    {
                        if (skip)
                            continue;
                        {|#0:else|}
                            Execute();

                        if (stop)
                            break;
                        {|#1:else|}
                            Finish();
                    }
                }

                void Execute() { }
                void Finish() { }
            }
            """;
        const string fixedSource = """
            class Example
            {
                void Run(bool skip, bool stop)
                {
                    while (true)
                    {
                        if (skip)
                            continue;
                        Execute();

                        if (stop)
                            break;
                        Finish();
                    }
                }

                void Execute() { }
                void Finish() { }
            }
            """;

        var expected = new[]
        {
            VerifyCS.Diagnostic(RemoveRedundantElseAnalyzer.DiagnosticId).WithLocation(0),
            VerifyCS.Diagnostic(RemoveRedundantElseAnalyzer.DiagnosticId).WithLocation(1),
        };

        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }


    [Fact]
    public Task PreservesCommentsAroundElseBody()
    {
        const string source = """
            class Example
            {
                void Run(bool done)
                {
                    if (done)
                    {
                        Finish();
                        return;
                    }
                    // explain fallback
                    {|#0:else|} // keep with moved body
                    {
                        // prepare fallback
                        Execute(); // execute fallback
                    } // end fallback
                }

                void Finish() { }
                void Execute() { }
            }
            """;
        const string fixedSource = """
            class Example
            {
                void Run(bool done)
                {
                    if (done)
                    {
                        Finish();
                        return;
                    }
                    // explain fallback
                    // keep with moved body
                    // prepare fallback
                    Execute(); // execute fallback
                               // end fallback
                }

                void Finish() { }
                void Execute() { }
            }
            """;

        var expected = VerifyCS.Diagnostic(RemoveRedundantElseAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task DoesNotReportNonterminatingBranch()
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
                    else
                    {
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
    public Task DoesNotReportElseIf()
    {
        const string source = """
            class Example
            {
                int GetValue(bool first, bool second)
                {
                    if (first)
                        return 1;
                    else if (second)
                        return 2;
                    else
                        return 0;
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task FixAllRemovesEveryRedundantElse()
    {
        const string source = """
            using System;

            class Example
            {
                int First(bool enabled)
                {
                    if (enabled)
                        return 1;
                    {|#0:else|}
                        return 0;
                }

                void Second(bool invalid)
                {
                    if (invalid)
                    {
                        throw new InvalidOperationException();
                    }
                    {|#1:else|}
                    {
                        Execute();
                    }
                }

                void Execute() { }
            }
            """;
        const string fixedSource = """
            using System;

            class Example
            {
                int First(bool enabled)
                {
                    if (enabled)
                        return 1;
                    return 0;
                }

                void Second(bool invalid)
                {
                    if (invalid)
                    {
                        throw new InvalidOperationException();
                    }
                    Execute();
                }

                void Execute() { }
            }
            """;

        var expected = new[]
        {
            VerifyCS.Diagnostic(RemoveRedundantElseAnalyzer.DiagnosticId).WithLocation(0),
            VerifyCS.Diagnostic(RemoveRedundantElseAnalyzer.DiagnosticId).WithLocation(1),
        };

        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }
}
