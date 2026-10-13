using System.Collections.Immutable;
using System.Text.Json;
using HooSharper.Analyzers;
using HooSharper.CodeFixes;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
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
    var analyzers = typeof(PreferEarlyReturnAnalyzer).Assembly.GetTypes()
        .Where(t => !t.IsAbstract && typeof(DiagnosticAnalyzer).IsAssignableFrom(t))
        .Select(t => (DiagnosticAnalyzer)Activator.CreateInstance(t)!).ToImmutableArray();
    var fixers = typeof(PreferEarlyReturnCodeFixProvider).Assembly.GetTypes()
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
            var fixedProject = changes[0].ChangedSolution.GetProject(project.Id)!;
            var fixedCompilation = await fixedProject.GetCompilationAsync()
                ?? throw new InvalidOperationException("Fixed project did not compile.");
            var errors = fixedCompilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString()).ToArray();
            var fixedWarnings = WarningCounts(fixedCompilation.GetDiagnostics());
            CheckNullableWarnings(group.Key, baselineWarnings, fixedWarnings, failures);
            var remaining = await AnalyzeAsync(fixedCompilation,
                analyzers.Where(a => a.SupportedDiagnostics.Any(d => d.Id == group.Key)).ToImmutableArray(),
                fixedProject.AnalyzerOptions, true);
            var changedDocuments = changes[0].ChangedSolution.GetChanges(original)
                .GetProjectChanges().SelectMany(p => p.GetChangedDocuments()).Count();
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
        for (var round = 0; round < 20; round++)
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
                current = operations.OfType<ApplyChangesOperation>().Single().ChangedSolution.GetProject(project.Id)!;
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
            if (failures.Count != 0 || beforeKeys.SequenceEqual(Keys(after)))
                break;
        }
        if (!converged)
            failures.Add("Cumulative Fix All did not converge to zero diagnostics within 20 rounds.");
        if (converged && failures.Count == 0 && fixedDirectory is not null)
        {
            var exported = new Dictionary<string, string>();
            foreach (var document in current.Documents)
            {
                var relative = Path.GetRelativePath(Path.GetDirectoryName(projectPath)!, document.FilePath!);
                if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ||
                    relative.Split(Path.DirectorySeparatorChar).Any(p => p is "obj" or "bin"))
                    continue;
                var destination = Path.Combine(fixedDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var content = (await document.GetTextAsync()).ToString();
                await File.WriteAllTextAsync(destination, content);
                exported[relative.Replace('\\', '/')] = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)))
                    .ToLowerInvariant();
            }
            await File.WriteAllTextAsync(Path.Combine(fixedDirectory, ".hoosharper-export.json"),
                JsonSerializer.Serialize(exported, new JsonSerializerOptions { WriteIndented = true }));
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
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(new
    {
        project = projectPath, properties, sourceHashes, documents = project.Documents.Count(),
        analyzerSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            File.ReadAllBytes(typeof(PreferEarlyReturnAnalyzer).Assembly.Location))).ToLowerInvariant(),
        fixerSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            File.ReadAllBytes(typeof(PreferEarlyReturnCodeFixProvider).Assembly.Location))).ToLowerInvariant(),
        inputHashes, references,
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
