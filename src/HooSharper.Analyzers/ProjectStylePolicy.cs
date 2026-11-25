using System.Threading;
using Microsoft.CodeAnalysis;

namespace HooSharper.Analyzers;

internal static class ProjectStylePolicy
{
    // The compiler stores diagnostic severities separately from regular EditorConfig
    // settings. A per-file option overrides the global analyzer configuration.
    internal static bool IsEnabled(CompilationOptions options, SyntaxTree tree, string id, CancellationToken cancellationToken)
    {
        var provider = options.SyntaxTreeOptionsProvider;
        if ((provider is not null && (provider.TryGetDiagnosticValue(tree, id, cancellationToken, out var severity) ||
            provider.TryGetGlobalDiagnosticValue(id, cancellationToken, out severity))) ||
            options.SpecificDiagnosticOptions.TryGetValue(id, out severity))
            return severity is ReportDiagnostic.Warn or ReportDiagnostic.Error or ReportDiagnostic.Info;
        return false;
    }
}
