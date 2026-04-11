using System;
using System.Collections.Generic;
using System.Text;

namespace Tedd.AIOptimizeSql.Database.Models.Enums;

public enum HypothesisBatchState
{
    Stopped,
    Queued,
    Paused,
    Running
}
