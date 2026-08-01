namespace Tedd.AIOptimizeSql.Database.Models.Enums;

/// <summary>
/// What an <see cref="AiConversation"/> was started for. Used to break token
/// spend down by activity on the token usage dashboard.
/// </summary>
public enum AiConversationKind
{
    /// <summary>The AI deep dive of a database analysis run.</summary>
    DatabaseAnalysis,

    /// <summary>Generating one hypothesis inside a research iteration.</summary>
    Hypothesis,

    /// <summary>Combining the successful hypotheses of an iteration into one.</summary>
    CombinedHypothesis,

    /// <summary>Repairing SQL that failed to apply or revert.</summary>
    HypothesisRepair,

    /// <summary>Completing an experiment blueprint in the Create Experiment wizard.</summary>
    ExperimentBlueprint,

    /// <summary>Writing or finishing the sandbox setup/teardown scripts.</summary>
    SandboxScript,

    /// <summary>Filling in a manually created hypothesis from the UI.</summary>
    HypothesisSuggestion,
}
