using VerifyBoolean = HooSharper.Analyzers.Tests.AnalyzerVerifier<
    HooSharper.Analyzers.SimplifyBooleanComparisonAnalyzer,
    HooSharper.CodeFixes.SimplifyBooleanComparisonCodeFixProvider>;
using VerifyBraces = HooSharper.Analyzers.Tests.AnalyzerVerifier<
    HooSharper.Analyzers.OmitBracesForSingleLineIfAnalyzer,
    HooSharper.CodeFixes.OmitBracesForSingleLineIfCodeFixProvider>;
using VerifyElse = HooSharper.Analyzers.Tests.AnalyzerVerifier<
    HooSharper.Analyzers.RemoveRedundantElseAnalyzer,
    HooSharper.CodeFixes.RemoveRedundantElseCodeFixProvider>;
using VerifyChain = HooSharper.Analyzers.Tests.AnalyzerVerifier<
    HooSharper.Analyzers.WrapFluentChainAnalyzer,
    HooSharper.CodeFixes.WrapFluentChainCodeFixProvider>;
using VerifyPattern = HooSharper.Analyzers.Tests.AnalyzerVerifier<
    HooSharper.Analyzers.UseNotPatternAnalyzer,
    HooSharper.CodeFixes.UseNotPatternCodeFixProvider>;
using VerifyReturn = HooSharper.Analyzers.Tests.AnalyzerVerifier<
    HooSharper.Analyzers.SimplifyBooleanReturnAnalyzer,
    HooSharper.CodeFixes.SimplifyBooleanReturnCodeFixProvider>;

namespace HooSharper.Analyzers.Tests;

public sealed class CallerArgumentExpressionTests
{
    private const string CaptureApi = """
        using System;
        using System.Runtime.CompilerServices;
        class Capture : System.Collections.IEnumerable
        {
            public Capture() { }
            public System.Collections.IEnumerator GetEnumerator() => throw new NotImplementedException();
            public void Add(bool value, [CallerArgumentExpression("value")] string text = "") { }
            public Capture(bool value, [CallerArgumentExpression("value")] string text = "") { }
            public bool this[bool value, [CallerArgumentExpression("value")] string text = ""] => value;
            public static bool Check(bool value, [CallerArgumentExpression("value")] string text = "", bool other = false) => value;
            public static void Callback(Func<bool> action, [CallerArgumentExpression("action")] string text = "") { }
        }
        static class Extensions
        {
            public static bool CheckReceiver(this bool value, [CallerArgumentExpression("value")] string text = "") => value;
        }

        """;

    [Theory]
    [InlineData("Capture.Check(value: flag == true)")]
    [InlineData("Capture.Check(flag && (flag == true))")]
    [InlineData("new Capture(flag == true)")]
    [InlineData("new Capture { flag == true }")]
    [InlineData("capture[flag == true]")]
    [InlineData("(flag == true).CheckReceiver()")]
    [InlineData("Capture.Callback(() => flag == true)")]
    public Task PreservesCapturedText(string expression) =>
        VerifyBoolean.VerifyAnalyzerAsync(CaptureApi +
            "class Test { void Run(Capture capture, bool flag) { " +
            (expression.StartsWith("capture[") ? "_ = " : "") + expression + "; } }");

    [Theory]
    [InlineData("Capture.Check(flag {|#0:==|} true, text: \"custom\")", "Capture.Check(flag, text: \"custom\")")]
    [InlineData("Capture.Check(true, other: flag {|#0:==|} true)", "Capture.Check(true, other: flag)")]
    [InlineData("Capture.Check(flag == true) {|#0:==|} true", "Capture.Check(flag == true)")]
    public Task RewritesOnlyUnobservedText(string before, string after)
    {
        const string prefix = CaptureApi + "class Test { bool Run(bool flag) => ";
        return VerifyBoolean.VerifyCodeFixAsync(prefix + before + "; }",
            VerifyBoolean.Diagnostic(SimplifyBooleanComparisonAnalyzer.DiagnosticId).WithLocation(0),
            prefix + after + "; }");
    }

    [Fact]
    public Task TypePatternMustPreserveCaptureInsideTheCopiedInitializer() =>
        AnalyzerVerifier<UseTypePatternAnalyzer, HooSharper.CodeFixes.UseTypePatternCodeFixProvider>.VerifyAnalyzerAsync(
            CaptureApi + """
            class Test
            {
                void Run(bool ready)
                {
                    var text = (object)Capture.Check(ready    ==    true) as string;
                    if (text != null) Console.WriteLine(text);
                }
            }
            """);

    [Fact]
    public Task RemovingBracesMustNotNormalizeCapturedWhitespace() =>
        VerifyBraces.VerifyAnalyzerAsync(CaptureApi + """
            class Test
            {
                void Run(bool ready)
                {
                    if (ready)
                    {
                        Capture.Check(ready    ==      true);
                    }
                }
            }
            """);

    [Fact]
    public Task RemovingElsePreservesUnrelatedSiblingCapture() =>
        VerifyElse.VerifyCodeFixAsync(CaptureApi + """
            class Test
            {
                bool Run(bool ready)
                {
                    Capture.Check(ready    ==      true);
                    if (ready) return true;
                    {|#0:else|} return false;
                }
            }
            """, VerifyElse.Diagnostic(RemoveRedundantElseAnalyzer.DiagnosticId).WithLocation(0), CaptureApi + """
            class Test
            {
                bool Run(bool ready)
                {
                    Capture.Check(ready    ==      true);
                    if (ready) return true;
                    return false;
                }
            }
            """);

    [Fact]
    public Task PreservesCapturedPatternWhileRewritingOuterPattern()
    {
        const string prefix = CaptureApi + "class Test { bool Run(object value) => ";
        return VerifyPattern.VerifyCodeFixAsync(prefix + "{|#0:!|}(Capture.Check(!(value is string)) is true); }",
            VerifyPattern.Diagnostic(UseNotPatternAnalyzer.DiagnosticId).WithLocation(0),
            prefix + "Capture.Check(!(value is string)) is not true; }");
    }

    [Fact]
    public Task PreservesReceiverTextWhenWrappingAFluentChain() =>
        VerifyChain.VerifyAnalyzerAsync("""
            using System.Runtime.CompilerServices;
            static class Test
            {
                static string Capture(this string value, [CallerArgumentExpression("value")] string text = "") => text;
                static string Run(string input) => input.Trim().Trim('a').Trim('b').Trim('c').Trim('d').Trim('e').Trim('f').Trim('g').Trim('h').Capture().Trim();
            }
            """);

    [Fact]
    public Task PreservesCapturedStatementLambda() =>
        VerifyReturn.VerifyAnalyzerAsync(CaptureApi + """
            class Test
            {
                void Run(bool flag) => Capture.Callback(() => { if (flag) return true; return false; });
            }
            """);

    [Fact]
    public Task PreservesCapturedInterpolationHole() =>
        VerifyBoolean.VerifyAnalyzerAsync("""
            using System.Runtime.CompilerServices;
            [InterpolatedStringHandler]
            public ref struct Handler
            {
                public Handler(int literalLength, int formattedCount) { }
                public void AppendFormatted(bool value, [CallerArgumentExpression("value")] string text = "") { }
            }
            class C
            {
                static void Log(Handler handler) { }
                static void Run(bool flag) => Log($"{flag == true}");
            }
            """);

    [Theory]
    [InlineData("Capture(flags: flag == true)")]
    [InlineData("Capture(other: flag == true, flags: false)")]
    [InlineData("Capture(flags: true, other: flag == false)")]
    [InlineData("Capture(flags: flag   ==   true, other: false)")]
    public Task PreservesWholeInvocationCapturedForExpandedParams(string invocation) =>
        VerifyBoolean.VerifyAnalyzerAsync($$"""
            using System.Runtime.CompilerServices;
            class C
            {
                static string Capture(bool other = false, [CallerArgumentExpression("flags")] string text = "", params bool[] flags) => text;
                static string Run(bool flag) => {{invocation}};
            }
            """);

    [Fact]
    public Task CanRewriteOtherArgumentWhenCapturedParamsAreEmpty()
    {
        const string source = """
            using System.Runtime.CompilerServices;
            class C
            {
                static string Capture(bool other, [CallerArgumentExpression("flags")] string text = "", params bool[] flags) => text;
                static string Run(bool flag) => Capture(flag {|#0:==|} true);
            }
            """;
        return VerifyBoolean.VerifyCodeFixAsync(source,
            VerifyBoolean.Diagnostic(SimplifyBooleanComparisonAnalyzer.DiagnosticId).WithLocation(0),
            source.Replace("flag {|#0:==|} true", "flag"));
    }
}
