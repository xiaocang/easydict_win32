extern alias LongDocWorker;

using Easydict.SidecarClient.Protocol;
using FluentAssertions;
using Xunit;
using LongDocIpcEventWriter = LongDocWorker::Easydict.Workers.LongDoc.Infrastructure.IpcEventWriter;
using LongDocRequestDispatcher = LongDocWorker::Easydict.Workers.LongDoc.Infrastructure.RequestDispatcher;
using LongDocWorkerHandlerException = LongDocWorker::Easydict.Workers.LongDoc.Infrastructure.WorkerHandlerException;

namespace Easydict.WinUI.Tests.Services.Workers;

[Trait("Category", "WinUI")]
public sealed class LongDocWorkerLifecycleTests
{
    [Fact]
    public async Task DispatchAsync_InvokesCompletionCallback_AfterTranslateDocumentSuccessResponse()
    {
        using var output = new StringWriter();
        var dispatcher = new LongDocRequestDispatcher(new LongDocIpcEventWriter(output));
        var completedMethods = new List<string>();
        dispatcher.OnRequestCompleted = completedMethods.Add;
        dispatcher.Register(LongDocMethods.TranslateDocument, (_, _, _) =>
            Task.FromResult<object?>(new { ok = true }));

        await dispatcher.DispatchAsync(JsonLineSerializer.SerializeLine(new IpcRequest
        {
            Id = "req-1",
            Method = LongDocMethods.TranslateDocument,
        }));

        completedMethods.Should().Equal(LongDocMethods.TranslateDocument);
        output.ToString().Should().Contain("\"id\":\"req-1\"");
        output.ToString().Should().Contain("\"ok\":true");
    }

    [Fact]
    public async Task DispatchAsync_InvokesCompletionCallback_AfterTranslateDocumentErrorResponse()
    {
        using var output = new StringWriter();
        var dispatcher = new LongDocRequestDispatcher(new LongDocIpcEventWriter(output));
        var completedMethods = new List<string>();
        dispatcher.OnRequestCompleted = completedMethods.Add;
        dispatcher.Register(LongDocMethods.TranslateDocument, (_, _, _) =>
            throw new LongDocWorkerHandlerException(WorkerErrorCodes.ServiceError, "boom"));

        await dispatcher.DispatchAsync(JsonLineSerializer.SerializeLine(new IpcRequest
        {
            Id = "req-2",
            Method = LongDocMethods.TranslateDocument,
        }));

        completedMethods.Should().Equal(LongDocMethods.TranslateDocument);
        output.ToString().Should().Contain("\"code\":\"service_error\"");
        output.ToString().Should().Contain("\"message\":\"boom\"");
    }

    [Fact]
    public void Program_WiresTranslateDocumentCompletionToShutdown()
    {
        var programPath = Path.Combine(
            FindProjectRoot(),
            "src",
            "Easydict.Workers.LongDoc",
            "Program.cs");
        var source = File.ReadAllText(programPath);

        source.Should().Contain("dispatcher.OnRequestCompleted = method =>");
        source.Should().Contain("method != LongDocMethods.TranslateDocument");
        source.Should().Contain("_shutdownRequested.TrySetResult();");
    }

    private static string FindProjectRoot()
    {
        var current = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            var solutionPath = Path.Combine(current, "Easydict.Win32.sln");
            if (File.Exists(solutionPath))
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..");
    }
}
