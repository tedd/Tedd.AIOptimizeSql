using Riok.Mapperly.Abstractions;
using Tedd.AIOptimizeSql.WebUI.ViewModels;

namespace Tedd.AIOptimizeSql.WebUI.Mappers;

[Mapper]
public static partial class HypothesisBatchRunStatsMapper
{
    public static partial HypothesisBatchRunStatsViewModel ToViewModel(HypothesisBatchRunStatsSource source);
}
