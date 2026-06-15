#![cfg(windows)]

use std::fs;
use std::path::PathBuf;
use std::process::{Command, Output};
use std::time::{SystemTime, UNIX_EPOCH};

#[test]
fn help_lists_long_document_options() {
    let output = long_doc_cli()
        .arg("--help")
        .output()
        .expect("long document CLI should run");

    assert_success(&output);
    let stdout = stdout(&output);
    for expected in [
        "--help",
        "--list-services",
        "--input",
        "--target-language",
        "--from",
        "--output",
        "--result-json",
        "--retry-failed",
        "--service",
        "--output-mode",
        "--layout",
        "--pdf-export-mode",
        "--page",
        "--page-range",
        "--max-concurrency",
    ] {
        assert!(
            stdout.contains(expected),
            "help should mention {expected}\nstdout:\n{stdout}"
        );
    }
    assert!(
        !stdout.contains("--app-dir"),
        "legacy no-op app-dir should stay hidden from first rs portable help\nstdout:\n{stdout}"
    );
    assert!(
        stderr(&output).trim().is_empty(),
        "help should not write diagnostics to stderr:\n{}",
        stderr(&output)
    );
}

#[test]
fn list_services_succeeds_without_document_arguments() {
    let settings_dir = unique_temp_dir("easydict-long-doc-cli-list-settings");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");

    let output = long_doc_cli()
        .arg("--list-services")
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .output()
        .expect("long document CLI should run");

    assert_success(&output);
    let stdout = stdout(&output);
    let normalized = stdout.to_ascii_lowercase();
    for expected in ["google", "openai"] {
        assert!(
            normalized.contains(expected),
            "service list should include {expected}\nstdout:\n{stdout}"
        );
    }
    assert!(
        stdout
            .lines()
            .filter(|line| !line.trim().is_empty())
            .count()
            >= 2,
        "service list should contain multiple service rows\nstdout:\n{stdout}"
    );
    assert!(
        stderr(&output).trim().is_empty(),
        "service listing should not write diagnostics to stderr:\n{}",
        stderr(&output)
    );

    let _ = fs::remove_dir_all(settings_dir);
}

#[test]
fn command_requires_input() {
    let output = long_doc_cli()
        .args(["--target-language", "zh-Hans", "--service", "google"])
        .output()
        .expect("long document CLI should run");

    assert_failure(&output);
    let stderr = stderr(&output);
    assert!(
        stderr.to_ascii_lowercase().contains("input"),
        "stderr should explain that --input is required:\n{stderr}"
    );
    assert!(
        stdout(&output).trim().is_empty(),
        "missing input should not write output"
    );
}

#[test]
fn command_requires_target_language() {
    let work_dir = unique_temp_dir("easydict-long-doc-cli-target");
    fs::create_dir_all(&work_dir).expect("work directory should be created");
    let input_path = work_dir.join("sample.txt");
    fs::write(&input_path, "Hello long document").expect("sample input should be written");

    let output = long_doc_cli()
        .arg("--input")
        .arg(&input_path)
        .args(["--service", "google"])
        .output()
        .expect("long document CLI should run");

    assert_failure(&output);
    let stderr = stderr(&output);
    let normalized = stderr.to_ascii_lowercase();
    assert!(
        normalized.contains("target") && normalized.contains("language"),
        "stderr should explain that --target-language is required:\n{stderr}"
    );
    assert!(
        stdout(&output).trim().is_empty(),
        "missing target language should not write output"
    );

    let _ = fs::remove_dir_all(work_dir);
}

#[test]
fn page_and_page_range_are_mutually_exclusive() {
    let work_dir = unique_temp_dir("easydict-long-doc-cli-page-conflict");
    let app_dir = work_dir.join("app");
    fs::create_dir_all(&app_dir).expect("app directory should be created");
    let input_path = work_dir.join("sample.pdf");
    let output_path = work_dir.join("translated.txt");
    fs::write(
        &input_path,
        "%PDF-1.7\n% parse conflict should win before IO",
    )
    .expect("sample input should be written");

    let output = long_doc_cli()
        .arg("--input")
        .arg(&input_path)
        .args([
            "--target-language",
            "zh-Hans",
            "--from",
            "en",
            "--output-mode",
            "bilingual",
            "--service",
            "google",
            "--page",
            "2",
            "--page-range",
            "1-3",
            "--max-concurrency",
            "3",
            "--app-dir",
        ])
        .arg(&app_dir)
        .arg("--output")
        .arg(&output_path)
        .output()
        .expect("long document CLI should run");

    assert_failure(&output);
    let stderr = stderr(&output);
    let normalized = stderr.to_ascii_lowercase();
    assert!(
        normalized.contains("--page") && normalized.contains("--page-range"),
        "stderr should describe the page/page-range conflict:\n{stderr}"
    );
    assert!(
        !normalized.contains("unknown option"),
        "all long document options should be recognized before conflict validation:\n{stderr}"
    );
    assert!(
        stdout(&output).trim().is_empty(),
        "conflicting page options should not write output"
    );

    let _ = fs::remove_dir_all(work_dir);
}

#[test]
fn retry_failed_requires_result_json_path() {
    let work_dir = unique_temp_dir("easydict-long-doc-cli-retry-requires-sidecar");
    fs::create_dir_all(&work_dir).expect("work directory should be created");
    let input_path = work_dir.join("sample.txt");
    fs::write(&input_path, "Hello long document").expect("sample input should be written");

    let output = long_doc_cli()
        .arg("--input")
        .arg(&input_path)
        .args(["--target-language", "zh-Hans", "--retry-failed"])
        .output()
        .expect("long document CLI should run");

    assert_failure(&output);
    let stderr = stderr(&output);
    assert!(
        stderr.contains("--retry-failed requires --result-json"),
        "stderr should explain retry sidecar requirement:\n{stderr}"
    );
    assert!(
        stdout(&output).trim().is_empty(),
        "invalid retry arguments should not write stdout"
    );

    let _ = fs::remove_dir_all(work_dir);
}

#[test]
fn retry_failed_without_failed_chunks_reexports_from_result_json_without_provider_lookup() {
    let work_dir = unique_temp_dir("easydict-long-doc-cli-retry-no-failures");
    fs::create_dir_all(&work_dir).expect("work directory should be created");
    let input_path = work_dir.join("sample.txt");
    let output_path = work_dir.join("translated.txt");
    let result_json_path = work_dir.join("translated-result.json");
    fs::write(&input_path, "Hello retry").expect("sample input should be written");
    fs::write(&output_path, "stale output").expect("stale output should be written");
    fs::write(
        &result_json_path,
        format!(
            r#"{{
  "state": "Completed",
  "outputPath": "{}",
  "totalChunks": 1,
  "succeededChunks": 1,
  "resultJsonPath": "{}",
  "checkpoint": {{
    "inputMode": "PlainText",
    "outputMode": "Monolingual",
    "serviceId": "google",
    "from": "English",
    "to": "SimplifiedChinese",
    "text": {{
      "sourceChunks": ["Hello retry"],
      "chunkMetadata": [
        {{
          "chunkIndex": 0,
          "pageNumber": 1,
          "sourceBlockType": "Paragraph",
          "orderInPage": 0
        }}
      ],
      "translatedChunks": {{
        "0": "[zh] Hello retry"
      }},
      "failedChunkIndexes": []
    }}
  }}
}}"#,
            json_path(&output_path),
            json_path(&result_json_path)
        ),
    )
    .expect("result sidecar should be written");

    let output = long_doc_cli()
        .arg("--input")
        .arg(&input_path)
        .args(["--target-language", "zh-Hans", "--from", "en"])
        .arg("--output")
        .arg(&output_path)
        .arg("--result-json-path")
        .arg(&result_json_path)
        .arg("--retry-failed")
        .output()
        .expect("long document CLI should run");

    assert_success(&output);
    let stdout = stdout(&output);
    assert!(
        stdout.contains("State: Completed") && stdout.contains("Result JSON:"),
        "retry CLI output should report completed sidecar result:\n{stdout}"
    );
    assert_eq!(
        fs::read_to_string(&output_path).expect("retry output should be written"),
        "[zh] Hello retry"
    );
    let rewritten =
        fs::read_to_string(&result_json_path).expect("retry sidecar should be rewritten");
    assert!(
        rewritten.contains(r#""failedChunkIndexes": []"#)
            && rewritten.contains(r#""translatedChunks""#),
        "retry should rewrite checkpoint sidecar:\n{rewritten}"
    );
    let stderr = stderr(&output);
    assert!(
        !stderr.contains("Long Document worker") && !stderr.contains("CompatHost"),
        "retry CLI should stay on Rust-native sidecar route:\n{stderr}"
    );

    let _ = fs::remove_dir_all(work_dir);
}

#[test]
fn retry_failed_pdf_ocr_sidecar_reexports_text_without_pdf_or_worker_lookup() {
    let work_dir = unique_temp_dir("easydict-long-doc-cli-retry-pdf-ocr");
    fs::create_dir_all(&work_dir).expect("work directory should be created");
    let input_path = work_dir.join("scan.pdf");
    let output_pdf_path = work_dir.join("scan-translated.pdf");
    let mut output_text_path = output_pdf_path.clone();
    output_text_path.set_extension("txt");
    let bilingual_text_path = work_dir.join("scan-translated-bilingual.txt");
    let result_json_path = work_dir.join("scan-result.json");
    fs::write(&input_path, "%PDF-1.7\n% retry uses sidecar OCR chunks\n")
        .expect("sample input should be written");
    fs::write(
        &result_json_path,
        format!(
            r#"{{
  "state": "Completed",
  "outputPath": "{}",
  "bilingualOutputPath": "{}",
  "totalChunks": 1,
  "succeededChunks": 1,
  "resultJsonPath": "{}",
  "checkpoint": {{
    "routeMetadataVersion": 1,
    "inputPath": "{}",
    "outputPath": "{}",
    "pdfExportMode": "ContentStreamReplacement",
    "inputMode": "Pdf",
    "outputMode": "Both",
    "serviceId": "google",
    "from": "English",
    "to": "SimplifiedChinese",
    "text": {{
      "sourceChunks": ["Scanned OCR retry"],
      "chunkMetadata": [
        {{
          "chunkIndex": 0,
          "pageNumber": 1,
          "sourceBlockType": "Paragraph",
          "orderInPage": 0
        }}
      ],
      "translatedChunks": {{
        "0": "[zh] Scanned OCR retry"
      }},
      "failedChunkIndexes": []
    }},
    "pdf": {{
      "sourceChunks": ["Scanned OCR retry"],
      "chunkMetadata": [
        {{
          "chunkIndex": 0,
          "pageNumber": 1,
          "sourceBlockId": "pdf-p1-ocr-b1",
          "sourceBlockType": "Paragraph",
          "orderInPage": 0,
          "readingOrderScore": 1.0,
          "boundingBox": null,
          "textStyle": null,
          "translationSkipped": false,
          "preserveOriginalTextInPdfExport": false,
          "retryCount": 0,
          "fallbackText": null,
          "detectedFontNames": null
        }}
      ],
      "translatedChunks": {{
        "0": "[zh] Scanned OCR retry"
      }},
      "failedChunkIndexes": []
    }}
  }}
}}"#,
            json_path(&output_text_path),
            json_path(&bilingual_text_path),
            json_path(&result_json_path),
            json_path(&input_path),
            json_path(&output_pdf_path)
        ),
    )
    .expect("PDF OCR result sidecar should be written");

    let output = long_doc_cli()
        .arg("--input")
        .arg(&input_path)
        .args(["--target-language", "zh-Hans", "--from", "en"])
        .arg("--output")
        .arg(&output_pdf_path)
        .arg("--result-json")
        .arg(&result_json_path)
        .arg("--retry-failed")
        .output()
        .expect("long document CLI should run");

    assert_success(&output);
    assert!(
        !output_pdf_path.exists(),
        "retrying a PDF OCR checkpoint should reexport text instead of writing a PDF"
    );
    assert_eq!(
        fs::read_to_string(&output_text_path).expect("retry text output should be written"),
        "[zh] Scanned OCR retry"
    );
    let bilingual_text =
        fs::read_to_string(&bilingual_text_path).expect("retry bilingual output should be written");
    assert!(bilingual_text.contains("Scanned OCR retry"));
    assert!(bilingual_text.contains("[zh] Scanned OCR retry"));

    let rewritten =
        fs::read_to_string(&result_json_path).expect("retry sidecar should be rewritten");
    assert!(rewritten.contains(r#""pdf""#));
    assert!(rewritten.contains("pdf-p1-ocr-b1"));
    assert!(
        stdout(&output).contains("State: Completed"),
        "retry CLI output should report completed state:\n{}",
        stdout(&output)
    );
    let stderr = stderr(&output);
    for forbidden in ["Long Document worker", "CompatHost", ".NET workers"] {
        assert!(
            !stderr.contains(forbidden),
            "PDF OCR retry should not probe retained worker path {forbidden}:\n{stderr}"
        );
    }

    let _ = fs::remove_dir_all(work_dir);
}

#[test]
fn retry_failed_malformed_sidecar_fails_locally_without_worker_wording() {
    let work_dir = unique_temp_dir("easydict-long-doc-cli-retry-malformed-sidecar");
    let app_dir = work_dir.join("app");
    fs::create_dir_all(app_dir.join("workers").join("longdoc"))
        .expect("stale worker directory should be created");
    fs::create_dir_all(app_dir.join("dotnet").join("host").join("fxr"))
        .expect("stale dotnet runtime directory should be created");
    fs::write(app_dir.join("Easydict.CompatHost.exe"), "stale")
        .expect("stale compat host marker should be written");
    let input_path = work_dir.join("sample.txt");
    let result_json_path = work_dir.join("translated-result.json");
    fs::write(&input_path, "Hello retry").expect("sample input should be written");
    fs::write(&result_json_path, "{not-json").expect("malformed sidecar should be written");

    let output = long_doc_cli()
        .arg("--input")
        .arg(&input_path)
        .args(["--target-language", "zh-Hans", "--from", "en"])
        .arg("--result-json")
        .arg(&result_json_path)
        .arg("--retry-failed")
        .arg("--app-dir")
        .arg(&app_dir)
        .output()
        .expect("long document CLI should run");

    assert_failure(&output);
    let stderr = stderr(&output);
    assert!(
        stderr.contains("Could not parse long document result JSON"),
        "stderr should report malformed sidecar parsing locally:\n{stderr}"
    );
    for forbidden in ["Long Document worker", "CompatHost", ".NET workers"] {
        assert!(
            !stderr.contains(forbidden),
            "malformed retry sidecar should not probe retained worker path {forbidden}:\n{stderr}"
        );
    }
    assert!(
        stdout(&output).trim().is_empty(),
        "malformed retry sidecar should not write stdout"
    );

    let _ = fs::remove_dir_all(work_dir);
}

#[test]
fn result_json_path_is_prechecked_before_provider_lookup() {
    let work_dir = unique_temp_dir("easydict-long-doc-cli-result-json-precheck");
    fs::create_dir_all(&work_dir).expect("work directory should be created");
    let input_path = work_dir.join("sample.txt");
    let output_path = work_dir.join("translated.txt");
    let result_json_path = work_dir.join("translated-result.json");
    fs::write(&input_path, "Hello long document").expect("sample input should be written");
    fs::create_dir_all(&result_json_path).expect("conflicting sidecar directory should exist");

    let output = long_doc_cli()
        .arg("--input")
        .arg(&input_path)
        .args(["--target-language", "zh-Hans", "--from", "en"])
        .arg("--output")
        .arg(&output_path)
        .arg("--result-json")
        .arg(&result_json_path)
        .output()
        .expect("long document CLI should run");

    assert_failure(&output);
    let stderr = stderr(&output);
    assert!(
        stderr.contains("Long document output path") && stderr.contains("is a directory"),
        "stderr should report the result JSON path preflight failure:\n{stderr}"
    );
    assert!(
        stderr.contains(&result_json_path.display().to_string()),
        "stderr should name the conflicting result JSON path:\n{stderr}"
    );
    assert!(
        !output_path.exists(),
        "provider translation should not run or write output after sidecar preflight failure"
    );

    let _ = fs::remove_dir_all(work_dir);
}

#[test]
fn app_dir_is_legacy_noop_and_does_not_enable_retained_worker_lookup() {
    let work_dir = unique_temp_dir("easydict-long-doc-cli-appdir-no-worker");
    let app_dir = work_dir.join("app");
    let settings_dir = work_dir.join("settings");
    fs::create_dir_all(&app_dir).expect("app directory should be created");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");
    fs::write(
        settings_dir.join("settings.json"),
        r#"{"LocalAIProvider":"WindowsAI"}"#,
    )
    .expect("settings should be written");
    let input_path = work_dir.join("sample.txt");
    fs::write(&input_path, "Hello long document").expect("sample input should be written");

    let output = long_doc_cli()
        .arg("--input")
        .arg(&input_path)
        .args([
            "--target-language",
            "zh-Hans",
            "--from",
            "en",
            "--service",
            "windows-local-ai",
            "--app-dir",
        ])
        .arg(&app_dir)
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .env("EASYDICT_FOUNDRY_LOCAL_CLI", "__missing_foundry_cli__.cmd")
        .output()
        .expect("long document CLI should run");

    assert_failure(&output);
    let stderr = stderr(&output);
    let normalized = stderr.to_ascii_lowercase();
    assert!(
        normalized.contains("rust-native")
            || normalized.contains("phi silica")
            || normalized.contains("windows ai"),
        "stderr should stay on a Rust-native LocalAI failure path:\n{stderr}"
    );
    assert!(
        !stderr.contains(".NET workers"),
        "default CLI error should not mention retired retained runtime:\n{stderr}"
    );
    assert!(
        !stderr.contains("Long Document worker executable"),
        "--app-dir must not probe retained LongDoc worker paths:\n{stderr}"
    );
    assert!(
        !normalized.contains("compat host"),
        "LongDoc CLI should not describe a compat host route:\n{stderr}"
    );

    let _ = fs::remove_dir_all(work_dir);
}

#[test]
fn target_auto_fails_before_native_or_retained_worker_lookup() {
    let work_dir = unique_temp_dir("easydict-long-doc-cli-target-auto");
    let settings_dir = work_dir.join("settings");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");
    fs::write(
        settings_dir.join("settings.json"),
        r#"{"LocalAIProvider":"WindowsAI"}"#,
    )
    .expect("settings should be written");
    let input_path = work_dir.join("sample.txt");
    fs::write(&input_path, "Hello long document").expect("sample input should be written");

    let output = long_doc_cli()
        .arg("--input")
        .arg(&input_path)
        .args([
            "--target-language",
            "auto",
            "--from",
            "en",
            "--service",
            "windows-local-ai",
        ])
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .env("EASYDICT_FOUNDRY_LOCAL_CLI", "__missing_foundry_cli__.cmd")
        .output()
        .expect("long document CLI should run");

    assert_failure(&output);
    let stderr = stderr(&output);
    assert!(
        stderr.contains("Long Document target language cannot be Auto"),
        "stderr should reject target Auto before provider lookup:\n{stderr}"
    );
    assert!(
        !stderr.contains("Long Document worker"),
        "target Auto should not probe retained LongDoc workers:\n{stderr}"
    );
    assert!(
        !stderr.contains(".NET workers"),
        "target Auto should not expose retained .NET worker details:\n{stderr}"
    );
    assert!(
        !stderr.to_ascii_lowercase().contains("compat host"),
        "target Auto should not describe a compat host:\n{stderr}"
    );

    let _ = fs::remove_dir_all(work_dir);
}

#[test]
fn env_overrides_local_ai_provider_and_openvino_cache_dir_for_native_preflight() {
    let work_dir = unique_temp_dir("easydict-long-doc-cli-openvino-env");
    let settings_dir = work_dir.join("settings");
    let cache_dir = work_dir.join("cache");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");
    fs::create_dir_all(&cache_dir).expect("cache directory should be created");
    fs::write(
        settings_dir.join("settings.json"),
        r#"{"LocalAIProvider":"WindowsAI"}"#,
    )
    .expect("settings should be written");
    let input_path = work_dir.join("sample.txt");
    fs::write(&input_path, "Hello local OpenVINO long document")
        .expect("sample input should be written");

    let output = long_doc_cli()
        .arg("--input")
        .arg(&input_path)
        .args([
            "--target-language",
            "zh-Hans",
            "--from",
            "en",
            "--service",
            "windows-local-ai",
        ])
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .env("EASYDICT_LOCAL_AI_PROVIDER", "open_vino")
        .env("EASYDICT_OPENVINO_CACHE_DIR", &cache_dir)
        .env("EASYDICT_OPENVINO_DEVICE", "GPU")
        .output()
        .expect("long document CLI should run");

    assert_failure(&output);
    let stderr = stderr(&output);
    assert!(
        stderr.contains("OpenVINO runtime or NLLB-200 model is not downloaded"),
        "stderr should report the native OpenVINO download preflight:\n{stderr}"
    );
    assert!(
        stderr.contains("Download model"),
        "stderr should guide users to download the OpenVINO model:\n{stderr}"
    );
    assert!(
        !stderr.contains("requires a Rust-native route"),
        "OpenVINO env route should not fall back to generic retained worker wording:\n{stderr}"
    );
    assert!(
        !stderr.contains("Long Document worker"),
        "OpenVINO env route should not probe retained LongDoc workers:\n{stderr}"
    );
    assert!(
        !stderr.to_ascii_lowercase().contains("compat host"),
        "OpenVINO env route should not describe a compat host:\n{stderr}"
    );

    let _ = fs::remove_dir_all(work_dir);
}

#[test]
fn env_overrides_foundry_local_endpoint_and_model_before_worker_lookup() {
    let work_dir = unique_temp_dir("easydict-long-doc-cli-foundry-env");
    let settings_dir = work_dir.join("settings");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");
    fs::write(
        settings_dir.join("settings.json"),
        r#"{"LocalAIProvider":"WindowsAI"}"#,
    )
    .expect("settings should be written");
    let input_path = work_dir.join("sample.txt");
    fs::write(&input_path, "Hello Foundry Local long document")
        .expect("sample input should be written");

    let output = long_doc_cli()
        .arg("--input")
        .arg(&input_path)
        .args([
            "--target-language",
            "zh-Hans",
            "--from",
            "en",
            "--service",
            "windows-local-ai",
        ])
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .env("EASYDICT_LOCAL_AI_PROVIDER", "foundry-local")
        .env("EASYDICT_FOUNDRY_LOCAL_ENDPOINT", "foundry-local-invalid")
        .env("EASYDICT_FOUNDRY_LOCAL_MODEL", "cli-foundry-model")
        .output()
        .expect("long document CLI should run");

    assert_failure(&output);
    let stderr = stderr(&output);
    assert!(
        !stderr.contains("requires a Rust-native route"),
        "Foundry env route should enter native LocalAI handling:\n{stderr}"
    );
    assert!(
        !stderr.contains("Long Document worker"),
        "Foundry env route should not probe retained LongDoc workers:\n{stderr}"
    );
    assert!(
        !stderr.to_ascii_lowercase().contains("compat host"),
        "Foundry env route should not describe a compat host:\n{stderr}"
    );

    let _ = fs::remove_dir_all(work_dir);
}

#[test]
fn local_ai_provider_aliases_route_to_native_preflight_without_worker_lookup() {
    #[derive(Clone, Copy)]
    enum ExpectedRoute {
        WindowsAi,
        FoundryLocal,
        OpenVino,
    }

    let cases = [
        ("windows_ai", ExpectedRoute::WindowsAi),
        ("windows-ai", ExpectedRoute::WindowsAi),
        ("phi_silica", ExpectedRoute::WindowsAi),
        ("foundry_local", ExpectedRoute::FoundryLocal),
        ("local-ai", ExpectedRoute::FoundryLocal),
        ("open_vino", ExpectedRoute::OpenVino),
        ("open-vino", ExpectedRoute::OpenVino),
    ];

    for (alias, expected_route) in cases {
        let work_dir = unique_temp_dir(&format!(
            "easydict-long-doc-cli-local-ai-alias-{}",
            alias.replace(['-', '_'], "-")
        ));
        let settings_dir = work_dir.join("settings");
        let cache_dir = work_dir.join("openvino-cache");
        fs::create_dir_all(&settings_dir).expect("settings directory should be created");
        fs::create_dir_all(&cache_dir).expect("OpenVINO cache directory should be created");
        fs::write(
            settings_dir.join("settings.json"),
            r#"{"LocalAIProvider":"Auto"}"#,
        )
        .expect("settings should be written");
        let input_path = work_dir.join("sample.txt");
        fs::write(&input_path, "Hello LocalAI alias long document")
            .expect("sample input should be written");

        let output = long_doc_cli()
            .arg("--input")
            .arg(&input_path)
            .args([
                "--target-language",
                "zh-Hans",
                "--from",
                "en",
                "--service",
                "windows-local-ai",
            ])
            .env("EASYDICT_SETTINGS_DIR", &settings_dir)
            .env("EASYDICT_LOCAL_AI_PROVIDER", alias)
            .env("EASYDICT_WINDOWS_AI_DISABLE_WINRT", "1")
            .env("EASYDICT_FOUNDRY_LOCAL_ENDPOINT", "foundry-local-invalid")
            .env("EASYDICT_FOUNDRY_LOCAL_MODEL", "cli-foundry-model")
            .env("EASYDICT_OPENVINO_CACHE_DIR", &cache_dir)
            .output()
            .expect("long document CLI should run");

        assert_failure(&output);
        let stderr = stderr(&output);
        match expected_route {
            ExpectedRoute::WindowsAi => {
                let normalized = stderr.to_ascii_lowercase();
                assert!(
                    normalized.contains("windows ai")
                        || normalized.contains("phi silica")
                        || normalized.contains("winrt client is disabled"),
                    "alias {alias} should route to native WindowsAI/Phi handling:\n{stderr}"
                );
            }
            ExpectedRoute::FoundryLocal => {
                assert!(
                    stderr.contains("OpenAI HTTP request failed"),
                    "alias {alias} should route to native Foundry/OpenAI-compatible handling:\n{stderr}"
                );
            }
            ExpectedRoute::OpenVino => {
                assert!(
                    stderr.contains("OpenVINO runtime or NLLB-200 model is not downloaded"),
                    "alias {alias} should route to native OpenVINO preflight:\n{stderr}"
                );
            }
        }
        assert_no_retained_longdoc_worker_wording(&stderr, alias);

        let _ = fs::remove_dir_all(work_dir);
    }
}

#[test]
fn foundry_local_cli_override_targeting_retained_worker_is_not_spawned() {
    let work_dir = unique_temp_dir("easydict-long-doc-cli-bad-foundry-override");
    let settings_dir = work_dir.join("settings");
    let fake_foundry_dir = work_dir.join("fake-foundry");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");
    fs::create_dir_all(&fake_foundry_dir).expect("fake Foundry directory should be created");
    let marker_path = fake_foundry_dir.join("retained-worker-cli-was-spawned.txt");
    let fake_foundry_path = fake_foundry_dir.join("Easydict.Workers.LocalAi.exe.cmd");
    fs::write(
        &fake_foundry_path,
        format!(
            "@echo off\r\necho spawned >\"{}\"\r\necho Foundry Local endpoint: http://127.0.0.1:1/v1/chat/completions\r\n",
            marker_path.display()
        ),
    )
    .expect("fake retained-worker CLI should be written");
    let input_path = work_dir.join("sample.txt");
    fs::write(&input_path, "Hello Foundry Local long document")
        .expect("sample input should be written");

    let output = long_doc_cli()
        .arg("--input")
        .arg(&input_path)
        .args([
            "--target-language",
            "zh-Hans",
            "--from",
            "en",
            "--service",
            "foundry-local",
        ])
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .env("EASYDICT_LOCAL_AI_PROVIDER", "foundry-local")
        .env("EASYDICT_FOUNDRY_LOCAL_CLI", &fake_foundry_path)
        .output()
        .expect("long document CLI should run");

    assert_failure(&output);
    let stderr = stderr(&output);
    assert!(
        !marker_path.exists(),
        "Foundry CLI override that points at a retained worker name must not be spawned:\n{stderr}"
    );
    assert!(
        !stderr.contains("Long Document worker"),
        "bad Foundry CLI override should not probe retained LongDoc workers:\n{stderr}"
    );
    assert!(
        !stderr.to_ascii_lowercase().contains("compat host"),
        "bad Foundry CLI override should not describe a compat host:\n{stderr}"
    );

    let _ = fs::remove_dir_all(work_dir);
}

fn long_doc_cli() -> Command {
    let binary = option_env!("CARGO_BIN_EXE_easydict_long_doc")
        .expect("easydict_long_doc binary should be built for integration tests");
    Command::new(binary)
}

fn assert_success(output: &Output) {
    assert!(
        output.status.success(),
        "command should succeed\nstdout:\n{}\nstderr:\n{}",
        stdout(output),
        stderr(output)
    );
}

fn assert_failure(output: &Output) {
    assert!(
        !output.status.success(),
        "command should fail\nstdout:\n{}\nstderr:\n{}",
        stdout(output),
        stderr(output)
    );
}

fn assert_no_retained_longdoc_worker_wording(stderr: &str, context: &str) {
    for forbidden in ["Long Document worker", "CompatHost", ".NET workers"] {
        assert!(
            !stderr.contains(forbidden),
            "{context} should not probe retained worker path {forbidden}:\n{stderr}"
        );
    }
    assert!(
        !stderr.to_ascii_lowercase().contains("compat host"),
        "{context} should not describe a compat host route:\n{stderr}"
    );
}

fn stdout(output: &Output) -> String {
    String::from_utf8_lossy(&output.stdout).into_owned()
}

fn stderr(output: &Output) -> String {
    String::from_utf8_lossy(&output.stderr).into_owned()
}

fn json_path(path: &std::path::Path) -> String {
    path.display().to_string().replace('\\', "\\\\")
}

fn unique_temp_dir(prefix: &str) -> PathBuf {
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .expect("system clock should be after unix epoch")
        .as_nanos();
    std::env::temp_dir().join(format!("{prefix}-{}-{nanos}", std::process::id()))
}
