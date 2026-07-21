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
