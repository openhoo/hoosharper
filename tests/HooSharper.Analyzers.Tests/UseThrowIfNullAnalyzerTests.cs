using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

using HooSharper.CodeFixes;
using VerifyCS = HooSharper.Analyzers.Tests.AnalyzerVerifier<
    HooSharper.Analyzers.UseThrowIfNullAnalyzer,
    HooSharper.CodeFixes.UseThrowIfNullCodeFixProvider>;

namespace HooSharper.Analyzers.Tests;

public sealed class UseThrowIfNullAnalyzerTests
{
    [Fact]
    public Task ConvertsIsNullBlockGuard()
    {
        const string source = """
            using System;

            class Example
            {
                void Run(object argument)
                {
                    if (argument {|#0:is|} null)
                    {
                        throw new ArgumentNullException(nameof(argument));
                    }
                }
            }
            """;
        const string fixedSource = """
            using System;

            class Example
            {
                void Run(object argument)
                {
                    ArgumentNullException.ThrowIfNull(argument);
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(UseThrowIfNullAnalyzer.DiagnosticId)
            .WithLocation(0)
            .WithMessage("Use ArgumentNullException.ThrowIfNull");
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task ConvertsBuiltInEqualitySingleStatementGuard()
    {
        const string source = """
            using System;

            class Example
            {
                void Run(object argument)
                {
                    if (argument {|#0:==|} null)
                        throw new ArgumentNullException(nameof(argument));
                }
            }
            """;
        const string fixedSource = """
            using System;

            class Example
            {
                void Run(object argument)
                {
                    ArgumentNullException.ThrowIfNull(argument);
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(UseThrowIfNullAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task ConvertsNullOnLeftAndPreservesQualification()
    {
        const string source = """
            class Example
            {
                void Run(object argument)
                {
                    if (null {|#0:==|} argument)
                        throw new System.ArgumentNullException(nameof(argument));
                }
            }
            """;
        const string fixedSource = """
            class Example
            {
                void Run(object argument)
                {
                    System.ArgumentNullException.ThrowIfNull(argument);
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(UseThrowIfNullAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task PreservesComments()
    {
        const string source = """
            using System;

            class Example
            {
                void Run(object argument)
                {
                    // Validate public input.
                    if (argument {|#0:is|} null)
                    {
                        // Keep the parameter name stable.
                        throw new ArgumentNullException(nameof(argument));
                    }
                }
            }
            """;
        const string fixedSource = """
            using System;

            class Example
            {
                void Run(object argument)
                {
                    // Validate public input.
                    // Keep the parameter name stable.
                    ArgumentNullException.ThrowIfNull(argument);
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(UseThrowIfNullAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task FixAllConvertsEveryGuard()
    {
        const string source = """
            using System;

            class Example
            {
                void Run(object first, string second)
                {
                    if (first {|#0:is|} null)
                        throw new ArgumentNullException(nameof(first));

                    if (second {|#1:==|} null)
                    {
                        throw new ArgumentNullException(nameof(second));
                    }
                }
            }
            """;
        const string fixedSource = """
            using System;

            class Example
            {
                void Run(object first, string second)
                {
                    ArgumentNullException.ThrowIfNull(first);

                    ArgumentNullException.ThrowIfNull(second);
                }
            }
            """;

        var expected = new[]
        {
            VerifyCS.Diagnostic(UseThrowIfNullAnalyzer.DiagnosticId).WithLocation(0),
            VerifyCS.Diagnostic(UseThrowIfNullAnalyzer.DiagnosticId).WithLocation(1),
        };
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }

    [Fact]
    public Task DoesNotReportWrongNameOf()
    {
        const string source = """
            using System;

            class Example
            {
                void Run(object argument, object other)
                {
                    if (argument is null)
                        throw new ArgumentNullException(nameof(other));
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task DoesNotReportCustomMessage()
    {
        const string source = """
            using System;

            class Example
            {
                void Run(object argument)
                {
                    if (argument is null)
                        throw new ArgumentNullException(nameof(argument), "Required");
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task DoesNotReportCustomException()
    {
        const string source = """
            using System;

            class CustomArgumentNullException : Exception
            {
                public CustomArgumentNullException(string name) { }
            }

            class Example
            {
                void Run(object argument)
                {
                    if (argument is null)
                        throw new CustomArgumentNullException(nameof(argument));
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task DoesNotReportOverloadedEquality()
    {
        const string source = """
            using System;

            class Value
            {
                public static bool operator ==(Value? left, Value? right) => true;
                public static bool operator !=(Value? left, Value? right) => false;
                public override bool Equals(object? obj) => false;
                public override int GetHashCode() => 0;
            }

            class Example
            {
                void Run(Value argument)
                {
                    if (argument == null)
                        throw new ArgumentNullException(nameof(argument));
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task DoesNotReportWhenThrowIfNullIsUnavailable()
    {
        const string source = """
            using System;

            class Example
            {
                void Run(object argument)
                {
                    if (argument is null)
                        throw new ArgumentNullException(nameof(argument));
                }
            }
            """;

        var test = new CSharpCodeFixTest<UseThrowIfNullAnalyzer, UseThrowIfNullCodeFixProvider, DefaultVerifier>
        {
            ReferenceAssemblies = ReferenceAssemblies.NetStandard.NetStandard20,
            TestCode = source,
        };
        return test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public Task DoesNotReportUserMethodNamedNameof()
    {
        const string source = """
            using System;

            class Example
            {
                string nameof(object value) => "other";

                void Run(object argument)
                {
                    if (argument is null)
                        throw new ArgumentNullException(nameof(argument));
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task DoesNotReportElseBranchOrDirectives()
    {
        const string source = """
            using System;

            class Example
            {
                void WithElse(object argument)
                {
                    if (argument is null)
                        throw new ArgumentNullException(nameof(argument));
                    else
                        Console.WriteLine(argument);
                }

                void WithDirective(object argument)
                {
                    if (argument is null)
                    {
            #if DEBUG
                        throw new ArgumentNullException(nameof(argument));
            #endif
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }
}
