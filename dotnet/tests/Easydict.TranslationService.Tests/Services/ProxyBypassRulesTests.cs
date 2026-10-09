using Easydict.TranslationService.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

public class ProxyBypassRulesTests
{
    private static readonly Uri ProxyUri = new("http://127.0.0.1:7890");

    [Theory]
    [InlineData("https://api.moonshot.cn/v1/chat/completions")]
    [InlineData("https://open.bigmodel.cn/api/paas/v4/chat/completions")]
    [InlineData("https://cn.bing.com/translator")]
    [InlineData("https://dict.youdao.com/jsonapi")]
    [InlineData("https://api.deepseek.com/chat/completions")]
    [InlineData("https://ark.cn-beijing.volces.com/api/v3/chat/completions")]
    [InlineData("https://api.interpreter.caiyunai.com/v1/translator")]
    [InlineData("https://api.niutrans.com/NiuTransServer/translation")]
    [InlineData("https://example.cn:8443/path")]
    [InlineData("https://YOUDAO.com/")]
    public void ChinaBypass_SendsMainlandHostsDirect(string url)
    {
        var proxy = ProxyBypassRules.Create(ProxyUri, bypassLocal: true, bypassChinaMainland: true);

        proxy.IsBypassed(new Uri(url)).Should().BeTrue();
    }

    [Theory]
    [InlineData("https://www.bing.com/translator")]
    [InlineData("https://translate.googleapis.com/translate_a/single")]
    [InlineData("https://api.openai.com/v1/chat/completions")]
    [InlineData("https://notyoudao.com/")]
    [InlineData("https://youdao.com.example.org/")]
    [InlineData("https://example.com/cn")]
    [InlineData("https://cn.example.com/")]
    public void ChinaBypass_KeepsOtherHostsProxied(string url)
    {
        var proxy = ProxyBypassRules.Create(ProxyUri, bypassLocal: true, bypassChinaMainland: true);

        proxy.IsBypassed(new Uri(url)).Should().BeFalse();
    }

    [Fact]
    public void ChinaBypassOff_ProxiesMainlandHosts()
    {
        var proxy = ProxyBypassRules.Create(ProxyUri, bypassLocal: true, bypassChinaMainland: false);

        proxy.IsBypassed(new Uri("https://api.moonshot.cn/v1")).Should().BeFalse();
        proxy.IsBypassed(new Uri("http://localhost:11434/v1")).Should().BeTrue();
    }

    [Fact]
    public void ChinaBypass_KeepsLocalBypassSetting()
    {
        var proxy = ProxyBypassRules.Create(ProxyUri, bypassLocal: false, bypassChinaMainland: true);

        proxy.IsBypassed(new Uri("http://localhost:11434/v1")).Should().BeFalse();
    }
}
