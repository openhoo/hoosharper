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
[Config(typeof(PerformanceConfig))]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class CodeFixBenchmarks
{
    private readonly Consumer _consumer = new();
    private AdhocWorkspace? _registrationWorkspace;
    private Document? _registrationDocument;
    private Diagnostic? _registrationDiagnostic;
    private CodeFixProvider? _provider;
    private string? _applicationSource;
    private DiagnosticAnalyzer? _applicationAnalyzer;
    private CodeFixProvider? _applicationProvider;

    [ParamsAllValues]
    public FixerScenario Scenario { get; set; }

    [Params(100, 1000)]
    public int Groups { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        var fixture = GetFixture();
        (_registrationWorkspace, _registrationDocument, _registrationDiagnostic) =
            await RoslynFixture.CreateFixFixtureAsync(fixture.Source, fixture.Analyzer, fixture.DiagnosticId)
                .ConfigureAwait(false);
        _provider = fixture.Provider;
        _applicationSource = fixture.Source;
        _applicationAnalyzer = fixture.Analyzer;
        _applicationProvider = fixture.Provider;
    }

    private async Task<(AdhocWorkspace Workspace, CodeAction Action)> CreateApplicationStateAsync()
    {
        var state = await RoslynFixture.CreateFixFixtureAsync(
                _applicationSource ?? throw new InvalidOperationException("GlobalSetup was not called."),
                _applicationAnalyzer ?? throw new InvalidOperationException("GlobalSetup was not called."),
                GetFixture().DiagnosticId)
            .ConfigureAwait(false);
        var actions = new List<CodeAction>(1);
        var context = new CodeFixContext(
            state.Document,
            state.Diagnostic,
            (action, _) => actions.Add(action),
            CancellationToken.None);
        await (_applicationProvider ?? throw new InvalidOperationException("GlobalSetup was not called."))
            .RegisterCodeFixesAsync(context).ConfigureAwait(false);
        return (state.Workspace, actions.FirstOrDefault()
            ?? throw new InvalidOperationException($"{Scenario} did not register a code action."));
    }

    [Benchmark]
    [BenchmarkCategory("Registration")]
    public async Task RegisterCodeFixes()
    {
        var actionCount = 0;
        var context = new CodeFixContext(
            _registrationDocument ?? throw new InvalidOperationException("GlobalSetup was not called."),
            _registrationDiagnostic ?? throw new InvalidOperationException("GlobalSetup was not called."),
            (_, _) => actionCount++,
            CancellationToken.None);
        await (_provider ?? throw new InvalidOperationException("GlobalSetup was not called."))
            .RegisterCodeFixesAsync(context).ConfigureAwait(false);
        _consumer.Consume(actionCount);
    }

    [Benchmark]
    [BenchmarkCategory("Application")]
    public async Task ApplyFirstAction()
    {
        var state = await CreateApplicationStateAsync().ConfigureAwait(false);
        using var workspace = state.Workspace;
        var operations = await state.Action.GetOperationsAsync(CancellationToken.None).ConfigureAwait(false);
        foreach (var operation in operations)
        {
            operation.Apply(workspace, CancellationToken.None);
        }
        _consumer.Consume(operations.Length);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _registrationWorkspace?.Dispose();
    }

    private FixFixture GetFixture() => Scenario switch
    {
        FixerScenario.UseTryGetValue => new(
            BenchmarkSource.CreateTryGetValueSource(Groups),
            new UseTryGetValueAnalyzer(),
            new UseTryGetValueCodeFixProvider(),
            UseTryGetValueAnalyzer.DiagnosticId),
        FixerScenario.UseUsingDeclaration => new(
            BenchmarkSource.CreateUsingDeclarationSource(Groups),
            new UseUsingDeclarationAnalyzer(),
            new UseUsingDeclarationCodeFixProvider(),
            UseUsingDeclarationAnalyzer.DiagnosticId),
        FixerScenario.WrapFluentChain => new(
            BenchmarkSource.CreateWrapFluentChainSource(Groups),
            new WrapFluentChainAnalyzer(),
            new WrapFluentChainCodeFixProvider(),
            WrapFluentChainAnalyzer.DiagnosticId),
        FixerScenario.PreferEarlyReturn => new(
            BenchmarkSource.CreatePreferEarlyReturnSource(Groups),
            new PreferEarlyReturnAnalyzer(),
            new PreferEarlyReturnCodeFixProvider(),
            PreferEarlyReturnAnalyzer.DiagnosticId),
        FixerScenario.PreferLoopContinue => new(
            BenchmarkSource.CreatePreferLoopContinueSource(Groups),
            new PreferLoopContinueAnalyzer(),
            new PreferLoopContinueCodeFixProvider(),
            PreferLoopContinueAnalyzer.DiagnosticId),
        FixerScenario.MergeNestedIf => new(
            BenchmarkSource.CreateMergeNestedIfSource(Groups),
            new MergeNestedIfAnalyzer(),
            new MergeNestedIfCodeFixProvider(),
            MergeNestedIfAnalyzer.DiagnosticId),
        FixerScenario.UseNullConditionalAccess => new(
            BenchmarkSource.CreateNullConditionalAccessSource(Groups),
            new UseNullConditionalAccessAnalyzer(),
            new UseNullConditionalAccessCodeFixProvider(),
            UseNullConditionalAccessAnalyzer.DiagnosticId),
        FixerScenario.UseNullCoalescingAssignment => new(
            BenchmarkSource.CreateNullCoalescingAssignmentSource(Groups),
            new UseNullCoalescingAssignmentAnalyzer(),
            new UseNullCoalescingAssignmentCodeFixProvider(),
            UseNullCoalescingAssignmentAnalyzer.DiagnosticId),
        _ => throw new ArgumentOutOfRangeException(nameof(Scenario)),
    };

    private sealed record FixFixture(
        string Source,
        DiagnosticAnalyzer Analyzer,
        CodeFixProvider Provider,
        string DiagnosticId);
}
