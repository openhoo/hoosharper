using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace HooSharper.Performance;

internal static class RoslynFixture
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);
    private static readonly CSharpCompilationOptions CompilationOptions = new(
        OutputKind.DynamicallyLinkedLibrary,
        optimizationLevel: OptimizationLevel.Release,
        concurrentBuild: true,
        nullableContextOptions: NullableContextOptions.Enable);

    public static ImmutableArray<MetadataReference> References { get; } = CreateReferences();

    public static Project CreateProject(AdhocWorkspace workspace, string source, string name = "BenchmarkProject")
    {
        var projectId = ProjectId.CreateNewId(name);
        var documentId = DocumentId.CreateNewId(projectId, "Benchmark.cs");
        var configId = DocumentId.CreateNewId(projectId, ".editorconfig");
        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectId,
                VersionStamp.Default,
                name,
                name,
                LanguageNames.CSharp,
                parseOptions: ParseOptions,
                compilationOptions: CompilationOptions,
                metadataReferences: References))
            .AddDocument(documentId, "Benchmark.cs", SourceText.From(source), filePath: "/bench/Benchmark.cs")
            .AddAnalyzerConfigDocument(
                configId,
                ".editorconfig",
                SourceText.From("root = true\n\n[*.cs]\nhoosharper_max_line_length = 80\nindent_style = space\nindent_size = 4\n"),
                filePath: "/bench/.editorconfig");
        return solution.GetProject(projectId)
            ?? throw new InvalidOperationException("Benchmark project could not be created.");
    }

    public static async Task<(AdhocWorkspace Workspace, Document Document, Diagnostic Diagnostic)> CreateFixFixtureAsync(
        string source,
        DiagnosticAnalyzer analyzer,
        string diagnosticId)
    {
        var workspace = new AdhocWorkspace();
        var project = CreateProject(workspace, source);
        var document = project.Documents.Single();
        var compilation = await project.GetCompilationAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("Benchmark compilation could not be created.");
        var diagnostics = await compilation.WithAnalyzers(
                [analyzer],
                new CompilationWithAnalyzersOptions(
                    project.AnalyzerOptions,
                    null,
                    concurrentAnalysis: true,
                    logAnalyzerExecutionTime: false,
                    reportSuppressedDiagnostics: false))
            .GetAnalyzerDiagnosticsAsync().ConfigureAwait(false);
        var diagnostic = diagnostics.FirstOrDefault(item => item.Id == diagnosticId)
            ?? throw new InvalidOperationException($"Generated fixture did not produce {diagnosticId}.");
        return (workspace, document, diagnostic);
    }

    private static ImmutableArray<MetadataReference> CreateReferences()
    {
        var trustedAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("Trusted platform assemblies are unavailable.");
        return trustedAssemblies.Split(Path.PathSeparator)
            .Select(static path => MetadataReference.CreateFromFile(path))
            .ToImmutableArray<MetadataReference>();
    }
}
