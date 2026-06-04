use std::collections::{HashMap, VecDeque};
use std::fs;
use std::io::{Read, Write};
use std::net::TcpListener;
use std::path::{Path, PathBuf};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use easydict_app::{
    cached_font_path_for_directory, delete_all_fonts_for_directory, download_with_retry_and_policy,
    ensure_font_for_directory, font_asset_for_language, font_cache_dir,
    has_any_cjk_font_for_directory, is_file_valid, ordered_urls_by_probe, requires_cjk_font,
    total_font_size_bytes_for_directory, FontDownloadError, ReqwestResourceDownloadClient,
    ResourceDownloadClient, ResourceDownloadError, ResourceDownloadProgress,
    ResourceDownloadRetryPolicy, ResourceProbeResult, TranslationLanguage,
};

#[derive(Default)]
struct FakeResourceDownloadClient {
    probes: HashMap<String, ResourceProbeResult>,
    downloads: HashMap<String, VecDeque<Result<Vec<u8>, &'static str>>>,
    requested_urls: Vec<String>,
    requested_stages: Vec<String>,
}

impl FakeResourceDownloadClient {
    fn with_probe(mut self, url: &str, ok: bool, elapsed_ms: u128) -> Self {
        self.probes
            .insert(url.to_string(), ResourceProbeResult { ok, elapsed_ms });
        self
    }

    fn with_download(mut self, url: &str, body: impl Into<Vec<u8>>) -> Self {
        self.downloads
            .entry(url.to_string())
            .or_default()
            .push_back(Ok(body.into()));
        self
    }

    fn with_failure(mut self, url: &str, message: &'static str) -> Self {
        self.downloads
            .entry(url.to_string())
            .or_default()
            .push_back(Err(message));
        self
    }
}

impl ResourceDownloadClient for FakeResourceDownloadClient {
    fn probe_url(
        &mut self,
        url: &str,
        _timeout: Duration,
    ) -> Result<ResourceProbeResult, ResourceDownloadError> {
        Ok(*self.probes.get(url).unwrap_or(&ResourceProbeResult {
            ok: false,
            elapsed_ms: u128::MAX,
        }))
    }

    fn download_to(
        &mut self,
        url: &str,
        output_path: &Path,
        stage: &str,
        progress: &mut dyn FnMut(ResourceDownloadProgress),
    ) -> Result<(), ResourceDownloadError> {
        self.requested_urls.push(url.to_string());
        self.requested_stages.push(stage.to_string());
        let result = self
            .downloads
            .get_mut(url)
            .and_then(VecDeque::pop_front)
            .unwrap_or(Err("missing fake response"));
        let body = result.map_err(|message| ResourceDownloadError::Network(message.to_string()))?;

        if let Some(parent) = output_path.parent() {
            fs::create_dir_all(parent).expect("create fake download parent");
        }
        let mut file = fs::File::create(output_path).expect("create fake download file");
        if body.is_empty() {
            return Ok(());
        }
        let total = body.len() as i64;
        let split = body.len().saturating_div(2).max(1).min(body.len());
        let mut written = 0_u64;
        for chunk in body.chunks(split) {
            file.write_all(chunk).expect("write fake download chunk");
            written += chunk.len() as u64;
            progress(ResourceDownloadProgress {
                stage: stage.to_string(),
                bytes_downloaded: written,
                total_bytes: total,
                percentage: (written as f64 / total as f64) * 100.0,
            });
        }
        Ok(())
    }
}

fn temp_dir(name: &str) -> PathBuf {
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .expect("system time after unix epoch")
        .as_nanos();
    std::env::temp_dir().join(format!(
        "easydict-resource-download-{name}-{}-{nanos}",
        std::process::id()
    ))
}

#[test]
fn resource_download_orders_urls_by_probe_success_then_latency() {
    let mut client = FakeResourceDownloadClient::default()
        .with_probe("https://slow.example/file", true, 80)
        .with_probe("https://fast.example/file", true, 10)
        .with_probe("https://fail.example/file", false, 1);
    let urls = vec![
        "https://fail.example/file".to_string(),
        "https://slow.example/file".to_string(),
        "https://fast.example/file".to_string(),
    ];

    let ordered = ordered_urls_by_probe(&mut client, &urls);

    assert_eq!(
        ordered,
        vec![
            "https://fast.example/file",
            "https://slow.example/file",
            "https://fail.example/file"
        ]
    );
}

#[test]
fn resource_download_moves_temp_file_and_reports_progress() {
    let dir = temp_dir("move-progress");
    let output = dir.join("asset.bin");
    let mut client =
        FakeResourceDownloadClient::default().with_download("https://one.example/asset", b"abcdef");
    let urls = vec!["https://one.example/asset".to_string()];
    let mut progress_events = Vec::new();

    download_with_retry_and_policy(
        &mut client,
        &urls,
        &output,
        "asset",
        &mut |progress| progress_events.push(progress),
        &ResourceDownloadRetryPolicy {
            max_retries: 0,
            retry_delays: vec![],
        },
    )
    .expect("download succeeds");

    assert_eq!(fs::read(&output).expect("read moved file"), b"abcdef");
    assert!(!PathBuf::from(format!("{}.tmp", output.display())).exists());
    assert_eq!(
        progress_events.last().map(|progress| (
            progress.stage.as_str(),
            progress.bytes_downloaded,
            progress.total_bytes,
            progress.percentage.round() as i32
        )),
        Some(("asset", 6, 6, 100))
    );

    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn resource_download_falls_back_to_next_source_after_failure() {
    let dir = temp_dir("fallback");
    let output = dir.join("asset.bin");
    let mut client = FakeResourceDownloadClient::default()
        .with_failure("https://bad.example/asset", "first mirror failed")
        .with_download("https://good.example/asset", b"ok");
    let urls = vec![
        "https://bad.example/asset".to_string(),
        "https://good.example/asset".to_string(),
    ];

    download_with_retry_and_policy(
        &mut client,
        &urls,
        &output,
        "asset",
        &mut |_| {},
        &ResourceDownloadRetryPolicy {
            max_retries: 0,
            retry_delays: vec![],
        },
    )
    .expect("fallback succeeds");

    assert_eq!(
        client.requested_urls,
        vec!["https://bad.example/asset", "https://good.example/asset"]
    );
    assert_eq!(fs::read(&output).expect("read fallback output"), b"ok");

    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn resource_download_rejects_truncated_content_length_without_publishing_output() {
    let listener = TcpListener::bind("127.0.0.1:0").expect("bind local test server");
    let url = format!("http://{}/asset.bin", listener.local_addr().unwrap());
    let server = std::thread::spawn(move || {
        let (mut stream, _) = listener.accept().expect("accept test request");
        let mut request = [0_u8; 1024];
        let _ = stream.read(&mut request);
        stream
            .write_all(b"HTTP/1.1 200 OK\r\nContent-Length: 6\r\n\r\nabc")
            .expect("write truncated test response");
    });

    let dir = temp_dir("truncated");
    let output = dir.join("asset.bin");
    let mut client = ReqwestResourceDownloadClient::new().expect("create reqwest client");

    let error = download_with_retry_and_policy(
        &mut client,
        &[url],
        &output,
        "asset",
        &mut |_| {},
        &ResourceDownloadRetryPolicy {
            max_retries: 0,
            retry_delays: vec![],
        },
    )
    .expect_err("truncated response should fail");
    server.join().expect("test server should finish");

    assert!(
        matches!(error, ResourceDownloadError::Truncated { .. })
            || matches!(error, ResourceDownloadError::AllSourcesFailed { .. })
            || matches!(error, ResourceDownloadError::Network(_))
    );
    assert!(!output.exists());
    assert!(!PathBuf::from(format!("{}.tmp", output.display())).exists());

    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn resource_download_file_validation_matches_dotnet_min_size_contract() {
    let dir = temp_dir("file-valid");
    fs::create_dir_all(&dir).expect("create temp dir");
    let file = dir.join("model.onnx");
    fs::write(&file, [0_u8; 4]).expect("write small file");

    assert!(!is_file_valid(dir.join("missing.onnx"), 1));
    assert!(!is_file_valid(&file, 5));
    assert!(is_file_valid(&file, 4));

    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn font_download_metadata_matches_legacy_cjk_contract() {
    assert!(requires_cjk_font(TranslationLanguage::SimplifiedChinese));
    assert!(requires_cjk_font(TranslationLanguage::TraditionalChinese));
    assert!(requires_cjk_font(TranslationLanguage::Japanese));
    assert!(requires_cjk_font(TranslationLanguage::Korean));
    assert!(!requires_cjk_font(TranslationLanguage::English));
    assert!(!requires_cjk_font(TranslationLanguage::Auto));

    let simplified = font_asset_for_language(TranslationLanguage::SimplifiedChinese)
        .expect("simplified CJK asset");
    assert_eq!(simplified.key, "zh-Hans");
    assert_eq!(simplified.file_name, "NotoSansSC-Regular.ttf");
    assert_eq!(simplified.download_urls.len(), 2);
    assert!(simplified.download_urls[0].contains("notofonts/noto-cjk"));
}

#[test]
fn font_download_cache_uses_exact_language_then_any_cjk_fallback() {
    let dir = temp_dir("font-cache");
    let fonts_dir = font_cache_dir(&dir);
    fs::create_dir_all(&fonts_dir).expect("create fonts dir");
    let japanese_path = fonts_dir.join("NotoSansJP-Regular.ttf");
    fs::write(&japanese_path, b"jp").expect("write japanese font");

    assert_eq!(
        cached_font_path_for_directory(&dir, TranslationLanguage::Japanese),
        Some(japanese_path.clone())
    );
    assert_eq!(
        cached_font_path_for_directory(&dir, TranslationLanguage::SimplifiedChinese),
        Some(japanese_path)
    );
    assert!(has_any_cjk_font_for_directory(&dir));

    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn font_download_ensure_downloads_ordered_asset_and_reports_stage() {
    let dir = temp_dir("font-ensure");
    let asset = font_asset_for_language(TranslationLanguage::SimplifiedChinese)
        .expect("simplified CJK asset");
    let first_url = asset.download_urls[0];
    let second_url = asset.download_urls[1];
    let mut client = FakeResourceDownloadClient::default()
        .with_probe(first_url, true, 50)
        .with_probe(second_url, true, 5)
        .with_download(second_url, b"font-bytes");
    let mut stages = Vec::new();

    let path = ensure_font_for_directory(
        &mut client,
        &dir,
        TranslationLanguage::SimplifiedChinese,
        &mut |progress| stages.push(progress.stage),
    )
    .expect("font download succeeds");

    assert_eq!(path, font_cache_dir(&dir).join("NotoSansSC-Regular.ttf"));
    assert_eq!(
        fs::read(&path).expect("read downloaded font"),
        b"font-bytes"
    );
    assert_eq!(client.requested_urls, vec![second_url.to_string()]);
    assert!(stages.iter().all(|stage| stage == "font-zh-Hans"));

    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn font_download_ensure_rejects_non_cjk_language() {
    let dir = temp_dir("font-unsupported");
    let mut client = FakeResourceDownloadClient::default();

    let error =
        ensure_font_for_directory(&mut client, &dir, TranslationLanguage::English, &mut |_| {})
            .expect_err("English has no CJK font asset");

    assert!(matches!(
        error,
        FontDownloadError::UnsupportedLanguage(TranslationLanguage::English)
    ));
    assert!(client.requested_urls.is_empty());

    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn font_download_total_size_and_delete_cover_all_known_assets() {
    let dir = temp_dir("font-delete");
    let fonts_dir = font_cache_dir(&dir);
    fs::create_dir_all(&fonts_dir).expect("create fonts dir");
    fs::write(fonts_dir.join("NotoSansSC-Regular.ttf"), [0_u8; 2]).expect("write sc");
    fs::write(fonts_dir.join("NotoSansKR-Regular.ttf"), [0_u8; 3]).expect("write kr");
    fs::write(fonts_dir.join("unmanaged.ttf"), [0_u8; 100]).expect("write unmanaged");

    assert_eq!(total_font_size_bytes_for_directory(&dir), 5);
    delete_all_fonts_for_directory(&dir);

    assert!(!has_any_cjk_font_for_directory(&dir));
    assert!(fonts_dir.join("unmanaged.ttf").is_file());

    let _ = fs::remove_dir_all(&dir);
}
