namespace Alcoler.Remote;

public sealed record RemoteDevice(string Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt);
