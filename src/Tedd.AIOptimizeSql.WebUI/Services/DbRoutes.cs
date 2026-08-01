namespace Tedd.AIOptimizeSql.WebUI.Services;

/// <summary>
/// Every URL in the application, in one place.
/// </summary>
/// <remarks>
/// The site is organised around one selected database: you pick it up front and everything
/// after that lives under <c>/db/{id}/…</c>, so a link, a bookmark and a browser tab all carry
/// the database they belong to instead of depending on hidden session state. The only routes
/// outside that prefix are the ones that exist before a database does — the picker, settings,
/// and the documentation.
/// </remarks>
public static class DbRoutes
{
    /// <summary>The database picker / welcome screen.</summary>
    public const string SelectDatabase = "/";

    public const string Documentation = "/documentation";
    public const string DatabaseMigration = "/database/migration";
    public const string Login = "/login";

    // ── Settings ────────────────────────────────────────────────────────────

    public const string Settings = "/settings";

    public const string DatabaseConnections = "/settings/databases";
    public const string NewDatabaseConnection = "/settings/databases/new";
    public static string DatabaseConnection(int id) => $"/settings/databases/{id}";

    public const string AiConnections = "/settings/ai";
    public const string NewAiConnection = "/settings/ai/new";
    public static string AiConnection(int id) => $"/settings/ai/{id}";

    // ── Inside one database ─────────────────────────────────────────────────

    public static string Dashboard(int dbId) => $"/db/{dbId}";

    public static string Browser(int dbId) => $"/db/{dbId}/browser";

    public static string Analyses(int dbId) => $"/db/{dbId}/analyses";
    public static string NewAnalysis(int dbId) => $"/db/{dbId}/analyses/new";
    public static string Analysis(int dbId, int analysisId) => $"/db/{dbId}/analyses/{analysisId}";

    public static string Experiments(int dbId) => $"/db/{dbId}/experiments";
    public static string NewExperiment(int dbId) => $"/db/{dbId}/experiments/new";

    /// <summary>The guided Create Experiment wizard, reachable from the menu, the browser, and findings.</summary>
    public static string ExperimentWizard(int dbId) => $"/db/{dbId}/experiments/wizard";

    /// <summary>The wizard seeded from an analysis finding, so the AI starts from a known problem.</summary>
    public static string ExperimentWizardForFinding(int dbId, int findingId) =>
        $"/db/{dbId}/experiments/wizard?finding={findingId}";

    public static string Experiment(int dbId, int experimentId) => $"/db/{dbId}/experiments/{experimentId}/view";
    public static string EditExperiment(int dbId, int experimentId) => $"/db/{dbId}/experiments/{experimentId}/edit";

    public static string ResearchIterations(int dbId) => $"/db/{dbId}/research-iterations";

    public static string ExperimentIterations(int dbId, int experimentId) =>
        $"/db/{dbId}/experiments/{experimentId}/research-iterations";

    public static string NewExperimentIteration(int dbId, int experimentId) =>
        $"/db/{dbId}/experiments/{experimentId}/research-iterations/new";

    public static string Iteration(int dbId, int experimentId, int iterationId) =>
        $"/db/{dbId}/experiments/{experimentId}/research-iterations/{iterationId}";

    public static string EditIteration(int dbId, int experimentId, int iterationId) =>
        $"/db/{dbId}/experiments/{experimentId}/research-iterations/{iterationId}/edit";

    public static string NewHypothesis(int dbId, int experimentId, int iterationId) =>
        $"/db/{dbId}/experiments/{experimentId}/research-iterations/{iterationId}/hypotheses/new";

    public static string Hypothesis(int dbId, int experimentId, int iterationId, int hypothesisId) =>
        $"/db/{dbId}/experiments/{experimentId}/research-iterations/{iterationId}/hypotheses/{hypothesisId}";

    public static string BenchmarkRun(int dbId, int experimentId, int iterationId, int benchmarkRunId) =>
        $"/db/{dbId}/experiments/{experimentId}/research-iterations/{iterationId}/benchmarks/{benchmarkRunId}";

    /// <summary>
    /// Reads the database id out of a <c>/db/{id}/…</c> path. Returns null for every route that
    /// is not database-scoped, which is what tells the layout to hide the in-database navigation.
    /// </summary>
    public static int? TryGetDatabaseId(string relativePath)
    {
        var path = relativePath.Split('?', 2)[0].Split('#', 2)[0].Trim('/');
        if (path.Length == 0)
            return null;

        var segments = path.Split('/');
        if (segments.Length < 2 || !string.Equals(segments[0], "db", StringComparison.OrdinalIgnoreCase))
            return null;

        return int.TryParse(segments[1], out var id) ? id : null;
    }

    /// <summary>
    /// Routes that must render before (or without) a usable metadata database: the login page is
    /// served to unauthenticated visitors, and the migration page exists precisely because the
    /// database is not ready.
    /// </summary>
    public static bool IsDatabaseFreeRoute(string relativePath)
    {
        var path = relativePath.Split('?', 2)[0].Trim('/');
        return string.Equals(path, "database/migration", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, "documentation", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, "login", StringComparison.OrdinalIgnoreCase);
    }
}
