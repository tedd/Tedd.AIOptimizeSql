using Riok.Mapperly.Abstractions;
using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.WebUI.ViewModels;

namespace Tedd.AIOptimizeSql.WebUI.Mappers;

[Mapper]
public static partial class ResearchIterationRunRowMapper
{
    public static partial ResearchIterationRunRowViewModel ToViewModel(ResearchIterationRunRowSource source);

    public static ResearchIterationRunRowViewModel FromTrackedIteration(ResearchIteration iteration) =>
        ToViewModel(new ResearchIterationRunRowSource(
            iteration,
            iteration.Hypotheses.Count,
            iteration.Hypotheses.Sum(h => h.TimeUsedMs)));
}
