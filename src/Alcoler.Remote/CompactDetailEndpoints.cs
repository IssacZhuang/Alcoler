using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Alcoler.Remote;

internal static class CompactDetailEndpoints
{
    public static void Map(WebApplication app, GatewayState state)
    {
        app.MapGet("/api/sessions/{sessionId}/turns/{rowId}", async (
            string sessionId, string rowId, HttpRequest request, CancellationToken cancellationToken) =>
        {
            if (!TryReadVersion(request, out var expected))
                return GatewayJson.Error(400, "bad_request", "version");
            var snapshot = await CaptureSnapshotAsync(state, new SessionId(sessionId), cancellationToken);
            if (snapshot is null)
                return GatewayJson.Error(404, "not_found", "session");
            var summary = CompactTranscript.FindTurn(snapshot, rowId);
            if (summary is null)
                return GatewayJson.Error(404, "not_found", "turn");
            return expected is not null && !string.Equals(expected, summary.Version, StringComparison.Ordinal)
                ? GatewayJson.Plain(CompactJson.StaleDetails(summary.Version, snapshot.Sequence), StatusCodes.Status409Conflict)
                : GatewayJson.Plain(CompactJson.TurnDetails(summary, snapshot.Sequence));
        });

        app.MapGet("/api/sessions/{sessionId}/rows/{rowId}", async (
            string sessionId, string rowId, HttpRequest request, CancellationToken cancellationToken) =>
        {
            if (!TryReadVersion(request, out var expected))
                return GatewayJson.Error(400, "bad_request", "version");
            var snapshot = await CaptureSnapshotAsync(state, new SessionId(sessionId), cancellationToken);
            if (snapshot is null)
                return GatewayJson.Error(404, "not_found", "session");
            var row = snapshot.Rows.FirstOrDefault(candidate => string.Equals(candidate.RowId, rowId, StringComparison.Ordinal));
            if (row is null)
                return GatewayJson.Error(404, "not_found", "row");
            var version = CompactContentVersion.ForRow(row);
            return expected is not null && !string.Equals(expected, version, StringComparison.Ordinal)
                ? GatewayJson.Plain(CompactJson.StaleDetails(version, snapshot.Sequence), StatusCodes.Status409Conflict)
                : GatewayJson.Plain(CompactJson.RowDetails(row, version, snapshot.Sequence));
        });
    }

    private static async ValueTask<ConversationSnapshot?> CaptureSnapshotAsync(
        GatewayState state, SessionId sessionId, CancellationToken cancellationToken)
    {
        var snapshot = state.Streams.TryCaptureSnapshot(sessionId);
        if (snapshot is not null)
            return snapshot;
        using var projection = await SessionStream.ResolveProjectionAsync(state.Options.Runtime(), sessionId, cancellationToken);
        return projection?.Snapshot();
    }

    private static bool TryReadVersion(HttpRequest request, out string? version)
    {
        var values = request.Query["version"];
        version = values.Count == 0 ? null : values.ToString();
        return values.Count <= 1 && (version is null || version.Length is > 0 and <= 128);
    }
}
