using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Codex.Decisions;

internal sealed partial class DecisionProtocolInput
{
    private static readonly IPNetwork[] NonPublicIpv4 = [
        .. new[] { "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16",
            "172.16.0.0/12", "192.0.0.0/24", "192.0.2.0/24", "192.88.99.0/24", "192.168.0.0/16",
            "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/3" }.Select(IPNetwork.Parse)
    ];
    private static readonly IPNetwork GlobalIpv6 = IPNetwork.Parse("2000::/3");
    private static readonly IPNetwork[] NonPublicIpv6 = [
        IPNetwork.Parse("2001::/23"), IPNetwork.Parse("2001:db8::/32"), IPNetwork.Parse("2002::/16"), IPNetwork.Parse("3fff::/20")
    ];
    private static readonly HttpClient ImageHttp = new(new SocketsHttpHandler
    {
        UseProxy = false,
        UseCookies = false,
        AllowAutoRedirect = false,
        ConnectCallback = ConnectPublicAsync,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        MaxResponseHeadersLength = 32
    });
    private readonly Dictionary<string, string> imageData = new(StringComparer.Ordinal);

    private async Task<string> ResolveImageAsync(string image, string parameter, CancellationToken ct)
    {
        if (image.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var separator = image.IndexOf(',');
            if (separator is < 6 or > 32 || !image.AsSpan(0, separator).EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                throw Invalid("图片 data URL 必须使用 base64。", parameter);
            var mime = image[5..(separator - 7)].ToLowerInvariant();
            return DataUrl(image[(separator + 1)..], mime, parameter, out _);
        }
        if (!Uri.TryCreate(image, UriKind.Absolute, out var target))
            throw Invalid("图片需要公开 HTTP(S) URL 或 data URL。", parameter);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            for (var redirects = 0; ; redirects++)
            {
                if (target.Scheme is not ("http" or "https") || target.UserInfo.Length != 0 ||
                    target.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6))
                    throw Invalid("图片 URL 必须是公开的 HTTP(S) 地址。", parameter);
                if (IPAddress.TryParse(target.DnsSafeHost, out var address) && !IsPublic(address))
                    throw Invalid("拒绝从非公开地址下载图片。", parameter);
                using var reply = await ImageHttp.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                if ((int)reply.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    if (redirects >= 3 || reply.Headers.Location is not { } location)
                        throw Invalid("图片重定向超出限制或地址无效。", parameter);
                    target = new Uri(target, location);
                    continue;
                }
                if (!reply.IsSuccessStatusCode) throw Invalid("无法下载图片。", parameter);
                if (reply.Content.Headers.ContentLength > ImageMaxBytes)
                    throw ImageTooLarge(parameter);
                using var data = new MemoryStream();
                using var content = await reply.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                var buffer = new byte[8192];
                int read;
                while ((read = await content.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0)
                {
                    if (data.Length + read > ImageMaxBytes) throw ImageTooLarge(parameter);
                    data.Write(buffer, 0, read);
                }
                var bytes = data.GetBuffer().AsSpan(0, checked((int)data.Length));
                var mime = Mime(bytes);
                if (mime is null) throw Invalid("远程资源不是支持的图片。", parameter);
                return EncodeImage(bytes, mime, parameter);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw Invalid("图片下载超时。", parameter);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or SocketException or UriFormatException)
        {
            throw Invalid("无法从公开的 HTTP(S) 地址下载图片。", parameter);
        }
    }

    private string DataUrl(string encoded, string mime, string parameter, out int size)
    {
        if (mime is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp") || encoded.Any(char.IsWhiteSpace))
            throw Invalid("不支持该图片格式或 base64 编码。", parameter);
        byte[] data;
        try { data = Convert.FromBase64String(encoded); }
        catch (FormatException) { throw Invalid("图片 base64 无效。", parameter); }
        if (Mime(data) != mime) throw Invalid("图片内容与声明格式不一致。", parameter);
        size = data.Length;
        return EncodeImage(data, mime, parameter);
    }

    private string EncodeImage(ReadOnlySpan<byte> data, string mime, string parameter)
    {
        if (data.Length > ImageMaxBytes) throw ImageTooLarge(parameter);
        pictureCount++;
        var hash = Convert.ToHexString(SHA256.HashData(data));
        if (imageData.TryGetValue(hash, out var existing)) return existing;
        if (data.Length > TotalImageBytes - imageBytes) throw ImageTooLarge(parameter);
        imageBytes += data.Length;
        var url = "data:" + mime + ";base64," + Convert.ToBase64String(data);
        imageData.Add(hash, url);
        return url;
    }

    private static DecisionProtocolException ImageTooLarge(string parameter) =>
        new(413, "request_too_large", "每张图片最多 16 MiB，合计最多 32 MiB。", parameter);

    private static string? Mime(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a })) return "image/png";
        if (data.StartsWith(new byte[] { 0xff, 0xd8, 0xff })) return "image/jpeg";
        if (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8)) return "image/gif";
        if (data.Length >= 12 && data.StartsWith("RIFF"u8) && data[8..].StartsWith("WEBP"u8)) return "image/webp";
        return null;
    }

    private static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => NonPublicIpv4.All(network => !network.Contains(address)),
            AddressFamily.InterNetworkV6 => address.ScopeId == 0 && GlobalIpv6.Contains(address) &&
                NonPublicIpv6.All(network => !network.Contains(address)),
            _ => false
        };
    }

    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var endpoint = context.DnsEndPoint;
        var addresses = await Dns.GetHostAddressesAsync(endpoint.Host, ct).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(address => !IsPublic(address)))
            throw new HttpRequestException("Image host did not resolve to public addresses.");
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(address, endpoint.Port, ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException) { socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        throw new HttpRequestException("Could not connect to public image host.");
    }
}
