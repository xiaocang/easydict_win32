using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Windows.AI;
using Microsoft.Windows.AI.Text;

namespace Easydict.WindowsAI;

/// <summary>
/// Real <see cref="IWindowsLanguageModelClient"/> backed by the in-box
/// <c>Microsoft.Windows.AI.Text.LanguageModel</c> (Phi Silica) WinRT API.
/// Only callable on a Copilot+ PC; on other hardware, <see cref="GetReadyState"/>
/// returns a non-Ready state and the consumer surfaces a friendly error.
/// </summary>
public sealed class WindowsLanguageModelClient : IWindowsLanguageModelClient
{
    public WindowsAIReadyState GetReadyState()
    {
        try
        {
            return MapReadyState(LanguageModel.GetReadyState());
        }
        catch
        {
            // GetReadyState should never throw, but if the WinRT activation itself
            // fails (missing runtime, unsupported OS), treat it as not-supported.
            return WindowsAIReadyState.NotSupportedOnCurrentSystem;
        }
    }

    public async Task<WindowsAIReadyState> EnsureReadyAsync(CancellationToken cancellationToken)
    {
        var state = GetReadyState();
        if (state != WindowsAIReadyState.NotReady)
        {
            return state;
        }

        try
        {
            await LanguageModel.EnsureReadyAsync().AsTask(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return GetReadyState();
        }

        return GetReadyState();
    }

    public async Task<WindowsAIResponse> GenerateAsync(
        string prompt,
        WindowsAIGenerationOptions options,
        CancellationToken cancellationToken)
    {
        using var model = await LanguageModel.CreateAsync().AsTask(cancellationToken);
        var sdkOptions = ToSdkOptions(options);

        var result = await model.GenerateResponseAsync(prompt, sdkOptions).AsTask(cancellationToken);
        return MapResult(result);
    }

    public IAsyncEnumerable<string> GenerateStreamAsync(
        string prompt,
        WindowsAIGenerationOptions options,
        CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        _ = StreamToChannelAsync(prompt, options, channel.Writer, cancellationToken);
        return ReadChannelAsync(channel.Reader, cancellationToken);
    }

    private static async Task StreamToChannelAsync(
        string prompt,
        WindowsAIGenerationOptions options,
        ChannelWriter<string> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            using var model = await LanguageModel.CreateAsync().AsTask(cancellationToken);
            var sdkOptions = ToSdkOptions(options);

            var operation = model.GenerateResponseAsync(prompt, sdkOptions);
            operation.Progress = (_, token) =>
            {
                if (!string.IsNullOrEmpty(token))
                {
                    writer.TryWrite(token);
                }
            };

            var result = await operation.AsTask(cancellationToken);

            if (result.Status != LanguageModelResponseStatus.Complete)
            {
                var mapped = MapResult(result);
                throw new WindowsLanguageModelException(mapped.Status, mapped.ErrorMessage);
            }

            writer.TryComplete();
        }
        catch (Exception ex)
        {
            writer.TryComplete(ex);
        }
    }

    private static async IAsyncEnumerable<string> ReadChannelAsync(
        ChannelReader<string> reader,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var chunk in reader.ReadAllAsync(cancellationToken))
        {
            yield return chunk;
        }
    }

    private static LanguageModelOptions ToSdkOptions(WindowsAIGenerationOptions options)
    {
        return new LanguageModelOptions
        {
            Temperature = options.Temperature,
            TopK = options.TopK,
            TopP = options.TopP,
        };
    }

    private static WindowsAIReadyState MapReadyState(AIFeatureReadyState state) => state switch
    {
        AIFeatureReadyState.Ready => WindowsAIReadyState.Ready,
        AIFeatureReadyState.NotReady => WindowsAIReadyState.NotReady,
        AIFeatureReadyState.CapabilityMissing => WindowsAIReadyState.CapabilityMissing,
        AIFeatureReadyState.NotCompatibleWithSystemHardware => WindowsAIReadyState.NotCompatibleWithSystemHardware,
        AIFeatureReadyState.OSUpdateNeeded => WindowsAIReadyState.OSUpdateNeeded,
        AIFeatureReadyState.DisabledByUser => WindowsAIReadyState.DisabledByUser,
        _ => WindowsAIReadyState.NotSupportedOnCurrentSystem,
    };

    private static WindowsAIResponse MapResult(LanguageModelResponseResult result) => result.Status switch
    {
        LanguageModelResponseStatus.Complete =>
            new WindowsAIResponse(WindowsAIResponseStatus.Complete, result.Text ?? string.Empty),

        LanguageModelResponseStatus.PromptLargerThanContext =>
            new WindowsAIResponse(
                WindowsAIResponseStatus.PromptLargerThanContext,
                string.Empty,
                result.ExtendedError?.Message),

        LanguageModelResponseStatus.BlockedByPolicy =>
            new WindowsAIResponse(
                WindowsAIResponseStatus.BlockedByPolicy,
                string.Empty,
                result.ExtendedError?.Message),

        _ =>
            new WindowsAIResponse(
                WindowsAIResponseStatus.Error,
                string.Empty,
                result.ExtendedError?.Message ?? result.Status.ToString()),
    };
}

internal sealed class WindowsLanguageModelException : Exception
{
    public WindowsAIResponseStatus Status { get; }

    public WindowsLanguageModelException(WindowsAIResponseStatus status, string? message)
        : base(message ?? status.ToString())
    {
        Status = status;
    }
}
