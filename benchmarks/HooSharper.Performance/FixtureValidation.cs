namespace HooSharper.Performance;

/// <summary>Exercise benchmark correctness without collecting timing measurements.</summary>
internal static class FixtureValidation
{
    public static async Task RunAsync()
    {
        var analyzerCases = 0;
        foreach (var density in Enum.GetValues<AnalyzerDiagnosticDensity>())
        foreach (var shape in Enum.GetValues<AnalyzerTreeShape>())
        foreach (var concurrent in new[] { false, true })
        {
            var benchmark = new AnalyzerBenchmarks
            {
                Groups = 4,
                Density = density,
                TreeShape = shape,
                ConcurrentAnalysis = concurrent,
            };
            await benchmark.Setup();
            await benchmark.All20();
            await benchmark.CollectionHotPaths();
            analyzerCases++;
        }

        var fixerCases = 0;
        foreach (var scenario in Enum.GetValues<FixerScenario>())
        foreach (var groups in new[] { 1, 10 })
        {
            var fixture = FixerBenchmarkFixture.CreateFixture(scenario, groups);
            using (var state = await FixerBenchmarkFixture.CreateActionStateAsync(fixture))
            {
                var operations = await state.Action.GetOperationsAsync(CancellationToken.None);
                await FixerBenchmarkFixture.ApplyAndMeasureAsync(state, operations);
                await FixerBenchmarkFixture.ValidateAppliedCompilationAsync(state);
            }

            using (var state = await FixerBenchmarkFixture.CreateFixAllStateAsync(fixture, groups))
            {
                var operations = await state.Action.GetOperationsAsync(CancellationToken.None);
                await FixerBenchmarkFixture.ApplyAndMeasureAsync(state.ActionState, operations);
                await FixerBenchmarkFixture.ValidateFixAllResultAsync(state);
            }

            Console.WriteLine($"Validated {scenario}: {groups} diagnostic(s), individual fix and Fix All.");
            fixerCases++;
        }

        Console.WriteLine($"Validated {analyzerCases} analyzer workloads and {fixerCases} code-fix workloads.");
    }
}
