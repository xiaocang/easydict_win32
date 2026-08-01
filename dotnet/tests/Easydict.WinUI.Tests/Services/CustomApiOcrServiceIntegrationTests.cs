using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Easydict.WinUI.Models;
using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "Integration")]
[Trait("Service", "ocr")]
public class CustomApiOcrServiceIntegrationTests
{
    [Fact]
    public async Task RecognizeAsync_SendsOpenAiReasoningControl_OverHttp()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var responseTask = CaptureRequestAndRespondAsync(listener, timeout.Token);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var options = new OcrServiceOptions(
                OcrEngineType.CustomApi,
                "test-key",
                $"http://127.0.0.1:{port}/v1/chat/completions",
                "gpt-5.4-mini",
                "extract the text");
            var service = new CustomApiOcrService(client, options);

            var result = await service.RecognizeAsync(new byte[4], 1, 1, cancellationToken: timeout.Token);
            var requestBody = await responseTask;

            result.Text.Should().Be("recognized text");
            using var document = JsonDocument.Parse(requestBody);
            document.RootElement.GetProperty("reasoning_effort").GetString().Should().Be("none");
            document.RootElement.TryGetProperty("thinking", out _).Should().BeFalse();
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<string> CaptureRequestAndRespondAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        using var connection = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = connection.GetStream();
        using var reader = new StreamReader(
            stream,
            Encoding.ASCII,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);

        var requestLine = await reader.ReadLineAsync(cancellationToken);
        requestLine.Should().StartWith("POST /v1/chat/completions HTTP/");

        var contentLength = 0;
        string? header;
        while (!string.IsNullOrEmpty(header = await reader.ReadLineAsync(cancellationToken)))
        {
            if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                contentLength = int.Parse(header["Content-Length:".Length..].Trim());
            }
        }

        contentLength.Should().BeGreaterThan(0);
        var body = new char[contentLength];
        var charsRead = 0;
        while (charsRead < body.Length)
        {
            var count = await reader.ReadAsync(
                body.AsMemory(charsRead, body.Length - charsRead),
                cancellationToken);
            if (count == 0)
            {
                throw new EndOfStreamException("HTTP request ended before its declared content length.");
            }

            charsRead += count;
        }

        const string responseBody =
            "{\"choices\":[{\"message\":{\"content\":\"recognized text\"},\"finish_reason\":\"stop\"}]}";
        var responseBytes = Encoding.UTF8.GetBytes(responseBody);
        var responseHeaders = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {responseBytes.Length}\r\nConnection: close\r\n\r\n");

        await stream.WriteAsync(responseHeaders, cancellationToken);
        await stream.WriteAsync(responseBytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);

        return new string(body);
    }
}
