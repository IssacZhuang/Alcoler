using System.Text.Json;

namespace Alcoler.Remote;

internal sealed class DeviceRegistry
{
    public sealed record DeviceRecord(
        string Id,
        string Name,
        string TokenHash,
        DateTimeOffset CreatedAt,
        DateTimeOffset LastSeenAt,
        bool Revoked);

    private const int TokenByteLength = 32;
    // Persisting lastSeenAt on every request would churn the devices.json write; once
    // per minute per device is the ceiling.
    private static readonly TimeSpan LastSeenPersistInterval = TimeSpan.FromMinutes(1);

    private readonly string _filePath;
    private readonly object _lock = new();
    private readonly Dictionary<string, DeviceRecord> _devices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastSeenPersistedAt = new(StringComparer.Ordinal);

    private DeviceRegistry(string filePath) => _filePath = filePath;

    public static DeviceRegistry Load(string dataDirectory)
    {
        var registry = new DeviceRegistry(Path.Combine(dataDirectory, "devices.json"));
        registry.TryLoad();
        return registry;
    }

    public DeviceRecord? Authenticate(string token)
    {
        if (token.Length == 0)
            return null;
        var hash = TokenHasher.Hash(token);
        lock (_lock)
        {
            foreach (var device in _devices.Values)
            {
                if (device.Revoked || !TokenHasher.Matches(hash, device.TokenHash))
                    continue;
                Touch(device, DateTimeOffset.UtcNow);
                return _devices[device.Id];
            }
            return null;
        }
    }

    public (DeviceRecord Device, string Token) Register(string name, DateTimeOffset now)
    {
        var token = TokenHasher.NewToken(TokenByteLength);
        var device = new DeviceRecord(
            Guid.NewGuid().ToString("N"),
            name,
            TokenHasher.HashText(token),
            now,
            now,
            Revoked: false);
        lock (_lock)
        {
            _devices[device.Id] = device;
            _lastSeenPersistedAt[device.Id] = now;
            Persist();
        }
        return (device, token);
    }

    public bool Revoke(string deviceId)
    {
        lock (_lock)
        {
            if (!_devices.TryGetValue(deviceId, out var device) || device.Revoked)
                return false;
            _devices[deviceId] = device with { Revoked = true };
            Persist();
            return true;
        }
    }

    public IReadOnlyList<RemoteDevice> List()
    {
        lock (_lock)
        {
            return _devices.Values
                .Where(device => !device.Revoked)
                .OrderByDescending(device => device.LastSeenAt)
                .Select(device => new RemoteDevice(device.Id, device.Name, device.CreatedAt, device.LastSeenAt))
                .ToList();
        }
    }

    private void Touch(DeviceRecord device, DateTimeOffset now)
    {
        _devices[device.Id] = device with { LastSeenAt = now };
        if (_lastSeenPersistedAt.TryGetValue(device.Id, out var persisted) &&
            now - persisted < LastSeenPersistInterval)
            return;
        _lastSeenPersistedAt[device.Id] = now;
        Persist();
    }

    private void TryLoad()
    {
        try
        {
            if (!File.Exists(_filePath))
                return;
            using var document = JsonDocument.Parse(File.ReadAllBytes(_filePath));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return;
            lock (_lock)
            {
                foreach (var entry in document.RootElement.EnumerateArray())
                {
                    var record = ReadRecord(entry);
                    if (record is not null)
                        _devices[record.Id] = record;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // An unreadable registry starts empty rather than failing the gateway.
        }
    }

    private static DeviceRecord? ReadRecord(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object)
            return null;
        if (!TryReadString(entry, "id", out var id) ||
            !TryReadString(entry, "name", out var name) ||
            !TryReadString(entry, "tokenHash", out var tokenHash))
            return null;
        if (!entry.TryGetProperty("createdAt", out var createdAt) ||
            !createdAt.TryGetDateTimeOffset(out var created))
            return null;
        if (!entry.TryGetProperty("lastSeenAt", out var lastSeen) ||
            !lastSeen.TryGetDateTimeOffset(out var seen))
            return null;
        var revoked = entry.TryGetProperty("revoked", out var revokedElement) &&
            revokedElement.ValueKind == JsonValueKind.True;
        return new DeviceRecord(id, name, tokenHash, created, seen, revoked);
    }

    private static bool TryReadString(JsonElement parent, string name, out string value)
    {
        value = "";
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
            return false;
        var text = element.GetString();
        if (text is null)
            return false;
        value = text;
        return true;
    }

    private void Persist()
    {
        // Caller holds _lock.
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartArray();
                foreach (var device in _devices.Values.OrderBy(device => device.CreatedAt))
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", device.Id);
                    writer.WriteString("name", device.Name);
                    writer.WriteString("tokenHash", device.TokenHash);
                    writer.WriteString("createdAt", device.CreatedAt);
                    writer.WriteString("lastSeenAt", device.LastSeenAt);
                    writer.WriteBoolean("revoked", device.Revoked);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            var temporary = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, stream.ToArray());
            File.Move(temporary, _filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Persistence is best-effort; an in-flight device stays authenticated this
            // process lifetime even if its record could not be written.
        }
    }
}
