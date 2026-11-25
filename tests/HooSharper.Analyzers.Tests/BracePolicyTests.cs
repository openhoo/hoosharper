using HooSharper.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace HooSharper.Analyzers.Tests;

public sealed class BracePolicyTests
{
    [Theory]
    [InlineData("SA1519", "if (a) { System.Console.WriteLine(\n1); }")]
    [InlineData("SA1515", "if (a) {\n// explanation\nSystem.Console.WriteLine(1); }")]
    [InlineData("SA1520", "if (a) { System.Console.WriteLine(1); } else if (b) return; else { return; }")]
    public Task ExplicitProjectPolicyKeepsBraces(string rule, string branch)
    {
        var test = new CSharpCodeFixTest<OmitBracesForSingleLineIfAnalyzer, OmitBracesForSingleLineIfCodeFixProvider, DefaultVerifier>
        {
            ReferenceAssemblies = ReferenceAssemblies.Net.Net100,
            TestCode = "class C { void Run(bool a, bool b) { " + branch + " } }",
        };
        test.TestState.AnalyzerConfigFiles.Add(("/.editorconfig",
            "root = true\n[*.cs]\ndotnet_diagnostic." + rule + ".severity = warning\n"));
        return test.RunAsync(TestContext.Current.CancellationToken);
    }
}
