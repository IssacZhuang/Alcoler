using Alcoler.Contracts.Projections;
using Alcoler.Localization;

namespace Alcoler.ViewModel;

internal static class SessionActivityText
{
    public static string Phase(TurnPhase phase) => phase switch
    {
        TurnPhase.ProcessingInput => Loc.Get("status_phase_preparing"),
        TurnPhase.AwaitingModel => Loc.Get("status_phase_awaiting_model"),
        TurnPhase.Streaming => Loc.Get("status_phase_streaming"),
        TurnPhase.ExecutingTools => Loc.Get("status_phase_running_tools"),
        TurnPhase.AwaitingPermission => Loc.Get("status_phase_awaiting_permission"),
        TurnPhase.AwaitingInteraction => Loc.Get("status_phase_awaiting_interaction"),
        TurnPhase.Completing => Loc.Get("status_phase_completing"),
        _ => string.Empty,
    };

    public static string Duration(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;
        if (elapsed.TotalHours >= 1)
            return Loc.Get("duration_hours_minutes", (int)elapsed.TotalHours, elapsed.Minutes);
        if (elapsed.TotalMinutes >= 1)
            return Loc.Get("duration_minutes_seconds", (int)elapsed.TotalMinutes, elapsed.Seconds);
        return Loc.Get("duration_seconds", (int)elapsed.TotalSeconds);
    }
}
