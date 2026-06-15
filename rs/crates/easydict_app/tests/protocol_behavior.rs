//! Default Rust protocol facade behavior plus retained-feature protocol guards.

#[cfg(feature = "retained-dotnet-workers")]
use easydict_app::compat_protocol::{
    deserialize_json_line, serialize_json_line, serialize_json_line_with_newline, worker_events,
    worker_kinds, worker_methods, CancelRequestParams, CancelRequestResult, ChunkEventData,
    IpcEvent, IpcRequest, IpcResponse, LocalAiTranslateParams, ReadyEventData, ShutdownResult,
    TranslateStreamResult, WORKER_PROTOCOL_VERSION_CURRENT,
};
use easydict_app::protocol::*;
use std::path::PathBuf;

#[test]
fn local_ai_provider_mode_aliases_normalize_to_protocol_values() {
    for alias in [
        None,
        Some(""),
        Some("Auto"),
        Some(" auto "),
        Some("unknown"),
    ] {
        assert_eq!(
            normalize_local_ai_provider_mode(alias),
            local_ai_provider_modes::AUTO,
            "alias {alias:?} should normalize to Auto"
        );
    }
    for alias in [
        Some("WindowsAI"),
        Some("windows-ai"),
        Some("windows_ai"),
        Some("Phi"),
        Some("phi-silica"),
        Some("phi_silica"),
    ] {
        assert_eq!(
            normalize_local_ai_provider_mode(alias),
            local_ai_provider_modes::WINDOWS_AI,
            "alias {alias:?} should normalize to WindowsAI"
        );
    }
    for alias in [
        Some("FoundryLocal"),
        Some("foundry-local"),
        Some("foundry_local"),
        Some("foundry"),
        Some("local-ai"),
        Some("local_ai"),
    ] {
        assert_eq!(
            normalize_local_ai_provider_mode(alias),
            local_ai_provider_modes::FOUNDRY_LOCAL,
            "alias {alias:?} should normalize to FoundryLocal"
        );
    }
    for alias in [Some("OpenVINO"), Some("open-vino"), Some("open_vino")] {
        assert_eq!(
            normalize_local_ai_provider_mode(alias),
            local_ai_provider_modes::OPENVINO,
            "alias {alias:?} should normalize to OpenVINO"
        );
    }
}

#[test]
fn settings_snapshot_cache_dir_helpers_trim_and_ignore_blank_values() {
    let settings = SettingsSnapshot {
        cache_dir: Some("  portable-cache  ".to_string()),
        ..SettingsSnapshot::default()
    };

    assert_eq!(settings.cache_dir_str(), Some("portable-cache"));
    assert_eq!(
        settings.cache_dir_path(),
        Some(PathBuf::from("portable-cache"))
    );

    let blank_settings = SettingsSnapshot {
        cache_dir: Some(" \t\r\n ".to_string()),
        ..SettingsSnapshot::default()
    };

    assert_eq!(blank_settings.cache_dir_str(), None);
    assert_eq!(blank_settings.cache_dir_path(), None);
}

#[test]
#[cfg(feature = "retained-dotnet-workers")]
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
    assert_eq!(worker_methods::LOCAL_AI_GRAMMAR_STREAM, "grammar_stream");

    assert_eq!(worker_events::READY, "ready");
    assert_eq!(worker_events::LONGDOC_BLOCK_TRANSLATED, "block_translated");
    assert_eq!(worker_events::LOCAL_AI_CHUNK, "chunk");

    assert_eq!(worker_kinds::LONGDOC, "longdoc");
    assert_eq!(worker_kinds::LOCAL_AI, "localai");
    assert_eq!(WORKER_PROTOCOL_VERSION_CURRENT, 1);
}

#[test]
#[cfg(feature = "retained-dotnet-workers")]
fn ipc_request_response_and_event_use_json_lines_shape() {
    let request = IpcRequest::new(
        "req-1",
        worker_methods::LOCAL_AI_TRANSLATE_STREAM,
        LocalAiTranslateParams {
            text: "Hello".to_string(),
            from_language: "English".to_string(),
            to_language: "ChineseSimplified".to_string(),
            provider_mode: local_ai_provider_modes::AUTO.to_string(),
            custom_prompt: None,
            include_explanations: None,
        },
    );

    let json = serialize_json_line(&request).expect("request serializes");
    assert!(!json.ends_with('\n'));
    assert!(json.contains("\"method\":\"translate_stream\""));
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
        TranslateStreamResult {
            done: true,
            full_text: Some("你好".to_string()),
        },
    );
    let response_json = serialize_json(&response).expect("response serializes");
    assert!(response_json.contains("\"fullText\":\"你好\""));
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
#[cfg(feature = "retained-dotnet-workers")]
fn worker_lifecycle_payloads_use_dotnet_json_shape() {
    let cancel = CancelRequestParams {
        target_request_id: "rust-worker-7".to_string(),
    };
    let cancel_json = serialize_json(&cancel).expect("cancel params serialize");
    assert_eq!(cancel_json, "{\"targetRequestId\":\"rust-worker-7\"}");

    let cancel_result = CancelRequestResult { cancelled: true };
    let cancel_result_json = serialize_json(&cancel_result).expect("cancel result serializes");
    assert_eq!(cancel_result_json, "{\"cancelled\":true}");

    let shutdown_result = ShutdownResult { ok: true };
    let shutdown_result_json =
        serialize_json(&shutdown_result).expect("shutdown result serializes");
    assert_eq!(shutdown_result_json, "{\"ok\":true}");
}

#[test]
fn translation_result_dto_roundtrips_alternatives_with_camel_case_field() {
    let dto = TranslationResultDto {
        translated_text: "Hallo".to_string(),
        service_id: Some("linguee".to_string()),
        service_name: Some("Linguee Dictionary".to_string()),
        detected_language: None,
        result_kind: Some("Success".to_string()),
        info_message: None,
        timing_ms: None,
        alternatives: Some(vec!["Servus".to_string(), "Hallöchen".to_string()]),
        word_result: None,
        raw_html: None,
    };

    let json = serialize_json(&dto).expect("dto serializes");
    assert!(json.contains("\"alternatives\":[\"Servus\",\"Hallöchen\"]"));

    let back: TranslationResultDto = deserialize_json(&json).expect("dto deserializes");
    assert_eq!(
        back.alternatives.as_deref(),
        Some(&["Servus".to_string(), "Hallöchen".to_string()][..])
    );
}

#[test]
fn translation_result_dto_roundtrips_word_result_with_camel_case_fields() {
    let dto = TranslationResultDto {
        translated_text: "hello".to_string(),
        service_id: Some("youdao".to_string()),
        service_name: Some("Youdao".to_string()),
        detected_language: Some("en".to_string()),
        result_kind: Some("Success".to_string()),
        info_message: None,
        timing_ms: None,
        alternatives: None,
        word_result: Some(WordResultDto {
            phonetics: Some(vec![PhoneticDto {
                text: Some("heh-loh".to_string()),
                audio_url: Some("https://dict.youdao.com/dictvoice?audio=hello".to_string()),
                accent: Some("US".to_string()),
            }]),
            definitions: Some(vec![DefinitionDto {
                part_of_speech: Some("int.".to_string()),
                meanings: Some(vec!["used as a greeting".to_string()]),
            }]),
            examples: Some(vec!["Hello, world.".to_string()]),
            word_forms: Some(vec![WordFormDto {
                name: Some("plural".to_string()),
                value: Some("hellos".to_string()),
            }]),
            synonyms: Some(vec![SynonymDto {
                part_of_speech: Some("n.".to_string()),
                meaning: Some("greeting".to_string()),
                words: Some(vec!["salutation".to_string(), "welcome".to_string()]),
            }]),
        }),
        raw_html: None,
    };

    let json = serialize_json(&dto).expect("dto serializes");
    assert!(json.contains("\"wordResult\""));
    assert!(json.contains("\"audioUrl\""));
    assert!(json.contains("\"partOfSpeech\""));
    assert!(json.contains("\"wordForms\""));

    let back: TranslationResultDto = deserialize_json(&json).expect("dto deserializes");
    let word_result = back.word_result.expect("word result");
    assert_eq!(
        word_result.phonetics.as_deref().unwrap()[0]
            .accent
            .as_deref(),
        Some("US")
    );
    assert_eq!(
        word_result.definitions.as_deref().unwrap()[0]
            .meanings
            .as_deref(),
        Some(&["used as a greeting".to_string()][..])
    );
    assert_eq!(
        word_result.synonyms.as_deref().unwrap()[0].words.as_deref(),
        Some(&["salutation".to_string(), "welcome".to_string()][..])
    );
}

#[test]
fn translation_result_dto_roundtrips_raw_html_as_optional_camel_case_field() {
    let dto = TranslationResultDto {
        translated_text: "fruit".to_string(),
        service_id: Some("mdx::demo".to_string()),
        service_name: Some("Demo Dictionary".to_string()),
        detected_language: None,
        result_kind: Some("Success".to_string()),
        info_message: None,
        timing_ms: None,
        alternatives: None,
        word_result: None,
        raw_html: Some("<div>fruit</div>".to_string()),
    };

    let json = serialize_json(&dto).expect("dto serializes");
    assert!(json.contains("\"rawHtml\":\"<div>fruit</div>\""));

    let back: TranslationResultDto = deserialize_json(&json).expect("dto deserializes");
    assert_eq!(back.raw_html.as_deref(), Some("<div>fruit</div>"));

    let legacy: TranslationResultDto =
        deserialize_json("{\"translatedText\":\"fruit\"}").expect("legacy dto deserializes");
    assert_eq!(legacy.raw_html, None);
}

#[test]
fn grammar_correct_params_and_result_roundtrip() {
    let params = GrammarCorrectParams {
        text: "I has a apple.".to_string(),
        language: Some("en".to_string()),
        services: Some(vec!["openai".to_string()]),
        include_explanations: true,
    };

    let params_json = serialize_json(&params).expect("params serializes");
    assert!(params_json.contains("\"language\":\"en\""));
    assert!(params_json.contains("\"includeExplanations\":true"));

    let parsed: GrammarCorrectParams = deserialize_json(&params_json).expect("params deserializes");
    assert_eq!(
        parsed.services.as_deref(),
        Some(&["openai".to_string()][..])
    );

    let result = GrammarCorrectResultDto {
        original_text: "I has a apple.".to_string(),
        corrected_text: "I have an apple.".to_string(),
        explanation: Some("Subject-verb agreement and article.".to_string()),
        raw_text: Some("[CORRECTED]I have an apple.[/CORRECTED]".to_string()),
        service_id: Some("openai".to_string()),
        service_name: Some("OpenAI".to_string()),
        language: Some("en".to_string()),
        timing_ms: Some(42),
        has_corrections: true,
    };
    let result_json = serialize_json(&result).expect("result serializes");
    assert!(result_json.contains("\"correctedText\":\"I have an apple.\""));

    let result: GrammarCorrectResultDto =
        deserialize_json(&result_json).expect("result deserializes");
    assert_eq!(result.corrected_text, "I have an apple.");
    assert!(result.has_corrections);
}

#[test]
#[cfg(feature = "retained-dotnet-workers")]
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
fn settings_snapshot_preserves_dotnet_json_names() {
    let settings = SettingsSnapshot {
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
        request_timeout_ms: Some(120_000),
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
    };

    let json = serialize_json(&settings).expect("settings snapshot serializes");
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
    assert!(json.contains("\"requestTimeoutMs\":120000"));
    assert!(json.contains("\"importedMdxDictionaries\""));
    assert!(json.contains("\"mddFilePaths\""));
    assert!(!json.contains("ollamaEndpoint"));

    let back: SettingsSnapshot = deserialize_json(&json).expect("settings snapshot deserializes");
    assert_eq!(back.open_ai_api_key.as_deref(), Some("sk-test"));
    assert_eq!(back.open_ai_temperature, Some(0.3));
    assert_eq!(back.caiyun_token.as_deref(), Some("caiyun-token"));
    assert_eq!(back.niu_trans_api_key.as_deref(), Some("niu-key"));
    assert_eq!(back.youdao_app_key.as_deref(), Some("youdao-key"));
    assert_eq!(back.youdao_app_secret.as_deref(), Some("youdao-secret"));
    assert_eq!(back.youdao_use_official_api, Some(true));
    assert_eq!(back.local_ai_provider.as_deref(), Some("Auto"));
    assert_eq!(back.ocr_engine.as_deref(), Some("CustomApi"));
    assert_eq!(back.ocr_api_key.as_deref(), Some("ocr-key"));
    assert_eq!(
        back.ocr_endpoint.as_deref(),
        Some("https://ocr.example.test/v1/responses")
    );
    assert_eq!(back.ocr_model.as_deref(), Some("gpt-vision"));
    assert_eq!(back.ocr_system_prompt.as_deref(), Some("Extract text."));
    assert_eq!(back.ocr_language.as_deref(), Some("ja-JP"));
    assert_eq!(back.proxy_enabled, Some(true));
    assert_eq!(back.long_doc_max_concurrency, Some(8));
    assert_eq!(back.long_doc_enable_document_context_pass, Some(false));
    assert_eq!(back.request_timeout_ms, Some(120_000));
    assert_eq!(
        back.imported_mdx_dictionaries
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
        request_timeout_ms: Some(120_000),
    };

    let json = serialize_json(&params).expect("params serialize");
    assert!(json.contains("\"inputPath\""));
    assert!(json.contains("\"resultJsonPath\""));
    assert!(json.contains("\"requestTimeoutMs\":120000"));
    assert!(!json.contains("visionApiKey"));

    let back: TranslateDocumentParams = deserialize_json(&json).expect("params deserialize");
    assert_eq!(back.page_range.as_deref(), Some("1-10"));
    assert_eq!(
        back.result_json_path.as_deref(),
        Some(r"C:\Temp\easydict-result.json")
    );
    assert_eq!(back.request_timeout_ms, Some(120_000));

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
fn mdx_lookup_dtos_are_serializable_for_native_mdx_contract() {
    let params = MdxLookupParams {
        dictionary_id: "dict-1".to_string(),
        query: "apple".to_string(),
        fuzzy: false,
    };
    let params_json = serialize_json(&params).expect("lookup params serialize");
    assert!(params_json.contains("\"dictionaryId\":\"dict-1\""));

    let entries = MdxLookupResult {
        entries: vec![MdxLookupEntry {
            key: "apple".to_string(),
            html: "<p>fruit</p>".to_string(),
            dictionary_name: Some("Demo".to_string()),
        }],
        mdd_resources_inlined: true,
    };
    let entries_json = serialize_json(&entries).expect("entries serialize");
    assert!(entries_json.contains("\"dictionaryName\":\"Demo\""));
    assert!(entries_json.contains("\"mddResourcesInlined\":true"));
}
