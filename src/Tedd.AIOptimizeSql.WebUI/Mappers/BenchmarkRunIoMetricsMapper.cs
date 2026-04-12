using Riok.Mapperly.Abstractions;
using Tedd.AIOptimizeSql.Database.Models;

namespace Tedd.AIOptimizeSql.WebUI.Mappers;

[Mapper]
public static partial class BenchmarkRunIoMetricsMapper
{
    [MapperIgnoreSource(nameof(BenchmarkRun.Id))]
    [MapperIgnoreSource(nameof(BenchmarkRun.TotalTimeMs))]
    [MapperIgnoreSource(nameof(BenchmarkRun.TotalServerCpuTimeMs))]
    [MapperIgnoreSource(nameof(BenchmarkRun.TotalServerElapsedTimeMs))]
    [MapperIgnoreSource(nameof(BenchmarkRun.ActualPlanXml))]
    [MapperIgnoreSource(nameof(BenchmarkRun.Messages))]
    [MapperIgnoreSource(nameof(BenchmarkRun.CreatedAt))]
    public static partial BenchmarkRunIoMetricsSource ToMetricsSource(BenchmarkRun run);
}
