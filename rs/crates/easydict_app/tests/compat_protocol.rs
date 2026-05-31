use easydict_app::compat_protocol::*;

#[test]
fn compat_host_method_names_match_migration_contract() {
    assert_eq!(compat_methods::TRANSLATE, "translate");
    assert_eq!(compat_methods::TRANSLATE_STREAM, "translate_stream");
    assert_eq!(compat_methods::GRAMMAR_CORRECT, "grammar_correct");
    assert_eq!(compat_methods::OCR_RECOGNIZE, "ocr_recognize");
    assert_eq!(compat_methods::LONGDOC_TRANSLATE, "longdoc_translate");
    assert_eq!(compat_methods::LOCAL_AI_PREPARE, "local_ai_prepare");
    assert_eq!(compat_methods::LOCAL_AI_TRANSLATE, "local_ai_translate");
    assert_eq!(compat_methods::MDX_LOOKUP, "mdx_lookup");
    assert_eq!(compat_methods::SETTINGS_MIGRATE, "settings_migrate");
}

#[test]
fn compat_host_stream_event_names_match_dotnet_contract() {
    assert_eq!(compat_events::TRANSLATE_CHUNK, "translate_chunk");
    assert_eq!(compat_events::TRANSLATE_DONE, "translate_done");
    assert_eq!(compat_events::GRAMMAR_CHUNK, "grammar_chunk");
    assert_eq!(compat_events::GRAMMAR_DONE, "grammar_done");
}

#[test]
fn existing_worker_method_names_stay_compatible_with_dotnet_workers() {
    assert_eq!(worker_methods::CONFIGURE, "configure");
    assert_eq!(worker_methods::CANCEL, "cancel");
    assert_eq!(worker_methods::SHUTDOWN, "shutdown");
    assert_eq!(
        worker_methods::LONGDOC_TRANSLATE_DOCUMENT,
        "translate_document"
    );
    assert_eq!(
        worker_methods::LOCAL_AI_TRANSLATE_STREAM,
        "translate_stream"
    );
    assert_eq!(worker_methods::LOCAL_AI_PREPARE_MODEL, "prepare_model");
    assert_eq!(worker_methods::OCR_RECOGNIZE, "recognize");

    assert_eq!(worker_events::READY, "ready");
    assert_eq!(worker_events::LONGDOC_BLOCK_TRANSLATED, "block_translated");
    assert_eq!(
        worker_events::LOCAL_AI_DOWNLOAD_PROGRESS,
        "download_progress"
    );

    assert_eq!(worker_kinds::LONGDOC, "longdoc");
    assert_eq!(worker_kinds::LOCAL_AI, "localai");
    assert_eq!(worker_kinds::OCR, "ocr");
    assert_eq!(WORKER_PROTOCOL_VERSION_CURRENT, 1);
}

#[test]
fn ipc_request_response_and_event_use_json_lines_shape() {
    let request = IpcRequest::new(
        "req-1",
        worker_methods::LOCAL_AI_TRANSLATE,
        LocalAiTranslateParams {
            text: "Hello".to_string(),
            from_language: "English".to_string(),
            to_language: "ChineseSimplified".to_string(),
            provider_mode: local_ai_provider_modes::AUTO.to_string(),
            custom_prompt: None,
        },
    );

    let json = serialize_json_line(&request).expect("request serializes");
    assert!(!json.ends_with('\n'));
    assert!(json.contains("\"method\":\"translate\""));
    assert!(json.contains("\"fromLanguage\":\"English\""));
    assert!(!json.contains("customPrompt"));

    let line = serialize_json_line_with_newline(&request).expect("request line serializes");
    assert!(line.ends_with('\n'));

    let parsed: IpcRequest<LocalAiTranslateParams> =
        deserialize_json_line(&line).expect("request deserializes");
    assert_eq!(parsed.id, "req-1");
    assert_eq!(parsed.params.expect("params").provider_mode, "Auto");

    let response = IpcResponse::ok(
        "req-1",
        LocalAiTranslateResult {
            translated_text: "你好".to_string(),
            service_id: "windows-local-ai".to_string(),
            service_name: "Windows Local AI".to_string(),
            detected_language: Some("English".to_string()),
            timing_ms: 42,
        },
    );
    let response_json = serialize_json(&response).expect("response serializes");
    assert!(response_json.contains("\"translatedText\":\"你好\""));
    assert!(response.is_success());
    assert!(!response.is_error());

    let event = IpcEvent::for_request(
        "req-1",
        worker_events::LOCAL_AI_CHUNK,
        ChunkEventData {
            text: "你".to_string(),
        },
    );
    let event_json = serialize_json(&event).expect("event serializes");
    assert_eq!(
        event_json,
        "{\"event\":\"chunk\",\"id\":\"req-1\",\"data\":{\"text\":\"你\"}}"
    );
}

#[test]
fn translate_stream_events_roundtrip_with_chunk_and_done_payloads() {
    let chunk = IpcEvent::for_request(
        "req-stream",
        compat_events::TRANSLATE_CHUNK,
        TranslateChunkEventData {
            text: "你".to_string(),
        },
    );

    let chunk_json = serialize_json_line(&chunk).expect("chunk event serializes");
    assert!(chunk_json.contains("\"event\":\"translate_chunk\""));
    assert!(chunk_json.contains("\"text\":\"你\""));

    let chunk_back: IpcEvent<TranslateChunkEventData> =
        deserialize_json_line(&chunk_json).expect("chunk event deserializes");
    assert_eq!(chunk_back.data.expect("chunk data").text, "你");

    let done = IpcEvent::for_request(
        "req-stream",
        compat_events::TRANSLATE_DONE,
        TranslationResultDto {
            translated_text: "你好".to_string(),
            service_id: Some("openai".to_string()),
            service_name: Some("OpenAI".to_string()),
            detected_language: None,
            result_kind: Some("Success".to_string()),
            info_message: None,
            timing_ms: Some(99),
        },
    );

    let done_json = serialize_json_line(&done).expect("done event serializes");
    assert!(done_json.contains("\"event\":\"translate_done\""));
    assert!(done_json.contains("\"translatedText\":\"你好\""));
    assert!(done_json.contains("\"resultKind\":\"Success\""));

    let done_back: IpcEvent<TranslationResultDto> =
        deserialize_json_line(&done_json).expect("done event deserializes");
    assert_eq!(
        done_back.data.expect("done data").service_id.as_deref(),
        Some("openai")
    );
}

#[test]
fn grammar_correct_params_result_and_events_roundtrip() {
    let request = IpcRequest::new(
        "req-grammar",
        compat_methods::GRAMMAR_CORRECT,
        GrammarCorrectParams {
            text: "I has a apple.".to_string(),
            language: Some("en".to_string()),
            services: Some(vec!["openai".to_string()]),
            include_explanations: true,
        },
    );

    let request_json = serialize_json_line(&request).expect("request serializes");
    assert!(request_json.contains("\"method\":\"grammar_correct\""));
    assert!(request_json.contains("\"language\":\"en\""));
    assert!(request_json.contains("\"includeExplanations\":true"));

    let parsed: IpcRequest<GrammarCorrectParams> =
        deserialize_json_line(&request_json).expect("request deserializes");
    assert_eq!(
        parsed.params.expect("params").services.as_deref(),
        Some(&["openai".to_string()][..])
    );

    let chunk = IpcEvent::for_request(
        "req-grammar",
        compat_events::GRAMMAR_CHUNK,
        GrammarChunkEventData {
            text: "[CORRECTED]".to_string(),
        },
    );
    let chunk_json = serialize_json_line(&chunk).expect("chunk serializes");
    assert!(chunk_json.contains("\"event\":\"grammar_chunk\""));
    let chunk_back: IpcEvent<GrammarChunkEventData> =
        deserialize_json_line(&chunk_json).expect("chunk deserializes");
    assert_eq!(chunk_back.data.expect("chunk data").text, "[CORRECTED]");

    let done = IpcEvent::for_request(
        "req-grammar",
        compat_events::GRAMMAR_DONE,
        GrammarCorrectResultDto {
            original_text: "I has a apple.".to_string(),
            corrected_text: "I have an apple.".to_string(),
            explanation: Some("Subject-verb agreement and article.".to_string()),
            raw_text: Some("[CORRECTED]I have an apple.[/CORRECTED]".to_string()),
            service_id: Some("openai".to_string()),
            service_name: Some("OpenAI".to_string()),
            language: Some("en".to_string()),
            timing_ms: Some(42),
            has_corrections: true,
        },
    );
    let done_json = serialize_json_line(&done).expect("done serializes");
    assert!(done_json.contains("\"event\":\"grammar_done\""));
    assert!(done_json.contains("\"correctedText\":\"I have an apple.\""));

    let done_back: IpcEvent<GrammarCorrectResultDto> =
        deserialize_json_line(&done_json).expect("done deserializes");
    let result = done_back.data.expect("done data");
    assert_eq!(result.corrected_text, "I have an apple.");
    assert!(result.has_corrections);
}

#[test]
fn ready_event_roundtrips_with_camel_case_wire_keys() {
    let ready = ReadyEventData {
        worker_kind: worker_kinds::LONGDOC.to_string(),
        worker_version: "1.0.0".to_string(),
        protocol_version: WORKER_PROTOCOL_VERSION_CURRENT,
        capabilities: vec![
            worker_methods::CONFIGURE.to_string(),
            worker_methods::LONGDOC_TRANSLATE_DOCUMENT.to_string(),
        ],
    };

    let json = serialize_json(&ready).expect("ready serializes");
    assert!(json.contains("\"workerKind\":\"longdoc\""));
    assert!(json.contains("\"protocolVersion\":1"));

    let back: ReadyEventData = deserialize_json(&json).expect("ready deserializes");
    assert_eq!(back.worker_kind, worker_kinds::LONGDOC);
    assert_eq!(back.capabilities, ["configure", "translate_document"]);
}

#[test]
fn configure_params_preserve_dotnet_settings_snapshot_names() {
    let configure = ConfigureParams {
        settings: SettingsSnapshot {
            open_ai_api_key: Some("sk-test".to_string()),
            open_ai_model: Some("gpt-4o-mini".to_string()),
            open_ai_temperature: Some(0.3),
            deep_l_api_key: Some("deepl-key".to_string()),
            github_models_api_key: Some("gh-key".to_string()),
            caiyun_token: Some("caiyun-token".to_string()),
            niu_trans_api_key: Some("niu-key".to_string()),
            youdao_app_key: Some("youdao-key".to_string()),
            youdao_app_secret: Some("youdao-secret".to_string()),
            youdao_use_official_api: Some(true),
            custom_open_ai_endpoint: Some("https://example.test/v1".to_string()),
            built_in_ai_api_key: Some("builtin-key".to_string()),
            foundry_local_endpoint: Some("http://127.0.0.1:5000".to_string()),
            open_vino_device: Some("CPU".to_string()),
            local_ai_provider: Some(local_ai_provider_modes::AUTO.to_string()),
            ocr_engine: Some("CustomApi".to_string()),
            ocr_api_key: Some("ocr-key".to_string()),
            ocr_endpoint: Some("https://ocr.example.test/v1/responses".to_string()),
            ocr_model: Some("gpt-vision".to_string()),
            ocr_system_prompt: Some("Extract text.".to_string()),
            ocr_language: Some("ja-JP".to_string()),
            proxy_enabled: Some(true),
            proxy_uri: Some("http://localhost:7890".to_string()),
            long_doc_max_concurrency: Some(8),
            long_doc_enable_document_context_pass: Some(false),
            imported_mdx_dictionaries: Some(vec![ImportedMdxDictionarySnapshot {
                service_id: "mdx::demo".to_string(),
                display_name: "Demo Dictionary".to_string(),
                file_path: r"C:\Dicts\demo.mdx".to_string(),
                is_encrypted: false,
                regcode: None,
                email: None,
                mdd_file_paths: vec![r"C:\Dicts\demo.mdd".to_string()],
            }]),
            ..SettingsSnapshot::default()
        },
    };

    let json = serialize_json(&configure).expect("configure serializes");
    assert!(json.contains("\"openAIApiKey\":\"sk-test\""));
    assert!(json.contains("\"openAIModel\":\"gpt-4o-mini\""));
    assert!(json.contains("\"openAITemperature\":0.3"));
    assert!(json.contains("\"deepLApiKey\":\"deepl-key\""));
    assert!(json.contains("\"githubModelsApiKey\":\"gh-key\""));
    assert!(json.contains("\"caiyunToken\":\"caiyun-token\""));
    assert!(json.contains("\"niuTransApiKey\":\"niu-key\""));
    assert!(json.contains("\"youdaoAppKey\":\"youdao-key\""));
    assert!(json.contains("\"youdaoAppSecret\":\"youdao-secret\""));
    assert!(json.contains("\"youdaoUseOfficialApi\":true"));
    assert!(json.contains("\"customOpenAIEndpoint\""));
    assert!(json.contains("\"builtInAIApiKey\":\"builtin-key\""));
    assert!(json.contains("\"openVinoDevice\":\"CPU\""));
    assert!(json.contains("\"localAIProvider\":\"Auto\""));
    assert!(json.contains("\"ocrEngine\":\"CustomApi\""));
    assert!(json.contains("\"ocrApiKey\":\"ocr-key\""));
    assert!(json.contains("\"ocrEndpoint\":\"https://ocr.example.test/v1/responses\""));
    assert!(json.contains("\"ocrModel\":\"gpt-vision\""));
    assert!(json.contains("\"ocrSystemPrompt\":\"Extract text.\""));
    assert!(json.contains("\"ocrLanguage\":\"ja-JP\""));
    assert!(json.contains("\"longDocMaxConcurrency\":8"));
    assert!(json.contains("\"longDocEnableDocumentContextPass\":false"));
    assert!(json.contains("\"importedMdxDictionaries\""));
    assert!(json.contains("\"mddFilePaths\""));
    assert!(!json.contains("ollamaEndpoint"));

    let back: ConfigureParams = deserialize_json(&json).expect("configure deserializes");
    assert_eq!(back.settings.open_ai_api_key.as_deref(), Some("sk-test"));
    assert_eq!(back.settings.open_ai_temperature, Some(0.3));
    assert_eq!(back.settings.caiyun_token.as_deref(), Some("caiyun-token"));
    assert_eq!(back.settings.niu_trans_api_key.as_deref(), Some("niu-key"));
    assert_eq!(back.settings.youdao_app_key.as_deref(), Some("youdao-key"));
    assert_eq!(
        back.settings.youdao_app_secret.as_deref(),
        Some("youdao-secret")
    );
    assert_eq!(back.settings.youdao_use_official_api, Some(true));
    assert_eq!(back.settings.local_ai_provider.as_deref(), Some("Auto"));
    assert_eq!(back.settings.ocr_engine.as_deref(), Some("CustomApi"));
    assert_eq!(back.settings.ocr_api_key.as_deref(), Some("ocr-key"));
    assert_eq!(
        back.settings.ocr_endpoint.as_deref(),
        Some("https://ocr.example.test/v1/responses")
    );
    assert_eq!(back.settings.ocr_model.as_deref(), Some("gpt-vision"));
    assert_eq!(
        back.settings.ocr_system_prompt.as_deref(),
        Some("Extract text.")
    );
    assert_eq!(back.settings.ocr_language.as_deref(), Some("ja-JP"));
    assert_eq!(back.settings.proxy_enabled, Some(true));
    assert_eq!(back.settings.long_doc_max_concurrency, Some(8));
    assert_eq!(
        back.settings.long_doc_enable_document_context_pass,
        Some(false)
    );
    assert_eq!(
        back.settings
            .imported_mdx_dictionaries
            .as_ref()
            .and_then(|dictionaries| dictionaries.first())
            .map(|dictionary| dictionary.service_id.as_str()),
        Some("mdx::demo")
    );
}

#[test]
fn translate_document_params_and_result_roundtrip() {
    let params = TranslateDocumentParams {
        input_path: r"C:\docs\paper.pdf".to_string(),
        output_path: Some(r"C:\docs\paper_zh.pdf".to_string()),
        input_mode: "Pdf".to_string(),
        from: "English".to_string(),
        to: "ChineseSimplified".to_string(),
        service_id: "openai".to_string(),
        output_mode: "Bilingual".to_string(),
        pdf_export_mode: Some("ContentStreamReplacement".to_string()),
        layout_detection: Some("OnnxLocal".to_string()),
        page_range: Some("1-10".to_string()),
        vision_endpoint: None,
        vision_api_key: None,
        vision_model: None,
        result_json_path: Some(r"C:\Temp\easydict-result.json".to_string()),
    };

    let json = serialize_json(&params).expect("params serialize");
    assert!(json.contains("\"inputPath\""));
    assert!(json.contains("\"resultJsonPath\""));
    assert!(!json.contains("visionApiKey"));

    let back: TranslateDocumentParams = deserialize_json(&json).expect("params deserialize");
    assert_eq!(back.page_range.as_deref(), Some("1-10"));
    assert_eq!(
        back.result_json_path.as_deref(),
        Some(r"C:\Temp\easydict-result.json")
    );

    let result = TranslateDocumentResult {
        state: "PartiallyCompleted".to_string(),
        output_path: Some(r"C:\docs\paper_zh.pdf".to_string()),
        bilingual_output_path: Some(r"C:\docs\paper_bilingual.pdf".to_string()),
        total_chunks: 3,
        succeeded_chunks: 2,
        failed_chunk_indexes: Some(vec![1]),
        quality_report: Some("{\"totalBlocks\":3}".to_string()),
        result_json_path: None,
    };
    let result_json = serialize_json(&result).expect("result serializes");
    let back: TranslateDocumentResult =
        deserialize_json(&result_json).expect("result deserializes");
    assert_eq!(back.failed_chunk_indexes, Some(vec![1]));
    assert_eq!(back.quality_report.as_deref(), Some("{\"totalBlocks\":3}"));
}

#[test]
fn ocr_params_and_result_roundtrip_with_bounding_rects() {
    let params = OcrRecognizeParams {
        pixel_data_path: r"C:\Temp\capture.bgra".to_string(),
        pixel_width: 320,
        pixel_height: 200,
        preferred_language_tag: Some("en-US".to_string()),
    };

    let json = serialize_json(&params).expect("ocr params serialize");
    assert!(json.contains("\"pixelDataPath\""));
    assert!(json.contains("\"preferredLanguageTag\":\"en-US\""));

    let result = OcrResultDto {
        text: "hello".to_string(),
        lines: vec![OcrLineDto {
            text: "hello".to_string(),
            bounding_rect: OcrRectDto {
                x: 1.0,
                y: 2.0,
                width: 3.0,
                height: 4.0,
            },
        }],
        detected_language: Some(OcrLanguageDto {
            tag: "en-US".to_string(),
            display_name: "English".to_string(),
        }),
        text_angle: Some(1.5),
    };

    let result_json = serialize_json(&result).expect("ocr result serialize");
    assert!(result_json.contains("\"boundingRect\""));
    assert!(result_json.contains("\"textAngle\":1.5"));

    let back: OcrResultDto = deserialize_json(&result_json).expect("ocr result deserialize");
    assert_eq!(back.text, "hello");
    assert_eq!(back.detected_language.expect("language").tag, "en-US");
    assert_eq!(back.lines[0].bounding_rect.width, 3.0);
}

#[test]
fn local_ai_model_and_progress_contracts_roundtrip() {
    let prepare = PrepareModelParams {
        provider: local_ai_provider_modes::FOUNDRY_LOCAL.to_string(),
        endpoint: Some("http://127.0.0.1:5273".to_string()),
        model: Some("qwen2.5".to_string()),
    };
    let prepare_json = serialize_json(&prepare).expect("prepare serializes");
    assert!(prepare_json.contains("\"provider\":\"FoundryLocal\""));

    let progress = IpcEvent::for_request(
        "req-download",
        worker_events::LOCAL_AI_DOWNLOAD_PROGRESS,
        DownloadProgressEventData {
            bytes_downloaded: 128,
            total_bytes: 256,
            current_file: Some("model.onnx".to_string()),
        },
    );
    let progress_json = serialize_json(&progress).expect("progress serializes");
    assert!(progress_json.contains("\"bytesDownloaded\":128"));
    assert!(progress_json.contains("\"currentFile\":\"model.onnx\""));

    let status = LocalModelStatusDto {
        state: "Ready".to_string(),
        status_key: Some("Ready".to_string()),
        detail: None,
    };
    let status_json = serialize_json(&status).expect("status serializes");
    assert!(!status_json.contains("detail"));

    let availability = IsAvailableResult {
        available: true,
        state: "Ready".to_string(),
        detail: None,
    };
    let availability_json = serialize_json(&availability).expect("availability serializes");
    let back: IsAvailableResult =
        deserialize_json(&availability_json).expect("availability deserializes");
    assert!(back.available);
}

#[test]
fn planned_mdx_and_settings_migration_contracts_are_serializable() {
    let lookup = IpcRequest::new(
        "req-mdx",
        compat_methods::MDX_LOOKUP,
        MdxLookupParams {
            dictionary_id: "dict-1".to_string(),
            query: "apple".to_string(),
            fuzzy: false,
        },
    );
    let lookup_json = serialize_json(&lookup).expect("lookup serializes");
    assert!(lookup_json.contains("\"method\":\"mdx_lookup\""));
    assert!(lookup_json.contains("\"dictionaryId\":\"dict-1\""));

    let entries = MdxLookupResult {
        entries: vec![MdxLookupEntry {
            key: "apple".to_string(),
            html: "<p>fruit</p>".to_string(),
            dictionary_name: Some("Demo".to_string()),
        }],
    };
    let entries_json = serialize_json(&entries).expect("entries serialize");
    assert!(entries_json.contains("\"dictionaryName\":\"Demo\""));

    let migrate = IpcRequest::new(
        "req-settings",
        compat_methods::SETTINGS_MIGRATE,
        SettingsMigrateParams {
            legacy_settings_path: Some(r"C:\old\settings.json".to_string()),
            target_settings_path: Some(r"C:\new\settings.json".to_string()),
        },
    );
    let migrate_json = serialize_json(&migrate).expect("migrate serializes");
    assert!(migrate_json.contains("\"method\":\"settings_migrate\""));
    assert!(migrate_json.contains("\"legacySettingsPath\""));

    let result = SettingsMigrateResult {
        migrated: true,
        warnings: vec!["missing optional provider".to_string()],
    };
    let result_json = serialize_json(&result).expect("migration result serializes");
    let back: SettingsMigrateResult =
        deserialize_json(&result_json).expect("migration result deserializes");
    assert!(back.migrated);
    assert_eq!(back.warnings, ["missing optional provider"]);
}
