using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace TrMarketplaceHubDesktop.Catalog;

/// Builds an HttpClient whose transport resolves DNS itself and validates every candidate
/// address as public before connecting, closing SSRF/DNS-rebinding gaps that a plain
/// HttpClientHandler (auto-redirect, OS-level DNS-then-connect) leaves open.
public static class SafeRemoteHttp
{
    public static HttpClient CreateClient(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectCallback = ConnectAsync,
        };
        return new HttpClient(handler) { Timeout = timeout };
    }

    static async ValueTask<System.IO.Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal)) addresses = [literal];
        else addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

        var safe = addresses.Where(IsPublicAddress).ToArray();
        if (safe.Length == 0) throw new HttpRequestException("Hedef adres güvenli/genel ağ aralığında değil.");

        Exception? last = null;
        foreach (var address in safe)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(address, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                socket.Dispose();
                last = ex;
            }
        }
        throw new HttpRequestException("Hedefe güvenli bağlantı kurulamadı.", last);
    }

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            if (b[0] == 0) return false;
            if (b[0] == 10) return false;
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false; // 100.64.0.0/10 CGNAT
            if (b[0] == 127) return false;
            if (b[0] == 169 && b[1] == 254) return false; // link-local, incl. 169.254.169.254 cloud metadata
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;
            if (b[0] == 192 && b[1] == 0 && b[2] == 0) return false; // IETF protocol assignments
            if (b[0] == 192 && b[1] == 168) return false;
            if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return false; // benchmarking
            if (b[0] >= 224) return false; // multicast + reserved
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return false;
            if (address.Equals(IPAddress.IPv6Loopback) || address.Equals(IPAddress.IPv6Any)) return false;
            var b = address.GetAddressBytes();
            if ((b[0] & 0xfe) == 0xfc) return false; // fc00::/7 unique local
            return true;
        }

        return false;
    }
}
