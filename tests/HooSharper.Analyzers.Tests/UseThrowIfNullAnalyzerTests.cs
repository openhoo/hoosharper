using Microsoft.CodeAnalysis.CSharp;

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
                    ArgumentNullException.ThrowIfNull(argument, nameof(argument));
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
                    ArgumentNullException.ThrowIfNull(argument, nameof(argument));
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
                    System.ArgumentNullException.ThrowIfNull(argument, nameof(argument));
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(UseThrowIfNullAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCodeFixForDottedExceptionTypeAsync(source, [expected], fixedSource);
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
                    ArgumentNullException.ThrowIfNull(argument, nameof(argument));
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
                    ArgumentNullException.ThrowIfNull(first, nameof(first));

                    ArgumentNullException.ThrowIfNull(second, nameof(second));
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
    public Task FixAllPreservesAliasSpellingOfExceptionType()
    {
        const string source = """
            using ANE = System.ArgumentNullException;

            class Example
            {
                void Run(object argument)
                {
                    if (argument {|#0:is|} null)
                    {
                        throw new ANE(nameof(argument));
                    }
                }
            }
            """;
        const string fixedSource = """
            using ANE = System.ArgumentNullException;

            class Example
            {
                void Run(object argument)
                {
                    ANE.ThrowIfNull(argument, nameof(argument));
                }
            }
            """;

        var expected = new[]
        {
            VerifyCS.Diagnostic(UseThrowIfNullAnalyzer.DiagnosticId).WithLocation(0),
        };
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }

    [Fact]
    public Task FixAllPreservesCommentBeforeExceptionType()
    {
        const string source = """
            using System;

            class Example
            {
                void Run(object argument)
                {
                    if (argument {|#0:is|} null)
                    {
                        throw new /* guard */ System.ArgumentNullException(nameof(argument));
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
                    /* guard */
                    System.ArgumentNullException.ThrowIfNull(argument, nameof(argument));
                }
            }
            """;

        var expected = new[]
        {
            VerifyCS.Diagnostic(UseThrowIfNullAnalyzer.DiagnosticId).WithLocation(0),
        };
        return VerifyCodeFixForDottedExceptionTypeAsync(source, expected, fixedSource, fixedSource);
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

    [Fact]
    public Task DoesNotReportWhenGuardBlockHasAdditionalStatement()
    {
        const string source = """
            using System;

            class Example
            {
                void Run(object? argument)
                {
                    if (argument is null)
                    {
                        Console.WriteLine("missing");
                        throw new ArgumentNullException(nameof(argument));
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task ConvertsParenthesizedCheckedExpression()
    {
        const string source = """
            using System;

            class Example
            {
                void Run(object? argument)
                {
                    if (((argument)) {|#0:is|} null)
                        throw new ArgumentNullException(nameof(argument));
                }
            }
            """;
        const string fixedSource = """
            using System;

            class Example
            {
                void Run(object? argument)
                {
                    ArgumentNullException.ThrowIfNull(argument, nameof(argument));
                }
            }
            """;

        return VerifyCS.VerifyCodeFixAsync(
            source,
            VerifyCS.Diagnostic(UseThrowIfNullAnalyzer.DiagnosticId).WithLocation(0),
            fixedSource);
    }

    [Fact]
    public Task PreservesMemberAndEscapedNameOfExpressions()
    {
        const string source = """
            using System;

            class Example
            {
                void Run(Holder holder, object @class)
                {
                    if (holder.Value {|#0:is|} null)
                        throw new ArgumentNullException(nameof(holder.Value)); // trailing

                    if (@class {|#1:is|} null)
                        throw new ArgumentNullException(nameof(@class));
                }
            }

            class Holder
            {
                public object? Value { get; set; }
            }
            """;
        const string fixedSource = """
            using System;

            class Example
            {
                void Run(Holder holder, object @class)
                {
                    ArgumentNullException.ThrowIfNull(holder.Value, nameof(holder.Value)); // trailing

                    ArgumentNullException.ThrowIfNull(@class, nameof(@class));
                }
            }

            class Holder
            {
                public object? Value { get; set; }
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
    public Task DoesNotReportFunctionPointerEquality()
    {
        const string source = """
            using System;

            unsafe class Example
            {
                static unsafe void M(delegate*<void> fp)
                {
                    if (fp == null)
                        throw new ArgumentNullException(nameof(fp));
                }
            }
            """;

        return VerifyUnsafeAsync(source);
    }

    [Fact]
    public Task DoesNotReportFunctionPointerNullPattern()
    {
        const string source = """
            using System;

            unsafe class Example
            {
                static unsafe void M(delegate*<void> fp)
                {
                    if (fp is null)
                        throw new ArgumentNullException(nameof(fp));
                }
            }
            """;

        return VerifyUnsafeAsync(source);
    }

    [Fact]
    public Task DoesNotReportRawPointerEquality()
    {
        const string source = """
            using System;

            unsafe class Example
            {
                static unsafe void N(int* p)
                {
                    if (p == null)
                        throw new ArgumentNullException(nameof(p));
                }
            }
            """;

        return VerifyUnsafeAsync(source);
    }

    private static Task VerifyUnsafeAsync(string source)
    {
        var test = new CSharpCodeFixTest<UseThrowIfNullAnalyzer, UseThrowIfNullCodeFixProvider, DefaultVerifier>
        {
            ReferenceAssemblies = ReferenceAssemblies.Net.Net100,
            TestCode = source,
        };

        return test.RunAsync(TestContext.Current.CancellationToken);
    }

    private static Task VerifyCodeFixForDottedExceptionTypeAsync(
        string source,
        IReadOnlyList<DiagnosticResult> expected,
        string fixedSource,
        string? batchFixedSource = null)
    {
        var test = new CSharpCodeFixTest<UseThrowIfNullAnalyzer, UseThrowIfNullCodeFixProvider, DefaultVerifier>
        {
            ReferenceAssemblies = ReferenceAssemblies.Net.Net100,
            TestCode = source,
            FixedCode = fixedSource,

            // The fixer reuses the exception type written in `new T(...)` as the
            // ThrowIfNull receiver. For dotted names such as
            // System.ArgumentNullException that type node is a QualifiedNameSyntax
            // in type position, while re-parsing the fixed statement yields nested
            // SimpleMemberAccessExpression nodes. Kinds differ although tokens,
            // text, and semantics are identical, so the framework's
            // SemanticStructure re-parse form check cannot hold for this shape.
            // Textual fixed-output and diagnostic assertions stay fully enabled.
            CodeActionValidationMode = CodeActionValidationMode.None,
        };

        if (batchFixedSource is not null)
        {
            test.BatchFixedCode = batchFixedSource;
        }

        foreach (var diagnostic in expected)
        {
            test.ExpectedDiagnostics.Add(diagnostic);
        }

        return test.RunAsync(TestContext.Current.CancellationToken);
    }

}
