using System.Text;

namespace Alcoler.Kernel.Runtime;

/// <summary>
/// Mid-turn user input is committed into the running turn (not a new one), wrapped so the
/// model can tell it apart from the turn's original request; text follows ZCode's
/// user_steer presentation. Admission mirrors the reference steer limits: inputs must be
/// non-empty and at most MAX_TURN_STEER_INPUT_BYTES UTF-8 bytes.
/// </summary>
internal static class Steering
{
    public const int MaxSteerInputBytes = 200_000;

    public const string EmptyInputReason = "empty_input";
    public const string InputTooLargeReason = "input_too_large";

    private const string UserSteerSuffix =
        "This is how Alcoler surfaces messages the user sends mid-turn — within the running turn, often alongside the next tool result, rather than as a separate conversation turn. Address the message above as you continue this turn.";

    private const string UserSteerPrefix =
        "<system-reminder>\nThe user sent a new message while you were working:\n";

    private const string UserSteerClosing = "\n\n" + UserSteerSuffix + "\n</system-reminder>";

    public static string? ValidateInput(string text, bool hasImages = false)
    {
        if (!hasImages && string.IsNullOrWhiteSpace(text))
            return EmptyInputReason;
        if (MeasureUtf8Bytes(text) > MaxSteerInputBytes)
            return InputTooLargeReason;
        return null;
    }

    public static int MeasureUtf8Bytes(string value) => Encoding.UTF8.GetByteCount(value);

    public static string FormatUserSteer(string body) =>
        string.Concat(UserSteerPrefix, body, UserSteerClosing);

    public static int FormattedUserSteerLength(string body) =>
        UserSteerPrefix.Length + body.Length + UserSteerClosing.Length;
}
