using HooSharper.CodeFixes;
using VerifyCS = HooSharper.Analyzers.Tests.AnalyzerVerifier<
    HooSharper.Analyzers.SimplifyBooleanReturnAnalyzer,
    HooSharper.CodeFixes.SimplifyBooleanReturnCodeFixProvider>;

namespace HooSharper.Analyzers.Tests;

public sealed class SimplifyBooleanReturnAnalyzerTests
{
    [Theory]
    [InlineData("true", "false", "value")]
    [InlineData("false", "true", "!value")]
    public Task SimplifiesBothPolarities(string branchValue, string nextValue, string replacement)
    {
        var source = $$"""
            class Example
            {
                bool Run(bool value)
                {
                    {|#0:if|} (value)
                        return {{branchValue}};
                    return {{nextValue}};
                }
            }
            """;
        var fixedSource = $$"""
            class Example
            {
                bool Run(bool value)
                {
                    return {{replacement}};
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(SimplifyBooleanReturnAnalyzer.DiagnosticId)
            .WithLocation(0)
            .WithMessage("Simplify these boolean returns");
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task SimplifiesBracedBranchAndPreservesComments()
    {
        const string source = """
            class Example
            {
                bool Run(bool left, bool right)
                {
                    {|#0:if|} (left && right)
                    {
                        // branch result
                        return false; // false when both
                    }
                    // fallback result
                    return true;
                }
            }
            """;
        const string fixedSource = """
            class Example
            {
                bool Run(bool left, bool right)
                {
                    return !(left && right);
                    // branch result
                    // false when both
                    // fallback result

                }
            }
            """;

        return VerifyCS.VerifyCodeFixAsync(
            source,
            VerifyCS.Diagnostic(SimplifyBooleanReturnAnalyzer.DiagnosticId).WithLocation(0),
            fixedSource);
    }

    [Fact]
    public Task RemovesExistingNegationForInversePolarity()
    {
        const string source = """
            class Example
            {
                bool Run(bool value)
                {
                    {|#0:if|} (!value)
                        return false;
                    return true;
                }
            }
            """;
        const string fixedSource = """
            class Example
            {
                bool Run(bool value)
                {
                    return value;
                }
            }
            """;

        return VerifyCS.VerifyCodeFixAsync(
            source,
            VerifyCS.Diagnostic(SimplifyBooleanReturnAnalyzer.DiagnosticId).WithLocation(0),
            fixedSource);
    }

    [Fact]
    public Task DoesNotReportNullableDynamicNonAdjacentElseOrDirectives()
    {
        const string source = """
            class Example
            {
                bool? Nullable(bool value)
                {
                    if (value)
                        return true;
                    return false;
                }

                bool Dynamic(dynamic value)
                {
                    if (value)
                        return true;
                    return false;
                }

                bool NonAdjacent(bool value)
                {
                    if (value)
                        return true;
                    System.Console.WriteLine();
                    return false;
                }

                bool WithElse(bool value)
                {
                    if (value)
                        return true;
                    else
                        System.Console.WriteLine();
                    return false;
                }

                bool WithDirectives(bool value)
                {
                    if (value)
                    {
            #if DEBUG
                        return true;
            #else
                        return true;
            #endif
                    }
                    return false;
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task DoesNotReportNonBooleanReturnContext()
    {
        const string source = """
            class Example
            {
                object Run(bool value)
                {
                    if (value)
                        return true;
                    return false;
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task FixAllSimplifiesEveryBooleanReturn()
    {
        const string source = """
            class Example
            {
                bool First(bool value)
                {
                    {|#0:if|} (value)
                        return true;
                    return false;
                }

                bool Second(bool left, bool right)
                {
                    {|#1:if|} (left || right)
                    {
                        return false;
                    }
                    return true;
                }
            }
            """;
        const string fixedSource = """
            class Example
            {
                bool First(bool value)
                {
                    return value;
                }

                bool Second(bool left, bool right)
                {
                    return !(left || right);
                }
            }
            """;

        var expected = new[]
        {
            VerifyCS.Diagnostic(SimplifyBooleanReturnAnalyzer.DiagnosticId).WithLocation(0),
            VerifyCS.Diagnostic(SimplifyBooleanReturnAnalyzer.DiagnosticId).WithLocation(1),
        };
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }
}
