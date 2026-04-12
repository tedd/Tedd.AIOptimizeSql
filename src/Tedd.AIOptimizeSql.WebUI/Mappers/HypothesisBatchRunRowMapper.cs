using Riok.Mapperly.Abstractions;
using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.WebUI.ViewModels;

namespace Tedd.AIOptimizeSql.WebUI.Mappers;

[Mapper]
public static partial class HypothesisBatchRunRowMapper
{
    public static partial HypothesisBatchRunRowViewModel ToViewModel(HypothesisBatchRunRowSource source);

    public static HypothesisBatchRunRowViewModel FromTrackedBatch(HypothesisBatch batch) =>
        ToViewModel(new HypothesisBatchRunRowSource(
            batch,
            batch.Hypotheses.Count,
            batch.Hypotheses.Sum(h => h.TimeUsedMs)));
}
