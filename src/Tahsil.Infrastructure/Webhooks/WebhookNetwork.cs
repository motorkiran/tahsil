using System.Net;
using System.Net.Sockets;

namespace Tahsil.Infrastructure.Webhooks;

/// <summary>
/// Keeps webhooks on the public internet (FND-40): whoever registers an endpoint must not reach Tahsil's own network
/// through it, such as cloud metadata, sidecars or internal services. The check runs when connecting, on the
/// addresses the name resolves to then, so a name that later resolves elsewhere (DNS rebinding) gains nothing.
/// Copied from Kimlik, with NAT64 and 6to4 addresses that embed private IPv4 addresses blocked as well.
/// </summary>
internal static class WebhookNetwork
{
    public static Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> Connect(bool allowPrivateNetworks) =>
        async (context, cancellationToken) =>
        {
            var host = context.DnsEndPoint.Host;
            var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, cancellationToken);
            var allowed = allowPrivateNetworks ? addresses : Array.FindAll(addresses, IsPublic);
            if (allowed.Length == 0)
            {
                throw new HttpRequestException($"Webhooks may not reach {host}: it is on a private network.");
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };

    /// <summary>Whether the address is on the public internet, rather than local, private, shared or reserved.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes switch
            {
                [0, ..] => false,                                  // "this" network
                [10, ..] => false,                                 // private
                [100, >= 64 and <= 127, ..] => false,              // shared address space (carrier-grade NAT)
                [127, ..] => false,                                // loopback
                [169, 254, ..] => false,                           // link-local, cloud metadata among it
                [172, >= 16 and <= 31, ..] => false,               // private
                [192, 0, 0, _] => false,                           // protocol assignments
                [192, 168, ..] => false,                           // private
                [198, 18 or 19, ..] => false,                      // benchmarking
                [>= 224, ..] => false,                             // multicast and reserved
                _ => true,
            };
        }

        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.IPv6None)
            || address.IsIPv6LinkLocal
            || address.IsIPv6SiteLocal
            || address.IsIPv6UniqueLocal
            || address.IsIPv6Multicast)
        {
            return false;
        }

        // Addresses that carry an IPv4 address inside are as public as that address.
        return EmbeddedIPv4(address) is not { } embedded || IsPublic(embedded);
    }

    /// <summary>The IPv4 address inside a NAT64 (<c>64:ff9b::/96</c>) or 6to4 (<c>2002::/16</c>) address.</summary>
    private static IPAddress? EmbeddedIPv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes is [0x00, 0x64, 0xff, 0x9b, 0, 0, 0, 0, 0, 0, 0, 0, ..])
        {
            return new IPAddress(bytes[12..16]);
        }

        return bytes is [0x20, 0x02, ..] ? new IPAddress(bytes[2..6]) : null;
    }
}
