using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using HooSharper.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace HooSharper.Performance;

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

[MemoryDiagnoser]
[Config(typeof(PerformanceConfig))]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class AnalyzerBenchmarks
{
    private static readonly ImmutableArray<DiagnosticAnalyzer> AllAnalyzers =
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

    private static readonly ImmutableArray<DiagnosticAnalyzer> CollectionAnalyzers =
    [
        new UseTryGetValueAnalyzer(),
        new UseDictionaryTryAddAnalyzer(),
        new UseHashSetAddResultAnalyzer(),
    ];

    private readonly Consumer _consumer = new();
    private Compilation? _compilation;
    private AnalyzerOptions? _analyzerOptions;

    [Params(100, 1000)]
    public int Groups { get; set; }


    [GlobalSetup]
    public async Task Setup()
    {
        using var workspace = new AdhocWorkspace();
        var project = RoslynFixture.CreateProject(workspace, BenchmarkSource.CreateAnalyzerSource(Groups));
        _compilation = await project.GetCompilationAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("Benchmark compilation could not be created.");
        _analyzerOptions = project.AnalyzerOptions;
    }

    [Benchmark]
    [BenchmarkCategory("AllAnalyzers")]
    public async Task All20()
    {
        var diagnostics = await RunAsync(AllAnalyzers).ConfigureAwait(false);
        _consumer.Consume(diagnostics.Length);
    }

    [Benchmark]
    [BenchmarkCategory("Collections")]
    public async Task CollectionHotPaths()
    {
        var diagnostics = await RunAsync(CollectionAnalyzers).ConfigureAwait(false);
        _consumer.Consume(diagnostics.Length);
    }


    private Task<ImmutableArray<Diagnostic>> RunAsync(ImmutableArray<DiagnosticAnalyzer> analyzers) =>
        (_compilation ?? throw new InvalidOperationException("GlobalSetup was not called."))
        .WithAnalyzers(
            analyzers,
            new CompilationWithAnalyzersOptions(
                _analyzerOptions ?? throw new InvalidOperationException("GlobalSetup was not called."),
                null,
                concurrentAnalysis: true,
                logAnalyzerExecutionTime: false,
                reportSuppressedDiagnostics: false))
        .GetAnalyzerDiagnosticsAsync();
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

    [Params(100, 1000)]
    public int Groups { get; set; }

    [ParamsAllValues]
    public AnalyzerKind Analyzer { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        using var workspace = new AdhocWorkspace();
        var project = RoslynFixture.CreateProject(workspace, BenchmarkSource.CreateAnalyzerSource(Groups));
        _compilation = await project.GetCompilationAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("Benchmark compilation could not be created.");
        _analyzerOptions = project.AnalyzerOptions;
        _analyzer = [CreateAnalyzer(Analyzer)];
    }

    [Benchmark]
    [BenchmarkCategory("IndividualAnalyzer")]
    public async Task IndividualAnalyzer()
    {
        var diagnostics = await (_compilation ?? throw new InvalidOperationException("GlobalSetup was not called."))
            .WithAnalyzers(
                _analyzer,
                new CompilationWithAnalyzersOptions(
                    _analyzerOptions ?? throw new InvalidOperationException("GlobalSetup was not called."),
                    null,
                    concurrentAnalysis: true,
                    logAnalyzerExecutionTime: false,
                    reportSuppressedDiagnostics: false))
            .GetAnalyzerDiagnosticsAsync()
            .ConfigureAwait(false);
        _consumer.Consume(diagnostics.Length);
    }

    private static DiagnosticAnalyzer CreateAnalyzer(AnalyzerKind analyzer) => analyzer switch
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
