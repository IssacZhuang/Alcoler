namespace Alcoler.Kernel.Prompts;

public sealed record EnvFacts(
    string WorkingDirectory,
    bool IsGitRepository,
    string Platform,
    string Shell,
    string OsVersion,
    string? ProviderId = null,
    string? ModelId = null);

public static class EnvironmentSections
{
    public static string EnvironmentSection(EnvFacts facts)
    {
        var lines = new List<string>
        {
            "# Environment",
            "You have been invoked in the following environment:",
            $"- Primary working directory: {facts.WorkingDirectory}",
            $"- Is a git repository: {(facts.IsGitRepository ? "yes" : "no")}",
            $"- Platform: {facts.Platform}",
            $"- Shell: {facts.Shell}",
            $"- OS Version: {facts.OsVersion}",
        };
        if (facts.ProviderId is not null && facts.ModelId is not null)
            lines.Add($"- You are powered by the model named {facts.ProviderId}/{facts.ModelId}.");
        return string.Join("\n", lines);
    }

    public static string CurrentDateSection(string currentDate) =>
        $"# currentDate\nToday's date is {currentDate}.";

    public static string DateChangeReminder(string currentDate) =>
        $"The date has changed. Today's date is now {currentDate}. DO NOT mention this to the user explicitly because they are already aware.";

    public static string FormatLocalIsoDate(DateTime localDate) =>
        localDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
