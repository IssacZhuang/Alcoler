using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Alcoler.Auth;

internal sealed record SiwcAccountIdentity(string Sub, string? Email, string? Name, string? PlanType, string? AccountId);

/// <summary>
/// The single stored ChatGPT account credential. Tokens never enter the workbench
/// config or any protocol message; this record is the only persistence shape.
/// </summary>
internal sealed record SiwcCredentialRecord(
    int Version,
    string IssuedClientId,
    string HostId,
    long ObtainedAtMs,
    long? ExpiresAtMs,
    string AccessToken,
    string? RefreshToken,
    string Scope,
    string? IdToken,
    IReadOnlyList<string>? ModelIds,
    SiwcAccountIdentity Account)
{
    public const int CurrentVersion = 1;

    public bool IsEntitled => SiwcProtocol.HasDirectTokenScope(Scope);

    // A missing expiry cannot prove validity, so it is treated as near expiry.
    public bool IsNearExpiry(long nowMs) =>
        ExpiresAtMs is null || nowMs >= ExpiresAtMs.Value - (long)SiwcProtocol.ExpiryMargin.TotalMilliseconds;

    /// <summary>
    /// Builds the record from a successful authorization or refresh. Refresh
    /// responses may omit the refresh and id tokens; previous values carry over.
    /// </summary>
    public static SiwcCredentialRecord FromTokens(
        SiwcIdTokenClaims? claims,
        string hostId,
        string issuedClientId,
        SiwcTokenResponse tokens,
        SiwcCredentialRecord? previous = null,
        IReadOnlyList<string>? modelIds = null,
        long? nowMs = null)
    {
        var now = nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var account = claims is not null
            ? new SiwcAccountIdentity(claims.Sub, claims.Email, claims.Name, claims.ChatgptPlanType, claims.ChatgptAccountId)
            : previous?.Account
            ?? throw new ArgumentException("Account identity requires claims or a previous record.");
        return new SiwcCredentialRecord(
            CurrentVersion,
            issuedClientId,
            hostId,
            now,
            tokens.ExpiresInSeconds is { } seconds ? now + seconds * 1000 : null,
            tokens.AccessToken,
            tokens.RefreshToken ?? previous?.RefreshToken,
            tokens.Scope.Length > 0 ? tokens.Scope : previous?.Scope ?? "",
            tokens.IdToken ?? previous?.IdToken,
            modelIds ?? previous?.ModelIds,
            account);
    }
}

internal sealed record SiwcAuthFile(string HostId, SiwcCredentialRecord? ChatGpt = null, string? ChatGptProtected = null);

/// <summary>
/// File-backed credential store at ~/.alcoler/auth.json. The record is DPAPI
/// protected per user on Windows and stored as plain JSON elsewhere; the host id
/// survives sign-out so registrations stay stable per install.
/// </summary>
internal sealed class SiwcCredentialStore
{
    private static readonly byte[] ProtectionEntropy = "Alcoler.SIWC.v1"u8.ToArray();
    private readonly string _path;
    private readonly object _gate = new();

    public SiwcCredentialStore(string path) => _path = path;

    public string ResolveOrCreateHostId()
    {
        lock (_gate)
        {
            var file = ReadFile();
            if (file?.HostId is { Length: > 0 } hostId)
                return hostId;
            hostId = SiwcProtocol.CreateHostId();
            WriteFile(new SiwcAuthFile(hostId));
            return hostId;
        }
    }

    public SiwcCredentialRecord? Load()
    {
        lock (_gate)
        {
            var file = ReadFile();
            if (file is null) return null;
            if (file.ChatGpt is { } record) return Validate(record);
            if (file.ChatGptProtected is { Length: > 0 } protectedJson && OperatingSystem.IsWindows())
            {
        try
        {
            var json = System.Text.Encoding.UTF8.GetString(
                System.Security.Cryptography.ProtectedData.Unprotect(
                    Convert.FromBase64String(protectedJson), ProtectionEntropy,
                    DataProtectionScope.CurrentUser));
            return Validate(JsonSerializer.Deserialize(json, SiwcAuthJsonContext.Default.SiwcCredentialRecord));
        }
                catch (Exception ex) when (ex is FormatException or CryptographicException or JsonException)
                {
                    return null;
                }
            }
            return null;
        }
    }

    public void Save(SiwcCredentialRecord record)
    {
        lock (_gate)
        {
            var hostId = ReadFile()?.HostId is { Length: > 0 } existing ? existing : SiwcProtocol.CreateHostId();
            SiwcAuthFile file;
            if (OperatingSystem.IsWindows())
            {
                var json = JsonSerializer.Serialize(record, SiwcAuthJsonContext.Default.SiwcCredentialRecord);
                var protectedJson = Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(
                    System.Text.Encoding.UTF8.GetBytes(json), ProtectionEntropy,
                    DataProtectionScope.CurrentUser));
                file = new SiwcAuthFile(hostId, ChatGptProtected: protectedJson);
            }
            else
            {
                file = new SiwcAuthFile(hostId, ChatGpt: record);
            }
            WriteFile(file);
        }
    }

    public void DeleteChatGpt()
    {
        lock (_gate)
        {
            var file = ReadFile();
            if (file is null) return;
            WriteFile(new SiwcAuthFile(file.HostId));
        }
    }

    private static SiwcCredentialRecord? Validate(SiwcCredentialRecord? record) =>
        record is { Version: SiwcCredentialRecord.CurrentVersion }
        && record.IssuedClientId.Length > 0
        && record.AccessToken.Length > 0
        && record.Account.Sub.Length > 0
            ? record
            : null;

    private SiwcAuthFile? ReadFile()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            return JsonSerializer.Deserialize(File.ReadAllText(_path), SiwcAuthJsonContext.Default.SiwcAuthFile);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void WriteFile(SiwcAuthFile file)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(string.IsNullOrEmpty(directory) ? "." : directory, Path.GetRandomFileName());
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(file, SiwcAuthJsonContext.Default.SiwcAuthFile));
            File.Move(tempPath, _path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); }
            catch { /* temp cleanup is best effort; the original failure must propagate */ }
            throw;
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SiwcAuthFile))]
[JsonSerializable(typeof(SiwcCredentialRecord))]
[JsonSerializable(typeof(SiwcAccountIdentity))]
internal sealed partial class SiwcAuthJsonContext : JsonSerializerContext;
