using System.Text;

namespace Alcoler.Remote;

internal sealed class AuditLog(string dataDirectory)
{
    private readonly string _filePath = Path.Combine(dataDirectory, "audit.jsonl");
    private readonly object _lock = new();

    public void Write(string action, string? deviceId = null, string? sessionId = null, string? detail = null)
    {
        try
        {
            var line = GatewayJson.Object(writer =>
            {
                writer.WriteString("at", DateTimeOffset.UtcNow);
                if (deviceId is not null)
                    writer.WriteString("deviceId", deviceId);
                writer.WriteString("action", action);
                if (sessionId is not null)
                    writer.WriteString("sessionId", sessionId);
                if (detail is not null)
                    writer.WriteString("detail", detail);
            });
            lock (_lock)
            {
                Directory.CreateDirectory(dataDirectory);
                File.AppendAllText(_filePath, Encoding.UTF8.GetString(line) + Environment.NewLine);
            }
        }
        catch
        {
            // Audit is best-effort and must never break the operation it records.
        }
    }
}
