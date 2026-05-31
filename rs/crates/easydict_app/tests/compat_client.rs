use easydict_app::compat_client::{
    default_compat_host_path, CompatClientError, CompatHostClient, CompatHostCommand,
    CompatHostFacade,
};
use easydict_app::compat_protocol::{
    compat_events, compat_methods, ipc_error_codes, BlockTranslatedEventData, ConfigureParams,
    DownloadProgressEventData, GrammarChunkEventData, GrammarCorrectParams,
    GrammarCorrectResultDto, LocalAiTranslateParams, LocalAiTranslateResult, LocalModelStatusDto,
    MdxLookupParams, MdxLookupResult, OcrRecognizeParams, OcrResultDto, PrepareModelParams,
    ProgressEventData, SettingsMigrateParams, SettingsMigrateResult, SettingsSnapshot,
    StatusEventData, TranslateChunkEventData, TranslateDocumentParams, TranslateDocumentResult,
    TranslateParams, TranslationResultDto,
};
use serde_json::Value;
use std::path::Path;

fn mock_host() -> CompatHostClient {
    CompatHostCommand::new("powershell.exe")
        .arg("-NoProfile")
        .arg("-ExecutionPolicy")
        .arg("Bypass")
        .arg("-Command")
        .arg(MOCK_HOST_SCRIPT)
        .spawn()
        .expect("mock host must spawn")
}

const MOCK_HOST_SCRIPT: &str = r#"
function Write-JsonLine($value) {
    $json = $value | ConvertTo-Json -Compress -Depth 16
    [Console]::Out.WriteLine($json)
    [Console]::Out.Flush()
}

while (($line = [Console]::In.ReadLine()) -ne $null) {
    if ([string]::IsNullOrWhiteSpace($line)) {
        continue
    }

    try {
        $request = $line | ConvertFrom-Json
    }
    catch {
        Write-JsonLine ([ordered]@{
            id = 'malformed'
            error = [ordered]@{
                code = 'invalid_json'
                message = $_.Exception.Message
            }
        })
        continue
    }

    switch ($request.method) {
        'configure' {
            Write-JsonLine ([ordered]@{
                id = $request.id
                result = [ordered]@{ ok = $true }
            })
        }
        'translate' {
            $text = [string]$request.params.text
            Write-JsonLine ([ordered]@{
                id = $request.id
                result = [ordered]@{
                    translatedText = "mock:$text"
                    serviceId = 'mock'
                    serviceName = 'Mock Compat Host'
                    detectedLanguage = 'English'
                    timingMs = 7
                }
            })
        }
        'translate_stream' {
            $text = [string]$request.params.text
            Write-JsonLine ([ordered]@{
                event = 'translate_chunk'
                id = $request.id
                data = [ordered]@{ text = 'mock:' }
            })
            Write-JsonLine ([ordered]@{
                event = 'translate_chunk'
                id = $request.id
                data = [ordered]@{ text = $text }
            })
            Write-JsonLine ([ordered]@{
                event = 'translate_done'
                id = $request.id
                data = [ordered]@{
                    translatedText = "mock:$text"
                    serviceId = 'mock'
                    serviceName = 'Mock Compat Host'
                    detectedLanguage = 'English'
                    timingMs = 8
                }
            })
            Write-JsonLine ([ordered]@{
                id = $request.id
                result = [ordered]@{
                    translatedText = "mock:$text"
                    serviceId = 'mock'
                    serviceName = 'Mock Compat Host'
                    detectedLanguage = 'English'
                    timingMs = 8
                }
            })
        }
        'grammar_correct' {
            $text = [string]$request.params.text
            $language = [string]$request.params.language
            Write-JsonLine ([ordered]@{
                event = 'grammar_chunk'
                id = $request.id
                data = [ordered]@{ text = '[CORRECTED]' }
            })
            Write-JsonLine ([ordered]@{
                event = 'grammar_chunk'
                id = $request.id
                data = [ordered]@{ text = 'I have an apple.' }
            })
            Write-JsonLine ([ordered]@{
                event = 'grammar_done'
                id = $request.id
                data = [ordered]@{
                    originalText = $text
                    correctedText = 'I have an apple.'
                    explanation = 'Use have with I and an before apple.'
                    rawText = '[CORRECTED]I have an apple.[/CORRECTED]'
                    serviceId = 'mock'
                    serviceName = 'Mock Compat Host'
                    language = $language
                    timingMs = 9
                    hasCorrections = $true
                }
            })
            Write-JsonLine ([ordered]@{
                id = $request.id
                result = [ordered]@{
                    originalText = $text
                    correctedText = 'I have an apple.'
                    explanation = 'Use have with I and an before apple.'
                    rawText = '[CORRECTED]I have an apple.[/CORRECTED]'
                    serviceId = 'mock'
                    serviceName = 'Mock Compat Host'
                    language = $language
                    timingMs = 9
                    hasCorrections = $true
                }
            })
        }
        'ocr_recognize' {
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
        }
        'longdoc_translate' {
            $outputPath = [string]$request.params.outputPath
            Write-JsonLine ([ordered]@{
                event = 'status'
                id = $request.id
                data = [ordered]@{ message = 'mock longdoc started' }
            })
            Write-JsonLine ([ordered]@{
                event = 'progress'
                id = $request.id
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
                event = 'block_translated'
                id = $request.id
                data = [ordered]@{
                    chunkIndex = 1
                    pageNumber = 1
                    sourceBlockId = 'block-1'
                    translatedText = '长文档'
                    retryCount = 0
                }
            })
            Write-JsonLine ([ordered]@{
                id = $request.id
                result = [ordered]@{
                    state = 'Completed'
                    outputPath = $outputPath
                    bilingualOutputPath = $outputPath
                    totalChunks = 2
                    succeededChunks = 2
                    failedChunkIndexes = @()
                    qualityReport = $null
                }
            })
        }
        'local_ai_prepare' {
            $provider = [string]$request.params.provider
            Write-JsonLine ([ordered]@{
                event = 'download_progress'
                id = $request.id
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
                    detail = $provider
                }
            })
        }
        'local_ai_translate' {
            $text = [string]$request.params.text
            $provider = [string]$request.params.providerMode
            $from = [string]$request.params.fromLanguage
            Write-JsonLine ([ordered]@{
                id = $request.id
                result = [ordered]@{
                    translatedText = "mock local:${text}:${provider}"
                    serviceId = 'windows-local-ai'
                    serviceName = 'Windows Local AI'
                    detectedLanguage = $from
                    timingMs = 10
                }
            })
        }
        'mdx_lookup' {
            $query = [string]$request.params.query
            $dictionaryId = [string]$request.params.dictionaryId
            Write-JsonLine ([ordered]@{
                id = $request.id
                result = [ordered]@{
                    entries = @(
                        [ordered]@{
                            key = $query
                            html = "<div>mock definition for $query</div>"
                            dictionaryName = $dictionaryId
                        }
                    )
                }
            })
        }
        'settings_migrate' {
            Write-JsonLine ([ordered]@{
                id = $request.id
                result = [ordered]@{
                    migrated = $true
                    warnings = @("mock migrated")
                }
            })
        }
        'emit_event_then_translate' {
            Write-JsonLine ([ordered]@{
                event = 'chunk'
                id = $request.id
                data = [ordered]@{ text = 'mock:' }
            })

            $text = [string]$request.params.text
            Write-JsonLine ([ordered]@{
                id = $request.id
                result = [ordered]@{
                    translatedText = "mock:$text"
                    serviceId = 'mock'
                    serviceName = 'Mock Compat Host'
                    detectedLanguage = 'English'
                    timingMs = 7
                }
            })
        }
        'fail_remote' {
            Write-JsonLine ([ordered]@{
                id = $request.id
                error = [ordered]@{
                    code = 'service_error'
                    message = 'mock service failed'
                }
            })
        }
        'exit_now' {
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
"#;

#[test]
fn default_compat_host_path_matches_packaging_contract() {
    let path = default_compat_host_path(Path::new(r"C:\Program Files\Easydict"));
    assert_eq!(
        path,
        Path::new(r"C:\Program Files\Easydict").join("Easydict.CompatHost.exe")
    );
}

#[test]
fn packaged_command_uses_default_compat_host_path() {
    let command = CompatHostCommand::packaged(Path::new(r"C:\Program Files\Easydict"));

    assert_eq!(
        command.program(),
        Path::new(r"C:\Program Files\Easydict").join("Easydict.CompatHost.exe")
    );
    assert!(command.args().is_empty());
}

#[test]
fn send_request_roundtrips_typed_translate_result() {
    let mut client = mock_host();

    let result: TranslationResultDto = client
        .send_request(
            compat_methods::TRANSLATE,
            &TranslateParams {
                text: "Hello".to_string(),
                from: Some("en".to_string()),
                to: Some("zh-Hans".to_string()),
                services: Some(vec!["mock".to_string()]),
            },
        )
        .expect("translate should succeed");

    assert_eq!(result.translated_text, "mock:Hello");
    assert_eq!(result.service_id.as_deref(), Some("mock"));
    assert_eq!(result.timing_ms, Some(7));
    assert!(client.take_events().is_empty());
}

#[test]
fn facade_translate_uses_locked_compat_method() {
    let mut facade = CompatHostFacade::new(mock_host());

    let result = facade
        .translate(&TranslateParams {
            text: "Facade".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["mock".to_string()]),
        })
        .expect("facade translate should succeed");

    assert_eq!(result.translated_text, "mock:Facade");
    assert_eq!(result.service_id.as_deref(), Some("mock"));
    assert!(facade.take_events().is_empty());
}

#[test]
fn facade_configure_uses_worker_configure_method() {
    let mut facade = CompatHostFacade::new(mock_host());

    let result = facade
        .configure(&ConfigureParams {
            settings: SettingsSnapshot {
                long_doc_max_concurrency: Some(8),
                long_doc_enable_document_context_pass: Some(false),
                ..SettingsSnapshot::default()
            },
        })
        .expect("facade configure should succeed");

    assert!(result.ok);
}

#[test]
fn facade_translate_stream_queues_chunk_and_done_events_before_result() {
    let mut facade = CompatHostFacade::new(mock_host());

    let result = facade
        .translate_stream(&TranslateParams {
            text: "Streaming".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["mock".to_string()]),
        })
        .expect("facade stream translate should succeed");

    assert_eq!(result.translated_text, "mock:Streaming");
    assert_eq!(result.timing_ms, Some(8));

    let events = facade.take_events();
    assert_eq!(events.len(), 3);
    assert_eq!(events[0].event, compat_events::TRANSLATE_CHUNK);
    assert_eq!(events[1].event, compat_events::TRANSLATE_CHUNK);
    assert_eq!(events[2].event, compat_events::TRANSLATE_DONE);

    let first_chunk: TranslateChunkEventData =
        serde_json::from_value(events[0].data.clone().expect("first chunk data"))
            .expect("first chunk data parses");
    let second_chunk: TranslateChunkEventData =
        serde_json::from_value(events[1].data.clone().expect("second chunk data"))
            .expect("second chunk data parses");
    let done: TranslationResultDto =
        serde_json::from_value(events[2].data.clone().expect("done data"))
            .expect("done data parses");

    assert_eq!(first_chunk.text, "mock:");
    assert_eq!(second_chunk.text, "Streaming");
    assert_eq!(done.translated_text, "mock:Streaming");
}

#[test]
fn facade_translate_stream_observes_chunks_and_clears_event_queue() {
    let mut facade = CompatHostFacade::new(mock_host());
    let mut chunks = Vec::new();

    let result = facade
        .translate_stream_observing_chunks(
            &TranslateParams {
                text: "Streaming".to_string(),
                from: Some("en".to_string()),
                to: Some("zh-Hans".to_string()),
                services: Some(vec!["mock".to_string()]),
            },
            |chunk| chunks.push(chunk.text),
        )
        .expect("facade stream translate should succeed");

    assert_eq!(chunks, ["mock:", "Streaming"]);
    assert_eq!(result.translated_text, "mock:Streaming");
    assert!(facade.take_events().is_empty());
}

#[test]
fn facade_grammar_correct_queues_chunk_and_done_events_before_result() {
    let mut facade = CompatHostFacade::new(mock_host());

    let result = facade
        .grammar_correct(&GrammarCorrectParams {
            text: "I has a apple.".to_string(),
            language: Some("en".to_string()),
            services: Some(vec!["mock".to_string()]),
            include_explanations: true,
        })
        .expect("facade grammar correct should succeed");

    assert_eq!(result.original_text, "I has a apple.");
    assert_eq!(result.corrected_text, "I have an apple.");
    assert_eq!(result.language.as_deref(), Some("en"));
    assert_eq!(result.timing_ms, Some(9));
    assert!(result.has_corrections);

    let events = facade.take_events();
    assert_eq!(events.len(), 3);
    assert_eq!(events[0].event, compat_events::GRAMMAR_CHUNK);
    assert_eq!(events[1].event, compat_events::GRAMMAR_CHUNK);
    assert_eq!(events[2].event, compat_events::GRAMMAR_DONE);

    let first_chunk: GrammarChunkEventData =
        serde_json::from_value(events[0].data.clone().expect("first chunk data"))
            .expect("first chunk data parses");
    let second_chunk: GrammarChunkEventData =
        serde_json::from_value(events[1].data.clone().expect("second chunk data"))
            .expect("second chunk data parses");
    let done: GrammarCorrectResultDto =
        serde_json::from_value(events[2].data.clone().expect("done data"))
            .expect("done data parses");

    assert_eq!(first_chunk.text, "[CORRECTED]");
    assert_eq!(second_chunk.text, "I have an apple.");
    assert_eq!(done.corrected_text, "I have an apple.");
}

#[test]
fn facade_ocr_recognize_uses_locked_compat_method() {
    let mut facade = CompatHostFacade::new(mock_host());

    let result: OcrResultDto = facade
        .ocr_recognize(&OcrRecognizeParams {
            pixel_data_path: r"C:\Temp\capture.bgra".to_string(),
            pixel_width: 4,
            pixel_height: 3,
            preferred_language_tag: Some("ja-JP".to_string()),
        })
        .expect("facade OCR should succeed");

    assert_eq!(result.text, "mock OCR ja-JP 4x3");
    assert_eq!(result.lines.len(), 1);
    assert_eq!(result.lines[0].bounding_rect.height, 3.0);
    assert_eq!(
        result
            .detected_language
            .as_ref()
            .map(|language| language.tag.as_str()),
        Some("ja-JP")
    );
}

#[test]
fn facade_longdoc_translate_uses_locked_compat_method_and_queues_worker_events() {
    let mut facade = CompatHostFacade::new(mock_host());

    let result: TranslateDocumentResult = facade
        .longdoc_translate(&TranslateDocumentParams {
            input_path: r"C:\Temp\source.md".to_string(),
            output_path: Some(r"C:\Temp\translated.md".to_string()),
            input_mode: "Markdown".to_string(),
            from: "English".to_string(),
            to: "ChineseSimplified".to_string(),
            service_id: "openai".to_string(),
            output_mode: "Bilingual".to_string(),
            pdf_export_mode: None,
            layout_detection: Some("Heuristic".to_string()),
            page_range: None,
            vision_endpoint: None,
            vision_api_key: None,
            vision_model: None,
            result_json_path: None,
        })
        .expect("facade longdoc should succeed");

    assert_eq!(result.state, "Completed");
    assert_eq!(result.total_chunks, 2);
    assert_eq!(
        result.output_path.as_deref(),
        Some(r"C:\Temp\translated.md")
    );

    let events = facade.take_events();
    assert_eq!(events.len(), 3);
    assert_eq!(events[0].event, "status");
    assert_eq!(events[1].event, "progress");
    assert_eq!(events[2].event, "block_translated");

    let status: StatusEventData =
        serde_json::from_value(events[0].data.clone().expect("status data"))
            .expect("status data parses");
    let progress: ProgressEventData =
        serde_json::from_value(events[1].data.clone().expect("progress data"))
            .expect("progress data parses");
    let block: BlockTranslatedEventData =
        serde_json::from_value(events[2].data.clone().expect("block data"))
            .expect("block data parses");

    assert_eq!(status.message, "mock longdoc started");
    assert_eq!(progress.percentage, 50.0);
    assert_eq!(block.translated_text, "长文档");
}

#[test]
fn facade_local_ai_prepare_queues_download_progress_before_status_result() {
    let mut facade = CompatHostFacade::new(mock_host());

    let result: LocalModelStatusDto = facade
        .local_ai_prepare(&PrepareModelParams {
            provider: "FoundryLocal".to_string(),
            endpoint: None,
            model: Some("phi".to_string()),
        })
        .expect("facade local AI prepare should succeed");

    assert_eq!(result.state, "Ready");
    assert_eq!(result.status_key.as_deref(), Some("Prepared"));
    assert_eq!(result.detail.as_deref(), Some("FoundryLocal"));

    let events = facade.take_events();
    assert_eq!(events.len(), 1);
    assert_eq!(events[0].event, "download_progress");

    let progress: DownloadProgressEventData =
        serde_json::from_value(events[0].data.clone().expect("download progress data"))
            .expect("download progress data parses");
    assert_eq!(progress.bytes_downloaded, 128);
    assert_eq!(progress.total_bytes, 256);
    assert_eq!(progress.current_file.as_deref(), Some("model.onnx"));
}

#[test]
fn facade_local_ai_translate_uses_locked_compat_method() {
    let mut facade = CompatHostFacade::new(mock_host());

    let result: LocalAiTranslateResult = facade
        .local_ai_translate(&LocalAiTranslateParams {
            text: "Hello".to_string(),
            from_language: "English".to_string(),
            to_language: "ChineseSimplified".to_string(),
            provider_mode: "FoundryLocal".to_string(),
            custom_prompt: None,
        })
        .expect("facade local AI translate should succeed");

    assert_eq!(result.translated_text, "mock local:Hello:FoundryLocal");
    assert_eq!(result.service_id, "windows-local-ai");
    assert_eq!(result.detected_language.as_deref(), Some("English"));
    assert_eq!(result.timing_ms, 10);
    assert!(facade.take_events().is_empty());
}

#[test]
fn facade_mdx_lookup_uses_locked_compat_method() {
    let mut facade = CompatHostFacade::new(mock_host());

    let result: MdxLookupResult = facade
        .mdx_lookup(&MdxLookupParams {
            dictionary_id: "mdx::demo".to_string(),
            query: "apple".to_string(),
            fuzzy: false,
        })
        .expect("facade MDX lookup should succeed");

    assert_eq!(result.entries.len(), 1);
    assert_eq!(result.entries[0].key, "apple");
    assert_eq!(
        result.entries[0].html,
        "<div>mock definition for apple</div>"
    );
    assert_eq!(
        result.entries[0].dictionary_name.as_deref(),
        Some("mdx::demo")
    );
}

#[test]
fn facade_settings_migrate_uses_locked_compat_method() {
    let mut facade = CompatHostFacade::new(mock_host());

    let result: SettingsMigrateResult = facade
        .settings_migrate(&SettingsMigrateParams {
            legacy_settings_path: Some(r"C:\Old\settings.json".to_string()),
            target_settings_path: Some(r"C:\New\settings.json".to_string()),
        })
        .expect("facade settings migration should succeed");

    assert!(result.migrated);
    assert_eq!(result.warnings, ["mock migrated"]);
}

#[test]
fn events_before_response_are_queued_for_callers() {
    let mut client = mock_host();

    let result: TranslationResultDto = client
        .send_request(
            "emit_event_then_translate",
            &TranslateParams {
                text: "Streaming".to_string(),
                from: None,
                to: Some("zh-Hans".to_string()),
                services: None,
            },
        )
        .expect("translate should succeed after event");

    assert_eq!(result.translated_text, "mock:Streaming");

    let events = client.take_events();
    assert_eq!(events.len(), 1);
    assert_eq!(events[0].event, "chunk");
    assert!(events[0]
        .id
        .as_deref()
        .is_some_and(|id| id.starts_with("rust-compat-")));
    assert_eq!(
        events[0].data.as_ref().and_then(|data| data.get("text")),
        Some(&Value::String("mock:".to_string()))
    );
    assert!(client.take_events().is_empty());
}

#[test]
fn remote_errors_preserve_protocol_code_and_message() {
    let mut client = mock_host();

    let error = client
        .send_request::<_, TranslationResultDto>(
            "fail_remote",
            &TranslateParams {
                text: "Hello".to_string(),
                from: None,
                to: None,
                services: None,
            },
        )
        .expect_err("remote failure should surface");

    match error {
        CompatClientError::Remote(remote) => {
            assert_eq!(remote.code, ipc_error_codes::SERVICE_ERROR);
            assert_eq!(remote.message, "mock service failed");
        }
        other => panic!("expected remote error, got {other:?}"),
    }
}

#[test]
fn process_exit_before_response_is_reported() {
    let mut client = mock_host();

    let error = client
        .send_request::<_, TranslationResultDto>(
            "exit_now",
            &TranslateParams {
                text: "Hello".to_string(),
                from: None,
                to: None,
                services: None,
            },
        )
        .expect_err("process exit should surface");

    assert!(matches!(error, CompatClientError::ProcessExited));
}

#[test]
fn missing_host_path_is_classified_for_fallback() {
    let error =
        match CompatHostCommand::new("__definitely_missing_easydict_compat_host__.exe").spawn() {
            Ok(_) => panic!("missing host should fail"),
            Err(error) => error,
        };

    assert!(error.is_not_found());
}

#[test]
fn packaged_facade_missing_host_is_classified_for_fallback() {
    let missing_app_dir =
        std::env::temp_dir().join(format!("easydict-missing-app-dir-{}", std::process::id()));

    let error = match CompatHostFacade::spawn_packaged(missing_app_dir) {
        Ok(_) => panic!("missing packaged host should fail"),
        Err(error) => error,
    };

    assert!(error.is_not_found());
}
