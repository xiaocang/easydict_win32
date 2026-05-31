using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Easydict.CompatHost;
using Easydict.SidecarClient;
using Easydict.SidecarClient.Protocol;
using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services.Workers;

[Trait("Category", "WinUI")]
public sealed class CompatHostDispatcherTests
{
    [Fact]
    public async Task Translate_DispatchesToInjectedTranslator_AndWritesResultEnvelope()
    {
        var dispatcher = new CompatHostDispatcher(new FakeTranslator());
        using var output = new StringWriter();

        var request = new IpcRequest
        {
            Id = "req-1",
            Method = CompatHostMethods.Translate,
            Params = new TranslateParams
            {
                Text = "Hello",
                From = "en",
                To = "zh-Hans",
                Services = ["mock"],
            },
        };

        var shouldExit = await dispatcher.DispatchAsync(JsonLineSerializer.Serialize(request), output);

        shouldExit.Should().BeFalse();

        var response = JsonLineSerializer.Deserialize<IpcResponse>(SingleLine(output));
        response.Should().NotBeNull();
        response!.IsSuccess.Should().BeTrue();

        var result = response.Result!.Value.Deserialize<TranslationResultDto>();
        result.Should().NotBeNull();
        result!.TranslatedText.Should().Be("fake:Hello:en:zh-Hans");
        result.ServiceId.Should().Be("mock");
        result.ServiceName.Should().Be("Fake Translator");
    }

    [Fact]
    public async Task TranslateStream_WritesChunkEventsDoneEventAndResultEnvelope()
    {
        var dispatcher = new CompatHostDispatcher(new FakeTranslator());
        using var output = new StringWriter();

        var request = new IpcRequest
        {
            Id = "req-stream",
            Method = CompatHostMethods.TranslateStream,
            Params = new TranslateParams
            {
                Text = "Hello",
                From = "en",
                To = "zh-Hans",
                Services = ["mock"],
            },
        };

        var shouldExit = await dispatcher.DispatchAsync(JsonLineSerializer.Serialize(request), output);

        shouldExit.Should().BeFalse();

        var lines = OutputLines(output);
        lines.Should().HaveCount(4);

        var firstChunk = JsonLineSerializer.Deserialize<IpcEvent>(lines[0]);
        firstChunk.Should().NotBeNull();
        firstChunk!.Event.Should().Be(IpcEventTypes.TranslateChunk);
        firstChunk.Id.Should().Be("req-stream");
        firstChunk.Data!.Value.Deserialize<TranslateChunkEventData>()!.Text.Should().Be("fake-stream:");

        var secondChunk = JsonLineSerializer.Deserialize<IpcEvent>(lines[1]);
        secondChunk.Should().NotBeNull();
        secondChunk!.Event.Should().Be(IpcEventTypes.TranslateChunk);
        secondChunk.Data!.Value.Deserialize<TranslateChunkEventData>()!.Text.Should().Be("Hello");

        var done = JsonLineSerializer.Deserialize<IpcEvent>(lines[2]);
        done.Should().NotBeNull();
        done!.Event.Should().Be(IpcEventTypes.TranslateDone);
        var doneResult = done.Data!.Value.Deserialize<TranslationResultDto>();
        doneResult.Should().NotBeNull();
        doneResult!.TranslatedText.Should().Be("fake-stream:Hello");

        var response = JsonLineSerializer.Deserialize<IpcResponse>(lines[3]);
        response.Should().NotBeNull();
        response!.IsSuccess.Should().BeTrue();
        response.Result!.Value.Deserialize<TranslationResultDto>()!.TranslatedText.Should().Be("fake-stream:Hello");
    }

    [Fact]
    public async Task GrammarCorrect_WritesChunkEventsDoneEventAndResultEnvelope()
    {
        var dispatcher = new CompatHostDispatcher(new FakeTranslator());
        using var output = new StringWriter();

        var request = new IpcRequest
        {
            Id = "req-grammar",
            Method = CompatHostMethods.GrammarCorrect,
            Params = new GrammarCorrectParams
            {
                Text = "I has a apple.",
                Language = "en",
                Services = ["mock"],
            },
        };

        var shouldExit = await dispatcher.DispatchAsync(JsonLineSerializer.Serialize(request), output);

        shouldExit.Should().BeFalse();

        var lines = OutputLines(output);
        lines.Should().HaveCount(4);

        var firstChunk = JsonLineSerializer.Deserialize<IpcEvent>(lines[0]);
        firstChunk.Should().NotBeNull();
        firstChunk!.Event.Should().Be(IpcEventTypes.GrammarChunk);
        firstChunk.Id.Should().Be("req-grammar");
        firstChunk.Data!.Value.Deserialize<GrammarChunkEventData>()!.Text.Should().Be("[CORRECTED]");

        var secondChunk = JsonLineSerializer.Deserialize<IpcEvent>(lines[1]);
        secondChunk.Should().NotBeNull();
        secondChunk!.Event.Should().Be(IpcEventTypes.GrammarChunk);
        secondChunk.Data!.Value.Deserialize<GrammarChunkEventData>()!.Text.Should().Be("I have an apple.");

        var done = JsonLineSerializer.Deserialize<IpcEvent>(lines[2]);
        done.Should().NotBeNull();
        done!.Event.Should().Be(IpcEventTypes.GrammarDone);
        var doneResult = done.Data!.Value.Deserialize<GrammarCorrectResultDto>();
        doneResult.Should().NotBeNull();
        doneResult!.CorrectedText.Should().Be("I have an apple.");
        doneResult.ServiceId.Should().Be("mock");

        var response = JsonLineSerializer.Deserialize<IpcResponse>(lines[3]);
        response.Should().NotBeNull();
        response!.IsSuccess.Should().BeTrue();
        response.Result!.Value.Deserialize<GrammarCorrectResultDto>()!.CorrectedText
            .Should()
            .Be("I have an apple.");
    }

    [Fact]
    public async Task UnsupportedFacadeMethod_ReturnsServiceError_NotMethodNotFound()
    {
        var dispatcher = new CompatHostDispatcher(new FakeTranslator());
        using var output = new StringWriter();

        var request = new IpcRequest
        {
            Id = "req-ocr",
            Method = CompatHostMethods.OcrRecognize,
        };

        var shouldExit = await dispatcher.DispatchAsync(JsonLineSerializer.Serialize(request), output);

        shouldExit.Should().BeFalse();

        var response = JsonLineSerializer.Deserialize<IpcResponse>(SingleLine(output));
        response.Should().NotBeNull();
        response!.IsError.Should().BeTrue();
        response.Error!.Code.Should().Be(IpcErrorCodes.ServiceError);
        response.Error.Message.Should().Contain("not implemented yet");
    }

    [Fact]
    public async Task OcrRecognize_DispatchesToInjectedRecognizer_AndWritesResultEnvelope()
    {
        var runtimeState = new CompatHostRuntimeState();
        runtimeState.Configure(new SettingsSnapshot
        {
            OcrEngine = "CustomApi",
            OcrEndpoint = "https://ocr.example.test/v1/responses",
            OcrModel = "gpt-vision",
            OcrLanguage = "ja-JP",
        });
        var fakeOcr = new FakeOcrRecognizer();
        var dispatcher = new CompatHostDispatcher(
            new FakeTranslator(),
            fakeOcr,
            runtimeState: runtimeState);
        using var output = new StringWriter();

        var request = new IpcRequest
        {
            Id = "req-ocr",
            Method = CompatHostMethods.OcrRecognize,
            Params = new OcrRecognizeParams
            {
                PixelDataPath = @"C:\Temp\capture.bgra",
                PixelWidth = 2,
                PixelHeight = 1,
                PreferredLanguageTag = "en-US",
            },
        };

        var shouldExit = await dispatcher.DispatchAsync(JsonLineSerializer.Serialize(request), output);

        shouldExit.Should().BeFalse();

        var response = JsonLineSerializer.Deserialize<IpcResponse>(SingleLine(output));
        response.Should().NotBeNull();
        response!.IsSuccess.Should().BeTrue();

        var result = response.Result!.Value.Deserialize<OcrResultDto>();
        result.Should().NotBeNull();
        result!.Text.Should().Be("fake OCR en-US 2x1");
        result.Lines.Should().ContainSingle().Which.BoundingRect.Width.Should().Be(2);
        result.DetectedLanguage!.Tag.Should().Be("en-US");
        fakeOcr.ReceivedSettings.OcrEngine.Should().Be("CustomApi");
        fakeOcr.ReceivedSettings.OcrEndpoint.Should().Be("https://ocr.example.test/v1/responses");
        fakeOcr.ReceivedSettings.OcrModel.Should().Be("gpt-vision");
        fakeOcr.ReceivedSettings.OcrLanguage.Should().Be("ja-JP");
    }

    [Fact]
    public async Task Configure_StoresSettingsSnapshot_AndWritesOkEnvelope()
    {
        var runtimeState = new CompatHostRuntimeState();
        var dispatcher = new CompatHostDispatcher(
            new FakeTranslator(),
            runtimeState: runtimeState);
        using var output = new StringWriter();

        var request = new IpcRequest
        {
            Id = "req-configure",
            Method = WorkerMethods.Configure,
            Params = new ConfigureParams
            {
                Settings = new SettingsSnapshot
                {
                    OpenAIModel = "gpt-test",
                    LongDocMaxConcurrency = 7,
                },
            },
        };

        var shouldExit = await dispatcher.DispatchAsync(JsonLineSerializer.Serialize(request), output);

        shouldExit.Should().BeFalse();
        runtimeState.Settings.OpenAIModel.Should().Be("gpt-test");
        runtimeState.Settings.LongDocMaxConcurrency.Should().Be(7);

        var response = JsonLineSerializer.Deserialize<IpcResponse>(SingleLine(output));
        response.Should().NotBeNull();
        response!.IsSuccess.Should().BeTrue();
        response.Result!.Value.Deserialize<ConfigureResult>()!.Ok.Should().BeTrue();
    }

    [Fact]
    public async Task LongDocTranslate_DispatchesToInjectedTranslator_ForwardsEventsAndWritesResultEnvelope()
    {
        var runtimeState = new CompatHostRuntimeState();
        runtimeState.Configure(new SettingsSnapshot
        {
            OpenAIModel = "gpt-longdoc",
        });

        var fakeLongDoc = new FakeLongDocTranslator();
        var dispatcher = new CompatHostDispatcher(
            new FakeTranslator(),
            longDocTranslator: fakeLongDoc,
            runtimeState: runtimeState);
        using var output = new StringWriter();

        var request = new IpcRequest
        {
            Id = "req-longdoc",
            Method = CompatHostMethods.LongDocTranslate,
            Params = new TranslateDocumentParams
            {
                InputPath = @"C:\Temp\source.md",
                OutputPath = @"C:\Temp\translated.md",
                InputMode = "Markdown",
                From = "English",
                To = "SimplifiedChinese",
                ServiceId = "openai",
                OutputMode = "Bilingual",
            },
        };

        var shouldExit = await dispatcher.DispatchAsync(JsonLineSerializer.Serialize(request), output);

        shouldExit.Should().BeFalse();
        fakeLongDoc.ReceivedSettings.OpenAIModel.Should().Be("gpt-longdoc");

        var lines = OutputLines(output);
        lines.Should().HaveCount(4);

        var status = JsonLineSerializer.Deserialize<IpcEvent>(lines[0]);
        status.Should().NotBeNull();
        status!.Id.Should().Be("req-longdoc");
        status.Event.Should().Be(LongDocEvents.Status);
        status.Data!.Value.Deserialize<StatusEventData>()!.Message.Should().Be("fake status");

        var progress = JsonLineSerializer.Deserialize<IpcEvent>(lines[1]);
        progress.Should().NotBeNull();
        progress!.Id.Should().Be("req-longdoc");
        progress.Event.Should().Be(LongDocEvents.Progress);
        progress.Data!.Value.Deserialize<ProgressEventData>()!.Percentage.Should().Be(50);

        var block = JsonLineSerializer.Deserialize<IpcEvent>(lines[2]);
        block.Should().NotBeNull();
        block!.Id.Should().Be("req-longdoc");
        block.Event.Should().Be(LongDocEvents.BlockTranslated);
        block.Data!.Value.Deserialize<BlockTranslatedEventData>()!.TranslatedText.Should().Be("你好");

        var response = JsonLineSerializer.Deserialize<IpcResponse>(lines[3]);
        response.Should().NotBeNull();
        response!.IsSuccess.Should().BeTrue();
        response.Result!.Value.Deserialize<TranslateDocumentResult>()!.OutputPath
            .Should()
            .Be(@"C:\Temp\translated.md");
    }

    [Fact]
    public async Task LocalAiPrepare_DispatchesToInjectedService_ForwardsDownloadProgressAndWritesResultEnvelope()
    {
        var runtimeState = new CompatHostRuntimeState();
        runtimeState.Configure(new SettingsSnapshot
        {
            LocalAIProvider = LocalAiProviderModes.FoundryLocal,
            FoundryLocalModel = "phi-test",
        });

        var fakeLocalAi = new FakeLocalAiService();
        var dispatcher = new CompatHostDispatcher(
            new FakeTranslator(),
            localAiService: fakeLocalAi,
            runtimeState: runtimeState);
        using var output = new StringWriter();

        var request = new IpcRequest
        {
            Id = "req-local-prepare",
            Method = CompatHostMethods.LocalAiPrepare,
            Params = new PrepareModelParams
            {
                Provider = LocalAiProviderModes.FoundryLocal,
                Model = "phi-test",
            },
        };

        var shouldExit = await dispatcher.DispatchAsync(JsonLineSerializer.Serialize(request), output);

        shouldExit.Should().BeFalse();
        fakeLocalAi.ReceivedSettings.FoundryLocalModel.Should().Be("phi-test");

        var lines = OutputLines(output);
        lines.Should().HaveCount(2);

        var progress = JsonLineSerializer.Deserialize<IpcEvent>(lines[0]);
        progress.Should().NotBeNull();
        progress!.Id.Should().Be("req-local-prepare");
        progress.Event.Should().Be(LocalAiEvents.DownloadProgress);
        progress.Data!.Value.Deserialize<DownloadProgressEventData>()!.BytesDownloaded.Should().Be(64);

        var response = JsonLineSerializer.Deserialize<IpcResponse>(lines[1]);
        response.Should().NotBeNull();
        response!.IsSuccess.Should().BeTrue();
        response.Result!.Value.Deserialize<LocalModelStatusDto>()!.State.Should().Be("Ready");
    }

    [Fact]
    public async Task LocalAiTranslate_DispatchesToInjectedService_AndWritesResultEnvelope()
    {
        var runtimeState = new CompatHostRuntimeState();
        runtimeState.Configure(new SettingsSnapshot
        {
            LocalAIProvider = LocalAiProviderModes.WindowsAI,
        });

        var fakeLocalAi = new FakeLocalAiService();
        var dispatcher = new CompatHostDispatcher(
            new FakeTranslator(),
            localAiService: fakeLocalAi,
            runtimeState: runtimeState);
        using var output = new StringWriter();

        var request = new IpcRequest
        {
            Id = "req-local-translate",
            Method = CompatHostMethods.LocalAiTranslate,
            Params = new LocalAiTranslateParams
            {
                Text = "Hello",
                FromLanguage = "English",
                ToLanguage = "SimplifiedChinese",
                ProviderMode = LocalAiProviderModes.WindowsAI,
            },
        };

        var shouldExit = await dispatcher.DispatchAsync(JsonLineSerializer.Serialize(request), output);

        shouldExit.Should().BeFalse();
        fakeLocalAi.ReceivedSettings.LocalAIProvider.Should().Be(LocalAiProviderModes.WindowsAI);

        var response = JsonLineSerializer.Deserialize<IpcResponse>(SingleLine(output));
        response.Should().NotBeNull();
        response!.IsSuccess.Should().BeTrue();
        response.Result!.Value.Deserialize<LocalAiTranslateResult>()!.TranslatedText
            .Should()
            .Be("fake local:Hello:WindowsAI");
    }

    [Fact]
    public async Task MdxLookup_DispatchesToInjectedService_AndWritesResultEnvelope()
    {
        var runtimeState = new CompatHostRuntimeState();
        runtimeState.Configure(new SettingsSnapshot
        {
            ImportedMdxDictionaries =
            [
                new ImportedMdxDictionarySnapshot
                {
                    ServiceId = "mdx::demo",
                    DisplayName = "Demo Dictionary",
                    FilePath = @"C:\Dicts\demo.mdx",
                },
            ],
        });

        var fakeMdx = new FakeMdxLookupService();
        var dispatcher = new CompatHostDispatcher(
            new FakeTranslator(),
            mdxLookupService: fakeMdx,
            runtimeState: runtimeState);
        using var output = new StringWriter();

        var request = new IpcRequest
        {
            Id = "req-mdx",
            Method = CompatHostMethods.MdxLookup,
            Params = new MdxLookupParams
            {
                DictionaryId = "mdx::demo",
                Query = "apple",
                Fuzzy = false,
            },
        };

        var shouldExit = await dispatcher.DispatchAsync(JsonLineSerializer.Serialize(request), output);

        shouldExit.Should().BeFalse();
        fakeMdx.ReceivedSettings.ImportedMdxDictionaries.Should().ContainSingle();

        var response = JsonLineSerializer.Deserialize<IpcResponse>(SingleLine(output));
        response.Should().NotBeNull();
        response!.IsSuccess.Should().BeTrue();
        var result = response.Result!.Value.Deserialize<MdxLookupResult>();
        result.Should().NotBeNull();
        result!.Entries.Should().ContainSingle().Which.Html.Should().Contain("apple");
    }

    [Fact]
    public async Task SettingsMigrate_DispatchesToInjectedMigrator_AndWritesResultEnvelope()
    {
        var fakeMigrator = new FakeSettingsMigrator();
        var dispatcher = new CompatHostDispatcher(
            new FakeTranslator(),
            settingsMigrator: fakeMigrator);
        using var output = new StringWriter();

        var request = new IpcRequest
        {
            Id = "req-settings",
            Method = CompatHostMethods.SettingsMigrate,
            Params = new SettingsMigrateParams
            {
                LegacySettingsPath = @"C:\Old\settings.json",
                TargetSettingsPath = @"C:\New\settings.json",
            },
        };

        var shouldExit = await dispatcher.DispatchAsync(JsonLineSerializer.Serialize(request), output);

        shouldExit.Should().BeFalse();
        fakeMigrator.ReceivedPath.Should().Be(@"C:\Old\settings.json");

        var response = JsonLineSerializer.Deserialize<IpcResponse>(SingleLine(output));
        response.Should().NotBeNull();
        response!.IsSuccess.Should().BeTrue();
        var result = response.Result!.Value.Deserialize<SettingsMigrateResult>();
        result.Should().NotBeNull();
        result!.Migrated.Should().BeTrue();
        result.Warnings.Should().Equal("fake warning");
    }

    [Fact]
    public async Task UnknownMethod_ReturnsMethodNotFound()
    {
        var dispatcher = new CompatHostDispatcher(new FakeTranslator());
        using var output = new StringWriter();

        var request = new IpcRequest
        {
            Id = "req-missing",
            Method = "missing_method",
        };

        await dispatcher.DispatchAsync(JsonLineSerializer.Serialize(request), output);

        var response = JsonLineSerializer.Deserialize<IpcResponse>(SingleLine(output));
        response.Should().NotBeNull();
        response!.IsError.Should().BeTrue();
        response.Error!.Code.Should().Be(IpcErrorCodes.MethodNotFound);
    }

    [Fact]
    public async Task Shutdown_WritesOkAndRequestsExit()
    {
        var dispatcher = new CompatHostDispatcher(new FakeTranslator());
        using var output = new StringWriter();

        var request = new IpcRequest
        {
            Id = "req-shutdown",
            Method = WorkerMethods.Shutdown,
        };

        var shouldExit = await dispatcher.DispatchAsync(JsonLineSerializer.Serialize(request), output);

        shouldExit.Should().BeTrue();
        SingleLine(output).Should().Contain("\"ok\":true");
    }

    [Fact]
    public async Task Application_RunAsync_ProcessesMultipleJsonLines_UntilShutdown()
    {
        var dispatcher = new CompatHostDispatcher(new FakeTranslator());
        var input = string.Join(Environment.NewLine,
            JsonLineSerializer.Serialize(new IpcRequest
            {
                Id = "req-1",
                Method = CompatHostMethods.Translate,
                Params = new TranslateParams { Text = "Hello" },
            }),
            JsonLineSerializer.Serialize(new IpcRequest
            {
                Id = "req-2",
                Method = WorkerMethods.Shutdown,
            }),
            JsonLineSerializer.Serialize(new IpcRequest
            {
                Id = "req-3",
                Method = CompatHostMethods.Translate,
                Params = new TranslateParams { Text = "Should not run" },
            }));

        using var reader = new StringReader(input);
        using var writer = new StringWriter();

        var exitCode = await CompatHostApplication.RunAsync(reader, writer, dispatcher);

        exitCode.Should().Be(0);
        var lines = writer.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(2);
        lines[0].Should().Contain("fake:Hello");
        lines[1].Should().Contain("\"ok\":true");
    }

    [Fact]
    public async Task CompatHostProcess_RespondsToJsonLines_AndShutsDown()
    {
        var hostAssembly = typeof(CompatHostApplication).Assembly.Location;
        File.Exists(hostAssembly).Should().BeTrue();

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };
        process.StartInfo.ArgumentList.Add(hostAssembly);

        process.Start().Should().BeTrue();

        await process.StandardInput.WriteLineAsync(JsonLineSerializer.Serialize(new IpcRequest
        {
            Id = "req-missing",
            Method = "missing_method",
        }));
        await process.StandardInput.WriteLineAsync(JsonLineSerializer.Serialize(new IpcRequest
        {
            Id = "req-shutdown",
            Method = WorkerMethods.Shutdown,
        }));
        await process.StandardInput.FlushAsync();

        var missingLine = await ReadLineWithTimeoutAsync(process.StandardOutput);
        var shutdownLine = await ReadLineWithTimeoutAsync(process.StandardOutput);

        var missing = JsonLineSerializer.Deserialize<IpcResponse>(missingLine);
        missing.Should().NotBeNull();
        missing!.Id.Should().Be("req-missing");
        missing.Error!.Code.Should().Be(IpcErrorCodes.MethodNotFound);

        var shutdown = JsonLineSerializer.Deserialize<IpcResponse>(shutdownLine);
        shutdown.Should().NotBeNull();
        shutdown!.Id.Should().Be("req-shutdown");
        shutdown.Result!.Value.GetProperty("ok").GetBoolean().Should().BeTrue();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(cts.Token);
        process.ExitCode.Should().Be(0, await process.StandardError.ReadToEndAsync());
    }

    [Fact]
    public async Task TranslationManagerCompatTranslator_StreamsThroughRegisteredService_AndReturnsAccumulatedResult()
    {
        var manager = new TranslationManager();
        manager.RegisterService(new FakeStreamTranslationService());
        manager.DefaultServiceId = "fake-stream";
        await using var translator = new TranslationManagerCompatTranslator(manager);
        var chunks = new List<string>();

        var result = await translator.TranslateStreamAsync(
            new TranslateParams
            {
                Text = "Hello",
                From = "en",
                To = "zh-Hans",
            },
            (chunk, _) =>
            {
                chunks.Add(chunk);
                return Task.CompletedTask;
            });

        chunks.Should().Equal("adapter:", "Hello");
        result.TranslatedText.Should().Be("adapter:Hello");
        result.ServiceId.Should().Be("fake-stream");
        result.ServiceName.Should().Be("Fake Stream Service");
    }

    [Fact]
    public async Task TranslationManagerCompatTranslator_CorrectGrammarAsync_StreamsAndParsesResult()
    {
        var manager = new TranslationManager();
        manager.RegisterService(new FakeStreamTranslationService());
        manager.DefaultServiceId = "fake-stream";
        await using var translator = new TranslationManagerCompatTranslator(manager);
        var chunks = new List<string>();

        var result = await translator.CorrectGrammarAsync(
            new GrammarCorrectParams
            {
                Text = "I has a apple.",
                Language = "en",
                Services = ["fake-stream"],
            },
            (chunk, _) =>
            {
                chunks.Add(chunk);
                return Task.CompletedTask;
            });

        chunks.Should().Equal("[CORRECTED]", "I have an apple.", "[/CORRECTED]");
        result.OriginalText.Should().Be("I has a apple.");
        result.CorrectedText.Should().Be("I have an apple.");
        result.ServiceId.Should().Be("fake-stream");
        result.ServiceName.Should().Be("Fake Stream Service");
        result.Language.Should().Be("en");
        result.HasCorrections.Should().BeTrue();
    }

    [Fact]
    public async Task OcrWorkerCompatRecognizer_RecognizeAsync_ProxiesToJsonLineWorker()
    {
        var recognizer = new OcrWorkerCompatRecognizer(() => new SidecarClientOptions
        {
            ExecutablePath = "powershell.exe",
            Arguments =
            [
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-Command",
                OcrMockWorkerScript,
            ],
            DefaultTimeoutMs = 0,
        });

        var result = await recognizer.RecognizeAsync(
            new OcrRecognizeParams
            {
                PixelDataPath = @"C:\Temp\capture.bgra",
                PixelWidth = 4,
                PixelHeight = 3,
                PreferredLanguageTag = "ja-JP",
            },
            new SettingsSnapshot
            {
                OcrEngine = "WindowsNative",
                OcrLanguage = "ja-JP",
            });

        result.Text.Should().Be("mock OCR ja-JP 4x3");
        result.Lines.Should().ContainSingle().Which.BoundingRect.Height.Should().Be(3);
        result.DetectedLanguage!.Tag.Should().Be("ja-JP");
    }

    [Fact]
    public async Task OcrWorkerCompatRecognizer_RecognizeAsync_FallsBackWhenWorkerIsMissing()
    {
        var fallbackCalls = 0;
        var recognizer = new OcrWorkerCompatRecognizer(
            () => new SidecarClientOptions
            {
                ExecutablePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.missing.exe"),
                DefaultTimeoutMs = 0,
            },
            (parameters, settings, _) =>
            {
                fallbackCalls++;
                parameters.PixelWidth.Should().Be(4);
                parameters.PixelHeight.Should().Be(3);
                parameters.PreferredLanguageTag.Should().Be("ja-JP");
                settings.OcrLanguage.Should().Be("ja-JP");
                return Task.FromResult(new OcrResultDto
                {
                    Text = "fallback OCR",
                    Lines =
                    [
                        new OcrLineDto
                        {
                            Text = "fallback OCR",
                            BoundingRect = new OcrRectDto(0, 0, 4, 3),
                        }
                    ],
                    DetectedLanguage = new OcrLanguageDto
                    {
                        Tag = "ja-JP",
                        DisplayName = "Japanese",
                    },
                });
            });

        var result = await recognizer.RecognizeAsync(
            new OcrRecognizeParams
            {
                PixelDataPath = @"C:\Temp\capture.bgra",
                PixelWidth = 4,
                PixelHeight = 3,
                PreferredLanguageTag = "ja-JP",
            },
            new SettingsSnapshot
            {
                OcrEngine = "WindowsNative",
                OcrLanguage = "ja-JP",
            });

        fallbackCalls.Should().Be(1);
        result.Text.Should().Be("fallback OCR");
        result.DetectedLanguage!.Tag.Should().Be("ja-JP");
    }

    [Fact]
    public async Task OcrWorkerCompatRecognizer_RecognizeAsync_FallsBackWhenWorkerExitsDuringRecognize()
    {
        var fallbackCalls = 0;
        var recognizer = new OcrWorkerCompatRecognizer(
            () => new SidecarClientOptions
            {
                ExecutablePath = "powershell.exe",
                Arguments =
                [
                    "-NoProfile",
                    "-ExecutionPolicy",
                    "Bypass",
                    "-Command",
                    OcrExitDuringRecognizeScript,
                ],
                DefaultTimeoutMs = 0,
            },
            (_, _, _) =>
            {
                fallbackCalls++;
                return Task.FromResult(new OcrResultDto { Text = "fallback after exit" });
            });

        var result = await recognizer.RecognizeAsync(
            new OcrRecognizeParams
            {
                PixelDataPath = @"C:\Temp\capture.bgra",
                PixelWidth = 4,
                PixelHeight = 3,
            },
            new SettingsSnapshot { OcrEngine = "WindowsNative" });

        fallbackCalls.Should().Be(1);
        result.Text.Should().Be("fallback after exit");
    }

    [Fact]
    public async Task LongDocWorkerCompatTranslator_TranslateAsync_ProxiesToJsonLineWorkerAndHydratesResultFile()
    {
        var translator = new LongDocWorkerCompatTranslator(() => new SidecarClientOptions
        {
            ExecutablePath = "powershell.exe",
            Arguments =
            [
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-Command",
                LongDocMockWorkerScript,
            ],
            DefaultTimeoutMs = 0,
        });
        var events = new List<IpcEvent>();

        var result = await translator.TranslateAsync(
            new TranslateDocumentParams
            {
                InputPath = @"C:\Temp\source.md",
                OutputPath = @"C:\Temp\translated.md",
                InputMode = "Markdown",
                From = "English",
                To = "SimplifiedChinese",
                ServiceId = "openai",
                OutputMode = "Bilingual",
            },
            new SettingsSnapshot
            {
                OpenAIModel = "gpt-worker",
            },
            events.Add);

        result.State.Should().Be("Completed");
        result.OutputPath.Should().Be(@"C:\Temp\translated.md");
        result.TotalChunks.Should().Be(2);
        result.SucceededChunks.Should().Be(2);

        events.Select(evt => evt.Event)
            .Should()
            .Equal(LongDocEvents.Status, LongDocEvents.Progress, LongDocEvents.BlockTranslated);
        events[0].Data!.Value.Deserialize<StatusEventData>()!.Message
            .Should()
            .Be("mock longdoc gpt-worker");
        events[2].Data!.Value.Deserialize<BlockTranslatedEventData>()!.TranslatedText
            .Should()
            .Be("长文档");
    }

    [Fact]
    public async Task LocalAiWorkerCompatService_ProxiesPrepareAndTranslateToJsonLineWorker()
    {
        var service = new LocalAiWorkerCompatService(() => new SidecarClientOptions
        {
            ExecutablePath = "powershell.exe",
            Arguments =
            [
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-Command",
                LocalAiMockWorkerScript,
            ],
            DefaultTimeoutMs = 0,
        });

        var events = new List<IpcEvent>();
        var settings = new SettingsSnapshot
        {
            LocalAIProvider = LocalAiProviderModes.FoundryLocal,
            FoundryLocalModel = "phi-worker",
        };

        var status = await service.PrepareModelAsync(
            new PrepareModelParams
            {
                Provider = LocalAiProviderModes.FoundryLocal,
                Model = "phi-worker",
            },
            settings,
            events.Add);

        status.State.Should().Be("Ready");
        status.StatusKey.Should().Be("Prepared");
        events.Should().ContainSingle();
        events[0].Event.Should().Be(LocalAiEvents.DownloadProgress);
        events[0].Data!.Value.Deserialize<DownloadProgressEventData>()!.TotalBytes.Should().Be(256);

        var result = await service.TranslateAsync(
            new LocalAiTranslateParams
            {
                Text = "Hello",
                FromLanguage = "English",
                ToLanguage = "SimplifiedChinese",
                ProviderMode = LocalAiProviderModes.FoundryLocal,
            },
            settings);

        result.TranslatedText.Should().Be("mock local Hello via phi-worker");
        result.ServiceId.Should().Be("windows-local-ai");
        result.DetectedLanguage.Should().Be("English");
    }

    [Fact]
    public async Task MdxCompatLookupService_LookupAsync_FollowsRedirectsAndReturnsRawHtml()
    {
        var service = new MdxCompatLookupService(_ => new FakeMdxReader(new Dictionary<string, string?>
        {
            ["colour"] = "@@@LINK=color",
            ["color"] = "<div>A visual attribute</div>",
        }));

        var result = await service.LookupAsync(
            new MdxLookupParams
            {
                DictionaryId = "mdx::demo",
                Query = "colour",
                Fuzzy = false,
            },
            new SettingsSnapshot
            {
                ImportedMdxDictionaries =
                [
                    new ImportedMdxDictionarySnapshot
                    {
                        ServiceId = "mdx::demo",
                        DisplayName = "Demo Dictionary",
                        FilePath = @"C:\Dicts\demo.mdx",
                    },
                ],
            });

        result.Entries.Should().ContainSingle();
        result.Entries[0].Key.Should().Be("color");
        result.Entries[0].Html.Should().Be("<div>A visual attribute</div>");
        result.Entries[0].DictionaryName.Should().Be("Demo Dictionary");
    }

    [Fact]
    public async Task MdxCompatLookupService_LookupAsync_FuzzyUsesCandidateKeys()
    {
        var service = new MdxCompatLookupService(_ => new FakeMdxReader(
            new Dictionary<string, string?>
            {
                ["apple"] = "<b>fruit</b>",
                ["application"] = "<b>software</b>",
            },
            fuzzyKeys: ["apple", "application"]));

        var result = await service.LookupAsync(
            new MdxLookupParams
            {
                DictionaryId = "mdx::demo",
                Query = "app",
                Fuzzy = true,
            },
            new SettingsSnapshot
            {
                ImportedMdxDictionaries =
                [
                    new ImportedMdxDictionarySnapshot
                    {
                        ServiceId = "mdx::demo",
                        DisplayName = "Demo Dictionary",
                        FilePath = @"C:\Dicts\demo.mdx",
                    },
                ],
            });

        result.Entries.Select(entry => entry.Key).Should().Equal("apple", "application");
        result.Entries.Select(entry => MdxCompatLookupService.ToReadableText(entry.Html))
            .Should()
            .Equal("fruit", "software");
    }

    [Fact]
    public async Task FileSettingsCompatMigrator_MigrateAsync_NormalizesLegacySettingsShape()
    {
        var directory = Path.Combine(Path.GetTempPath(), "easydict-settings-migrate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "legacy.json");
        var target = Path.Combine(directory, "target.json");
        try
        {
            await File.WriteAllTextAsync(source, """
{
  "WindowWidth": 640,
  "WindowHeight": 720,
  "MiniWindowXDips": 10,
  "MiniWindowYDips": 0,
  "UseLongDocWorker": false,
  "MiniWindowEnabledServices": ["google", "openvino-local-ai"],
  "MainWindowEnabledServices": ["openvino-local-ai"],
  "FixedWindowEnabledServices": ["google"],
  "MiniWindowServiceEnabledQuery": { "openvino-local-ai": true }
}
""");

            var result = await new FileSettingsCompatMigrator().MigrateAsync(new SettingsMigrateParams
            {
                LegacySettingsPath = source,
                TargetSettingsPath = target,
            });

            result.Migrated.Should().BeTrue();
            result.Warnings.Should().BeEmpty();

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(target));
            var root = document.RootElement;
            root.GetProperty("WindowWidthDips").GetDouble().Should().Be(640);
            root.GetProperty("WindowHeightDips").GetDouble().Should().Be(720);
            root.GetProperty("MiniWindowPositionSaved").GetBoolean().Should().BeTrue();
            root.TryGetProperty("UseLongDocWorker", out _).Should().BeFalse();
            root.GetProperty("LocalAIProvider").GetString().Should().Be("OpenVINO");
            root.GetProperty("MiniWindowEnabledServices")
                .EnumerateArray()
                .Select(item => item.GetString())
                .Should()
                .Contain("windows-local-ai")
                .And
                .NotContain("openvino-local-ai");
            root.GetProperty("MiniWindowServiceEnabledQuery")
                .GetProperty("windows-local-ai")
                .GetBoolean()
                .Should()
                .BeTrue();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void OcrWorkerCompatRecognizer_DefaultPathMatchesWorkerPackagingLayout()
    {
        OcrWorkerCompatRecognizer.ResolveOcrWorkerPath(@"C:\Program Files\Easydict")
            .Should()
            .Be(Path.Combine(
                @"C:\Program Files\Easydict",
                "workers",
                "ocr",
                "Easydict.Workers.Ocr.exe"));
    }

    [Fact]
    public void LongDocWorkerCompatTranslator_DefaultPathMatchesWorkerPackagingLayout()
    {
        LongDocWorkerCompatTranslator.ResolveLongDocWorkerPath(@"C:\Program Files\Easydict")
            .Should()
            .Be(Path.Combine(
                @"C:\Program Files\Easydict",
                "workers",
                "longdoc",
                "Easydict.Workers.LongDoc.exe"));
    }

    [Fact]
    public void LocalAiWorkerCompatService_DefaultPathMatchesWorkerPackagingLayout()
    {
        LocalAiWorkerCompatService.ResolveLocalAiWorkerPath(@"C:\Program Files\Easydict")
            .Should()
            .Be(Path.Combine(
                @"C:\Program Files\Easydict",
                "workers",
                "localai",
                "Easydict.Workers.LocalAi.exe"));
    }

    private static string SingleLine(StringWriter writer)
    {
        return OutputLines(writer)
            .Should()
            .ContainSingle()
            .Subject;
    }

    private static string[] OutputLines(StringWriter writer)
    {
        return writer.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    }

    private static async Task<string> ReadLineWithTimeoutAsync(StreamReader reader)
    {
        var readTask = reader.ReadLineAsync();
        var completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().Be(readTask, "compat host should write JSONL promptly");

        var line = await readTask;
        line.Should().NotBeNull();
        return line!;
    }

    private sealed class FakeTranslator : ICompatHostTranslator
    {
        public Task<TranslationResultDto> TranslateAsync(
            TranslateParams parameters,
            CancellationToken cancellationToken = default)
        {
            var serviceId = parameters.Services?.FirstOrDefault() ?? "fake";
            return Task.FromResult(new TranslationResultDto
            {
                TranslatedText = $"fake:{parameters.Text}:{parameters.From ?? "auto"}:{parameters.To ?? "zh-Hans"}",
                ServiceId = serviceId,
                ServiceName = "Fake Translator",
                DetectedLanguage = "en",
                TimingMs = 3,
            });
        }

        public async Task<TranslationResultDto> TranslateStreamAsync(
            TranslateParams parameters,
            Func<string, CancellationToken, Task> onChunkAsync,
            CancellationToken cancellationToken = default)
        {
            var serviceId = parameters.Services?.FirstOrDefault() ?? "fake";
            await onChunkAsync("fake-stream:", cancellationToken);
            await onChunkAsync(parameters.Text, cancellationToken);

            return new TranslationResultDto
            {
                TranslatedText = $"fake-stream:{parameters.Text}",
                ServiceId = serviceId,
                ServiceName = "Fake Translator",
                DetectedLanguage = "en",
                TimingMs = 4,
            };
        }

        public async Task<GrammarCorrectResultDto> CorrectGrammarAsync(
            GrammarCorrectParams parameters,
            Func<string, CancellationToken, Task> onChunkAsync,
            CancellationToken cancellationToken = default)
        {
            var serviceId = parameters.Services?.FirstOrDefault() ?? "fake";
            await onChunkAsync("[CORRECTED]", cancellationToken);
            await onChunkAsync("I have an apple.", cancellationToken);

            return new GrammarCorrectResultDto
            {
                OriginalText = parameters.Text,
                CorrectedText = "I have an apple.",
                Explanation = "Use have with I and an before vowel sound.",
                RawText = "[CORRECTED]I have an apple.[/CORRECTED]",
                ServiceId = serviceId,
                ServiceName = "Fake Translator",
                Language = parameters.Language,
                TimingMs = 5,
                HasCorrections = true,
            };
        }
    }

    private sealed class FakeOcrRecognizer : ICompatHostOcrRecognizer
    {
        public SettingsSnapshot ReceivedSettings { get; private set; } = new();

        public Task<OcrResultDto> RecognizeAsync(
            OcrRecognizeParams parameters,
            SettingsSnapshot settings,
            CancellationToken cancellationToken = default)
        {
            ReceivedSettings = settings;
            return Task.FromResult(new OcrResultDto
            {
                Text = $"fake OCR {parameters.PreferredLanguageTag} {parameters.PixelWidth}x{parameters.PixelHeight}",
                Lines =
                [
                    new OcrLineDto
                    {
                        Text = "fake OCR",
                        BoundingRect = new OcrRectDto(0, 0, parameters.PixelWidth, parameters.PixelHeight),
                    },
                ],
                DetectedLanguage = new OcrLanguageDto
                {
                    Tag = parameters.PreferredLanguageTag ?? "auto",
                    DisplayName = "Fake Language",
                },
            });
        }
    }

    private sealed class FakeLongDocTranslator : ICompatHostLongDocTranslator
    {
        public SettingsSnapshot ReceivedSettings { get; private set; } = new();

        public Task<TranslateDocumentResult> TranslateAsync(
            TranslateDocumentParams parameters,
            SettingsSnapshot settings,
            Action<IpcEvent> onEvent,
            CancellationToken cancellationToken = default)
        {
            ReceivedSettings = settings;
            onEvent(new IpcEvent
            {
                Id = "worker-req",
                Event = LongDocEvents.Status,
                Data = JsonLineSerializer.ToElement(new StatusEventData
                {
                    Message = "fake status",
                }),
            });
            onEvent(new IpcEvent
            {
                Id = "worker-req",
                Event = LongDocEvents.Progress,
                Data = JsonLineSerializer.ToElement(new ProgressEventData
                {
                    Stage = "Translating",
                    CurrentBlock = 1,
                    TotalBlocks = 2,
                    CurrentPage = 1,
                    TotalPages = 1,
                    Percentage = 50,
                    CurrentBlockPreview = "Hello",
                }),
            });
            onEvent(new IpcEvent
            {
                Id = "worker-req",
                Event = LongDocEvents.BlockTranslated,
                Data = JsonLineSerializer.ToElement(new BlockTranslatedEventData
                {
                    ChunkIndex = 1,
                    PageNumber = 1,
                    SourceBlockId = "block-1",
                    TranslatedText = "你好",
                    RetryCount = 0,
                }),
            });

            return Task.FromResult(new TranslateDocumentResult
            {
                State = "Completed",
                OutputPath = parameters.OutputPath,
                BilingualOutputPath = parameters.OutputPath,
                TotalChunks = 2,
                SucceededChunks = 2,
                FailedChunkIndexes = [],
            });
        }
    }

    private sealed class FakeLocalAiService : ICompatHostLocalAiService
    {
        public SettingsSnapshot ReceivedSettings { get; private set; } = new();

        public Task<LocalModelStatusDto> PrepareModelAsync(
            PrepareModelParams parameters,
            SettingsSnapshot settings,
            Action<IpcEvent> onEvent,
            CancellationToken cancellationToken = default)
        {
            ReceivedSettings = settings;
            onEvent(new IpcEvent
            {
                Id = "worker-local-prepare",
                Event = LocalAiEvents.DownloadProgress,
                Data = JsonLineSerializer.ToElement(new DownloadProgressEventData
                {
                    BytesDownloaded = 64,
                    TotalBytes = 128,
                    CurrentFile = "model.onnx",
                }),
            });

            return Task.FromResult(new LocalModelStatusDto
            {
                State = "Ready",
                StatusKey = $"Prepared:{parameters.Provider}",
            });
        }

        public Task<LocalAiTranslateResult> TranslateAsync(
            LocalAiTranslateParams parameters,
            SettingsSnapshot settings,
            CancellationToken cancellationToken = default)
        {
            ReceivedSettings = settings;
            return Task.FromResult(new LocalAiTranslateResult
            {
                TranslatedText = $"fake local:{parameters.Text}:{parameters.ProviderMode}",
                ServiceId = "windows-local-ai",
                ServiceName = "Windows Local AI",
                DetectedLanguage = parameters.FromLanguage,
                TimingMs = 12,
            });
        }
    }

    private sealed class FakeMdxLookupService : ICompatHostMdxLookupService
    {
        public SettingsSnapshot ReceivedSettings { get; private set; } = new();

        public Task<MdxLookupResult> LookupAsync(
            MdxLookupParams parameters,
            SettingsSnapshot settings,
            CancellationToken cancellationToken = default)
        {
            ReceivedSettings = settings;
            return Task.FromResult(new MdxLookupResult
            {
                Entries =
                [
                    new MdxLookupEntry
                    {
                        Key = parameters.Query,
                        Html = $"<div>{parameters.Query}</div>",
                        DictionaryName = parameters.DictionaryId,
                    },
                ],
            });
        }
    }

    private sealed class FakeSettingsMigrator : ICompatHostSettingsMigrator
    {
        public string? ReceivedPath { get; private set; }

        public Task<SettingsMigrateResult> MigrateAsync(
            SettingsMigrateParams parameters,
            CancellationToken cancellationToken = default)
        {
            ReceivedPath = parameters.LegacySettingsPath;
            return Task.FromResult(new SettingsMigrateResult
            {
                Migrated = true,
                Warnings = ["fake warning"],
            });
        }
    }

    private sealed class FakeMdxReader : MdxCompatLookupService.IMdxDictionaryReader
    {
        private readonly IReadOnlyDictionary<string, string?> _definitions;
        private readonly IReadOnlyList<string> _fuzzyKeys;

        public FakeMdxReader(
            IReadOnlyDictionary<string, string?> definitions,
            IReadOnlyList<string>? fuzzyKeys = null)
        {
            _definitions = definitions;
            _fuzzyKeys = fuzzyKeys ?? [];
        }

        public (string Key, string? Html) Lookup(string query)
        {
            return (query, _definitions.GetValueOrDefault(query));
        }

        public IEnumerable<string> FuzzyKeys(string query)
        {
            return _fuzzyKeys;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeStreamTranslationService : IStreamTranslationService, IGrammarCorrectionService
    {
        public string ServiceId => "fake-stream";
        public string DisplayName => "Fake Stream Service";
        public bool RequiresApiKey => false;
        public bool IsConfigured => true;
        public IReadOnlyList<Language> SupportedLanguages { get; } =
            [Language.Auto, Language.English, Language.SimplifiedChinese];
        public bool IsStreaming => true;

        public bool SupportsLanguagePair(Language from, Language to) => true;

        public Task<TranslationResult> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new TranslationResult
            {
                OriginalText = request.Text,
                TranslatedText = $"adapter:{request.Text}",
                TargetLanguage = request.ToLanguage,
                ServiceName = DisplayName,
            });
        }

        public Task<Language> DetectLanguageAsync(
            string text,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Language.English);
        }

        public async IAsyncEnumerable<string> TranslateStreamAsync(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return "adapter:";
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return request.Text;
        }

        public async IAsyncEnumerable<string> CorrectGrammarStreamAsync(
            GrammarCorrectionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return "[CORRECTED]";
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return "I have an apple.";
            yield return "[/CORRECTED]";
        }
    }

    private const string OcrMockWorkerScript = """
function Write-JsonLine($value) {
    $json = $value | ConvertTo-Json -Compress -Depth 16
    [Console]::Out.WriteLine($json)
    [Console]::Out.Flush()
}

Write-JsonLine ([ordered]@{
    event = 'ready'
    data = [ordered]@{
        workerKind = 'ocr'
        workerVersion = '1.0.0'
        protocolVersion = 1
        capabilities = @('configure', 'recognize', 'shutdown')
    }
})

while (($line = [Console]::In.ReadLine()) -ne $null) {
    if ([string]::IsNullOrWhiteSpace($line)) {
        continue
    }

    $request = $line | ConvertFrom-Json
    switch ($request.method) {
        'configure' {
            Write-JsonLine ([ordered]@{
                id = $request.id
                result = [ordered]@{ ok = $true }
            })
        }
        'recognize' {
            $lang = [string]$request.params.preferredLanguageTag
            $width = [int]$request.params.pixelWidth
            $height = [int]$request.params.pixelHeight
            Write-JsonLine ([ordered]@{
                id = $request.id
                result = [ordered]@{
                    text = "mock OCR $lang ${width}x$height"
                    lines = @(
                        [ordered]@{
                            text = 'mock OCR'
                            boundingRect = [ordered]@{
                                x = 0
                                y = 0
                                width = $width
                                height = $height
                            }
                        }
                    )
                    detectedLanguage = [ordered]@{
                        tag = $lang
                        displayName = 'Mock Language'
                    }
                    textAngle = 0
                }
            })
            exit 0
        }
        default {
            Write-JsonLine ([ordered]@{
                id = $request.id
                error = [ordered]@{
                    code = 'method_not_found'
                    message = 'unknown method'
                }
            })
        }
    }
}
""";

    private const string OcrExitDuringRecognizeScript = """
function Write-JsonLine($value) {
    $json = $value | ConvertTo-Json -Compress -Depth 16
    [Console]::Out.WriteLine($json)
    [Console]::Out.Flush()
}

Write-JsonLine ([ordered]@{
    event = 'ready'
    data = [ordered]@{
        workerKind = 'ocr'
        workerVersion = '1.0.0'
        protocolVersion = 1
        capabilities = @('configure', 'recognize', 'shutdown')
    }
})

while (($line = [Console]::In.ReadLine()) -ne $null) {
    if ([string]::IsNullOrWhiteSpace($line)) {
        continue
    }

    $request = $line | ConvertFrom-Json
    switch ($request.method) {
        'configure' {
            Write-JsonLine ([ordered]@{
                id = $request.id
                result = [ordered]@{ ok = $true }
            })
        }
        'recognize' {
            exit 1
        }
    }
}
""";

    private const string LongDocMockWorkerScript = """
function Write-JsonLine($value) {
    $json = $value | ConvertTo-Json -Compress -Depth 16
    [Console]::Out.WriteLine($json)
    [Console]::Out.Flush()
}

$configuredModel = ''

Write-JsonLine ([ordered]@{
    event = 'ready'
    data = [ordered]@{
        workerKind = 'longdoc'
        workerVersion = '1.0.0'
        protocolVersion = 1
        capabilities = @('configure', 'translate_document', 'shutdown')
    }
})

while (($line = [Console]::In.ReadLine()) -ne $null) {
    if ([string]::IsNullOrWhiteSpace($line)) {
        continue
    }

    $request = $line | ConvertFrom-Json
    switch ($request.method) {
        'configure' {
            $configuredModel = [string]$request.params.settings.openAIModel
            Write-JsonLine ([ordered]@{
                id = $request.id
                result = [ordered]@{ ok = $true }
            })
        }
        'translate_document' {
            Write-JsonLine ([ordered]@{
                id = $request.id
                event = 'status'
                data = [ordered]@{
                    message = "mock longdoc $configuredModel"
                }
            })
            Write-JsonLine ([ordered]@{
                id = $request.id
                event = 'progress'
                data = [ordered]@{
                    stage = 'Translating'
                    currentBlock = 1
                    totalBlocks = 2
                    currentPage = 1
                    totalPages = 1
                    percentage = 50
                    currentBlockPreview = 'source'
                }
            })
            Write-JsonLine ([ordered]@{
                id = $request.id
                event = 'block_translated'
                data = [ordered]@{
                    chunkIndex = 1
                    pageNumber = 1
                    sourceBlockId = 'block-1'
                    translatedText = '长文档'
                    retryCount = 0
                }
            })

            $resultPath = [string]$request.params.resultJsonPath
            $fullResult = [ordered]@{
                state = 'Completed'
                outputPath = [string]$request.params.outputPath
                bilingualOutputPath = [string]$request.params.outputPath
                totalChunks = 2
                succeededChunks = 2
                failedChunkIndexes = @()
                qualityReport = $null
            }
            $fullResult | ConvertTo-Json -Compress -Depth 16 | Set-Content -Path $resultPath -NoNewline -Encoding utf8

            Write-JsonLine ([ordered]@{
                id = $request.id
                result = [ordered]@{
                    state = 'Completed'
                    totalChunks = 0
                    succeededChunks = 0
                    resultJsonPath = $resultPath
                }
            })
            exit 0
        }
        default {
            Write-JsonLine ([ordered]@{
                id = $request.id
                error = [ordered]@{
                    code = 'method_not_found'
                    message = 'unknown method'
                }
            })
        }
    }
}
""";

    private const string LocalAiMockWorkerScript = """
function Write-JsonLine($value) {
    $json = $value | ConvertTo-Json -Compress -Depth 16
    [Console]::Out.WriteLine($json)
    [Console]::Out.Flush()
}

$configuredModel = ''

Write-JsonLine ([ordered]@{
    event = 'ready'
    data = [ordered]@{
        workerKind = 'localai'
        workerVersion = '1.0.0'
        protocolVersion = 1
        capabilities = @('configure', 'prepare_model', 'translate', 'shutdown')
    }
})

while (($line = [Console]::In.ReadLine()) -ne $null) {
    if ([string]::IsNullOrWhiteSpace($line)) {
        continue
    }

    $request = $line | ConvertFrom-Json
    switch ($request.method) {
        'configure' {
            $configuredModel = [string]$request.params.settings.foundryLocalModel
            Write-JsonLine ([ordered]@{
                id = $request.id
                result = [ordered]@{ ok = $true }
            })
        }
        'prepare_model' {
            Write-JsonLine ([ordered]@{
                id = $request.id
                event = 'download_progress'
                data = [ordered]@{
                    bytesDownloaded = 128
                    totalBytes = 256
                    currentFile = 'model.onnx'
                }
            })
            Write-JsonLine ([ordered]@{
                id = $request.id
                result = [ordered]@{
                    state = 'Ready'
                    statusKey = 'Prepared'
                    detail = $configuredModel
                }
            })
            exit 0
        }
        'translate' {
            $text = [string]$request.params.text
            $from = [string]$request.params.fromLanguage
            Write-JsonLine ([ordered]@{
                id = $request.id
                result = [ordered]@{
                    translatedText = "mock local $text via $configuredModel"
                    serviceId = 'windows-local-ai'
                    serviceName = 'Windows Local AI'
                    detectedLanguage = $from
                    timingMs = 9
                }
            })
            exit 0
        }
        default {
            Write-JsonLine ([ordered]@{
                id = $request.id
                error = [ordered]@{
                    code = 'method_not_found'
                    message = 'unknown method'
                }
            })
        }
    }
}
""";
}
