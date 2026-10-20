namespace Alcoler.Kernel.Runtime;

/// <summary>
/// Mid-turn user input is committed into the running turn (not a new one), wrapped so the
/// model can tell it apart from the turn's original request; text follows ZCode's
/// user_steer presentation.
/// </summary>
internal static class Steering
{
    private const string UserSteerSuffix =
        "This is how Alcoler surfaces messages the user sends mid-turn — within the running turn, often alongside the next tool result, rather than as a separate conversation turn. Address the message above as you continue this turn.";

    public static string FormatUserSteer(string body) =>
        $"<system-reminder>\nThe user sent a new message while you were working:\n{body}\n\n{UserSteerSuffix}\n</system-reminder>";
}
