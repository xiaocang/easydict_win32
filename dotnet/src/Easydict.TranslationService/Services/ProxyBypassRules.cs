using System.Net;

namespace Easydict.TranslationService.Services;

/// <summary>
/// Builds the <see cref="WebProxy"/> for the user's proxy settings, including the optional
/// China-mainland bypass: services hosted in mainland China are reachable directly there and
/// are often slower or flakier when routed through an overseas proxy.
/// </summary>
public static class ProxyBypassRules
{
    /// <summary>
    /// Registrable domains (and every subdomain) sent direct when the China bypass is on:
    /// the whole <c>.cn</c> TLD plus mainland-hosted services under other TLDs.
    /// </summary>
    internal static readonly IReadOnlyList<string> ChinaMainlandDomains =
    [
        "cn",
        "cn.bing.com",       // Bing's mainland host; www.bing.com stays proxied
        "youdao.com",
        "deepseek.com",
        "volces.com",        // Doubao (Volcengine Ark)
        "volcengine.com",
        "caiyunai.com",
        "niutrans.com",
        "baidu.com",
        "qq.com",
        "aliyuncs.com",
    ];

    /// <summary>
    /// <see cref="WebProxy.BypassList"/> entries are regular expressions matched against
    /// <c>scheme://host[:port]</c>; each one is anchored to a whole domain label so that
    /// e.g. <c>notyoudao.com</c> or <c>youdao.com.example.org</c> stay proxied.
    /// </summary>
    internal static string[] ChinaMainlandBypassList { get; } = ChinaMainlandDomains
        .Select(domain => $@"^[a-z][a-z0-9+.-]*://([^/:]+\.)?{System.Text.RegularExpressions.Regex.Escape(domain)}(:\d+)?$")
        .ToArray();

    public static WebProxy Create(Uri proxyUri, bool bypassLocal, bool bypassChinaMainland)
    {
        var proxy = new WebProxy(proxyUri)
        {
            BypassProxyOnLocal = bypassLocal
        };

        if (bypassChinaMainland)
        {
            proxy.BypassList = ChinaMainlandBypassList;
        }

        return proxy;
    }
}
