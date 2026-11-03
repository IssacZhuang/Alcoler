using System.Net;
using System.Net.Sockets;

namespace Alcoler.Remote;

internal static class NetworkPolicy
{
    public static bool IsPrivateClient(IPAddress? address)
    {
        if (address is null)
            return true;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address))
            return true;
        return address.AddressFamily == AddressFamily.InterNetwork
            ? IsPrivateV4(address)
            : IsPrivateV6(address);
    }

    private static bool IsPrivateV4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 &&
            (bytes[0] == 10 ||
            (bytes[0] == 172 && (uint)(bytes[1] - 16) <= 15) ||
            (bytes[0] == 192 && bytes[1] == 168) ||
            (bytes[0] == 169 && bytes[1] == 254) ||
            // RFC 6598 CGNAT range; overlay networks (Tailscale tailnets) source
            // their clients from here with device-level encryption.
            (bytes[0] == 100 && (uint)(bytes[1] - 64) <= 63));
    }

    private static bool IsPrivateV6(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 16 &&
            ((bytes[0] & 0xFE) == 0xFC || // fc00::/7 unique local
            (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)); // fe80::/10 link local
    }
}
