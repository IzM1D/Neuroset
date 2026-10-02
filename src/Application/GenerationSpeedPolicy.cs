using Neuroset.Domain;

namespace Neuroset.Application;

/// <summary>
/// Turns low reasoning effort into an actual fast answer. Low effort on the wire only shortens the
/// model's thinking; the wall-clock cost of a simple request is dominated by extra round trips, so
/// a fast turn also drops the planning tools, caps the tool budget, and asks for a direct answer.
/// </summary>
public static class GenerationSpeedPolicy
{
    /// <summary>Enough iterations for one tool round plus an answer, not for a plan.</summary>
    public const int FastModeMaximumIterations = 4;

    /// <summary>Tools that exist to plan or delegate more work, which is the opposite of a fast turn.</summary>
    private static readonly HashSet<string> PlanningToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "request_work_budget",
        "delegate_coding_task"
    };

    /// <summary>Coding chats keep their full budget: a four-iteration coding agent cannot finish anything.</summary>
    public static bool IsFastMode(ConversationMode mode, ReasoningEffort effort) =>
        mode == ConversationMode.Chat && effort == ReasoningEffort.Low;

    public static bool IsFastMode(ConversationMode mode, ReasoningPreference preference) =>
        mode == ConversationMode.Chat && preference == ReasoningPreference.Low;

    public static int LimitIterations(int iterations) => Math.Clamp(Math.Min(iterations, FastModeMaximumIterations), 1, FastModeMaximumIterations);

    public static T[] WithoutPlanningTools<T>(IEnumerable<T> tools, Func<T, string> nameSelector) =>
        tools.Where(tool => !PlanningToolNames.Contains(nameSelector(tool))).ToArray();

    /// <summary>Replaces the tool-budget preamble, which only makes sense when a budget can grow.</summary>
    public static string BuildFastModePreamble(int iterations) => $"""
        This turn is optimized for speed. Answer the user directly and stop.
        You have {iterations} tool iterations, no budget extension, and no planning or delegation tools.
        Call a tool only when the answer genuinely depends on data you do not have: fresh facts, a file, or a computation you cannot do reliably yourself. Prefer a single round of tool calls, and run independent calls together instead of one after another.
        Do not restate the question, do not describe your plan, and do not pad the answer. Keep it as short as the question allows while still being correct and complete.
        """;

    /// <summary>Used when a fast turn has no tools at all, so the preamble above would be misleading.</summary>
    public const string FastModeDirective =
        "This turn is optimized for speed. Answer the user directly: no restating the question, no plan, no padding. Keep it as short as the question allows while still being correct and complete.";
}
