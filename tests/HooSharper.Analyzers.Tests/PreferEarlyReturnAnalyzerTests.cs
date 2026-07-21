using HooSharper.CodeFixes;
using VerifyCS = HooSharper.Analyzers.Tests.AnalyzerVerifier<
    HooSharper.Analyzers.PreferEarlyReturnAnalyzer,
    HooSharper.CodeFixes.PreferEarlyReturnCodeFixProvider>;

namespace HooSharper.Analyzers.Tests;

public sealed class PreferEarlyReturnAnalyzerTests
{
    [Fact]
    public Task EmptySourceProducesNoDiagnostics() =>
        VerifyCS.VerifyAnalyzerAsync(string.Empty);

    [Fact]
    public Task ConvertsFinalIfIntoGuardClause()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled)
                {
                    Prepare();
                    {|#0:if|} (enabled)
                    {
                        Execute();
                        Finish();
                    }
                }

                void Prepare() { }
                void Execute() { }
                void Finish() { }
            }
            """;
        const string fixedSource = """
            class Example
            {
                void Run(bool enabled)
                {
                    Prepare();
                    if (!enabled)
                        return;
                    Execute();
                    Finish();
                }

                void Prepare() { }
                void Execute() { }
                void Finish() { }
            }
            """;

        var expected = VerifyCS.Diagnostic(PreferEarlyReturnAnalyzer.DiagnosticId)
            .WithLocation(0)
            .WithMessage("Invert this condition and return early");

        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task SimplifiesNegatedCondition()
    {
        const string source = """
            class Example
            {
                void Run(bool disabled)
                {
                    {|#0:if|} (!disabled)
                    {
                        Execute();
                    }
                }

                void Execute() { }
            }
            """;
        const string fixedSource = """
            class Example
            {
                void Run(bool disabled)
                {
                    if (disabled)
                        return;
                    Execute();
                }

                void Execute() { }
            }
            """;

        var expected = VerifyCS.Diagnostic(PreferEarlyReturnAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task FixAllConvertsEveryEligibleMethod()
    {
        const string source = """
            class Example
            {
                void First(bool enabled)
                {
                    {|#0:if|} (enabled)
                    {
                        Execute();
                    }
                }

                void Second(bool ready)
                {
                    {|#1:if|} (ready)
                    {
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
                void First(bool enabled)
                {
                    if (!enabled)
                        return;
                    Execute();
                }

                void Second(bool ready)
                {
                    if (!ready)
                        return;
                    Finish();
                }

                void Execute() { }
                void Finish() { }
            }
            """;

        var expected = new[]
        {
            VerifyCS.Diagnostic(PreferEarlyReturnAnalyzer.DiagnosticId).WithLocation(0),
            VerifyCS.Diagnostic(PreferEarlyReturnAnalyzer.DiagnosticId).WithLocation(1),
        };

        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }

    [Fact]
    public Task PreservesFloatingPointNaNSemantics()
    {
        const string source = """
            class Example
            {
                void Run(double value)
                {
                    {|#0:if|} (value > 0)
                    {
                        Execute();
                    }
                }

                void Execute() { }
            }
            """;
        const string fixedSource = """
            class Example
            {
                void Run(double value)
                {
                    if (!(value > 0))
                        return;
                    Execute();
                }

                void Execute() { }
            }
            """;

        var expected = VerifyCS.Diagnostic(PreferEarlyReturnAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task DoesNotReportValueReturningMethod()
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

                    return 0;
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }
    [Fact]
    public Task DoesNotReportWhenMovedLocalCollidesWithSiblingNestedLocal()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled)
                {
                    {
                        var value = 1;
                        Use(value);
                    }

                    if (enabled)
                    {
                        var value = 2;
                        Use(value);
                    }
                }

                void Use(int value) { }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task DoesNotReportWhenMovedPatternDesignationCollidesWithSiblingNestedLocal()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled, object input)
                {
                    {
                        var value = 1;
                        Use(value);
                    }

                    if (enabled)
                    {
                        if (input is int value)
                        {
                            Use(value);
                        }
                    }
                }

                void Use(int value) { }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task DoesNotReportWhenMovedOutDesignationCollidesWithSiblingNestedLocal()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled, string input)
                {
                    {
                        var value = 1;
                        Use(value);
                    }

                    if (enabled)
                    {
                        if (int.TryParse(input, out var value))
                        {
                            Use(value);
                        }
                    }
                }

                void Use(int value) { }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task DoesNotReportWhenMovedLocalFunctionCollidesWithSiblingNestedLocalFunction()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled)
                {
                    {
                        void Work() { }
                        Work();
                    }

                    if (enabled)
                    {
                        void Work() { }
                        Work();
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task PreservesClosingBraceAndTrailingComments()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled)
                {
                    {|#0:if|} (enabled)
                    {
                        Execute();
                        // closing brace comment
                    } // trailing if comment
                }

                void Execute() { }
            }
            """;
        const string fixedSource = """
            class Example
            {
                void Run(bool enabled)
                {
                    if (!enabled)
                        return;
                    Execute();
                    // closing brace comment
                    // trailing if comment
                }

                void Execute() { }
            }
            """;

        var expected = VerifyCS.Diagnostic(PreferEarlyReturnAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task DoesNotReportCustomConditionWithTrueAndFalseOperatorsButNoLogicalNot()
    {
        const string source = """
            readonly struct Truthy
            {
                public static bool operator true(Truthy value) => true;
                public static bool operator false(Truthy value) => false;
            }

            class Example
            {
                void Run(Truthy condition)
                {
                    if (condition)
                    {
                        Execute();
                    }
                }

                void Execute() { }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

}
