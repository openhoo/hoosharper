using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace HooSharper.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HooSharperAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HOO0001";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "HooSharper is active",
        "HooSharper analyzer is active",
        "HooSharper",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "A bootstrap diagnostic used to verify the analyzer pipeline.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
    }
}
