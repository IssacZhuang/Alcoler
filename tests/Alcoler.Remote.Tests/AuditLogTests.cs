using System.Net;
using System.Text.Json;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Identifiers;
using Alcoler.Remote.Tests.Infrastructure;
using Xunit;

namespace Alcoler.Remote.Tests;

[Collection("api-gateway")]
public sealed class AuditLogTests
{
    private readonly GatewayHarness _harness;

    public AuditLogTests(GatewayHarness harness) => _harness = harness;

    [Fact]
    public async Task AuditLog_RecordsPairAndCommand_WithoutDeviceTokenSecrets()
    {
        var (deviceId, deviceToken) = await _harness.PairAsync();
        var sessionId = await _harness.CreateSessionAsync(deviceToken, title: "audited");
        using var ack = await _harness.PostCommandAsync(
            sessionId, new SendTextCommand(StrongId.NewCommandId(), sessionId, BaseRevision: 0, Text: "audited turn"), deviceToken);
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);

        var path = Path.Combine(_harness.DataDirectory, "audit.jsonl");
        Assert.True(File.Exists(path), $"expected {path} to exist");
        var lines = await File.ReadAllLinesAsync(path);
        Assert.NotEmpty(lines);

        var actions = new List<string>();
        var pairSucceeded = false;
        var command = false;
        foreach (var line in lines)
        {
            if (line.Length == 0)
                continue;
            using var entry = JsonDocument.Parse(line);
            var root = entry.RootElement;
            var action = root.GetProperty("action").GetString();
            Assert.False(string.IsNullOrEmpty(action));
            actions.Add(action!);
            if (action == "pair_succeeded")
            {
                pairSucceeded = root.GetProperty("deviceId").GetString() == deviceId;
            }
            else if (action == "command")
            {
                command = root.GetProperty("deviceId").GetString() == deviceId &&
                    root.GetProperty("sessionId").GetString() == sessionId.Value &&
                    root.GetProperty("detail").GetString()?.Contains("send_text", StringComparison.Ordinal) == true;
            }
        }

        Assert.Contains("gateway_started", actions);
        Assert.True(pairSucceeded, "audit.jsonl lacks a pair_succeeded entry for the paired device");
        Assert.True(command, "audit.jsonl lacks a command entry with the device and session ids");
        var allText = string.Join(Environment.NewLine, lines);
        Assert.DoesNotContain(deviceToken, allText, StringComparison.Ordinal);
    }
}
