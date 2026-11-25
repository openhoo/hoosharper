using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HooSharper.Analyzers;
using HooSharper.CodeFixes;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: HooSharper.SmokeTests PROJECT OUTPUT.json [--fix-all] [--write-fixed DIRECTORY] [--property NAME=VALUE]");
    return 2;
}

var properties = new Dictionary<string, string>();
var fixAll = false;
var requireAllRules = false;
string? fixedDirectory = null;
for (var i = 2; i < args.Length; i++)
{
    if (args[i] == "--fix-all")
        fixAll = true;
    else if (args[i] == "--require-all-rules")
        requireAllRules = true;
    else if (args[i] == "--write-fixed" && i + 1 < args.Length)
        fixedDirectory = Path.GetFullPath(args[++i]);
    else if (args[i] == "--property" && i + 1 < args.Length && args[i + 1].Contains('='))
    {
        var pair = args[++i].Split('=', 2);
        properties.Add(pair[0], pair[1]);
    }
    else
        throw new ArgumentException($"Unknown or incomplete argument: {args[i]}");
}

MSBuildLocator.RegisterDefaults();
if (fixedDirectory is not null && !fixAll)
    throw new ArgumentException("--write-fixed requires --fix-all.");
if (fixedDirectory is not null && Directory.Exists(fixedDirectory) &&
    Directory.EnumerateFileSystemEntries(fixedDirectory).Any())
    throw new ArgumentException("--write-fixed requires a new or empty directory.");
return await RunAsync(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]), properties, fixAll, fixedDirectory, requireAllRules);

static async Task<int> RunAsync(string projectPath, string outputPath,
    Dictionary<string, string> properties, bool fixAll, string? fixedDirectory, bool requireAllRules)
{
    var analyzerAssembly = typeof(PreferEarlyReturnAnalyzer).Assembly;
    var fixerAssembly = typeof(PreferEarlyReturnCodeFixProvider).Assembly;
    var analyzerSha256 = LoadedAssemblyHash(analyzerAssembly);
    var fixerSha256 = LoadedAssemblyHash(fixerAssembly);
    using var workspace = MSBuildWorkspace.Create(properties);
    var workspaceFailures = new System.Collections.Concurrent.ConcurrentQueue<string>();
    using var failureRegistration = workspace.RegisterWorkspaceFailedHandler(e =>
    {
        Console.Error.WriteLine(e.Diagnostic);
        if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
            workspaceFailures.Enqueue(e.Diagnostic.Message);
    });
    var project = await workspace.OpenProjectAsync(projectPath);
    var original = project.Solution;
    var compilation = await project.GetCompilationAsync()
        ?? throw new InvalidOperationException("Project did not produce a compilation.");
    var compilerDiagnostics = compilation.GetDiagnostics();
    var failures = compilerDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
        .Select(d => d.ToString()).Concat(workspaceFailures).ToList();
    var analyzers = analyzerAssembly.GetTypes()
        .Where(t => !t.IsAbstract && typeof(DiagnosticAnalyzer).IsAssignableFrom(t))
        .Select(t => (DiagnosticAnalyzer)Activator.CreateInstance(t)!).ToImmutableArray();
    var fixers = fixerAssembly.GetTypes()
        .Where(t => !t.IsAbstract && typeof(CodeFixProvider).IsAssignableFrom(t))
        .Select(t => (CodeFixProvider)Activator.CreateInstance(t)!)
        .SelectMany(provider => provider.FixableDiagnosticIds.Select(id => (id, provider)))
        .ToDictionary(pair => pair.id, pair => pair.provider);
    var sequential = await AnalyzeAsync(compilation, analyzers, project.AnalyzerOptions, false);
    var concurrent = await AnalyzeAsync(compilation, analyzers, project.AnalyzerOptions, true);
    if (!Keys(sequential).SequenceEqual(Keys(concurrent)))
        failures.Add("Sequential and concurrent analysis produced different diagnostics.");
    failures.AddRange(sequential.Where(d => d.Id == "AD0001").Select(d => d.ToString()));
    if (requireAllRules)
    {
        foreach (var id in Enumerable.Range(1001, 20).Select(n => $"HOO{n}"))
            if (!sequential.Any(d => d.Id == id))
                failures.Add($"Required positive fixture did not produce {id}.");
    }

    var fixResults = new List<object>();
    var cumulativeRounds = new List<object>();
    var sourceByteHashes = new Dictionary<string, string>();
    string? exportManifestSha256 = null;
    var baselineWarnings = WarningCounts(compilerDiagnostics);
    if (fixAll && failures.Count == 0)
    {
        foreach (var group in sequential.Where(d => d.Id.StartsWith("HOO", StringComparison.Ordinal))
                     .GroupBy(d => d.Id).OrderBy(g => g.Key))
        {
            var provider = fixers[group.Key];
            var first = group.First();
            var document = original.GetDocument(first.Location.SourceTree!)
                ?? throw new InvalidOperationException($"No document for {first}.");
            var actions = new List<CodeAction>();
            await provider.RegisterCodeFixesAsync(new CodeFixContext(document, first,
                (action, _) => actions.Add(action), CancellationToken.None));
            if (actions.Count == 0)
            {
                failures.Add($"{group.Key}: diagnostic has no code action.");
                continue;
            }
            var diagnostics = group.ToImmutableArray();
            var context = new FixAllContext(project, provider, FixAllScope.Project,
                actions[0].EquivalenceKey, provider.FixableDiagnosticIds,
                new ProjectDiagnostics(original, diagnostics), CancellationToken.None);
            var action = await provider.GetFixAllProvider()!.GetFixAsync(context);
            if (action is null)
            {
                failures.Add($"{group.Key}: project Fix All returned no action.");
                continue;
            }
            var operations = await action.GetOperationsAsync(CancellationToken.None);
            var changes = operations.OfType<ApplyChangesOperation>().ToArray();
            if (changes.Length != 1)
                throw new InvalidOperationException($"{group.Key}: expected one ApplyChangesOperation.");
            var fixedProject = await ReparseChangesAsync(project, changes[0].ChangedSolution);
            var fixedCompilation = await fixedProject.GetCompilationAsync()
                ?? throw new InvalidOperationException("Fixed project did not compile.");
            var errors = fixedCompilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString()).ToArray();
            var fixedWarnings = WarningCounts(fixedCompilation.GetDiagnostics());
            CheckNullableWarnings(group.Key, baselineWarnings, fixedWarnings, failures);
            var remaining = await AnalyzeAsync(fixedCompilation,
                analyzers.Where(a => a.SupportedDiagnostics.Any(d => d.Id == group.Key)).ToImmutableArray(),
                fixedProject.AnalyzerOptions, true);
            var changedDocuments = 0;
            foreach (var documentId in fixedProject.GetChanges(project).GetChangedDocuments())
            {
                var beforeText = await project.GetDocument(documentId)!.GetTextAsync();
                if (!beforeText.ContentEquals(await fixedProject.GetDocument(documentId)!.GetTextAsync()))
                    changedDocuments++;
            }
            if (errors.Length != 0)
                failures.AddRange(errors.Select(error => $"{group.Key} Fix All: {error}"));
            if (remaining.Any(d => d.Id == "AD0001"))
                failures.Add($"{group.Key} Fix All: analyzer crashed after rewrite.");
            if (changedDocuments == 0 || remaining.Count(d => d.Id == group.Key) >= diagnostics.Length)
                failures.Add($"{group.Key} Fix All made no diagnostic progress.");
            fixResults.Add(new
            {
                id = group.Key, before = diagnostics.Length,
                after = remaining.Count(d => d.Id == group.Key), changedDocuments, errors,
                compilerWarningCounts = fixedWarnings,
            });
            Console.WriteLine($"{group.Key}: {diagnostics.Length} -> {remaining.Count(d => d.Id == group.Key)}, " +
                              $"{changedDocuments} documents, {errors.Length} compiler errors");
        }

        var current = project;
        var converged = false;
        // Independent rule checks start from the original solution. Their
        // failures must not truncate this separate cumulative experiment.
        var failuresBeforeCumulative = failures.Count;
        const int maxRounds = 20;
        for (var round = 0; round < maxRounds; round++)
        {
            var currentCompilation = (await current.GetCompilationAsync())!;
            var pending = await AnalyzeAsync(currentCompilation, analyzers, current.AnalyzerOptions, true);
            if (pending.Any(d => d.Id == "AD0001"))
            {
                failures.AddRange(pending.Where(d => d.Id == "AD0001").Select(d => d.ToString()));
                break;
            }
            if (pending.Length == 0)
            {
                converged = true;
                break;
            }
            var beforeKeys = Keys(pending).ToArray();
            foreach (var id in pending.Select(d => d.Id).Distinct().Order(StringComparer.Ordinal))
            {
                currentCompilation = (await current.GetCompilationAsync())!;
                var ruleDiagnostics = await AnalyzeAsync(currentCompilation,
                    analyzers.Where(a => a.SupportedDiagnostics.Any(d => d.Id == id)).ToImmutableArray(),
                    current.AnalyzerOptions, true);
                if (ruleDiagnostics.Any(d => d.Id == "AD0001"))
                {
                    failures.AddRange(ruleDiagnostics.Where(d => d.Id == "AD0001").Select(d => d.ToString()));
                    break;
                }
                if (ruleDiagnostics.Length == 0)
                    continue;
                var provider = fixers[id];
                var document = current.Solution.GetDocument(ruleDiagnostics[0].Location.SourceTree!)!;
                var actions = new List<CodeAction>();
                await provider.RegisterCodeFixesAsync(new CodeFixContext(document, ruleDiagnostics[0],
                    (action, _) => actions.Add(action), CancellationToken.None));
                if (actions.Count == 0)
                {
                    failures.Add($"Cumulative {id}: no code action.");
                    continue;
                }
                var context = new FixAllContext(current, provider, FixAllScope.Project,
                    actions[0].EquivalenceKey, provider.FixableDiagnosticIds,
                    new ProjectDiagnostics(current.Solution, ruleDiagnostics), CancellationToken.None);
                var action = await provider.GetFixAllProvider()!.GetFixAsync(context);
                if (action is null)
                {
                    failures.Add($"Cumulative {id}: no Fix All action.");
                    continue;
                }
                var operations = await action.GetOperationsAsync(CancellationToken.None);
                current = await ReparseChangesAsync(current, operations.OfType<ApplyChangesOperation>().Single().ChangedSolution);
                var errors = (await current.GetCompilationAsync())!.GetDiagnostics()
                    .Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()).ToArray();
                if (errors.Length != 0)
                {
                    failures.AddRange(errors.Select(e => $"Cumulative {id}: {e}"));
                    break;
                }
            }
            var after = await AnalyzeAsync((await current.GetCompilationAsync())!, analyzers,
                current.AnalyzerOptions, true);
            var currentWarnings = WarningCounts((await current.GetCompilationAsync())!.GetDiagnostics());
            CheckNullableWarnings("Cumulative Fix All", baselineWarnings, currentWarnings, failures);
            if (after.Any(d => d.Id == "AD0001"))
                failures.AddRange(after.Where(d => d.Id == "AD0001").Select(d => d.ToString()));
            cumulativeRounds.Add(new
            {
                round = round + 1, before = pending.Length, after = after.Length,
                compilerWarningCounts = currentWarnings,
            });
            Console.WriteLine($"Cumulative round {round + 1}: {pending.Length} -> {after.Length}");
            if (after.Length == 0)
            {
                converged = true;
                break;
            }
            if (failures.Count > failuresBeforeCumulative)
                break;
            if (beforeKeys.SequenceEqual(Keys(after)))
            {
                failures.Add($"Cumulative Fix All stopped in round {round + 1}: the diagnostics are unchanged.");
                break;
            }
        }
        if (!converged && failures.Count == failuresBeforeCumulative)
            failures.Add($"Cumulative Fix All still has diagnostics after {maxRounds} rounds.");
        if (converged && failures.Count == 0 && fixedDirectory is not null)
        {
            var exported = new Dictionary<string, string>();
            foreach (var document in current.Documents)
            {
                var relative = Path.GetRelativePath(Path.GetDirectoryName(projectPath)!, document.FilePath!);
                if (Path.IsPathRooted(relative) ||
                    relative.Split(Path.DirectorySeparatorChar).Any(p => p is ".." or "obj" or "bin"))
                    continue;
                var destination = Path.Combine(fixedDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var originalText = await project.GetDocument(document.Id)!.GetTextAsync();
                var bytes = await File.ReadAllBytesAsync(document.FilePath!);
                var key = relative.Replace('\\', '/');
                sourceByteHashes[key] = Hash(bytes);
                var fixedBytes = EncodeExport(key, bytes, originalText, await document.GetTextAsync());
                await File.WriteAllBytesAsync(destination, fixedBytes);
                exported[key] = Hash(fixedBytes);
            }
            Directory.CreateDirectory(fixedDirectory);
            var manifest = JsonSerializer.SerializeToUtf8Bytes(exported, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllBytesAsync(Path.Combine(fixedDirectory, ".hoosharper-export.json"), manifest);
            exportManifestSha256 = Hash(manifest);
        }
    }

    var root = Path.GetDirectoryName(projectPath)!;
    var findings = sequential.Select(d =>
    {
        var span = d.Location.GetLineSpan();
        return new
        {
            id = d.Id, file = Path.GetRelativePath(root, span.Path).Replace('\\', '/'),
            line = span.StartLinePosition.Line + 1, column = span.StartLinePosition.Character + 1,
            endLine = span.EndLinePosition.Line + 1, endColumn = span.EndLinePosition.Character + 1,
            severity = d.Severity.ToString(), message = d.GetMessage(),
        };
    }).OrderBy(d => d.file).ThenBy(d => d.line).ThenBy(d => d.column).ThenBy(d => d.id).ToArray();
    var sourceHashes = new Dictionary<string, string>();
    foreach (var document in project.Documents)
    {
        var text = await document.GetTextAsync();
        sourceHashes[Path.GetRelativePath(root, document.FilePath!).Replace('\\', '/')] =
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }
    var inputHashes = new Dictionary<string, string>();
    foreach (var document in project.AnalyzerConfigDocuments.Concat(project.AdditionalDocuments))
    {
        var text = await document.GetTextAsync();
        inputHashes[Path.GetRelativePath(root, document.FilePath!).Replace('\\', '/')] =
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }
    var references = compilation.References.Select(reference => new
    {
        path = reference.Display,
        identity = compilation.GetAssemblyOrModuleSymbol(reference)?.ToDisplayString(),
        sha256 = reference.Display is not null && File.Exists(reference.Display)
            ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                File.ReadAllBytes(reference.Display))).ToLowerInvariant() : null,
    }).ToArray();
    var parseOptions = (Microsoft.CodeAnalysis.CSharp.CSharpParseOptions)project.ParseOptions!;
    var compilationOptions = (Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions)project.CompilationOptions!;
    if (analyzerSha256 != Hash(File.ReadAllBytes(analyzerAssembly.Location)) ||
        fixerSha256 != Hash(File.ReadAllBytes(fixerAssembly.Location)))
        failures.Add("Analyzer or fixer assembly changed on disk while the smoke run was in progress.");
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(new
    {
        project = projectPath, properties, sourceHashes, documents = project.Documents.Count(),
        analyzerSha256, fixerSha256,
        inputHashes, references, sourceByteHashes, exportManifestSha256,
        effectiveOptions = new
        {
            languageVersion = parseOptions.LanguageVersion.ToString(),
            preprocessorSymbols = parseOptions.PreprocessorSymbolNames.Order(StringComparer.Ordinal).ToArray(),
            nullable = compilationOptions.NullableContextOptions.ToString(),
            compilationOptions.AllowUnsafe, compilationOptions.CheckOverflow,
            outputKind = compilationOptions.OutputKind.ToString(),
            project.AssemblyName,
        },
        analyzerCount = analyzers.Length, compilerErrors = failures,
        compilerWarnings = compilerDiagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning),
        compilerWarningCounts = baselineWarnings,
        concurrentMatchesSequential = Keys(sequential).SequenceEqual(Keys(concurrent)), findings,
        fixes = fixResults, cumulativeRounds, passed = failures.Count == 0,
    }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"{project.Name}: {project.Documents.Count()} documents, {findings.Length} findings, " +
                      $"{failures.Count} failures. Report: {outputPath}");
    foreach (var failure in failures)
        Console.Error.WriteLine(failure);
    return failures.Count == 0 ? 0 : 1;
}

static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

static string LoadedAssemblyHash(Assembly assembly)
{
    var bytes = File.ReadAllBytes(assembly.Location);
    using var stream = new MemoryStream(bytes);
    using var reader = new PEReader(stream);
    var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(reader);
    if (metadata.GetGuid(metadata.GetModuleDefinition().Mvid) != assembly.ManifestModule.ModuleVersionId)
        throw new InvalidDataException($"Assembly on disk differs from the loaded module: {assembly.Location}");
    return Hash(bytes);
}

static byte[] EncodeExport(string path, byte[] bytes, Microsoft.CodeAnalysis.Text.SourceText original,
    Microsoft.CodeAnalysis.Text.SourceText updated)
{
    // Roslyn's encoding can support a preamble even when the file has none.
    var encoding = original.Encoding ?? throw new InvalidDataException($"Unknown source encoding: {path}");
    var preamble = encoding.GetPreamble();
    var skip = bytes.AsSpan().StartsWith(preamble) ? preamble.Length : 0;
    var text = original.ToString();
    if (encoding.GetString(bytes, skip, bytes.Length - skip) != text)
        throw new InvalidDataException($"Source bytes no longer match the analyzed document: {path}");
    if (original.ContentEquals(updated))
        return bytes;

    // Fail if encoding new text would lose a character or change unrelated bytes.
    var strict = Encoding.GetEncoding(encoding.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    if (!strict.GetBytes(text).AsSpan().SequenceEqual(bytes.AsSpan(skip)))
        throw new InvalidDataException($"Source cannot round-trip in its original encoding: {path}");
    return [.. bytes.AsSpan(0, skip), .. strict.GetBytes(updated.ToString())];
}

// The IDE saves text, not the fixer's syntax tree. Reparse changed files with
// their actual language version and symbols so grouping and else binding match a fresh build.
static async Task<Project> ReparseChangesAsync(Project before, Solution after)
{
    var changed = after.GetProject(before.Id)!;
    foreach (var id in changed.GetChanges(before).GetChangedDocuments())
    {
        var document = changed.GetDocument(id)!;
        var tree = (await document.GetSyntaxTreeAsync())!;
        var parsed = CSharpSyntaxTree.ParseText(await document.GetTextAsync(), (CSharpParseOptions)tree.Options, tree.FilePath);
        after = after.WithDocumentSyntaxRoot(id, await parsed.GetRootAsync());
    }

    return after.GetProject(before.Id)!;
}

static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(Compilation compilation,
    ImmutableArray<DiagnosticAnalyzer> analyzers, AnalyzerOptions options, bool concurrent) =>
    await compilation.WithAnalyzers(analyzers, new CompilationWithAnalyzersOptions(
        options, onAnalyzerException: null, concurrentAnalysis: concurrent,
        logAnalyzerExecutionTime: false, reportSuppressedDiagnostics: false)).GetAnalyzerDiagnosticsAsync();

static IEnumerable<string> Keys(IEnumerable<Diagnostic> diagnostics) => diagnostics
    .Select(d => $"{d.Id}:{d.Location.SourceTree?.FilePath}:{d.Location.SourceSpan}:{d.GetMessage()}")
    .Order(StringComparer.Ordinal);

static Dictionary<string, int> WarningCounts(IEnumerable<Diagnostic> diagnostics) => diagnostics
    .Where(d => d.Severity == DiagnosticSeverity.Warning).GroupBy(d => d.Id)
    .ToDictionary(g => g.Key, g => g.Count());

static void CheckNullableWarnings(string rule, Dictionary<string, int> before,
    Dictionary<string, int> after, List<string> failures)
{
    foreach (var pair in after.Where(p => p.Key.StartsWith("CS86", StringComparison.Ordinal) ||
                                         p.Key.StartsWith("CS87", StringComparison.Ordinal)))
    {
        if (pair.Value > before.GetValueOrDefault(pair.Key))
            failures.Add($"{rule}: nullable warning {pair.Key} increased from " +
                         $"{before.GetValueOrDefault(pair.Key)} to {pair.Value}.");
    }
}

sealed class ProjectDiagnostics(Solution solution, ImmutableArray<Diagnostic> diagnostics)
    : FixAllContext.DiagnosticProvider
{
    public override Task<IEnumerable<Diagnostic>> GetDocumentDiagnosticsAsync(Document document,
        CancellationToken cancellationToken) => Task.FromResult(diagnostics.Where(d =>
        d.Location.SourceTree is not null && solution.GetDocument(d.Location.SourceTree)?.Id == document.Id));

    public override Task<IEnumerable<Diagnostic>> GetProjectDiagnosticsAsync(Project project,
        CancellationToken cancellationToken) => Task.FromResult(Enumerable.Empty<Diagnostic>());

    public override Task<IEnumerable<Diagnostic>> GetAllDiagnosticsAsync(Project project,
        CancellationToken cancellationToken) => Task.FromResult<IEnumerable<Diagnostic>>(diagnostics);
}
