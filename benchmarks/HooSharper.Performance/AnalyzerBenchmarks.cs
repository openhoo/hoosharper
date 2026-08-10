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
    private const int OmitBracesDiagnosticsPerPositiveGroup = 12;

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

    public static ImmutableArray<string> AllDiagnosticIds { get; } =
    [
        PreferEarlyReturnAnalyzer.DiagnosticId,
        OmitBracesForSingleLineIfAnalyzer.DiagnosticId,
        RemoveRedundantElseAnalyzer.DiagnosticId,
        PreferLoopContinueAnalyzer.DiagnosticId,
        UseTypePatternAnalyzer.DiagnosticId,
        SimplifyBooleanComparisonAnalyzer.DiagnosticId,
        UseTryGetValueAnalyzer.DiagnosticId,
        UseNullCoalescingAssignmentAnalyzer.DiagnosticId,
        UseThrowIfNullAnalyzer.DiagnosticId,
        MergeNestedIfAnalyzer.DiagnosticId,
        UseDictionaryTryAddAnalyzer.DiagnosticId,
        UseHashSetAddResultAnalyzer.DiagnosticId,
        UseUsingDeclarationAnalyzer.DiagnosticId,
        UseNullCoalescingExpressionAnalyzer.DiagnosticId,
        UseNullConditionalAccessAnalyzer.DiagnosticId,
        UseStringContainsAnalyzer.DiagnosticId,
        SimplifyBooleanReturnAnalyzer.DiagnosticId,
        RemoveRedundantNullConditionalGuardAnalyzer.DiagnosticId,
        UseNotPatternAnalyzer.DiagnosticId,
        WrapFluentChainAnalyzer.DiagnosticId,
    ];

    public static ImmutableArray<string> CollectionDiagnosticIds { get; } =
    [
        UseTryGetValueAnalyzer.DiagnosticId,
        UseDictionaryTryAddAnalyzer.DiagnosticId,
        UseHashSetAddResultAnalyzer.DiagnosticId,
    ];

    public static int ExpectedAllDiagnostics(int groups, AnalyzerDiagnosticDensity density) =>
        ExpectedDiagnosticCounts(groups, density, AllDiagnosticIds).Values.Sum();

    public static int ExpectedCollectionDiagnostics(int groups, AnalyzerDiagnosticDensity density) =>
        ExpectedDiagnosticCounts(groups, density, CollectionDiagnosticIds).Values.Sum();

    public static int ExpectedIndividualDiagnostics(int groups, AnalyzerDiagnosticDensity density, AnalyzerKind analyzer)
    {
        var positiveGroups = ExpectedPositiveGroups(groups, density);
        var negativeGroups = groups - positiveGroups;
        var diagnosticId = GetDiagnosticId(analyzer);
        return (positiveGroups * ExpectedPerPositiveGroup(diagnosticId)) +
            (negativeGroups * ExpectedPerNegativeGroup(diagnosticId));
    }
    public static ImmutableDictionary<string, int> ExpectedDiagnosticCounts(
        int groups,
        AnalyzerDiagnosticDensity density,
        IEnumerable<string> diagnosticIds)
    {
        var positiveGroups = ExpectedPositiveGroups(groups, density);
        var negativeGroups = groups - positiveGroups;
        var builder = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
        foreach (var diagnosticId in diagnosticIds)
        {
            builder.Add(
                diagnosticId,
                (positiveGroups * ExpectedPerPositiveGroup(diagnosticId)) +
                (negativeGroups * ExpectedPerNegativeGroup(diagnosticId)));
        }

        return builder.ToImmutable();
    }


    public static string GetDiagnosticId(AnalyzerKind analyzer) => analyzer switch
    {
        AnalyzerKind.HOO1001PreferEarlyReturn => PreferEarlyReturnAnalyzer.DiagnosticId,
        AnalyzerKind.HOO1002OmitBracesForSingleLineIf => OmitBracesForSingleLineIfAnalyzer.DiagnosticId,
        AnalyzerKind.HOO1003RemoveRedundantElse => RemoveRedundantElseAnalyzer.DiagnosticId,
        AnalyzerKind.HOO1004PreferLoopContinue => PreferLoopContinueAnalyzer.DiagnosticId,
        AnalyzerKind.HOO1005UseTypePattern => UseTypePatternAnalyzer.DiagnosticId,
        AnalyzerKind.HOO1006SimplifyBooleanComparison => SimplifyBooleanComparisonAnalyzer.DiagnosticId,
        AnalyzerKind.HOO1007UseTryGetValue => UseTryGetValueAnalyzer.DiagnosticId,
        AnalyzerKind.HOO1008UseNullCoalescingAssignment => UseNullCoalescingAssignmentAnalyzer.DiagnosticId,
        AnalyzerKind.HOO1009UseThrowIfNull => UseThrowIfNullAnalyzer.DiagnosticId,
        AnalyzerKind.HOO1010MergeNestedIf => MergeNestedIfAnalyzer.DiagnosticId,
        AnalyzerKind.HOO1011UseDictionaryTryAdd => UseDictionaryTryAddAnalyzer.DiagnosticId,
        AnalyzerKind.HOO1012UseHashSetAddResult => UseHashSetAddResultAnalyzer.DiagnosticId,
        AnalyzerKind.HOO1013UseUsingDeclaration => UseUsingDeclarationAnalyzer.DiagnosticId,
        AnalyzerKind.HOO1014UseNullCoalescingExpression => UseNullCoalescingExpressionAnalyzer.DiagnosticId,
        AnalyzerKind.HOO1015UseNullConditionalAccess => UseNullConditionalAccessAnalyzer.DiagnosticId,
        AnalyzerKind.HOO1016UseStringContains => UseStringContainsAnalyzer.DiagnosticId,
        AnalyzerKind.HOO1017SimplifyBooleanReturn => SimplifyBooleanReturnAnalyzer.DiagnosticId,
        AnalyzerKind.HOO1018RemoveRedundantNullConditionalGuard => RemoveRedundantNullConditionalGuardAnalyzer.DiagnosticId,
        AnalyzerKind.HOO1019UseNotPattern => UseNotPatternAnalyzer.DiagnosticId,
        AnalyzerKind.HOO1020WrapFluentChain => WrapFluentChainAnalyzer.DiagnosticId,
        _ => throw new ArgumentOutOfRangeException(nameof(analyzer), analyzer, null),
    };

    private static int ExpectedPerPositiveGroup(string diagnosticId) => diagnosticId switch
    {
        PreferEarlyReturnAnalyzer.DiagnosticId => 4,
        OmitBracesForSingleLineIfAnalyzer.DiagnosticId => OmitBracesDiagnosticsPerPositiveGroup,
        UseTryGetValueAnalyzer.DiagnosticId => 0,
        RemoveRedundantNullConditionalGuardAnalyzer.DiagnosticId => 0,
        _ => 1,
    };
    private static int ExpectedPerNegativeGroup(string diagnosticId) =>
        diagnosticId == PreferEarlyReturnAnalyzer.DiagnosticId ? 3 : 0;


    private static int ExpectedPositiveGroups(int groups, AnalyzerDiagnosticDensity density) =>
        density switch
        {
            AnalyzerDiagnosticDensity.Clean => 0,
            AnalyzerDiagnosticDensity.Sparse => (groups + 15) / 16,
            AnalyzerDiagnosticDensity.Mixed => (groups + 1) / 2,
            AnalyzerDiagnosticDensity.DenseStress => groups,
            _ => throw new ArgumentOutOfRangeException(nameof(density), density, null),
        };

    public static void ValidateDiagnostics(
        ImmutableArray<Diagnostic> diagnostics,
        IReadOnlyDictionary<string, int> expectedCounts,
        string workload)
    {
        if (diagnostics.Any(static diagnostic => diagnostic.Id == "AD0001"))
        {
            throw new InvalidOperationException($"{workload} produced AD0001 analyzer failure.");
        }

        var unexpected = diagnostics.FirstOrDefault(diagnostic => !expectedCounts.ContainsKey(diagnostic.Id));
        if (unexpected is not null)
        {
            throw new InvalidOperationException(
                $"{workload} produced unexpected diagnostic {unexpected.Id}: {unexpected}.");
        }

        foreach (var expected in expectedCounts)
        {
            var actual = diagnostics.Count(diagnostic => diagnostic.Id == expected.Key);
            if (actual != expected.Value)
            {
                throw new InvalidOperationException(
                    $"{workload} produced {actual} {expected.Key} diagnostics; expected {expected.Value}.");
            }
        }
    }
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

        var allExpectedCounts = AnalyzerCatalog.ExpectedDiagnosticCounts(
            Groups,
            Density,
            AnalyzerCatalog.AllDiagnosticIds);
        var collectionExpectedCounts = AnalyzerCatalog.ExpectedDiagnosticCounts(
            Groups,
            Density,
            AnalyzerCatalog.CollectionDiagnosticIds);
        _allExpected = allExpectedCounts.Values.Sum();
        _collectionsExpected = collectionExpectedCounts.Values.Sum();
        await ValidateStableDiagnosticCountAsync(
                AnalyzerCatalog.All,
                allExpectedCounts,
                "all analyzer fixture")
            .ConfigureAwait(false);
        await ValidateStableDiagnosticCountAsync(
                AnalyzerCatalog.Collections,
                collectionExpectedCounts,
                "collection analyzer fixture")
            .ConfigureAwait(false);
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
    private static void EnsureExpected(int actual, int expected, string workload)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"{workload} produced {actual} diagnostics; expected {expected}.");
        }
    }


    private async Task ValidateStableDiagnosticCountAsync(
        ImmutableArray<DiagnosticAnalyzer> analyzers,
        IReadOnlyDictionary<string, int> expectedCounts,
        string workload)
    {
        var first = await RunAsync(analyzers).ConfigureAwait(false);
        AnalyzerCatalog.ValidateDiagnostics(first, expectedCounts, workload);
        var second = await RunAsync(analyzers).ConfigureAwait(false);
        AnalyzerCatalog.ValidateDiagnostics(second, expectedCounts, $"{workload} repeat");
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
        _expectedDiagnostics = AnalyzerCatalog.ExpectedIndividualDiagnostics(
            Groups,
            AnalyzerDiagnosticDensity.Mixed,
            Analyzer);
        var expectedId = AnalyzerCatalog.GetDiagnosticId(Analyzer);
        var expectedCounts = ImmutableDictionary<string, int>.Empty.Add(expectedId, _expectedDiagnostics);
        var first = await RunAsync().ConfigureAwait(false);
        AnalyzerCatalog.ValidateDiagnostics(first, expectedCounts, $"Mixed fixture for {Analyzer}");
        var second = await RunAsync().ConfigureAwait(false);
        AnalyzerCatalog.ValidateDiagnostics(second, expectedCounts, $"Mixed fixture repeat for {Analyzer}");
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
    private Compilation? _compilation;
    private AnalyzerOptions? _analyzerOptions;
    private ImmutableDictionary<string, int> _expectedDiagnosticCounts = ImmutableDictionary<string, int>.Empty;

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
        _compilation = await project.GetCompilationAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("Benchmark compilation could not be created.");
        RoslynFixture.ValidateNoCompilerErrors(_compilation);
        _analyzerOptions = project.AnalyzerOptions;
        _expectedDiagnosticCounts = AnalyzerCatalog.ExpectedDiagnosticCounts(
            Groups,
            AnalyzerDiagnosticDensity.Mixed,
            AnalyzerCatalog.AllDiagnosticIds);

        var result = await CreateAnalysis()
            .GetAnalysisResultAsync(CancellationToken.None)
            .ConfigureAwait(false);
        AnalyzerCatalog.ValidateDiagnostics(
            result.GetAllDiagnostics(),
            _expectedDiagnosticCounts,
            "Telemetry fixture");
    }

    [Benchmark]
    [BenchmarkCategory("TelemetryAttribution")]
    public async Task AnalysisResultWithTelemetry()
    {
        var result = await CreateAnalysis()
            .GetAnalysisResultAsync(CancellationToken.None)
            .ConfigureAwait(false);
        var diagnostics = result.GetAllDiagnostics();
        AnalyzerCatalog.ValidateDiagnostics(
            diagnostics,
            _expectedDiagnosticCounts,
            "Telemetry analysis");
        _consumer.Consume(diagnostics.Length);
        _consumer.Consume(result.AnalyzerTelemetryInfo.Count);
    }

    private CompilationWithAnalyzers CreateAnalysis() =>
        (_compilation ?? throw new InvalidOperationException("GlobalSetup was not called."))
        .WithAnalyzers(
            AnalyzerCatalog.All,
            new CompilationWithAnalyzersOptions(
                _analyzerOptions ?? throw new InvalidOperationException("GlobalSetup was not called."),
                null,
                concurrentAnalysis: ConcurrentAnalysis,
                logAnalyzerExecutionTime: true,
                reportSuppressedDiagnostics: false));
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
