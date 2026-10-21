using System.Text.Json.Serialization;

namespace Alcoler.Contracts.Tools;

[JsonConverter(typeof(JsonStringEnumConverter<ShellDialect>))]
public enum ShellDialect
{
    Posix,
    Cmd,
    GitBash,
}

public sealed record ShellEnvironment(string FileName, ShellDialect Dialect)
{
    [JsonIgnore]
    public string Name => Dialect switch
    {
        ShellDialect.Cmd => "cmd.exe",
        ShellDialect.GitBash => "Git Bash",
        _ => Path.GetFileName(FileName),
    };

    [JsonIgnore]
    public bool IsPosix => Dialect != ShellDialect.Cmd;

    [JsonIgnore]
    public string Arguments => IsPosix ? "-c" : "/c";
}
