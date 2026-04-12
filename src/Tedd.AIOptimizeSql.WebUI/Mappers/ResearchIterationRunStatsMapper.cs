using Riok.Mapperly.Abstractions;
using Tedd.AIOptimizeSql.WebUI.ViewModels;

namespace Tedd.AIOptimizeSql.WebUI.Mappers;

[Mapper]
public static partial class ResearchIterationRunStatsMapper
{
    public static partial ResearchIterationRunStatsViewModel ToViewModel(ResearchIterationRunStatsSource source);
}
