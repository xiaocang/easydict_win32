using System.Text.Json;
using Easydict.TranslationService.LocalApi;
using Easydict.TranslationService.Models;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.LocalApi;

public class OpenAIMessageMapperTests
{
    [Fact]
    public void Empty_messages_returns_error()
    {
        var r = OpenAIMessageMapper.Map(
            new ChatRequest { Model = "easydict-openai", Messages = new() },
            Language.SimplifiedChinese);
        r.Request.Should().BeNull();
        r.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Empty_user_content_returns_error()
    {
        var r = OpenAIMessageMapper.Map(
            BuildRequest(("user", "   ")),
            Language.SimplifiedChinese);
        r.Request.Should().BeNull();
    }

    [Fact]
    public void User_message_becomes_text()
    {
        var r = OpenAIMessageMapper.Map(
            BuildRequest(("user", "Hello world")),
            Language.SimplifiedChinese);
        r.Request!.Text.Should().Be("Hello world");
        r.Request!.ToLanguage.Should().Be(Language.SimplifiedChinese);
        r.Request!.FromLanguage.Should().Be(Language.Auto);
    }

    [Fact]
    public void Multiple_user_messages_are_concatenated()
    {
        var r = OpenAIMessageMapper.Map(
            BuildRequest(("user", "first"), ("user", "second")),
            Language.English);
        r.Request!.Text.Should().Be("first\n\nsecond");
    }

    [Fact]
    public void Extra_body_target_language_overrides_default()
    {
        var req = BuildRequest(("user", "你好"));
        req.ExtraBody = ParseJson("{\"easydict\":{\"source_language\":\"zh\",\"target_language\":\"en\"}}");
        var r = OpenAIMessageMapper.Map(req, Language.SimplifiedChinese);
        r.Request!.ToLanguage.Should().Be(Language.English);
        r.Request!.FromLanguage.Should().Be(Language.SimplifiedChinese);
    }

    [Fact]
    public void System_prompt_regex_extracts_target()
    {
        var req = BuildRequest(("system", "Translate to English."), ("user", "你好"));
        var r = OpenAIMessageMapper.Map(req, Language.SimplifiedChinese);
        r.Request!.ToLanguage.Should().Be(Language.English);
    }

    [Fact]
    public void System_prompt_regex_extracts_from_and_to()
    {
        var req = BuildRequest(("system", "translate from zh to ja"), ("user", "你好"));
        var r = OpenAIMessageMapper.Map(req, Language.English);
        r.Request!.FromLanguage.Should().Be(Language.SimplifiedChinese);
        r.Request!.ToLanguage.Should().Be(Language.Japanese);
    }

    [Fact]
    public void Extra_body_wins_over_system_regex()
    {
        var req = BuildRequest(("system", "translate to ko"), ("user", "hi"));
        req.ExtraBody = ParseJson("{\"easydict\":{\"target_language\":\"ja\"}}");
        var r = OpenAIMessageMapper.Map(req, Language.English);
        r.Request!.ToLanguage.Should().Be(Language.Japanese);
    }

    [Fact]
    public void Unknown_iso_code_does_not_override_default()
    {
        var req = BuildRequest(("user", "hi"));
        req.ExtraBody = ParseJson("{\"easydict\":{\"target_language\":\"xx-XX\"}}");
        var r = OpenAIMessageMapper.Map(req, Language.SimplifiedChinese);
        r.Request!.ToLanguage.Should().Be(Language.SimplifiedChinese);
    }

    [Fact]
    public void Vision_content_parts_text_only_is_extracted()
    {
        var req = new ChatRequest
        {
            Model = "easydict-openai",
            Messages = new()
            {
                new ChatMessage { Role = "user", Content = ParseJson(
                    "[{\"type\":\"text\",\"text\":\"hello\"},{\"type\":\"image_url\",\"image_url\":{\"url\":\"x\"}}]") }
            }
        };
        var r = OpenAIMessageMapper.Map(req, Language.SimplifiedChinese);
        r.Request!.Text.Should().Be("hello");
    }

    private static ChatRequest BuildRequest(params (string role, string content)[] messages)
    {
        var req = new ChatRequest { Model = "easydict-openai", Messages = new() };
        foreach (var (role, content) in messages)
        {
            req.Messages.Add(new ChatMessage { Role = role, Content = ParseJson(JsonSerializer.Serialize(content)) });
        }
        return req;
    }

    private static JsonElement ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
