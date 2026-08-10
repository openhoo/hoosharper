using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

using HooSharper.CodeFixes;
using VerifyCS = HooSharper.Analyzers.Tests.AnalyzerVerifier<
    HooSharper.Analyzers.MergeNestedIfAnalyzer,
    HooSharper.CodeFixes.MergeNestedIfCodeFixProvider>;

namespace HooSharper.Analyzers.Tests;

public sealed class MergeNestedIfAnalyzerTests
{
    [Fact]
    public Task MergesBasicNestedIf()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled, bool ready)
                {
                    {|#0:if|} (enabled)
                    {
                        if (ready)
                        {
                            Execute();
                        }
                    }
                }

                void Execute() { }
            }
            """;
        const string fixedSource = """
            class Example
            {
                void Run(bool enabled, bool ready)
                {
                    if (enabled && ready)
                    {
                        Execute();
                    }
                }

                void Execute() { }
            }
            """;

        var expected = VerifyCS.Diagnostic(MergeNestedIfAnalyzer.DiagnosticId)
            .WithLocation(0)
            .WithMessage("Merge these nested if statements");
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task ParenthesizesLowerPrecedenceExpressions()
    {
        const string source = """
            class Example
            {
                void Run(bool first, bool second, bool third, bool fourth)
                {
                    {|#0:if|} (first || second)
                    {
                        if (third || fourth)
                            Execute();
                    }
                }

                void Execute() { }
            }
            """;
        const string fixedSource = """
            class Example
            {
                void Run(bool first, bool second, bool third, bool fourth)
                {
                    if ((first || second) && (third || fourth))
                        Execute();
                }

                void Execute() { }
            }
            """;

        var expected = VerifyCS.Diagnostic(MergeNestedIfAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task PreservesCommentsFromRemovedStructure()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled, bool ready)
                {
                    {|#0:if|} (enabled) // outer condition
                    {
                        // before inner
                        if (ready) // inner condition
                        {
                            Execute(); // action
                        }
                    }
                }

                void Execute() { }
            }
            """;
        const string fixedSource = """
            class Example
            {
                void Run(bool enabled, bool ready)
                {
                    // outer condition
                    // before inner
                    // inner condition
                    if (enabled && ready)
                    {
                        Execute(); // action
                    }
                }

                void Execute() { }
            }
            """;

        var expected = VerifyCS.Diagnostic(MergeNestedIfAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task DoesNotReportUserDefinedBooleanConditions()
    {
        const string source = """
            class Condition
            {
                public static bool operator true(Condition value) => true;
                public static bool operator false(Condition value) => false;
            }

            class Example
            {
                void Run(bool enabled, Condition custom)
                {
                    if (enabled)
                    {
                        if (custom)
                            Execute();
                    }

                    if (custom)
                    {
                        if (enabled)
                            Execute();
                    }
                }

                void Execute() { }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task DoesNotReportWhenThirdLevelConditionIsUserDefinedBoolean()
    {
        const string source = """
            class Condition
            {
                public static bool operator true(Condition value) => true;
                public static bool operator false(Condition value) => false;
            }

            class Example
            {
                void Run(bool first, bool second, Condition custom)
                {
                    if (first)
                    {
                        if (second)
                        {
                            if (custom)
                                Execute();
                        }
                    }
                }

                void Execute() { }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task KeepsTrailingClosingBraceCommentInPlace()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled, bool ready)
                {
                    {|#0:if|} (enabled)
                    {
                        if (ready)
                        {
                            Execute();
                        }
                    } // keep here
                }

                void Execute() { }
            }
            """;
        const string fixedSource = """
            class Example
            {
                void Run(bool enabled, bool ready)
                {
                    if (enabled && ready)
                    {
                        Execute();
                    } // keep here
                }

                void Execute() { }
            }
            """;

        var expected = VerifyCS.Diagnostic(MergeNestedIfAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task DoesNotReportElseOrUnbracedOuterOrMultipleStatements()
    {
        const string source = """
            class Example
            {
                void Run(bool first, bool second)
                {
                    if (first)
                    {
                        if (second)
                            Execute();
                    }
                    else
                    {
                        Finish();
                    }

                    if (first)
                        if (second)
                            Execute();

                    if (first)
                    {
                        Prepare();
                        if (second)
                            Execute();
                    }

                    if (first)
                    {
                        if (second)
                            Execute();
                        else
                            Finish();
                    }
                }

                void Prepare() { }
                void Execute() { }
                void Finish() { }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task DoesNotReportDirectives()
    {
        const string source = """
            class Example
            {
                void Run(bool first, bool second)
                {
                    if (first)
                    {
            #if DEBUG
                        if (second)
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
    public Task DoesNotReportWhenConditionVariableWouldCollideWithLaterDeclaration()
    {
        const string source = """
            class Example
            {
                void Run(object value, bool enabled)
                {
                    if (enabled)
                    {
                        if (value is string text)
                            Use(text);
                    }

                    { string text = "later"; Use(text); }

                    if (enabled)
                    {
                        if (int.TryParse(value.ToString(), out var number))
                            Use(number);
                    }

                    { int number = 0; Use(number); }
                }

                void Use(object value) { }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task FixAllMergesEveryEligiblePair()
    {
        const string source = """
            class Example
            {
                void Run(bool first, bool second, bool third, bool fourth)
                {
                    {|#0:if|} (first)
                    {
                        if (second)
                            Execute();
                    }

                    {|#1:if|} (third || fourth)
                    {
                        if (first)
                        {
                            Finish();
                        }
                    }
                }

                void Execute() { }
                void Finish() { }
            }
            """;
        const string fixedSource = """
            class Example
            {
                void Run(bool first, bool second, bool third, bool fourth)
                {
                    if (first && second)
                        Execute();

                    if ((third || fourth) && first)
                    {
                        Finish();
                    }
                }

                void Execute() { }
                void Finish() { }
            }
            """;

        var expected = new[]
        {
            VerifyCS.Diagnostic(MergeNestedIfAnalyzer.DiagnosticId).WithLocation(0),
            VerifyCS.Diagnostic(MergeNestedIfAnalyzer.DiagnosticId).WithLocation(1),
        };
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }
    [Fact]
    public Task FixAllMergesTripleNestedIfStatements()
    {
        const string source = """
            class Example
            {
                void Run(bool first, bool second, bool third)
                {
                    {|#0:if|} (first)
                    {
                        if (second)
                        {
                            if (third)
                                Execute();
                        }
                    }
                }

                void Execute() { }
            }
            """;
        const string fixedSource = """
            class Example
            {
                void Run(bool first, bool second, bool third)
                {
                    if (first && second && third)
                        Execute();
                }

                void Execute() { }
            }
            """;

        var expected = new[]
        {
            VerifyCS.Diagnostic(MergeNestedIfAnalyzer.DiagnosticId).WithLocation(0),
        };

        var test = new CSharpCodeFixTest<
            MergeNestedIfAnalyzer,
            MergeNestedIfCodeFixProvider,
            DefaultVerifier>
        {
            ReferenceAssemblies = ReferenceAssemblies.Net.Net100,
            TestCode = source,
            FixedCode = fixedSource,
            BatchFixedCode = fixedSource,
            NumberOfFixAllIterations = 1,
        };
        test.ExpectedDiagnostics.AddRange(expected);
        test.SolutionTransforms.Add((solution, projectId) =>
        {
            var project = solution.GetProject(projectId)!;
            return solution.WithProjectParseOptions(
                projectId,
                ((CSharpParseOptions)project.ParseOptions!).WithLanguageVersion(LanguageVersion.Latest));
        });

        return test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public Task FixAllMergesTripleNestedIfIntoParseableStatement()
    {
        const string source = """
            class Example
            {
                void Run(bool first, bool second, bool third)
                {
                    {|#0:if|} (first)
                    {
                        if (second)
                        {
                            if (third)
                            {
                                Execute();
                            }
                        }
                    }
                }

                void Execute() { }
            }
            """;
        const string fixedSource = """
            class Example
            {
                void Run(bool first, bool second, bool third)
                {
                    if (first && second && third)
                    {
                        Execute();
                    }
                }

                void Execute() { }
            }
            """;

        var expected = new[]
        {
            VerifyCS.Diagnostic(MergeNestedIfAnalyzer.DiagnosticId).WithLocation(0),
        };
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }

    [Fact]
    public Task DoesNotReportWhenConditionVariableWouldCollideWithEarlierDeclaration()
    {
        const string source = """
            class Example
            {
                void Run(object value, bool enabled)
                {
                    { string text = "earlier"; Use(text); }

                    if (enabled)
                    {
                        if (value is string text)
                            Use(text);
                    }
                }

                void Use(object value) { }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }
}
