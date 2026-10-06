using Microsoft.Win32;
using System.Net;

namespace ClaudeLinkLite;

public static class Network
{
    public static HttpClientHandler Handler()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        // Follow the user's Windows internet proxy explicitly. Certificate
        // validation remains the Windows/.NET default; never bypass TLS checks.
        using var settings = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        if (Convert.ToInt32(settings?.GetValue("ProxyEnable", 0)) != 1) return handler;
        string proxy = settings?.GetValue("ProxyServer")?.ToString() ?? "";
        if (proxy.Contains('=')) proxy = proxy.Split(';').FirstOrDefault(x => x.StartsWith("https=", StringComparison.OrdinalIgnoreCase))?.Split('=', 2)[1]
            ?? proxy.Split(';').FirstOrDefault(x => x.StartsWith("http=", StringComparison.OrdinalIgnoreCase))?.Split('=', 2)[1] ?? "";
        if (proxy.Length == 0) return handler;
        if (!proxy.Contains("://")) proxy = "http://" + proxy;
        if (Uri.TryCreate(proxy, UriKind.Absolute, out var address)) handler.Proxy = new WebProxy(address) { BypassProxyOnLocal = true };
        return handler;
    }
}
