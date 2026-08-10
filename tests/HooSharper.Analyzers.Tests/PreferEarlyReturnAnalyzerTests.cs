using HooSharper.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
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
    [Fact]
    public Task RejectsDirectiveContainingFinalIf()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled)
                {
                    if (enabled)
                    {
            #if DEBUG
                        Execute();
            #endif
                    }
                }

                void Execute() { }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task PreservesOverloadedEqualitySemantics()
    {
        const string source = """
            readonly struct Value
            {
                public static bool operator ==(Value left, Value right) => true;
                public static bool operator !=(Value left, Value right) => false;
                public override bool Equals(object? obj) => obj is Value;
                public override int GetHashCode() => 0;
            }

            class Example
            {
                void Run(Value left, Value right)
                {
                    {|#0:if|} (left == right)
                    {
                        Execute();
                    }
                }

                void Execute() { }
            }
            """;
        const string fixedSource = """
            readonly struct Value
            {
                public static bool operator ==(Value left, Value right) => true;
                public static bool operator !=(Value left, Value right) => false;
                public override bool Equals(object? obj) => obj is Value;
                public override int GetHashCode() => 0;
            }

            class Example
            {
                void Run(Value left, Value right)
                {
                    if (!(left == right))
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
    public Task PreservesOverloadedLogicalNotSemantics()
    {
        const string source = """
            readonly struct Value
            {
                public static bool operator !(Value value) => true;
            }

            class Example
            {
                void Run(Value value)
                {
                    {|#0:if|} (!value)
                    {
                        Execute();
                    }
                }

                void Execute() { }
            }
            """;
        const string fixedSource = """
            readonly struct Value
            {
                public static bool operator !(Value value) => true;
            }

            class Example
            {
                void Run(Value value)
                {
                    if (!(!value))
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
    public Task PreservesConditionOperatorComments()
    {
        const string source = """
            class Example
            {
                void RunLogical(bool enabled)
                {
                    {|#0:if|} (! /* logical */ enabled)
                    {
                        Execute();
                    }
                }

                void RunEquality(bool enabled, bool other)
                {
                    {|#1:if|} (enabled == /* equality */ other)
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
                void RunLogical(bool enabled)
                {
                    if (!(! /* logical */ enabled))
                        return;
                    Execute();
                }

                void RunEquality(bool enabled, bool other)
                {
                    if (enabled != /* equality */ other)
                        return;
                    Execute();
                }

                void Execute() { }
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
    public Task ReportsDirectLabelWithoutSiblingCollision()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled)
                {
                    {|#0:if|} (enabled)
                    {
                    Target:
                        Execute();
                    }
                }

                void Execute() { }
            }
            """;

        var expected = VerifyCS.Diagnostic(PreferEarlyReturnAnalyzer.DiagnosticId)
            .WithLocation(0)
            .WithMessage("Invert this condition and return early");
        return VerifyCS.VerifyAnalyzerAsync(source, expected);
    }

    [Fact]
    public Task ConvertsNestedDesignationWithoutBindingErrors()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled, object item)
                {
                    {|#0:if|} (enabled)
                    {
                        if (item is int value)
                        {
                            Use(value);
                        }
                    }
                }

                void Use(int value) { }
            }
            """;
        const string fixedSource = """
            class Example
            {
                void Run(bool enabled, object item)
                {
                    if (!enabled)
                        return;
                    if (!(item is int value))
                        return;
                    Use(value);
                }

                void Use(int value) { }
            }
            """;

        var expected = VerifyCS.Diagnostic(PreferEarlyReturnAnalyzer.DiagnosticId)
            .WithLocation(0)
            .WithMessage("Invert this condition and return early");
        var test = new CSharpCodeFixTest<
            PreferEarlyReturnAnalyzer,
            PreferEarlyReturnCodeFixProvider,
            DefaultVerifier>
        {
            ReferenceAssemblies = ReferenceAssemblies.Net.Net100,
            TestCode = source,
            FixedCode = fixedSource,
            BatchFixedCode = fixedSource,
            NumberOfIncrementalIterations = 2,
            NumberOfFixAllIterations = 2,
        };
        test.ExpectedDiagnostics.Add(expected);
        return test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public Task DoesNotReportWhenMovedLocalCollidesWithEarlierForeachOrCatch()
    {
        const string source = """
            class Example
            {
                void Foreach(bool enabled, int[] values)
                {
                    foreach (var value in values)
                    {
                        Use(value);
                    }

                    if (enabled)
                    {
                        int value = 0;
                        Use(value);
                    }
                }

                void Catch(bool enabled)
                {
                    try
                    {
                        Execute();
                    }
                    catch (System.Exception error)
                    {
                        Use(error);
                    }

                    if (enabled)
                    {
                        System.Exception error = new();
                        Use(error);
                    }
                }

                void Execute() { }
                void Use(object value) { }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }
    [Fact]
    public Task PreservesOpeningBraceComment()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled)
                {
                    {|#0:if|} (enabled)
                    { // opening body comment
                        Execute();
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
                    if (!enabled)
                        return;
                    // opening body comment
                    Execute();
                }

                void Execute() { }
            }
            """;

        var expected = VerifyCS.Diagnostic(PreferEarlyReturnAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

}
