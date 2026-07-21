using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using HooSharper.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace HooSharper.Performance;

public enum AnalyzerDiagnosticDensity
{
    Clean,
    Sparse,
    Mixed,
    DenseStress,
}

public enum AnalyzerTreeShape
{
    SingleLargeTree,
    ManyTrees,
}

public enum AnalyzerKind
{
    HOO1001PreferEarlyReturn,
    HOO1002OmitBracesForSingleLineIf,
    HOO1003RemoveRedundantElse,
    HOO1004PreferLoopContinue,
    HOO1005UseTypePattern,
    HOO1006SimplifyBooleanComparison,
    HOO1007UseTryGetValue,
    HOO1008UseNullCoalescingAssignment,
    HOO1009UseThrowIfNull,
    HOO1010MergeNestedIf,
    HOO1011UseDictionaryTryAdd,
    HOO1012UseHashSetAddResult,
    HOO1013UseUsingDeclaration,
    HOO1014UseNullCoalescingExpression,
    HOO1015UseNullConditionalAccess,
    HOO1016UseStringContains,
    HOO1017SimplifyBooleanReturn,
    HOO1018RemoveRedundantNullConditionalGuard,
    HOO1019UseNotPattern,
    HOO1020WrapFluentChain,
}

internal static class AnalyzerCatalog
{
    public static ImmutableArray<DiagnosticAnalyzer> All { get; } =
    [
        new PreferEarlyReturnAnalyzer(),
        new OmitBracesForSingleLineIfAnalyzer(),
        new RemoveRedundantElseAnalyzer(),
        new PreferLoopContinueAnalyzer(),
        new UseTypePatternAnalyzer(),
        new SimplifyBooleanComparisonAnalyzer(),
        new UseTryGetValueAnalyzer(),
        new UseNullCoalescingAssignmentAnalyzer(),
        new UseThrowIfNullAnalyzer(),
        new MergeNestedIfAnalyzer(),
        new UseDictionaryTryAddAnalyzer(),
        new UseHashSetAddResultAnalyzer(),
        new UseUsingDeclarationAnalyzer(),
        new UseNullCoalescingExpressionAnalyzer(),
        new UseNullConditionalAccessAnalyzer(),
        new UseStringContainsAnalyzer(),
        new SimplifyBooleanReturnAnalyzer(),
        new RemoveRedundantNullConditionalGuardAnalyzer(),
        new UseNotPatternAnalyzer(),
        new WrapFluentChainAnalyzer(),
    ];

    public static ImmutableArray<DiagnosticAnalyzer> Collections { get; } =
    [
        new UseTryGetValueAnalyzer(),
        new UseDictionaryTryAddAnalyzer(),
        new UseHashSetAddResultAnalyzer(),
    ];

    public static DiagnosticAnalyzer Create(AnalyzerKind analyzer) => analyzer switch
    {
        AnalyzerKind.HOO1001PreferEarlyReturn => new PreferEarlyReturnAnalyzer(),
        AnalyzerKind.HOO1002OmitBracesForSingleLineIf => new OmitBracesForSingleLineIfAnalyzer(),
        AnalyzerKind.HOO1003RemoveRedundantElse => new RemoveRedundantElseAnalyzer(),
        AnalyzerKind.HOO1004PreferLoopContinue => new PreferLoopContinueAnalyzer(),
        AnalyzerKind.HOO1005UseTypePattern => new UseTypePatternAnalyzer(),
        AnalyzerKind.HOO1006SimplifyBooleanComparison => new SimplifyBooleanComparisonAnalyzer(),
        AnalyzerKind.HOO1007UseTryGetValue => new UseTryGetValueAnalyzer(),
        AnalyzerKind.HOO1008UseNullCoalescingAssignment => new UseNullCoalescingAssignmentAnalyzer(),
        AnalyzerKind.HOO1009UseThrowIfNull => new UseThrowIfNullAnalyzer(),
        AnalyzerKind.HOO1010MergeNestedIf => new MergeNestedIfAnalyzer(),
        AnalyzerKind.HOO1011UseDictionaryTryAdd => new UseDictionaryTryAddAnalyzer(),
        AnalyzerKind.HOO1012UseHashSetAddResult => new UseHashSetAddResultAnalyzer(),
        AnalyzerKind.HOO1013UseUsingDeclaration => new UseUsingDeclarationAnalyzer(),
        AnalyzerKind.HOO1014UseNullCoalescingExpression => new UseNullCoalescingExpressionAnalyzer(),
        AnalyzerKind.HOO1015UseNullConditionalAccess => new UseNullConditionalAccessAnalyzer(),
        AnalyzerKind.HOO1016UseStringContains => new UseStringContainsAnalyzer(),
        AnalyzerKind.HOO1017SimplifyBooleanReturn => new SimplifyBooleanReturnAnalyzer(),
        AnalyzerKind.HOO1018RemoveRedundantNullConditionalGuard => new RemoveRedundantNullConditionalGuardAnalyzer(),
        AnalyzerKind.HOO1019UseNotPattern => new UseNotPatternAnalyzer(),
        AnalyzerKind.HOO1020WrapFluentChain => new WrapFluentChainAnalyzer(),
        _ => throw new ArgumentOutOfRangeException(nameof(analyzer), analyzer, null),
    };
}

[MemoryDiagnoser]
[Config(typeof(PerformanceConfig))]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class AnalyzerBenchmarks
{
    private static readonly ImmutableArray<DiagnosticAnalyzer> NoOpAnalyzers = [new BroadSyntaxNoOpAnalyzer()];
    private readonly Consumer _consumer = new();
    private Compilation? _compilation;
    private AnalyzerOptions? _analyzerOptions;
    private int _allExpected;
    private int _collectionsExpected;

    [Params(100)]
    public int Groups { get; set; }

    [ParamsAllValues]
    public AnalyzerDiagnosticDensity Density { get; set; }

    [ParamsAllValues]
    public AnalyzerTreeShape TreeShape { get; set; }

    [Params(true, false)]
    public bool ConcurrentAnalysis { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        using var workspace = new AdhocWorkspace();
        var sources = BenchmarkSource.CreateAnalyzerSources(Groups, Density, TreeShape);
        var project = RoslynFixture.CreateProject(workspace, sources);
        _compilation = await project.GetCompilationAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("Benchmark compilation could not be created.");
        RoslynFixture.ValidateNoCompilerErrors(_compilation);
        _analyzerOptions = project.AnalyzerOptions;

        _allExpected = await GetStableDiagnosticCountAsync(AnalyzerCatalog.All).ConfigureAwait(false);
        _collectionsExpected = await GetStableDiagnosticCountAsync(AnalyzerCatalog.Collections).ConfigureAwait(false);
        if (Density != AnalyzerDiagnosticDensity.Clean && (_allExpected == 0 || _collectionsExpected == 0))
        {
            throw new InvalidOperationException("Positive analyzer workload produced no diagnostics.");
        }
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("WarmFullCompilation", "Control")]
    public void CompilationDiagnosticsControl()
    {
        var diagnostics = Compilation.GetDiagnostics();
        _consumer.Consume(diagnostics.Length);
    }

    [Benchmark]
    [BenchmarkCategory("WarmFullCompilation", "DriverFloor")]
    public async Task NoOpBroadSyntaxCallbackFloor()
    {
        var count = (await RunAsync(NoOpAnalyzers).ConfigureAwait(false)).Length;
        if (count != 0)
        {
            throw new InvalidOperationException("No-op analyzer unexpectedly reported diagnostics.");
        }
        _consumer.Consume(count);
    }

    [Benchmark]
    [BenchmarkCategory("WarmFullCompilation", "AllAnalyzers")]
    public async Task All20()
    {
        var count = (await RunAsync(AnalyzerCatalog.All).ConfigureAwait(false)).Length;
        EnsureExpected(count, _allExpected, nameof(All20));
        _consumer.Consume(count);
    }

    [Benchmark]
    [BenchmarkCategory("WarmFullCompilation", "Collections")]
    public async Task CollectionHotPaths()
    {
        var count = (await RunAsync(AnalyzerCatalog.Collections).ConfigureAwait(false)).Length;
        EnsureExpected(count, _collectionsExpected, nameof(CollectionHotPaths));
        _consumer.Consume(count);
    }

    private Compilation Compilation => _compilation ?? throw new InvalidOperationException("GlobalSetup was not called.");

    private Task<ImmutableArray<Diagnostic>> RunAsync(ImmutableArray<DiagnosticAnalyzer> analyzers) =>
        Compilation.WithAnalyzers(analyzers, CreateOptions(logAnalyzerExecutionTime: false)).GetAnalyzerDiagnosticsAsync();

    private CompilationWithAnalyzersOptions CreateOptions(bool logAnalyzerExecutionTime) => new(
        _analyzerOptions ?? throw new InvalidOperationException("GlobalSetup was not called."),
        null,
        concurrentAnalysis: ConcurrentAnalysis,
        logAnalyzerExecutionTime: logAnalyzerExecutionTime,
        reportSuppressedDiagnostics: false);

    private async Task<int> GetStableDiagnosticCountAsync(ImmutableArray<DiagnosticAnalyzer> analyzers)
    {
        var first = (await RunAsync(analyzers).ConfigureAwait(false)).Length;
        var second = (await RunAsync(analyzers).ConfigureAwait(false)).Length;
        EnsureExpected(second, first, "fixture validation");
        return first;
    }

    private static void EnsureExpected(int actual, int expected, string workload)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException($"{workload} produced {actual} diagnostics; expected {expected}.");
        }
    }
}

[MemoryDiagnoser]
[Config(typeof(PerformanceConfig))]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class IndividualAnalyzerBenchmarks
{
    private readonly Consumer _consumer = new();
    private Compilation? _compilation;
    private AnalyzerOptions? _analyzerOptions;
    private ImmutableArray<DiagnosticAnalyzer> _analyzer;
    private int _expectedDiagnostics;

    [Params(100)]
    public int Groups { get; set; }

    [ParamsAllValues]
    public AnalyzerKind Analyzer { get; set; }

    [Params(true, false)]
    public bool ConcurrentAnalysis { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        using var workspace = new AdhocWorkspace();
        var sources = BenchmarkSource.CreateAnalyzerSources(
            Groups,
            AnalyzerDiagnosticDensity.Mixed,
            AnalyzerTreeShape.SingleLargeTree);
        var project = RoslynFixture.CreateProject(workspace, sources);
        _compilation = await project.GetCompilationAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("Benchmark compilation could not be created.");
        RoslynFixture.ValidateNoCompilerErrors(_compilation);
        _analyzerOptions = project.AnalyzerOptions;
        _analyzer = [AnalyzerCatalog.Create(Analyzer)];
        _expectedDiagnostics = (await RunAsync().ConfigureAwait(false)).Length;
        var repeatedCount = (await RunAsync().ConfigureAwait(false)).Length;
        if (_expectedDiagnostics == 0 || repeatedCount != _expectedDiagnostics)
        {
            throw new InvalidOperationException($"Mixed fixture for {Analyzer} produced an invalid diagnostic count.");
        }
    }

    [Benchmark]
    [BenchmarkCategory("WarmFullCompilation", "IndividualAnalyzer")]
    public async Task IndividualAnalyzer()
    {
        var count = (await RunAsync().ConfigureAwait(false)).Length;
        if (count != _expectedDiagnostics)
        {
            throw new InvalidOperationException($"{Analyzer} produced {count} diagnostics; expected {_expectedDiagnostics}.");
        }
        _consumer.Consume(count);
    }

    private Task<ImmutableArray<Diagnostic>> RunAsync() =>
        (_compilation ?? throw new InvalidOperationException("GlobalSetup was not called."))
        .WithAnalyzers(
            _analyzer,
            new CompilationWithAnalyzersOptions(
                _analyzerOptions ?? throw new InvalidOperationException("GlobalSetup was not called."),
                null,
                concurrentAnalysis: ConcurrentAnalysis,
                logAnalyzerExecutionTime: false,
                reportSuppressedDiagnostics: false))
        .GetAnalyzerDiagnosticsAsync();
}

[MemoryDiagnoser]
[Config(typeof(PerformanceConfig))]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class AnalyzerTelemetryBenchmarks
{
    private readonly Consumer _consumer = new();
    private CompilationWithAnalyzers? _analysis;
    private int _expectedDiagnostics;

    [Params(100)]
    public int Groups { get; set; }

    [Params(true, false)]
    public bool ConcurrentAnalysis { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        using var workspace = new AdhocWorkspace();
        var project = RoslynFixture.CreateProject(
            workspace,
            BenchmarkSource.CreateAnalyzerSources(Groups, AnalyzerDiagnosticDensity.Mixed, AnalyzerTreeShape.SingleLargeTree));
        var compilation = await project.GetCompilationAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("Benchmark compilation could not be created.");
        RoslynFixture.ValidateNoCompilerErrors(compilation);
        _analysis = compilation.WithAnalyzers(
            AnalyzerCatalog.All,
            new CompilationWithAnalyzersOptions(
                project.AnalyzerOptions,
                null,
                concurrentAnalysis: ConcurrentAnalysis,
                logAnalyzerExecutionTime: true,
                reportSuppressedDiagnostics: false));
        _expectedDiagnostics = (await _analysis.GetAnalyzerDiagnosticsAsync().ConfigureAwait(false)).Length;
        if (_expectedDiagnostics == 0)
        {
            throw new InvalidOperationException("Telemetry fixture produced no diagnostics.");
        }
    }

    [Benchmark]
    [BenchmarkCategory("TelemetryAttribution")]
    public async Task AnalysisResultWithTelemetry()
    {
        var result = await (_analysis ?? throw new InvalidOperationException("GlobalSetup was not called."))
            .GetAnalysisResultAsync(CancellationToken.None)
            .ConfigureAwait(false);
        var diagnostics = result.GetAllDiagnostics();
        if (diagnostics.Length != _expectedDiagnostics)
        {
            throw new InvalidOperationException($"Telemetry analysis produced {diagnostics.Length} diagnostics; expected {_expectedDiagnostics}.");
        }
        _consumer.Consume(diagnostics.Length);
        _consumer.Consume(result.AnalyzerTelemetryInfo.Count);
    }
}

#pragma warning disable RS1036, RS1038, RS1041 // Benchmark-only analyzer executes in the net10.0 harness.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
internal sealed class BroadSyntaxNoOpAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(
            static _ => { },
            SyntaxKind.IfStatement,
            SyntaxKind.LocalDeclarationStatement,
            SyntaxKind.UsingStatement,
            SyntaxKind.ConditionalExpression,
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxKind.EqualsExpression,
            SyntaxKind.NotEqualsExpression,
            SyntaxKind.LogicalNotExpression);
    }
}
#pragma warning restore RS1036, RS1038, RS1041
