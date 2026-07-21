using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;

namespace HooSharper.Performance;

internal sealed class PerformanceConfig : ManualConfig
{
    public PerformanceConfig()
    {
        AddJob(Job.Default
            .WithWarmupCount(5)
            .WithIterationCount(12)
            .WithMinIterationTime(Perfolizer.Horology.TimeInterval.FromMilliseconds(200))
            .WithId("StableLocal"));
        AddExporter(JsonExporter.Full);
    }
}

internal sealed class SingleInvocationPerformanceConfig : ManualConfig
{
    public SingleInvocationPerformanceConfig()
    {
        AddJob(Job.Default
            .WithWarmupCount(5)
            .WithIterationCount(12)
            .WithInvocationCount(1)
            .WithUnrollFactor(1)
            .WithId("SingleInvocation"));
        AddExporter(JsonExporter.Full);
    }
}
