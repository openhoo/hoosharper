using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using HooSharper.Analyzers;
using HooSharper.CodeFixes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;

namespace HooSharper.Performance;

public enum FixerScenario
{
    UseTryGetValue,
    UseUsingDeclaration,
    WrapFluentChain,
    PreferEarlyReturn,
    PreferLoopContinue,
    MergeNestedIf,
    UseNullConditionalAccess,
    UseNullCoalescingAssignment,
}

[MemoryDiagnoser]
[Config(typeof(SingleInvocationPerformanceConfig))]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class CodeFixBenchmarks
{
    private readonly Consumer _consumer = new();
    private PreparedFixture? _registrationFixture;
    private FixFixture? _fixture;
    private ActionState? _computeState;
    private ImmutableArray<CodeActionOperation> _computedOperations;
    private ActionState? _applyState;
    private ImmutableArray<CodeActionOperation> _applyOperations;

    [ParamsAllValues]
    public FixerScenario Scenario { get; set; }

    [Params(100, 1000)]
    public int Groups { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _fixture = FixerBenchmarkFixture.CreateFixture(Scenario, Groups);
        try
        {
            _registrationFixture = await FixerBenchmarkFixture.CreatePreparedFixtureAsync(_fixture)
                .ConfigureAwait(false);
            await FixerBenchmarkFixture.ValidateActionsAsync(_registrationFixture, _fixture.Provider)
                .ConfigureAwait(false);
        }
        catch
        {
            _registrationFixture?.Dispose();
            _registrationFixture = null;
            throw;
        }
    }

    [Benchmark]
    [BenchmarkCategory("Registration")]
    public async Task Registration()
    {
        var actionCount = 0;
        var fixture = _registrationFixture
            ?? throw new InvalidOperationException("GlobalSetup was not called.");
        var context = new CodeFixContext(
            fixture.Document,
            fixture.Diagnostics[0],
            (_, _) => actionCount++,
            CancellationToken.None);
        await GetFixture().Provider.RegisterCodeFixesAsync(context).ConfigureAwait(false);
        if (actionCount == 0)
        {
            throw new InvalidOperationException($"{Scenario} did not register a code action.");
        }

        _consumer.Consume(actionCount);
    }

    [IterationSetup(Target = nameof(ComputeOperations))]
    public void SetupComputeOperations() =>
        _computeState = FixerBenchmarkFixture.CreateActionStateAsync(GetFixture()).GetAwaiter().GetResult();

    [Benchmark]
    [BenchmarkCategory("ComputeOperations")]
    public async Task ComputeOperations()
    {
        try
        {
            _computedOperations = await (_computeState
                    ?? throw new InvalidOperationException("IterationSetup was not called."))
                .Action.GetOperationsAsync(CancellationToken.None).ConfigureAwait(false);
            if (_computedOperations.IsDefaultOrEmpty)
            {
                throw new InvalidOperationException($"{Scenario} produced no code action operations.");
            }

            _consumer.Consume(_computedOperations.Length);
        }
        catch
        {
            DisposeComputeState();
            throw;
        }
    }

    [IterationCleanup(Target = nameof(ComputeOperations))]
    public void CleanupComputeOperations()
    {
        try
        {
            var state = _computeState
                ?? throw new InvalidOperationException("IterationSetup was not called.");
            var result = FixerBenchmarkFixture.ApplyAndMeasureAsync(state, _computedOperations)
                .GetAwaiter().GetResult();
            _consumer.Consume(result.OperationCount);
            _consumer.Consume(result.TextLength);
            _consumer.Consume(result.Checksum);
        }
        finally
        {
            DisposeComputeState();
        }
    }

    [IterationSetup(Target = nameof(ApplyOperations))]
    public void SetupApplyOperations()
    {
        _applyState = FixerBenchmarkFixture.CreateActionStateAsync(GetFixture()).GetAwaiter().GetResult();
        try
        {
            _applyOperations = _applyState.Action.GetOperationsAsync(CancellationToken.None)
                .GetAwaiter().GetResult();
            if (_applyOperations.IsDefaultOrEmpty)
            {
                throw new InvalidOperationException($"{Scenario} produced no code action operations.");
            }
        }
        catch
        {
            DisposeApplyState();
            throw;
        }
    }

    [Benchmark]
    [BenchmarkCategory("ApplyOperations")]
    public void ApplyOperations()
    {
        try
        {
            FixerBenchmarkFixture.ApplyOperations(
                _applyState ?? throw new InvalidOperationException("IterationSetup was not called."),
                _applyOperations);
            _consumer.Consume(_applyOperations.Length);
        }
        catch
        {
            DisposeApplyState();
            throw;
        }
    }

    [IterationCleanup(Target = nameof(ApplyOperations))]
    public void CleanupApplyOperations()
    {
        try
        {
            var result = FixerBenchmarkFixture.MeasureAppliedStateAsync(
                    _applyState ?? throw new InvalidOperationException("IterationSetup was not called."),
                    _applyOperations.Length)
                .GetAwaiter().GetResult();
            _consumer.Consume(result.OperationCount);
            _consumer.Consume(result.TextLength);
            _consumer.Consume(result.Checksum);
        }
        finally
        {
            DisposeApplyState();
        }
    }

    [Benchmark]
    [BenchmarkCategory("DiscoverRegisterApply")]
    public async Task DiscoverRegisterApply()
    {
        ActionState? state = null;
        try
        {
            state = await FixerBenchmarkFixture.CreateActionStateAsync(GetFixture()).ConfigureAwait(false);
            var operations = await state.Action.GetOperationsAsync(CancellationToken.None).ConfigureAwait(false);
            var result = await FixerBenchmarkFixture.ApplyAndMeasureAsync(state, operations).ConfigureAwait(false);
            _consumer.Consume(result.OperationCount);
            _consumer.Consume(result.TextLength);
            _consumer.Consume(result.Checksum);
        }
        finally
        {
            state?.Dispose();
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        DisposeComputeState();
        DisposeApplyState();
        _registrationFixture?.Dispose();
        _registrationFixture = null;
    }

    private FixFixture GetFixture() =>
        _fixture ?? throw new InvalidOperationException("GlobalSetup was not called.");

    private void DisposeComputeState()
    {
        _computeState?.Dispose();
        _computeState = null;
        _computedOperations = default;
    }

    private void DisposeApplyState()
    {
        _applyState?.Dispose();
        _applyState = null;
        _applyOperations = default;
    }
}

[MemoryDiagnoser]
[Config(typeof(SingleInvocationPerformanceConfig))]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class CodeFixFixAllBenchmarks
{
    private readonly Consumer _consumer = new();
    private FixFixture? _fixture;
    private FixAllState? _state;

    [ParamsAllValues]
    public FixerScenario Scenario { get; set; }

    [Params(1, 10, 100)]
    public int DiagnosticCount { get; set; }

    [GlobalSetup]
    public void Setup() => _fixture = FixerBenchmarkFixture.CreateFixture(Scenario, DiagnosticCount);

    [IterationSetup(Target = nameof(DocumentFixAll))]
    public void SetupDocumentFixAll() =>
        _state = FixerBenchmarkFixture.CreateFixAllStateAsync(GetFixture(), DiagnosticCount)
            .GetAwaiter().GetResult();

    [Benchmark]
    [BenchmarkCategory("DocumentFixAll")]
    public async Task DocumentFixAll()
    {
        try
        {
            var state = _state ?? throw new InvalidOperationException("IterationSetup was not called.");
            var operations = await state.Action.GetOperationsAsync(CancellationToken.None).ConfigureAwait(false);
            var result = await FixerBenchmarkFixture.ApplyAndMeasureAsync(state.ActionState, operations)
                .ConfigureAwait(false);
            _consumer.Consume(result.OperationCount);
            _consumer.Consume(result.TextLength);
            _consumer.Consume(result.Checksum);
        }
        catch
        {
            DisposeState();
            throw;
        }
    }

    [IterationCleanup(Target = nameof(DocumentFixAll))]
    public void CleanupDocumentFixAll()
    {
        try
        {
            if (_state is not null)
            {
                FixerBenchmarkFixture.ValidateFixAllResultAsync(_state).GetAwaiter().GetResult();
            }
        }
        finally
        {
            DisposeState();
        }
    }

    [GlobalCleanup]
    public void Cleanup() => DisposeState();

    private FixFixture GetFixture() =>
        _fixture ?? throw new InvalidOperationException("GlobalSetup was not called.");

    private void DisposeState()
    {
        _state?.Dispose();
        _state = null;
    }
}

internal static class FixerBenchmarkFixture
{
    public static FixFixture CreateFixture(FixerScenario scenario, int groups) => scenario switch
    {
        FixerScenario.UseTryGetValue => new(
            BenchmarkSource.CreateTryGetValueSource(groups),
            new UseTryGetValueAnalyzer(),
            new UseTryGetValueCodeFixProvider(),
            UseTryGetValueAnalyzer.DiagnosticId,
            groups),
        FixerScenario.UseUsingDeclaration => new(
            BenchmarkSource.CreateUsingDeclarationSource(groups),
            new UseUsingDeclarationAnalyzer(),
            new UseUsingDeclarationCodeFixProvider(),
            UseUsingDeclarationAnalyzer.DiagnosticId,
            groups),
        FixerScenario.WrapFluentChain => new(
            BenchmarkSource.CreateWrapFluentChainSource(groups),
            new WrapFluentChainAnalyzer(),
            new WrapFluentChainCodeFixProvider(),
            WrapFluentChainAnalyzer.DiagnosticId,
            groups),
        FixerScenario.PreferEarlyReturn => new(
            BenchmarkSource.CreatePreferEarlyReturnSource(groups),
            new PreferEarlyReturnAnalyzer(),
            new PreferEarlyReturnCodeFixProvider(),
            PreferEarlyReturnAnalyzer.DiagnosticId,
            groups),
        FixerScenario.PreferLoopContinue => new(
            BenchmarkSource.CreatePreferLoopContinueSource(groups),
            new PreferLoopContinueAnalyzer(),
            new PreferLoopContinueCodeFixProvider(),
            PreferLoopContinueAnalyzer.DiagnosticId,
            groups),
        FixerScenario.MergeNestedIf => new(
            BenchmarkSource.CreateMergeNestedIfSource(groups),
            new MergeNestedIfAnalyzer(),
            new MergeNestedIfCodeFixProvider(),
            MergeNestedIfAnalyzer.DiagnosticId,
            groups),
        FixerScenario.UseNullConditionalAccess => new(
            BenchmarkSource.CreateNullConditionalAccessSource(groups),
            new UseNullConditionalAccessAnalyzer(),
            new UseNullConditionalAccessCodeFixProvider(),
            UseNullConditionalAccessAnalyzer.DiagnosticId,
            groups),
        FixerScenario.UseNullCoalescingAssignment => new(
            BenchmarkSource.CreateNullCoalescingAssignmentSource(groups),
            new UseNullCoalescingAssignmentAnalyzer(),
            new UseNullCoalescingAssignmentCodeFixProvider(),
            UseNullCoalescingAssignmentAnalyzer.DiagnosticId,
            groups),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
    };

    public static async Task<PreparedFixture> CreatePreparedFixtureAsync(FixFixture fixture)
    {
        var workspace = new AdhocWorkspace();
        try
        {
            var project = RoslynFixture.CreateProject(workspace, fixture.Source);
            var document = project.Documents.Single();
            var compilation = await project.GetCompilationAsync().ConfigureAwait(false)
                ?? throw new InvalidOperationException("Benchmark compilation could not be created.");
            var compilerErrors = compilation.GetDiagnostics()
                .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .ToImmutableArray();
            if (!compilerErrors.IsEmpty)
            {
                throw new InvalidOperationException(
                    $"Generated fixture produced {compilerErrors.Length} compiler errors: {compilerErrors[0]}.");
            }

            var analyzerDiagnostics = await compilation.WithAnalyzers(
                    [fixture.Analyzer],
                    new CompilationWithAnalyzersOptions(
                        project.AnalyzerOptions,
                        null,
                        concurrentAnalysis: true,
                        logAnalyzerExecutionTime: false,
                        reportSuppressedDiagnostics: false))
                .GetAnalyzerDiagnosticsAsync().ConfigureAwait(false);
            if (analyzerDiagnostics.Any(static diagnostic => diagnostic.Id == "AD0001"))
            {
                throw new InvalidOperationException("Generated fixture produced AD0001 analyzer failure.");
            }

            var unexpectedDiagnostics = analyzerDiagnostics
                .Where(diagnostic => diagnostic.Id != fixture.DiagnosticId)
                .ToImmutableArray();
            if (!unexpectedDiagnostics.IsEmpty)
            {
                throw new InvalidOperationException(
                    $"Generated fixture produced unexpected diagnostic {unexpectedDiagnostics[0]}.");
            }

            var diagnostics = analyzerDiagnostics
                .Where(diagnostic => diagnostic.Id == fixture.DiagnosticId)
                .ToImmutableArray();
            if (diagnostics.Length != fixture.ExpectedDiagnosticCount)
            {
                throw new InvalidOperationException(
                    $"Expected {fixture.ExpectedDiagnosticCount} {fixture.DiagnosticId} diagnostics, but found {diagnostics.Length}.");
            }

            return new PreparedFixture(workspace, document, diagnostics, fixture.Source, fixture.Analyzer);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    public static async Task ValidateActionsAsync(PreparedFixture fixture, CodeFixProvider provider)
    {
        var actions = await RegisterActionsAsync(fixture, provider).ConfigureAwait(false);
        if (actions.Count == 0)
        {
            throw new InvalidOperationException($"{provider.GetType().Name} did not register a code action.");
        }
    }

    public static async Task<ActionState> CreateActionStateAsync(FixFixture fixture)
    {
        PreparedFixture? prepared = null;
        try
        {
            prepared = await CreatePreparedFixtureAsync(fixture).ConfigureAwait(false);
            var actions = await RegisterActionsAsync(prepared, fixture.Provider).ConfigureAwait(false);
            var action = actions.FirstOrDefault()
                ?? throw new InvalidOperationException(
                    $"{fixture.Provider.GetType().Name} did not register a code action.");
            return new ActionState(prepared, action);
        }
        catch
        {
            prepared?.Dispose();
            throw;
        }
    }

    public static async Task<FixAllState> CreateFixAllStateAsync(FixFixture fixture, int expectedDiagnosticCount)
    {
        ActionState? actionState = null;
        try
        {
            actionState = await CreateActionStateAsync(fixture).ConfigureAwait(false);
            if (actionState.Fixture.Diagnostics.Length != expectedDiagnosticCount)
            {
                throw new InvalidOperationException(
                    $"Expected {expectedDiagnosticCount} {fixture.DiagnosticId} diagnostics, but found " +
                    $"{actionState.Fixture.Diagnostics.Length}.");
            }

            var fixAllProvider = fixture.Provider.GetFixAllProvider()
                ?? throw new InvalidOperationException(
                    $"{fixture.Provider.GetType().Name} did not provide Fix All support.");
            var equivalenceKey = actionState.Action.EquivalenceKey
                ?? throw new InvalidOperationException("The registered code action has no equivalence key.");
            var diagnosticProvider = new PreparedDiagnosticProvider(
                actionState.Fixture.Document.Id,
                actionState.Fixture.Diagnostics);
            var context = new FixAllContext(
                actionState.Fixture.Document,
                fixture.Provider,
                FixAllScope.Document,
                equivalenceKey,
                [fixture.DiagnosticId],
                diagnosticProvider,
                CancellationToken.None);
            var action = await fixAllProvider.GetFixAsync(context).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"{fixture.Provider.GetType().Name} did not create a document Fix All action.");
            return new FixAllState(actionState, action);
        }
        catch
        {
            actionState?.Dispose();
            throw;
        }
    }

    public static async Task<ApplicationResult> ApplyAndMeasureAsync(
        ActionState state,
        ImmutableArray<CodeActionOperation> operations)
    {
        ApplyOperations(state, operations);
        return await MeasureAppliedStateAsync(state, operations.Length).ConfigureAwait(false);
    }

    public static void ApplyOperations(
        ActionState state,
        ImmutableArray<CodeActionOperation> operations)
    {
        if (operations.IsDefaultOrEmpty)
        {
            throw new InvalidOperationException("The code action produced no operations.");
        }

        foreach (var operation in operations)
        {
            operation.Apply(state.Fixture.Workspace, CancellationToken.None);
        }
    }

    public static async Task<ApplicationResult> MeasureAppliedStateAsync(ActionState state, int operationCount)
    {
        var changedDocument = state.Fixture.Workspace.CurrentSolution.GetDocument(state.Fixture.Document.Id)
            ?? throw new InvalidOperationException("The changed document is missing from the workspace.");
        var changedText = (await changedDocument.GetTextAsync().ConfigureAwait(false)).ToString();
        if (string.Equals(changedText, state.Fixture.OriginalSource, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Applying the code action did not change the document.");
        }

        return new ApplicationResult(operationCount, changedText.Length, ComputeChecksum(changedText));
    }
    public static async Task ValidateFixAllResultAsync(FixAllState state)
    {
        var changedDocument = state.ActionState.Fixture.Workspace.CurrentSolution.GetDocument(
                state.ActionState.Fixture.Document.Id)
            ?? throw new InvalidOperationException("The changed document is missing from the workspace.");
        var compilation = await changedDocument.Project.GetCompilationAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("The changed project compilation is missing.");
        var diagnostics = await compilation.WithAnalyzers(
                [state.ActionState.Fixture.Analyzer],
                new CompilationWithAnalyzersOptions(
                    changedDocument.Project.AnalyzerOptions,
                    null,
                    concurrentAnalysis: true,
                    logAnalyzerExecutionTime: false,
                    reportSuppressedDiagnostics: false))
            .GetAnalyzerDiagnosticsAsync()
            .ConfigureAwait(false);
        if (diagnostics.Any(static diagnostic => diagnostic.Id == "AD0001"))
        {
            throw new InvalidOperationException("Fix All result produced AD0001 analyzer failure.");
        }

        var targetId = state.ActionState.Fixture.Diagnostics[0].Id;
        var unexpected = diagnostics.FirstOrDefault(diagnostic => diagnostic.Id != targetId);
        if (unexpected is not null)
        {
            throw new InvalidOperationException($"Fix All result produced unexpected diagnostic {unexpected}.");
        }

        var remaining = diagnostics.Count(diagnostic => diagnostic.Id == targetId);
        if (remaining != 0)
        {
            throw new InvalidOperationException(
                $"Fix All left {remaining} {targetId} diagnostics after application.");
        }
    }


    private static async Task<List<CodeAction>> RegisterActionsAsync(
        PreparedFixture fixture,
        CodeFixProvider provider)
    {
        var actions = new List<CodeAction>();
        var context = new CodeFixContext(
            fixture.Document,
            fixture.Diagnostics[0],
            (action, _) => actions.Add(action),
            CancellationToken.None);
        await provider.RegisterCodeFixesAsync(context).ConfigureAwait(false);
        return actions;
    }

    private static int ComputeChecksum(string text)
    {
        var checksum = unchecked((int)2166136261);
        foreach (var character in text)
        {
            checksum = unchecked((checksum ^ character) * 16777619);
        }

        return checksum;
    }

    private sealed class PreparedDiagnosticProvider(
        DocumentId documentId,
        ImmutableArray<Diagnostic> diagnostics) : FixAllContext.DiagnosticProvider
    {
        public override Task<IEnumerable<Diagnostic>> GetDocumentDiagnosticsAsync(
            Document document,
            CancellationToken cancellationToken) =>
            Task.FromResult<IEnumerable<Diagnostic>>(
                document.Id == documentId ? diagnostics : ImmutableArray<Diagnostic>.Empty);

        public override Task<IEnumerable<Diagnostic>> GetProjectDiagnosticsAsync(
            Project project,
            CancellationToken cancellationToken) =>
            Task.FromResult<IEnumerable<Diagnostic>>(ImmutableArray<Diagnostic>.Empty);

        public override Task<IEnumerable<Diagnostic>> GetAllDiagnosticsAsync(
            Project project,
            CancellationToken cancellationToken) =>
            Task.FromResult<IEnumerable<Diagnostic>>(
                project.Id == documentId.ProjectId ? diagnostics : ImmutableArray<Diagnostic>.Empty);
    }
}

internal sealed record FixFixture(
    string Source,
    DiagnosticAnalyzer Analyzer,
    CodeFixProvider Provider,
    string DiagnosticId,
    int ExpectedDiagnosticCount);
internal sealed class PreparedFixture(
    AdhocWorkspace workspace,
    Document document,
    ImmutableArray<Diagnostic> diagnostics,
    string originalSource,
    DiagnosticAnalyzer analyzer) : IDisposable
{
    public AdhocWorkspace Workspace { get; } = workspace;

    public Document Document { get; } = document;

    public ImmutableArray<Diagnostic> Diagnostics { get; } = diagnostics;
    public DiagnosticAnalyzer Analyzer { get; } = analyzer;

    public string OriginalSource { get; } = originalSource;

    public void Dispose() => Workspace.Dispose();
}

internal sealed class ActionState(PreparedFixture fixture, CodeAction action) : IDisposable
{
    public PreparedFixture Fixture { get; } = fixture;

    public CodeAction Action { get; } = action;

    public void Dispose() => Fixture.Dispose();
}

internal sealed class FixAllState(ActionState actionState, CodeAction action) : IDisposable
{
    public ActionState ActionState { get; } = actionState;

    public CodeAction Action { get; } = action;

    public void Dispose() => ActionState.Dispose();
}

internal readonly record struct ApplicationResult(int OperationCount, int TextLength, int Checksum);
