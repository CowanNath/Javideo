using System.Net;
using System.Net.Sockets;

namespace Javideo.Worker.Services;

/// <summary>Validate external URLs and the address actually connected to.</summary>
public static class HttpGuard
{
    public static bool IsSafePublicUrl(string? url, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return false;
        if (u.HostNameType != UriHostNameType.Dns || !u.IsDefaultPort || u.UserInfo.Length != 0) return false;
        var host = u.Host.TrimEnd('.').ToLowerInvariant();
        if (host == "localhost" || !host.Contains('.')) return false;
        if (host.EndsWith(".local") || host.EndsWith(".internal") || host.EndsWith(".lan")) return false;
        uri = u;
        return true;
    }

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return b[0] != 0 && b[0] != 10 && b[0] != 127 && b[0] < 224
                && !(b[0] == 100 && b[1] is >= 64 and <= 127)
                && !(b[0] == 169 && b[1] == 254)
                && !(b[0] == 172 && b[1] is >= 16 and <= 31)
                && !(b[0] == 192 && (b[1] == 168 || (b[1] == 0 && b[2] == 0)))
                && !(b[0] == 198 && b[1] is 18 or 19);
        // ponytail: accept IPv6 global unicast only; extend here for new public ranges.
        return (b[0] & 0xe0) == 0x20 && !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8);
    }

    public static HttpClient CreateClient(TimeSpan timeout) => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            var publicAddresses = addresses.Where(IsPublicAddress).ToArray();
            if (publicAddresses.Length == 0) throw new HttpRequestException("下载地址未解析到公网 IP");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(publicAddresses, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        },
    }) { Timeout = timeout };

    public static async Task<HttpResponseMessage> GetAsync(HttpClient client, Uri uri, CancellationToken ct)
    {
        for (var i = 0; i < 6; i++)
        {
            if (!IsSafePublicUrl(uri.AbsoluteUri, out uri))
                throw new HttpRequestException("下载地址不合法");
            var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location == null) throw new HttpRequestException("重定向缺少下载地址");
                uri = new Uri(uri, location);
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode;
                response.Dispose();
                throw new HttpRequestException($"下载请求失败: {(int)status}");
            }
            return response;
        }
        throw new HttpRequestException("下载地址重定向次数过多");
    }
}