using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

using HooSharper.CodeFixes;
using VerifyCS = HooSharper.Analyzers.Tests.AnalyzerVerifier<
    HooSharper.Analyzers.UseUsingDeclarationAnalyzer,
    HooSharper.CodeFixes.UseUsingDeclarationCodeFixProvider>;

namespace HooSharper.Analyzers.Tests;

public sealed class UseUsingDeclarationAnalyzerTests
{
    [Fact]
    public Task ConvertsFinalUsingStatementInMethod()
    {
        const string source = """
            using System.IO;

            class Example
            {
                void Run()
                {
                    {|#0:using|} (var stream = new MemoryStream())
                    {
                        stream.WriteByte(1);
                        stream.Flush();
                    }
                }
            }
            """;
        const string fixedSource = """
            using System.IO;

            class Example
            {
                void Run()
                {
                    using var stream = new MemoryStream();
                    stream.WriteByte(1);
                    stream.Flush();
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(UseUsingDeclarationAnalyzer.DiagnosticId)
            .WithLocation(0)
            .WithMessage("Convert this using statement to a using declaration");
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task ConvertsFinalUsingStatementInNestedBlock()
    {
        const string source = """
            using System.IO;

            class Example
            {
                void Run(bool enabled)
                {
                    if (enabled)
                    {
                        {|#0:using|} (MemoryStream stream = new())
                        {
                            stream.WriteByte(1);
                        }
                    }
                }
            }
            """;
        const string fixedSource = """
            using System.IO;

            class Example
            {
                void Run(bool enabled)
                {
                    if (enabled)
                    {
                        using MemoryStream stream = new();
                        stream.WriteByte(1);
                    }
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(UseUsingDeclarationAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task DoesNotReportNonFinalUsingStatement()
    {
        const string source = """
            using System.IO;

            class Example
            {
                void Run()
                {
                    using (var stream = new MemoryStream())
                    {
                        stream.WriteByte(1);
                    }

                    Finish();
                }

                void Finish() { }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task DoesNotReportWhenFlatteningCausesNameCollision()
    {
        const string source = """
            using System.IO;

            class Example
            {
                void Run()
                {
                    {
                        var value = 1;
                        _ = value;
                    }

                    using (var stream = new MemoryStream())
                    {
                        var value = stream.Length;
                        _ = value;
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task DoesNotReportDirectivesOrUnsupportedResources()
    {
        const string source = """
            using System.IO;

            class Example
            {
                void Directive()
                {
                    using (var stream = new MemoryStream())
                    {
            #if DEBUG
                        stream.WriteByte(1);
            #endif
                    }
                }

                void Multiple()
                {
                    using (MemoryStream first = new(), second = new())
                    {
                        first.WriteByte(1);
                    }
                }

                void Empty()
                {
                    using (var stream = new MemoryStream())
                    {
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task DoesNotReportBeforeCSharp8()
    {
        const string source = """
            using System.IO;

            class Example
            {
                void Run()
                {
                    using (var stream = new MemoryStream())
                    {
                        stream.WriteByte(1);
                    }
                }
            }
            """;

        var test = new CSharpCodeFixTest<
            UseUsingDeclarationAnalyzer,
            UseUsingDeclarationCodeFixProvider,
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
                ((CSharpParseOptions)project.ParseOptions!).WithLanguageVersion(LanguageVersion.CSharp7_3));
        });

        return test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public Task PreservesCommentsAroundUsingAndBody()
    {
        const string source = """
            using System.IO;

            class Example
            {
                void Run()
                {
                    // before using
                    {|#0:using|} /* resource */ (var stream = new MemoryStream()) // after resource
                    {
                        // before work
                        stream.WriteByte(1); // work
                    } // after body
                }
            }
            """;
        const string fixedSource = """
            using System.IO;

            class Example
            {
                void Run()
                {
                    // before using
                    /* resource */
                    using var stream = new MemoryStream(); // after resource
                                                           // before work
                    stream.WriteByte(1); // work

                    // after body
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(UseUsingDeclarationAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task PreservesCommentsInsideResourceDeclarationAndInitializer()
    {
        const string source = """
            using System.IO;

            class Example
            {
                void Run()
                {
                    {|#0:using|} (var /* declaration */ stream = /* initializer */ new MemoryStream())
                    {
                        stream.WriteByte(1);
                    }
                }
            }
            """;
        const string fixedSource = """
            using System.IO;

            class Example
            {
                void Run()
                {
                    using var /* declaration */ stream = /* initializer */ new MemoryStream();
                    stream.WriteByte(1);
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(UseUsingDeclarationAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task PreservesLfLineEndings()
    {
        var source = """
            using System.IO;

            class Example
            {
                void Run()
                {
                    {|#0:using|} /* resource */ (var stream = new MemoryStream()) // after resource
                    {
                        // before work
                        stream.WriteByte(1);
                    } // after body
                }
            }
            """.ReplaceLineEndings("\n");
        var fixedSource = """
            using System.IO;

            class Example
            {
                void Run()
                {
                    /* resource */
                    using var stream = new MemoryStream(); // after resource
                                                           // before work
                    stream.WriteByte(1);

                    // after body
                }
            }
            """.ReplaceLineEndings("\n");

        var expected = VerifyCS.Diagnostic(UseUsingDeclarationAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task PreservesCrLfLineEndings()
    {
        var source = """
            using System.IO;

            class Example
            {
                void Run()
                {
                    {|#0:using|} /* resource */ (var stream = new MemoryStream()) // after resource
                    {
                        // before work
                        stream.WriteByte(1);
                    } // after body
                }
            }
            """.ReplaceLineEndings("\r\n");
        var fixedSource = """
            using System.IO;

            class Example
            {
                void Run()
                {
                    /* resource */
                    using var stream = new MemoryStream(); // after resource
                                                           // before work
                    stream.WriteByte(1);

                    // after body
                }
            }
            """.ReplaceLineEndings("\r\n");

        var expected = VerifyCS.Diagnostic(UseUsingDeclarationAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task FixAllConvertsEveryEligibleUsingStatement()
    {
        const string source = """
            using System.IO;

            class Example
            {
                void First()
                {
                    {|#0:using|} (var stream = new MemoryStream())
                    {
                        stream.WriteByte(1);
                    }
                }

                void Second()
                {
                    {
                        {|#1:using|} (var stream = new MemoryStream())
                        {
                            stream.WriteByte(2);
                        }
                    }
                }
            }
            """;
        const string fixedSource = """
            using System.IO;

            class Example
            {
                void First()
                {
                    using var stream = new MemoryStream();
                    stream.WriteByte(1);
                }

                void Second()
                {
                    {
                        using var stream = new MemoryStream();
                        stream.WriteByte(2);
                    }
                }
            }
            """;

        var expected = new[]
        {
            VerifyCS.Diagnostic(UseUsingDeclarationAnalyzer.DiagnosticId).WithLocation(0),
            VerifyCS.Diagnostic(UseUsingDeclarationAnalyzer.DiagnosticId).WithLocation(1),
        };
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }
    [Fact]
    public Task FixAllConvertsNestedTerminalUsingStatements()
    {
        const string source = """
            using System.IO;

            class Example
            {
                void Run()
                {
                    {|#0:using|} (var outer = new MemoryStream())
                    {
                        {|#1:using|} (var inner = new MemoryStream())
                        {
                            inner.WriteByte(1);
                            outer.WriteByte(2);
                        }
                    }
                }
            }
            """;
        const string fixedSource = """
            using System.IO;

            class Example
            {
                void Run()
                {
                    using var outer = new MemoryStream();
                    using var inner = new MemoryStream();
                    inner.WriteByte(1);
                    outer.WriteByte(2);
                }
            }
            """;

        var expected = new[]
        {
            VerifyCS.Diagnostic(UseUsingDeclarationAnalyzer.DiagnosticId).WithLocation(0),
            VerifyCS.Diagnostic(UseUsingDeclarationAnalyzer.DiagnosticId).WithLocation(1),
        };

        var test = new CSharpCodeFixTest<
            UseUsingDeclarationAnalyzer,
            UseUsingDeclarationCodeFixProvider,
            DefaultVerifier>
        {
            ReferenceAssemblies = ReferenceAssemblies.Net.Net100,
            TestCode = source,
            FixedCode = fixedSource,
            BatchFixedCode = fixedSource,
            NumberOfFixAllIterations = 2,
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

}
