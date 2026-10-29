using System.Net;
using System.Text.Json;
using Easydict.TranslationService.Services.AnkiConnect;
using Easydict.TranslationService.Models;
using Xunit;

namespace Easydict.TranslationService.Tests;

public class AnkiConnectClientTests
{
    [Fact]
    public async Task AddNote_SendsExpectedEnvelopeAndDisallowsDuplicates()
    {
        string? sent = null;
        using var http = new HttpClient(new Handler(async request =>
        {
            sent = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"result\":123,\"error\":null}") };
        }));
        var client = new AnkiConnectClient(http);
        var id = await client.AddNoteAsync(new AnkiNote("Deck", "Basic", new Dictionary<string, string> { ["Front"] = "Hello", ["Back"] = "你好" }));
        using var json = JsonDocument.Parse(sent!);
        var root = json.RootElement;
        Assert.Equal(123, id);
        Assert.Equal("addNote", root.GetProperty("action").GetString());
        Assert.Equal(6, root.GetProperty("version").GetInt32());
        var note = root.GetProperty("params").GetProperty("note");
        Assert.Equal("Deck", note.GetProperty("deckName").GetString());
        Assert.Equal("你好", note.GetProperty("fields").GetProperty("Back").GetString());
        Assert.False(note.GetProperty("options").GetProperty("allowDuplicate").GetBoolean());
    }

    [Fact]
    public async Task AddNote_SurfacesDuplicateError()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"result\":null,\"error\":\"cannot create note because it is a duplicate\"}") })));
        var client = new AnkiConnectClient(http);
        var error = await Assert.ThrowsAsync<AnkiConnectException>(() => client.AddNoteAsync(new AnkiNote("Deck", "Basic", new Dictionary<string, string> { ["Front"] = "Hello" })));
        Assert.Contains("duplicate", error.Message);
    }

    [Fact]
    public void Templates_EscapeLookupTextAndDoNotExpandTokensInsideIt()
    {
        var result = new TranslationResult
        {
            OriginalText = "<tag> {Translation}\nnext", TranslatedText = "译文", ServiceName = "test",
            WordResult = new WordResult { Definitions = [new Definition { PartOfSpeech = "n.", Meanings = ["a & b"] }] }
        };
        Assert.Equal("&lt;tag&gt; {Translation}<br>next / 译文 / n. a &amp; b",
            AnkiFieldTemplate.Expand("{word} / {Translation} / {Definition}", result));
    }

    [Fact]
    public void TargetSelection_UsesLastOnlyAsACompleteConfiguredPair()
    {
        var templates = new Dictionary<string, Dictionary<string, string>> { ["Last model"] = new() { ["Front"] = "{Word}" } };
        Assert.Equal(("Default deck", "Default model"), AnkiTargetSelection.Choose(false, "Default deck", "Default model", "Last deck", "Last model", templates));
        Assert.Equal(("Last deck", "Last model"), AnkiTargetSelection.Choose(true, "Default deck", "Default model", "Last deck", "Last model", templates));
        Assert.Equal(("Default deck", "Default model"), AnkiTargetSelection.Choose(true, "Default deck", "Default model", "Last deck", "Unknown", templates));
        Assert.Equal(("Default deck", "Default model"), AnkiTargetSelection.Choose(true, "Default deck", "Default model", "", "Last model", templates));
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
