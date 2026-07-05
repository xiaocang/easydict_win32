use base64::Engine;
use clap::{error::ErrorKind, Parser, Subcommand};
use image::codecs::jpeg::JpegEncoder;
use image::imageops::{self, FilterType};
use image::{ColorType, DynamicImage, Rgba, RgbaImage};
use regex::Regex;
use serde::Serialize;
use serde_json::Value;
use std::collections::{BTreeMap, BTreeSet};
use std::ffi::OsString;
use std::fs::{self, File};
use std::io::Write;
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};
use walkdir::WalkDir;

const DOTNET_SUFFIX: &str = "-dotnet-winui-reference.png";
const RUST_SUFFIX: &str = "-rust-win-fluent-iced.png";
const PIXEL_DELTA_TOLERANCE: i16 = 12;

pub fn run_cli(args: impl IntoIterator<Item = OsString>) -> i32 {
    match run(args) {
        Ok(code) => code,
        Err(error) => {
            eprintln!("UI parity analysis failed:");
            eprintln!("{error}");
            1
        }
    }
}

fn format_dips(value: f64) -> String {
    format!("{:.2}", round2(value))
}

fn run(args: impl IntoIterator<Item = OsString>) -> Result<i32, String> {
    let options = match RawCliOptions::try_parse_from(args) {
        Ok(options) => options,
        Err(error) => {
            let code = match error.kind() {
                ErrorKind::DisplayHelp | ErrorKind::DisplayVersion => 0,
                _ => 1,
            };
            if code == 0 {
                print!("{error}");
            } else {
                eprint!("{error}");
            }
            return Ok(code);
        }
    };
    if let Some(command) = options.command {
        match command {
            AnalyzerCommand::ScreenshotSummary(command) => {
                publish_screenshot_summary(command.resolve()?)?;
                return Ok(0);
            }
            AnalyzerCommand::Triage(command) => {
                publish_triage_report(command.resolve()?)?;
                return Ok(0);
            }
            AnalyzerCommand::CodeParity(command) => {
                return publish_code_parity_report(command.resolve()?);
            }
        }
    }

    let options = options.resolve()?;

    if options.self_test {
        return Ok(if run_self_test()? { 0 } else { 1 });
    }

    if options.require_manifest && options.manifest_path.is_none() {
        return Err(
            "UI parity manifest is required but no ui-parity-manifest.json was found. \
             Pass --manifest <path> or generate DotnetRustParityTests artifacts first."
                .to_string(),
        );
    }

    let pairs = load_pairs(&options)?;
    fs::create_dir_all(&options.output_dir).map_err(|error| error.to_string())?;

    let mut scenarios = Vec::with_capacity(pairs.len());
    for pair in &pairs {
        scenarios.push(analyze_pair(pair, &options)?);
    }
    annotate_interaction_effect_metrics(&mut scenarios, &pairs)?;

    let report = ParityReport::create(&options, scenarios);
    let coverage = ParityCoverageReport::create(&report);
    write_reports(&report, &coverage, &options)?;

    println!(
        "UI parity analysis completed: {} scenario(s), {} pass, {} warn, {} fail.",
        report.summary.total_scenarios,
        report.summary.pass_count,
        report.summary.warn_count,
        report.summary.fail_count
    );
    println!(
        "Coverage: {}/{} ({:.2}%), critical {}/{} ({:.2}%).",
        coverage.summary.covered,
        coverage.summary.total,
        coverage.summary.coverage_percent,
        coverage.summary.critical_covered,
        coverage.summary.critical_total,
        coverage.summary.critical_coverage_percent
    );
    println!(
        "Report: {}",
        options.output_dir.join("ui-parity-report.md").display()
    );

    if options.fail_on_threshold && report.summary.fail_count > 0 {
        return Ok(2);
    }

    if let Some(message) = coverage_gate_failure(&coverage, &options) {
        eprintln!("{message}");
        return Ok(3);
    }

    Ok(0)
}

#[derive(Parser, Debug)]
#[command(
    name = "UiParityAnalyzer",
    about = "Scores Rust/Iced UI screenshots against .NET WinUI references.",
    disable_version_flag = true
)]
struct RawCliOptions {
    #[command(subcommand)]
    command: Option<AnalyzerCommand>,
    /// Directory containing parity screenshots. Defaults to current directory.
    #[arg(long, default_value = ".")]
    screenshot_root: PathBuf,
    /// Output directory. Defaults to <screenshot-root>/ui-parity.
    #[arg(long)]
    output_dir: Option<PathBuf>,
    /// Optional ui-parity-manifest.json path.
    #[arg(long)]
    manifest: Option<PathBuf>,
    /// Only analyze screenshot pairs listed in the manifest; do not discover extra pairs under screenshot-root.
    #[arg(long)]
    manifest_only: bool,
    /// Pass threshold. Defaults to 85.
    #[arg(long, default_value_t = 85.0)]
    pass_score: f64,
    /// Warn threshold. Defaults to 70.
    #[arg(long, default_value_t = 70.0)]
    warn_score: f64,
    /// Per-layer/case gate override, repeatable. Format: layer/case=pass,warn.
    #[arg(long = "score-gate")]
    score_gate: Vec<String>,
    /// Fail when total visual matrix coverage is below this percentage.
    #[arg(long)]
    min_coverage: Option<f64>,
    /// Fail when critical visual matrix coverage is below this percentage.
    #[arg(long)]
    min_critical_coverage: Option<f64>,
    /// Fail when any critical expected visual evidence item is missing.
    #[arg(long)]
    fail_on_critical_coverage_missing: bool,
    /// Return exit code 2 when any scenario fails.
    #[arg(long)]
    fail_on_threshold: bool,
    /// Fail when ui-parity-manifest.json is missing instead of falling back to filename pairs.
    #[arg(long)]
    require_manifest: bool,
    /// Run synthetic analyzer self-test.
    #[arg(long)]
    self_test: bool,
}

#[derive(Subcommand, Debug)]
enum AnalyzerCommand {
    #[command(name = "screenshot-summary")]
    ScreenshotSummary(RawScreenshotSummaryOptions),
    #[command(name = "triage")]
    Triage(RawTriageOptions),
    #[command(name = "code-parity")]
    CodeParity(RawCodeParityOptions),
}

#[derive(Parser, Debug)]
struct RawScreenshotSummaryOptions {
    #[arg(long)]
    screenshot_root: PathBuf,
    #[arg(long)]
    artifact_name: String,
    #[arg(long, default_value = "UI automation screenshots")]
    title: String,
    #[arg(long)]
    summary_path: Option<PathBuf>,
    #[arg(long, default_value_t = 600_000)]
    max_inline_bytes: u64,
    #[arg(long, default_value_t = 120)]
    max_listed_screenshots: usize,
}

#[derive(Parser, Debug)]
struct RawTriageOptions {
    /// Root that contains ui-screenshot artifact directories.
    #[arg(long)]
    artifact_root: PathBuf,
    /// Output directory. Defaults to <artifact-root>/ui-parity-triage.
    #[arg(long)]
    output_dir: Option<PathBuf>,
    /// Maximum number of newest ui-parity reports to scan.
    #[arg(long, default_value_t = 80)]
    max_reports: usize,
    /// Number of rows to include in each markdown queue/hotspot table.
    #[arg(long, default_value_t = 20)]
    top: usize,
}

#[derive(Parser, Debug)]
struct RawCodeParityOptions {
    /// Repository root. Defaults to the current directory.
    #[arg(long)]
    repo_root: Option<PathBuf>,
    /// Output directory. Defaults to artifacts/ui-code-parity/code-parity-<timestamp>.
    #[arg(long)]
    output_dir: Option<PathBuf>,
    /// .NET XAML files to inspect. Defaults to the main WinUI view set.
    #[arg(long = "dotnet-xaml")]
    dotnet_xaml: Vec<PathBuf>,
    /// .NET ResourceDictionary files to inspect. Defaults to Easydict theme dictionaries.
    #[arg(long = "dotnet-resource")]
    dotnet_resources: Vec<PathBuf>,
    /// Rust UI source file to inspect.
    #[arg(long, default_value = "rs/crates/easydict_app/src/ui.rs")]
    rust_ui: PathBuf,
    /// Rust theme source file to inspect.
    #[arg(long, default_value = "rs/crates/easydict_app/src/theme.rs")]
    rust_theme: PathBuf,
    /// Context lines around each Rust automation id when mining fluent calls.
    #[arg(long, default_value_t = 18)]
    context_lines: usize,
    /// Number of comparison rows to include in markdown tables.
    #[arg(long, default_value_t = 80)]
    top: usize,
    /// Return exit code 2 when static drift or missing facts are found.
    #[arg(long)]
    fail_on_drift: bool,
}

#[derive(Debug, Clone)]
struct CliOptions {
    screenshot_root: PathBuf,
    output_dir: PathBuf,
    manifest_path: Option<PathBuf>,
    pass_score: f64,
    warn_score: f64,
    score_gate_rules: Vec<ScoreGateRule>,
    min_coverage_percent: Option<f64>,
    min_critical_coverage_percent: Option<f64>,
    fail_on_critical_coverage_missing: bool,
    fail_on_threshold: bool,
    require_manifest: bool,
    manifest_only: bool,
    self_test: bool,
}

#[derive(Debug, Clone)]
struct ScreenshotSummaryOptions {
    screenshot_root: PathBuf,
    artifact_name: String,
    title: String,
    summary_path: Option<PathBuf>,
    max_inline_bytes: u64,
    max_listed_screenshots: usize,
}

#[derive(Debug, Clone)]
struct TriageOptions {
    artifact_root: PathBuf,
    output_dir: PathBuf,
    max_reports: usize,
    top: usize,
}

#[derive(Debug, Clone)]
struct CodeParityOptions {
    repo_root: PathBuf,
    output_dir: PathBuf,
    dotnet_xaml: Vec<PathBuf>,
    dotnet_resources: Vec<PathBuf>,
    rust_ui: PathBuf,
    rust_theme: PathBuf,
    context_lines: usize,
    top: usize,
    fail_on_drift: bool,
}

impl RawTriageOptions {
    fn resolve(self) -> Result<TriageOptions, String> {
        if self.max_reports == 0 {
            return Err("--max-reports must be greater than zero.".to_string());
        }
        if self.top == 0 {
            return Err("--top must be greater than zero.".to_string());
        }
        let artifact_root = self.artifact_root.canonicalize().map_err(|_| {
            format!(
                "Artifact root does not exist: {}",
                self.artifact_root.display()
            )
        })?;
        let output_dir = self
            .output_dir
            .unwrap_or_else(|| artifact_root.join("ui-parity-triage"));
        Ok(TriageOptions {
            artifact_root,
            output_dir,
            max_reports: self.max_reports,
            top: self.top,
        })
    }
}

impl RawCodeParityOptions {
    fn resolve(self) -> Result<CodeParityOptions, String> {
        if self.context_lines == 0 {
            return Err("--context-lines must be greater than zero.".to_string());
        }
        if self.top == 0 {
            return Err("--top must be greater than zero.".to_string());
        }

        let repo_root = self
            .repo_root
            .unwrap_or(std::env::current_dir().map_err(|error| error.to_string())?)
            .canonicalize()
            .map_err(|error| format!("Repository root does not exist: {error}"))?;
        let output_dir = self.output_dir.unwrap_or_else(|| {
            repo_root
                .join("artifacts")
                .join("ui-code-parity")
                .join(format!("code-parity-{}", timestamp_millis()))
        });
        let dotnet_xaml = if self.dotnet_xaml.is_empty() {
            default_dotnet_xaml_paths()
        } else {
            self.dotnet_xaml
        };
        let dotnet_resources = if self.dotnet_resources.is_empty() {
            default_dotnet_resource_paths()
        } else {
            self.dotnet_resources
        };

        Ok(CodeParityOptions {
            repo_root: repo_root.clone(),
            output_dir: absolutize_against(&repo_root, output_dir),
            dotnet_xaml: dotnet_xaml
                .into_iter()
                .map(|path| absolutize_against(&repo_root, path))
                .collect(),
            dotnet_resources: dotnet_resources
                .into_iter()
                .map(|path| absolutize_against(&repo_root, path))
                .collect(),
            rust_ui: absolutize_against(&repo_root, self.rust_ui),
            rust_theme: absolutize_against(&repo_root, self.rust_theme),
            context_lines: self.context_lines,
            top: self.top,
            fail_on_drift: self.fail_on_drift,
        })
    }
}

impl RawCliOptions {
    fn resolve(self) -> Result<CliOptions, String> {
        if !self.self_test {
            validate_percentage(self.min_coverage, "--min-coverage")?;
            validate_percentage(self.min_critical_coverage, "--min-critical-coverage")?;
        }

        if self.pass_score < self.warn_score {
            return Err(format!(
                "--pass-score must be greater than or equal to --warn-score, got {}, {}.",
                self.pass_score, self.warn_score
            ));
        }

        let screenshot_root = self
            .screenshot_root
            .canonicalize()
            .or_else(|_| {
                if self.self_test {
                    Ok(self.screenshot_root.clone())
                } else {
                    Err(std::io::Error::new(
                        std::io::ErrorKind::NotFound,
                        "screenshot root missing",
                    ))
                }
            })
            .map_err(|_| {
                format!(
                    "Screenshot root does not exist: {}",
                    self.screenshot_root.display()
                )
            })?;
        let output_dir = self
            .output_dir
            .unwrap_or_else(|| screenshot_root.join("ui-parity"));
        let manifest_path = match self.manifest {
            Some(path) => Some(path.canonicalize().unwrap_or(path)),
            None => {
                let candidate = screenshot_root.join("ui-parity-manifest.json");
                candidate.exists().then_some(candidate)
            }
        };

        let mut score_gate_rules = Vec::with_capacity(self.score_gate.len());
        for gate in &self.score_gate {
            score_gate_rules.push(parse_score_gate_rule(gate)?);
        }

        Ok(CliOptions {
            screenshot_root,
            output_dir: absolutize(output_dir),
            manifest_path,
            pass_score: self.pass_score,
            warn_score: self.warn_score,
            score_gate_rules,
            min_coverage_percent: self.min_coverage,
            min_critical_coverage_percent: self.min_critical_coverage,
            fail_on_critical_coverage_missing: self.fail_on_critical_coverage_missing,
            fail_on_threshold: self.fail_on_threshold,
            require_manifest: self.require_manifest,
            manifest_only: self.manifest_only,
            self_test: self.self_test,
        })
    }
}

impl RawScreenshotSummaryOptions {
    fn resolve(self) -> Result<ScreenshotSummaryOptions, String> {
        let summary_path = self.summary_path.or_else(|| {
            std::env::var_os("GITHUB_STEP_SUMMARY")
                .filter(|value| !value.is_empty())
                .map(PathBuf::from)
        });

        Ok(ScreenshotSummaryOptions {
            screenshot_root: absolutize(self.screenshot_root),
            artifact_name: self.artifact_name,
            title: self.title,
            summary_path,
            max_inline_bytes: self.max_inline_bytes,
            max_listed_screenshots: self.max_listed_screenshots,
        })
    }
}

#[derive(Debug, Clone)]
struct ScreenshotSummaryEntry {
    file: PathBuf,
    relative_path: String,
    dimensions: ScreenshotDimensions,
    category: ScreenshotReviewCategory,
}

#[derive(Debug, Clone)]
struct ScreenshotDimensions {
    width: Option<u32>,
    height: Option<u32>,
    display: String,
}

#[derive(Debug, Clone)]
struct ScreenshotReviewCategory {
    name: &'static str,
    rank: u8,
}

fn publish_screenshot_summary(options: ScreenshotSummaryOptions) -> Result<(), String> {
    let mut summary = Vec::new();
    push_summary_line(&mut summary, format!("## {}", options.title));
    push_summary_line(&mut summary, "");
    push_summary_line(
        &mut summary,
        format!("Artifact: `{}`", options.artifact_name),
    );
    push_summary_line(&mut summary, "Path inside artifact: `.`");

    if !options.screenshot_root.exists() {
        push_summary_line(&mut summary, "");
        push_summary_line(&mut summary, "No screenshot directory was produced.");
        write_summary(&summary, options.summary_path.as_deref())?;
        return Ok(());
    }

    let root = options
        .screenshot_root
        .canonicalize()
        .map_err(|error| format!("Could not resolve screenshot root: {error}"))?;
    let mut entries = collect_screenshot_summary_entries(&root)?;
    entries.sort_by(|a, b| {
        a.category
            .rank
            .cmp(&b.category.rank)
            .then_with(|| a.relative_path.cmp(&b.relative_path))
    });

    push_summary_line(&mut summary, "");
    push_summary_line(
        &mut summary,
        format!("Generated **{}** screenshot(s).", entries.len()),
    );

    if !entries.is_empty() {
        let gallery_path = root.join("ui-screenshot-gallery.jpg");
        save_screenshot_gallery(&entries, &gallery_path, 200, 135, 50, 76)?;
        if fs::metadata(&gallery_path)
            .map_err(|error| format!("Could not stat gallery {}: {error}", gallery_path.display()))?
            .len()
            > options.max_inline_bytes
        {
            save_screenshot_gallery(&entries, &gallery_path, 145, 100, 42, 65)?;
        }

        push_summary_line(&mut summary, "");
        push_summary_line(&mut summary, "### Gallery");
        let gallery_bytes = fs::read(&gallery_path).map_err(|error| {
            format!("Could not read gallery {}: {error}", gallery_path.display())
        })?;
        if gallery_bytes.len() as u64 <= options.max_inline_bytes {
            let encoded = base64::engine::general_purpose::STANDARD.encode(gallery_bytes);
            push_summary_line(
                &mut summary,
                format!(
                    "<img alt=\"UI automation screenshot gallery\" src=\"data:image/jpeg;base64,{encoded}\" />"
                ),
            );
        } else {
            push_summary_line(
                &mut summary,
                format!(
                    "Gallery image was generated at `{}` in the artifact.",
                    relative_path(&root, &gallery_path)
                ),
            );
        }

        let priority_entries = entries
            .iter()
            .filter(|entry| entry.category.rank < 4)
            .collect::<Vec<_>>();
        if !priority_entries.is_empty() {
            push_summary_line(&mut summary, "");
            push_summary_line(&mut summary, "### Review priority");
            for entry in priority_entries {
                push_summary_line(
                    &mut summary,
                    format!(
                        "- **{}**: `{}` ({})",
                        entry.category.name, entry.relative_path, entry.dimensions.display
                    ),
                );
            }
        }

        push_summary_line(&mut summary, "");
        push_summary_line(&mut summary, "### Screenshot files");
        for entry in entries.iter().take(options.max_listed_screenshots) {
            push_summary_line(
                &mut summary,
                format!("- `{}` ({})", entry.relative_path, entry.dimensions.display),
            );
        }

        if entries.len() > options.max_listed_screenshots {
            push_summary_line(
                &mut summary,
                format!(
                    "- ... {} more screenshot(s)",
                    entries.len() - options.max_listed_screenshots
                ),
            );
        }
    }

    write_summary(&summary, options.summary_path.as_deref())
}

fn push_summary_line(summary: &mut Vec<String>, line: impl Into<String>) {
    summary.push(line.into());
}

fn collect_screenshot_summary_entries(root: &Path) -> Result<Vec<ScreenshotSummaryEntry>, String> {
    let mut entries = Vec::new();
    for entry in WalkDir::new(root).into_iter().filter_map(Result::ok) {
        let path = entry.path();
        if !path.is_file() || !is_png_file(path) || file_name_contains(path, "gallery") {
            continue;
        }

        let relative = relative_path(root, path);
        let dimensions = read_screenshot_dimensions(path);
        let category = screenshot_review_category(&relative, path, &dimensions);
        entries.push(ScreenshotSummaryEntry {
            file: path.to_path_buf(),
            relative_path: relative,
            dimensions,
            category,
        });
    }

    Ok(entries)
}

fn read_screenshot_dimensions(path: &Path) -> ScreenshotDimensions {
    match image::image_dimensions(path) {
        Ok((width, height)) => ScreenshotDimensions {
            width: Some(width),
            height: Some(height),
            display: format!("{width}x{height}"),
        },
        Err(_) => ScreenshotDimensions {
            width: None,
            height: None,
            display: "unreadable".to_string(),
        },
    }
}

fn screenshot_review_category(
    relative_path: &str,
    file_path: &Path,
    dimensions: &ScreenshotDimensions,
) -> ScreenshotReviewCategory {
    let path = relative_path.to_lowercase();
    let name = file_path
        .file_name()
        .and_then(|name| name.to_str())
        .unwrap_or_default()
        .to_lowercase();

    if path.starts_with("visual-diffs/") || name.ends_with("_diff.png") {
        return ScreenshotReviewCategory {
            name: "visual diff",
            rank: 0,
        };
    }

    if dimensions
        .width
        .zip(dimensions.height)
        .map(|(width, height)| width < 64 || height < 64)
        .unwrap_or(false)
    {
        return ScreenshotReviewCategory {
            name: "suspicious screenshot dimensions",
            rank: 1,
        };
    }

    if path.starts_with("baseline-candidates/") {
        return ScreenshotReviewCategory {
            name: "baseline candidate",
            rank: 2,
        };
    }

    if [
        "not_found",
        "failed",
        "failure",
        "missing",
        "error",
        "navigation_failed",
    ]
    .iter()
    .any(|needle| name.contains(needle))
    {
        return ScreenshotReviewCategory {
            name: "diagnostic failure snapshot",
            rank: 3,
        };
    }

    ScreenshotReviewCategory {
        name: "regular screenshot",
        rank: 4,
    }
}

fn save_screenshot_gallery(
    entries: &[ScreenshotSummaryEntry],
    output_path: &Path,
    thumb_width: u32,
    thumb_height: u32,
    label_height: u32,
    jpeg_quality: u8,
) -> Result<(), String> {
    if entries.is_empty() {
        return Ok(());
    }

    let columns = 4.min(entries.len()).max(1) as u32;
    let padding = 12;
    let rows = ((entries.len() as f64) / columns as f64).ceil() as u32;
    let sheet_width = (columns * thumb_width) + ((columns + 1) * padding);
    let sheet_height = (rows * (label_height + thumb_height + padding)) + padding;
    let mut sheet = RgbaImage::from_pixel(sheet_width, sheet_height, Rgba([255, 255, 255, 255]));

    for (index, entry) in entries.iter().enumerate() {
        let row = index as u32 / columns;
        let column = index as u32 % columns;
        let x = padding + (column * (thumb_width + padding));
        let y = padding + (row * (label_height + thumb_height + padding));
        fill_rect(
            &mut sheet,
            x,
            y,
            thumb_width,
            label_height + thumb_height,
            Rgba([245, 247, 250, 255]),
        );
        draw_rect_border(
            &mut sheet,
            x,
            y,
            thumb_width,
            label_height + thumb_height,
            Rgba([205, 213, 224, 255]),
        );
        draw_category_stripe(&mut sheet, x, y, thumb_width, entry.category.rank);

        match image::open(&entry.file) {
            Ok(image) => {
                let image = image.to_rgba8();
                let max_width = thumb_width.saturating_sub(12).max(1);
                let max_height = thumb_height.saturating_sub(12).max(1);
                let scale = (max_width as f64 / image.width() as f64)
                    .min(max_height as f64 / image.height() as f64)
                    .min(1.0);
                let draw_width = ((image.width() as f64) * scale).round().max(1.0) as u32;
                let draw_height = ((image.height() as f64) * scale).round().max(1.0) as u32;
                let resized =
                    imageops::resize(&image, draw_width, draw_height, FilterType::Lanczos3);
                let draw_x = x + ((thumb_width - draw_width) / 2);
                let draw_y = y + label_height + ((thumb_height - draw_height) / 2);
                imageops::overlay(&mut sheet, &resized, draw_x as i64, draw_y as i64);
            }
            Err(_) => {
                fill_rect(
                    &mut sheet,
                    x + 6,
                    y + label_height + 6,
                    thumb_width.saturating_sub(12),
                    thumb_height.saturating_sub(12),
                    Rgba([255, 236, 236, 255]),
                );
            }
        }
    }

    if let Some(parent) = output_path
        .parent()
        .filter(|parent| !parent.as_os_str().is_empty())
    {
        fs::create_dir_all(parent).map_err(|error| {
            format!(
                "Could not create gallery directory {}: {error}",
                parent.display()
            )
        })?;
    }
    let file = File::create(output_path).map_err(|error| {
        format!(
            "Could not create gallery {}: {error}",
            output_path.display()
        )
    })?;
    let rgb = DynamicImage::ImageRgba8(sheet).to_rgb8();
    let mut encoder = JpegEncoder::new_with_quality(file, jpeg_quality);
    encoder
        .encode(&rgb, rgb.width(), rgb.height(), ColorType::Rgb8.into())
        .map_err(|error| format!("Could not write gallery {}: {error}", output_path.display()))
}

fn fill_rect(image: &mut RgbaImage, x: u32, y: u32, width: u32, height: u32, color: Rgba<u8>) {
    let max_x = (x + width).min(image.width());
    let max_y = (y + height).min(image.height());
    for yy in y..max_y {
        for xx in x..max_x {
            image.put_pixel(xx, yy, color);
        }
    }
}

fn draw_rect_border(
    image: &mut RgbaImage,
    x: u32,
    y: u32,
    width: u32,
    height: u32,
    color: Rgba<u8>,
) {
    if width == 0 || height == 0 {
        return;
    }

    let right = (x + width - 1).min(image.width().saturating_sub(1));
    let bottom = (y + height - 1).min(image.height().saturating_sub(1));
    for xx in x..=right {
        if y < image.height() {
            image.put_pixel(xx, y, color);
        }
        if bottom < image.height() {
            image.put_pixel(xx, bottom, color);
        }
    }
    for yy in y..=bottom {
        if x < image.width() {
            image.put_pixel(x, yy, color);
        }
        if right < image.width() {
            image.put_pixel(right, yy, color);
        }
    }
}

fn draw_category_stripe(image: &mut RgbaImage, x: u32, y: u32, width: u32, rank: u8) {
    let color = match rank {
        0 => Rgba([222, 70, 70, 255]),
        1 => Rgba([230, 145, 56, 255]),
        2 => Rgba([94, 129, 172, 255]),
        3 => Rgba([180, 94, 170, 255]),
        _ => Rgba([145, 154, 166, 255]),
    };
    fill_rect(image, x + 1, y + 1, width.saturating_sub(2), 5, color);
}

fn write_summary(lines: &[String], summary_path: Option<&Path>) -> Result<(), String> {
    let mut text = lines.join("\n");
    text.push('\n');

    if let Some(path) = summary_path {
        if let Some(parent) = path
            .parent()
            .filter(|parent| !parent.as_os_str().is_empty())
        {
            fs::create_dir_all(parent).map_err(|error| {
                format!(
                    "Could not create summary directory {}: {error}",
                    parent.display()
                )
            })?;
        }
        let mut file = fs::OpenOptions::new()
            .create(true)
            .append(true)
            .open(path)
            .map_err(|error| format!("Could not open summary {}: {error}", path.display()))?;
        file.write_all(text.as_bytes())
            .map_err(|error| format!("Could not write summary {}: {error}", path.display()))?;
    } else {
        print!("{text}");
    }

    Ok(())
}

fn publish_triage_report(options: TriageOptions) -> Result<(), String> {
    fs::create_dir_all(&options.output_dir).map_err(|error| {
        format!(
            "Could not create triage output directory {}: {error}",
            options.output_dir.display()
        )
    })?;

    let report_collection = collect_triage_report_files(&options)?;
    let output = build_triage_output(&options, &report_collection);
    write_json(&options.output_dir.join("ui-parity-triage.json"), &output)?;
    fs::write(
        options.output_dir.join("ui-parity-triage.md"),
        triage_markdown_report(&output, options.top),
    )
    .map_err(|error| error.to_string())?;

    println!(
        "UI parity triage completed: {} report(s), {} queued scenario(s), {} region hotspot(s).",
        output.report_count,
        output.next_iteration_queue.len(),
        output.region_hotspots.len()
    );
    println!(
        "Report: {}",
        options.output_dir.join("ui-parity-triage.md").display()
    );
    Ok(())
}

fn collect_triage_report_files(options: &TriageOptions) -> Result<TriageReportCollection, String> {
    let mut reports = Vec::new();
    let mut skipped = Vec::new();
    for entry in WalkDir::new(&options.artifact_root)
        .into_iter()
        .filter_map(Result::ok)
    {
        let path = entry.path();
        if !path.is_file()
            || path.file_name().and_then(|name| name.to_str()) != Some("ui-parity-report.json")
        {
            continue;
        }

        let text = match fs::read_to_string(path) {
            Ok(text) => text,
            Err(error) => {
                skipped.push(TriageSkippedReport {
                    report_path: path.display().to_string(),
                    reason: format!("read failed: {error}"),
                });
                continue;
            }
        };
        let value = match serde_json::from_str::<Value>(&text) {
            Ok(value) => value,
            Err(error) => {
                skipped.push(TriageSkippedReport {
                    report_path: path.display().to_string(),
                    reason: format!("parse failed: {error}"),
                });
                continue;
            }
        };
        reports.push(TriageReportFile {
            path: path.to_path_buf(),
            modified_millis: file_modified_millis(path),
            artifact_name: triage_artifact_name(path, &value),
            value,
        });
    }

    reports.sort_by(|a, b| {
        b.modified_millis
            .cmp(&a.modified_millis)
            .then_with(|| a.path.cmp(&b.path))
    });
    reports.truncate(options.max_reports);
    Ok(TriageReportCollection { reports, skipped })
}

fn build_triage_output(
    options: &TriageOptions,
    collection: &TriageReportCollection,
) -> TriageOutput {
    let mut artifact_summaries = Vec::new();
    let mut scenario_aggregates = BTreeMap::<String, TriageScenarioAggregate>::new();
    let mut region_aggregates = BTreeMap::<String, TriageHotspotAggregate>::new();
    let mut finding_aggregates = BTreeMap::<String, TriageFindingAggregate>::new();
    let mut layer_aggregates = BTreeMap::<String, TriageHotspotAggregate>::new();

    for report in &collection.reports {
        artifact_summaries.push(triage_artifact_summary(options, report));
        let Some(scenarios) = get_array(&report.value, "Scenarios") else {
            continue;
        };

        for scenario in scenarios {
            let sample = TriageScenarioSample::from_value(report, scenario);
            scenario_aggregates
                .entry(sample.scenario_id.clone())
                .or_insert_with(|| TriageScenarioAggregate::new(&sample.scenario_id))
                .push(sample.clone());

            for region in &sample.regions {
                region_aggregates
                    .entry(region.name.clone())
                    .or_insert_with(|| TriageHotspotAggregate::new(&region.name))
                    .push(region.score, (85.0 - region.score).max(0.0), &sample);
            }

            for finding in &sample.findings {
                let key = format!("{} / {}", finding.layer_hint, finding.metric);
                finding_aggregates
                    .entry(key.clone())
                    .or_insert_with(|| TriageFindingAggregate::new(&key))
                    .push(finding, &sample);
                layer_aggregates
                    .entry(finding.layer_hint.clone())
                    .or_insert_with(|| TriageHotspotAggregate::new(&finding.layer_hint))
                    .push(sample.score, sample.deficit.max(1.0), &sample);
            }
        }
    }

    let mut queue = scenario_aggregates
        .into_values()
        .filter_map(|aggregate| aggregate.into_queue_item())
        .filter(|item| item.latest_status != "pass")
        .collect::<Vec<_>>();
    queue.sort_by(|a, b| {
        b.priority
            .partial_cmp(&a.priority)
            .unwrap_or(std::cmp::Ordering::Equal)
            .then_with(|| a.scenario_id.cmp(&b.scenario_id))
    });

    let mut region_hotspots = region_aggregates
        .into_values()
        .map(TriageHotspotAggregate::into_hotspot)
        .filter(|hotspot| hotspot.average_deficit > 0.0)
        .collect::<Vec<_>>();
    region_hotspots.sort_by(compare_triage_hotspots);

    let mut finding_hotspots = finding_aggregates
        .into_values()
        .map(TriageFindingAggregate::into_hotspot)
        .collect::<Vec<_>>();
    finding_hotspots.sort_by(compare_triage_finding_hotspots);

    let mut layer_hotspots = layer_aggregates
        .into_values()
        .map(TriageHotspotAggregate::into_hotspot)
        .collect::<Vec<_>>();
    layer_hotspots.sort_by(compare_triage_hotspots);

    let evidence_gaps = finding_hotspots
        .iter()
        .filter(|hotspot| hotspot.layer_hint == "evidence_quality")
        .take(options.top)
        .cloned()
        .collect::<Vec<_>>();

    TriageOutput {
        schema_version: "easydict.ui-parity.triage.v1".to_string(),
        generated_at_utc: now_string(),
        artifact_root: options.artifact_root.display().to_string(),
        report_count: collection.reports.len(),
        skipped_reports: collection.skipped.clone(),
        artifact_summaries,
        next_iteration_queue: queue.into_iter().take(options.top).collect(),
        region_hotspots: region_hotspots.into_iter().take(options.top).collect(),
        finding_hotspots: finding_hotspots.into_iter().take(options.top).collect(),
        layer_hotspots: layer_hotspots.into_iter().take(options.top).collect(),
        evidence_gaps,
    }
}

fn triage_markdown_report(output: &TriageOutput, top: usize) -> String {
    let mut out = String::new();
    out.push_str("# UI Parity Triage\n\n");
    out.push_str(&format!("Generated: `{}`\n", output.generated_at_utc));
    out.push_str(&format!("Artifact root: `{}`\n", output.artifact_root));
    out.push_str(&format!("Reports scanned: **{}**\n\n", output.report_count));
    if !output.skipped_reports.is_empty() {
        out.push_str(&format!(
            "Skipped malformed report(s): **{}**\n\n",
            output.skipped_reports.len()
        ));
    }

    if output.report_count == 0 {
        out.push_str("No `ui-parity-report.json` files were found.\n");
        return out;
    }

    out.push_str("## Next Iteration Queue\n\n");
    out.push_str("| Priority | Scenario | Action | Latest | Min | Seen | Worst region | Driver | Artifact |\n");
    out.push_str("| ---: | --- | --- | ---: | ---: | ---: | --- | --- | --- |\n");
    for item in output.next_iteration_queue.iter().take(top) {
        out.push_str(&format!(
            "| {:.2} | `{}` | {} | {:.2} `{}` | {:.2} | {} | {} | {} | `{}` |\n",
            item.priority,
            item.scenario_id,
            item.action,
            item.latest_score,
            item.latest_status,
            item.minimum_score,
            item.occurrences,
            item.worst_region.as_deref().unwrap_or("n/a"),
            item.primary_driver,
            item.latest_artifact
        ));
    }

    out.push_str("\n## Region Hotspots\n\n");
    out.push_str("| Region | Avg score | Min score | Avg deficit | Seen | Example |\n");
    out.push_str("| --- | ---: | ---: | ---: | ---: | --- |\n");
    for hotspot in output.region_hotspots.iter().take(top) {
        out.push_str(&format!(
            "| `{}` | {:.2} | {:.2} | {:.2} | {} | `{}` / `{}` |\n",
            hotspot.name,
            hotspot.average_score,
            hotspot.minimum_score,
            hotspot.average_deficit,
            hotspot.occurrences,
            hotspot.example_artifact,
            hotspot.example_scenario
        ));
    }

    out.push_str("\n## Finding Hotspots\n\n");
    out.push_str("| Driver | Layer | Severity | Seen | Avg value | Example |\n");
    out.push_str("| --- | --- | --- | ---: | ---: | --- |\n");
    for hotspot in output.finding_hotspots.iter().take(top) {
        out.push_str(&format!(
            "| `{}` | `{}` | `{}` | {} | {:.2} | `{}` / `{}`: {} |\n",
            hotspot.metric,
            hotspot.layer_hint,
            hotspot.max_severity,
            hotspot.occurrences,
            hotspot.average_value,
            hotspot.example_artifact,
            hotspot.example_scenario,
            markdown_escape_table(&hotspot.example_message)
        ));
    }

    out.push_str("\n## Layer Hotspots\n\n");
    out.push_str("| Layer | Avg score | Min score | Avg deficit | Seen | Example |\n");
    out.push_str("| --- | ---: | ---: | ---: | ---: | --- |\n");
    for hotspot in output.layer_hotspots.iter().take(top) {
        out.push_str(&format!(
            "| `{}` | {:.2} | {:.2} | {:.2} | {} | `{}` / `{}` |\n",
            hotspot.name,
            hotspot.average_score,
            hotspot.minimum_score,
            hotspot.average_deficit,
            hotspot.occurrences,
            hotspot.example_artifact,
            hotspot.example_scenario
        ));
    }

    if !output.evidence_gaps.is_empty() {
        out.push_str("\n## Evidence Gaps\n\n");
        out.push_str("| Metric | Seen | Example |\n");
        out.push_str("| --- | ---: | --- |\n");
        for gap in output.evidence_gaps.iter().take(top) {
            out.push_str(&format!(
                "| `{}` | {} | `{}` / `{}`: {} |\n",
                gap.metric,
                gap.occurrences,
                gap.example_artifact,
                gap.example_scenario,
                markdown_escape_table(&gap.example_message)
            ));
        }
    }

    out.push_str("\n## Latest Reports\n\n");
    out.push_str("| Artifact | Generated | Pass/Warn/Fail | Avg | Min | Report |\n");
    out.push_str("| --- | --- | --- | ---: | ---: | --- |\n");
    for artifact in output.artifact_summaries.iter().take(top) {
        out.push_str(&format!(
            "| `{}` | `{}` | {}/{}/{} | {:.2} | {:.2} | `{}` |\n",
            artifact.artifact_name,
            artifact.generated_at_utc,
            artifact.pass_count,
            artifact.warn_count,
            artifact.fail_count,
            artifact.average_score,
            artifact.minimum_score,
            artifact.report_path
        ));
    }

    if !output.skipped_reports.is_empty() {
        out.push_str("\n## Skipped Reports\n\n");
        out.push_str("| Report | Reason |\n");
        out.push_str("| --- | --- |\n");
        for skipped in output.skipped_reports.iter().take(top) {
            out.push_str(&format!(
                "| `{}` | {} |\n",
                skipped.report_path,
                markdown_escape_table(&skipped.reason)
            ));
        }
    }

    out
}

fn triage_artifact_summary(
    options: &TriageOptions,
    report: &TriageReportFile,
) -> TriageArtifactSummary {
    let summary = get_object(&report.value, "Summary");
    TriageArtifactSummary {
        artifact_name: report.artifact_name.clone(),
        generated_at_utc: get_string(&report.value, "GeneratedAtUtc").unwrap_or_default(),
        report_path: relative_path(&options.output_dir, &report.path),
        total_scenarios: summary
            .and_then(|value| get_u32(value, "TotalScenarios"))
            .unwrap_or_default() as usize,
        pass_count: summary
            .and_then(|value| get_u32(value, "PassCount"))
            .unwrap_or_default() as usize,
        warn_count: summary
            .and_then(|value| get_u32(value, "WarnCount"))
            .unwrap_or_default() as usize,
        fail_count: summary
            .and_then(|value| get_u32(value, "FailCount"))
            .unwrap_or_default() as usize,
        average_score: summary
            .and_then(|value| get_f64(value, "AverageScore"))
            .map(round2)
            .unwrap_or_default(),
        minimum_score: summary
            .and_then(|value| get_f64(value, "MinimumScore"))
            .map(round2)
            .unwrap_or_default(),
    }
}

fn triage_artifact_name(path: &Path, value: &Value) -> String {
    get_string(value, "ScreenshotRoot")
        .and_then(|root| {
            PathBuf::from(root)
                .file_name()
                .and_then(|name| name.to_str())
                .map(ToString::to_string)
        })
        .or_else(|| {
            path.parent()
                .and_then(Path::parent)
                .and_then(|artifact| artifact.file_name())
                .and_then(|name| name.to_str())
                .map(ToString::to_string)
        })
        .unwrap_or_else(|| path.display().to_string())
}

fn file_modified_millis(path: &Path) -> u128 {
    fs::metadata(path)
        .and_then(|metadata| metadata.modified())
        .ok()
        .and_then(|modified| modified.duration_since(UNIX_EPOCH).ok())
        .map(|duration| duration.as_millis())
        .unwrap_or_default()
}

fn compare_triage_hotspots(left: &TriageHotspot, right: &TriageHotspot) -> std::cmp::Ordering {
    right
        .average_deficit
        .partial_cmp(&left.average_deficit)
        .unwrap_or(std::cmp::Ordering::Equal)
        .then_with(|| {
            right
                .minimum_score
                .partial_cmp(&left.minimum_score)
                .unwrap_or(std::cmp::Ordering::Equal)
        })
        .then_with(|| left.name.cmp(&right.name))
}

fn compare_triage_finding_hotspots(
    left: &TriageFindingHotspot,
    right: &TriageFindingHotspot,
) -> std::cmp::Ordering {
    triage_severity_rank(&right.max_severity)
        .cmp(&triage_severity_rank(&left.max_severity))
        .then_with(|| right.occurrences.cmp(&left.occurrences))
        .then_with(|| {
            right
                .average_value
                .partial_cmp(&left.average_value)
                .unwrap_or(std::cmp::Ordering::Equal)
        })
        .then_with(|| left.metric.cmp(&right.metric))
}

fn triage_severity_rank(severity: &str) -> u8 {
    match severity {
        "error" => 3,
        "warning" => 2,
        "info" => 1,
        _ => 0,
    }
}

fn markdown_escape_table(value: &str) -> String {
    value.replace('|', "\\|")
}

fn publish_code_parity_report(options: CodeParityOptions) -> Result<i32, String> {
    fs::create_dir_all(&options.output_dir).map_err(|error| {
        format!(
            "Could not create code parity output directory {}: {error}",
            options.output_dir.display()
        )
    })?;

    let output = build_code_parity_output(&options)?;
    write_json(&options.output_dir.join("ui-code-parity.json"), &output)?;
    fs::write(
        options.output_dir.join("ui-code-parity.md"),
        code_parity_markdown_report(&output, options.top),
    )
    .map_err(|error| error.to_string())?;

    println!(
        "UI code parity completed: {} .NET layout fact(s), {} Rust id fact(s), {} theme issue(s), {} layout issue(s).",
        output.summary.dotnet_layout_fact_count,
        output.summary.rust_static_ui_id_count,
        output.theme_comparisons.len(),
        output.layout_comparisons.len()
    );
    println!(
        "Report: {}",
        options.output_dir.join("ui-code-parity.md").display()
    );

    let has_drift = output.summary.theme_drift_count
        + output.summary.theme_missing_count
        + output.summary.layout_drift_count
        + output.summary.layout_missing_count
        + output.summary.layout_different_count
        > 0;
    Ok(if options.fail_on_drift && has_drift {
        2
    } else {
        0
    })
}

fn build_code_parity_output(options: &CodeParityOptions) -> Result<CodeParityOutput, String> {
    let dotnet_facts = read_dotnet_xaml_facts(options)?;
    let rust_facts = read_rust_ui_facts(options)?;
    let dotnet_theme = read_dotnet_theme_facts(options)?;
    let rust_theme = read_rust_theme_facts(options)?;
    let ambiguous_dotnet_ids = duplicated_dotnet_ids(&dotnet_facts);

    let mut layout_comparisons = Vec::new();
    for fact in &dotnet_facts {
        let (candidate, matched_contextual_alias) = rust_fact_for_dotnet_fact(&rust_facts, fact);
        let has_ambiguous_source_id = candidate.is_some()
            && ambiguous_dotnet_ids.contains(&fact.id)
            && !matched_contextual_alias;
        for (property, dotnet_value) in &fact.properties {
            let Some(rust_property) = rust_property_for_dotnet_attribute(property) else {
                continue;
            };
            if has_ambiguous_source_id {
                continue;
            }
            let comparable_dotnet_value =
                resolve_code_layout_value(property, dotnet_value, &dotnet_theme);
            let rust_value = candidate
                .and_then(|fact| fact.properties.get(rust_property))
                .map(|value| resolve_code_layout_value(property, value, &dotnet_theme));
            let status = compare_code_property_value(
                property,
                &comparable_dotnet_value,
                rust_value.as_deref(),
            );
            if status != "pass" {
                layout_comparisons.push(CodeLayoutComparison {
                    status,
                    id: fact.id.clone(),
                    dotnet_kind: fact.kind.clone(),
                    rust_kind: candidate.map(|fact| fact.kind.clone()),
                    property: property.clone(),
                    rust_property: rust_property.to_string(),
                    dotnet_value: comparable_dotnet_value,
                    rust_value,
                    dotnet_location: format!("{}:{}", fact.file, fact.line),
                    rust_location: candidate.map(|fact| format!("{}:{}", fact.file, fact.line)),
                    recommendation: code_layout_recommendation(
                        &fact.id,
                        property,
                        rust_property,
                        dotnet_value,
                    ),
                });
            }
        }
    }
    layout_comparisons.sort_by(compare_code_layout_comparisons);
    let settings_services_focus = code_settings_services_focus(&layout_comparisons);
    let settings_services_expander_scheme = code_settings_services_expander_scheme(options)?;
    let interaction_contracts = code_interaction_contracts(options)?;

    let mut theme_comparisons = Vec::new();
    for (dotnet_key, rust_key) in code_theme_key_map() {
        let Some(dotnet_value) = dotnet_theme.colors.get(*dotnet_key) else {
            continue;
        };
        let rust_value = rust_theme.values.get(*rust_key).cloned();
        let status = compare_code_property_value("Color", dotnet_value, rust_value.as_deref());
        if status != "pass" {
            theme_comparisons.push(CodeThemeComparison {
                status,
                dotnet_key: (*dotnet_key).to_string(),
                rust_token: (*rust_key).to_string(),
                dotnet_value: dotnet_value.clone(),
                rust_value,
                recommendation: format!(
                    "Align Rust theme token `{rust_key}` with .NET resource `{dotnet_key}`."
                ),
            });
        }
    }
    theme_comparisons.sort_by(compare_code_theme_comparisons);

    let summary = CodeParitySummary {
        dotnet_layout_fact_count: dotnet_facts.len(),
        rust_static_ui_id_count: rust_facts.len(),
        dotnet_theme_fact_count: dotnet_theme.colors.len() + dotnet_theme.metrics.len(),
        rust_theme_fact_count: rust_theme.values.len(),
        ambiguous_dotnet_id_count: ambiguous_dotnet_ids.len(),
        theme_comparison_count: theme_comparisons.len(),
        layout_comparison_count: layout_comparisons.len(),
        theme_drift_count: theme_comparisons
            .iter()
            .filter(|item| item.status == "drift")
            .count(),
        theme_missing_count: theme_comparisons
            .iter()
            .filter(|item| item.status == "missing")
            .count(),
        theme_different_count: theme_comparisons
            .iter()
            .filter(|item| item.status == "different")
            .count(),
        layout_drift_count: layout_comparisons
            .iter()
            .filter(|item| item.status == "drift")
            .count(),
        layout_missing_count: layout_comparisons
            .iter()
            .filter(|item| item.status == "missing")
            .count(),
        layout_different_count: layout_comparisons
            .iter()
            .filter(|item| item.status == "different")
            .count(),
        interaction_contract_count: interaction_contracts.len(),
        interaction_contract_missing_count: interaction_contracts
            .iter()
            .filter(|item| item.status == "missing")
            .count(),
        interaction_contract_partial_count: interaction_contracts
            .iter()
            .filter(|item| item.status == "partial")
            .count(),
    };

    let layout_gap_buckets = code_layout_gap_buckets(&layout_comparisons);
    let layout_gap_components = code_layout_gap_components(&layout_comparisons);

    Ok(CodeParityOutput {
        schema_version: "easydict.ui-code-parity.v1".to_string(),
        generated_at_utc: now_string(),
        repo_root: options.repo_root.display().to_string(),
        summary,
        theme_comparisons,
        layout_comparisons,
        layout_gap_buckets,
        layout_gap_components,
        settings_services_focus,
        settings_services_expander_scheme,
        interaction_contracts,
        ambiguous_dotnet_ids: ambiguous_dotnet_ids.into_iter().collect(),
        dotnet_facts,
        rust_facts,
    })
}

fn code_parity_markdown_report(output: &CodeParityOutput, top: usize) -> String {
    let mut out = String::new();
    out.push_str("# UI Code Parity Report\n\n");
    out.push_str(&format!("Generated: `{}`\n", output.generated_at_utc));
    out.push_str(&format!("Repo: `{}`\n\n", output.repo_root));
    out.push_str("## Summary\n\n");
    out.push_str(&format!(
        "- .NET layout facts: **{}**\n",
        output.summary.dotnet_layout_fact_count
    ));
    out.push_str(&format!(
        "- Rust static UI ids: **{}**\n",
        output.summary.rust_static_ui_id_count
    ));
    out.push_str(&format!(
        "- .NET/Rust theme facts: **{} / {}**\n",
        output.summary.dotnet_theme_fact_count, output.summary.rust_theme_fact_count
    ));
    out.push_str(&format!(
        "- Ambiguous .NET ids skipped for matched layout: **{}**\n",
        output.summary.ambiguous_dotnet_id_count
    ));
    out.push_str(&format!(
        "- Theme drift/missing/different: **{} / {} / {}**\n",
        output.summary.theme_drift_count,
        output.summary.theme_missing_count,
        output.summary.theme_different_count
    ));
    out.push_str(&format!(
        "- Layout drift/missing/different: **{} / {} / {}**\n",
        output.summary.layout_drift_count,
        output.summary.layout_missing_count,
        output.summary.layout_different_count
    ));
    out.push_str(&format!(
        "- Settings Services focus items: **{}**\n",
        output.settings_services_focus.len()
    ));
    out.push_str(&format!(
        "- Settings Services expander scheme checks: **{}**\n\n",
        output.settings_services_expander_scheme.len()
    ));
    out.push_str(&format!(
        "- Interaction/effects/tray contracts: **{}** (missing **{}**, partial **{}**)\n\n",
        output.summary.interaction_contract_count,
        output.summary.interaction_contract_missing_count,
        output.summary.interaction_contract_partial_count
    ));

    out.push_str("## Layout Gap Buckets\n\n");
    out.push_str("| Area | Total | Missing | Drift | Different |\n");
    out.push_str("| --- | ---: | ---: | ---: | ---: |\n");
    if output.layout_gap_buckets.is_empty() {
        out.push_str("| pass | 0 | 0 | 0 | 0 |\n");
    } else {
        for bucket in &output.layout_gap_buckets {
            out.push_str(&format!(
                "| `{}` | {} | {} | {} | {} |\n",
                bucket.area, bucket.total, bucket.missing, bucket.drift, bucket.different
            ));
        }
    }

    out.push_str("\n### Top Layout Gap Components\n\n");
    out.push_str("| Area | Id | Total | Missing | Drift | Different | Properties |\n");
    out.push_str("| --- | --- | ---: | ---: | ---: | ---: | --- |\n");
    if output.layout_gap_components.is_empty() {
        out.push_str("| pass | n/a | 0 | 0 | 0 | 0 | n/a |\n");
    } else {
        for component in output.layout_gap_components.iter().take(top.min(40)) {
            out.push_str(&format!(
                "| `{}` | `{}` | {} | {} | {} | {} | `{}` |\n",
                component.area,
                markdown_escape_table(&component.id),
                component.total,
                component.missing,
                component.drift,
                component.different,
                markdown_escape_table(&component.properties.join(", "))
            ));
        }
    }

    out.push_str("## Theme Drift\n\n");
    out.push_str("| Status | .NET key | Rust token | .NET | Rust | Recommendation |\n");
    out.push_str("| --- | --- | --- | --- | --- | --- |\n");
    if output.theme_comparisons.is_empty() {
        out.push_str("| pass | n/a | n/a | n/a | n/a | n/a |\n");
    } else {
        for item in output.theme_comparisons.iter().take(top) {
            out.push_str(&format!(
                "| `{}` | `{}` | `{}` | `{}` | `{}` | {} |\n",
                item.status,
                item.dotnet_key,
                item.rust_token,
                item.dotnet_value,
                item.rust_value.as_deref().unwrap_or(""),
                markdown_escape_table(&item.recommendation)
            ));
        }
    }

    out.push_str("\n## Matched Layout Drift\n\n");
    out.push_str(
        "| Status | Id | Property | .NET | Rust | .NET location | Rust location | Recommendation |\n",
    );
    out.push_str("| --- | --- | --- | --- | --- | --- | --- | --- |\n");
    let matched_layout = output
        .layout_comparisons
        .iter()
        .filter(|item| item.rust_location.is_some() && item.status != "missing")
        .take(top)
        .collect::<Vec<_>>();
    if matched_layout.is_empty() {
        out.push_str("| pass | n/a | n/a | n/a | n/a | n/a | n/a | n/a |\n");
    } else {
        for item in matched_layout {
            out.push_str(&format!(
                "| `{}` | `{}` | `{}` | `{}` | `{}` | `{}` | `{}` | {} |\n",
                item.status,
                item.id,
                item.property,
                markdown_escape_table(&item.dotnet_value),
                markdown_escape_table(item.rust_value.as_deref().unwrap_or("")),
                markdown_escape_table(&item.dotnet_location),
                markdown_escape_table(item.rust_location.as_deref().unwrap_or("")),
                markdown_escape_table(&item.recommendation)
            ));
        }
    }

    out.push_str("\n## Settings Services Focus\n\n");
    out.push_str(
        "| Priority | Component | Id | Property | .NET | Rust | .NET location | Rust location | Recommendation |\n",
    );
    out.push_str("| --- | --- | --- | --- | --- | --- | --- | --- | --- |\n");
    if output.settings_services_focus.is_empty() {
        out.push_str("| pass | n/a | n/a | n/a | n/a | n/a | n/a | n/a | n/a |\n");
    } else {
        for item in output.settings_services_focus.iter().take(top) {
            out.push_str(&format!(
                "| `{}` | `{}` | `{}` | `{}` | `{}` | `{}` | `{}` | `{}` | {} |\n",
                item.priority,
                markdown_escape_table(&item.component),
                item.id,
                item.property,
                markdown_escape_table(&item.dotnet_value),
                markdown_escape_table(item.rust_value.as_deref().unwrap_or("")),
                markdown_escape_table(&item.dotnet_location),
                markdown_escape_table(item.rust_location.as_deref().unwrap_or("")),
                markdown_escape_table(&item.recommendation)
            ));
        }
    }

    out.push_str("\n## Settings Services Expander Scheme\n\n");
    out.push_str("| Status | Area | .NET | Rust | Recommendation |\n");
    out.push_str("| --- | --- | --- | --- | --- |\n");
    if output.settings_services_expander_scheme.is_empty() {
        out.push_str("| pass | n/a | n/a | n/a | n/a |\n");
    } else {
        for item in &output.settings_services_expander_scheme {
            out.push_str(&format!(
                "| `{}` | `{}` | `{}` | `{}` | {} |\n",
                item.status,
                markdown_escape_table(&item.area),
                markdown_escape_table(&item.dotnet_value),
                markdown_escape_table(&item.rust_value),
                markdown_escape_table(&item.recommendation)
            ));
        }
    }

    out.push_str("\n## Interaction / Effects / Tray Contracts\n\n");
    out.push_str("| Status | Area | Contract | .NET evidence | Rust evidence | Recommendation |\n");
    out.push_str("| --- | --- | --- | --- | --- | --- |\n");
    if output.interaction_contracts.is_empty() {
        out.push_str("| pass | n/a | n/a | n/a | n/a | n/a |\n");
    } else {
        for item in &output.interaction_contracts {
            out.push_str(&format!(
                "| `{}` | `{}` | `{}` | `{}` | `{}` | {} |\n",
                item.status,
                markdown_escape_table(&item.area),
                markdown_escape_table(&item.contract),
                markdown_escape_table(&item.dotnet_evidence),
                markdown_escape_table(&item.rust_evidence),
                markdown_escape_table(&item.recommendation)
            ));
        }
    }

    out.push_str("\n## Layout Drift (All)\n\n");
    out.push_str(
        "| Status | Id | Property | .NET | Rust | .NET location | Rust location | Recommendation |\n",
    );
    out.push_str("| --- | --- | --- | --- | --- | --- | --- | --- |\n");
    if output.layout_comparisons.is_empty() {
        out.push_str("| pass | n/a | n/a | n/a | n/a | n/a | n/a | n/a |\n");
    } else {
        for item in output.layout_comparisons.iter().take(top) {
            out.push_str(&format!(
                "| `{}` | `{}` | `{}` | `{}` | `{}` | `{}` | `{}` | {} |\n",
                item.status,
                item.id,
                item.property,
                markdown_escape_table(&item.dotnet_value),
                markdown_escape_table(item.rust_value.as_deref().unwrap_or("")),
                markdown_escape_table(&item.dotnet_location),
                markdown_escape_table(item.rust_location.as_deref().unwrap_or("")),
                markdown_escape_table(&item.recommendation)
            ));
        }
    }

    out.push_str("\n## Workflow\n\n");
    out.push_str("1. Treat .NET XAML and Light resource values as the source of truth.\n");
    out.push_str("2. Fix missing Rust automation ids, dimensions, spacing, alignment, and theme tokens before screenshot tuning.\n");
    out.push_str("3. Re-run `triage` and screenshot parity after code-level drift is cleared.\n");
    if !output.ambiguous_dotnet_ids.is_empty() {
        out.push_str("\n## Tool Limits\n\n");
        out.push_str("The following .NET ids appear in multiple XAML files and were skipped for direct matched-layout drift because Rust static scanning does not yet infer window context:\n\n");
        for id in output.ambiguous_dotnet_ids.iter().take(top) {
            out.push_str(&format!("- `{}`\n", markdown_escape_table(id)));
        }
    }
    out
}

fn read_dotnet_xaml_facts(options: &CodeParityOptions) -> Result<Vec<CodeFact>, String> {
    let tag_regex = Regex::new(r#"(?s)<(?P<tag>[\w:.]+)(?P<attrs>[^<>]*?)(?:/?)>"#)
        .expect("valid XAML tag regex");
    let mut facts = Vec::new();
    for path in &options.dotnet_xaml {
        if !path.exists() {
            continue;
        }
        let text = fs::read_to_string(path)
            .map_err(|error| format!("Could not read {}: {error}", path.display()))?;
        for capture in tag_regex.captures_iter(&text) {
            let tag = capture
                .name("tag")
                .map(|value| value.as_str())
                .unwrap_or("");
            if tag.starts_with('/') {
                continue;
            }
            let attrs = parse_xml_attributes(
                capture
                    .name("attrs")
                    .map(|value| value.as_str())
                    .unwrap_or_default(),
            );
            let Some(id) = attrs
                .get("AutomationProperties.AutomationId")
                .or_else(|| attrs.get("x:Name"))
                .or_else(|| attrs.get("Name"))
                .filter(|id| !id.trim().is_empty() && !id.contains('{'))
                .cloned()
            else {
                continue;
            };

            let mut properties = BTreeMap::new();
            for attribute in code_layout_attributes() {
                if let Some(value) = attrs.get(*attribute) {
                    properties.insert((*attribute).to_string(), value.clone());
                }
            }
            if properties.is_empty() {
                continue;
            }
            facts.push(CodeFact {
                source: "dotnet-xaml".to_string(),
                file: repo_relative_path(&options.repo_root, path),
                line: line_number(
                    &text,
                    capture.get(0).map(|value| value.start()).unwrap_or(0),
                ),
                id,
                kind: tag.to_string(),
                properties,
            });
        }
    }
    Ok(facts)
}

fn rust_fact_for_dotnet_fact<'a>(
    rust_facts: &'a BTreeMap<String, CodeFact>,
    dotnet_fact: &CodeFact,
) -> (Option<&'a CodeFact>, bool) {
    if let Some(rust_id) = dotnet_to_rust_ui_alias_for_fact(dotnet_fact) {
        return (rust_facts.get(rust_id), true);
    }
    (rust_fact_for_dotnet_id(rust_facts, &dotnet_fact.id), false)
}

fn rust_fact_for_dotnet_id<'a>(
    rust_facts: &'a BTreeMap<String, CodeFact>,
    dotnet_id: &str,
) -> Option<&'a CodeFact> {
    rust_facts
        .get(dotnet_id)
        .or_else(|| dotnet_to_rust_ui_alias(dotnet_id).and_then(|rust_id| rust_facts.get(rust_id)))
}

fn dotnet_to_rust_ui_alias_for_fact(fact: &CodeFact) -> Option<&'static str> {
    let file = fact.file.replace('\\', "/");
    match () {
        _ if file.ends_with("Views/MainPage.xaml") => dotnet_main_page_alias(&fact.id),
        _ if file.ends_with("Views/MiniWindow.xaml") => {
            dotnet_floating_window_alias("mini", &fact.id)
        }
        _ if file.ends_with("Views/FixedWindow.xaml") => {
            dotnet_floating_window_alias("fixed", &fact.id)
        }
        _ if file.ends_with("Views/PopButtonWindow.xaml") => dotnet_pop_button_alias(&fact.id),
        _ => None,
    }
}

fn dotnet_main_page_alias(dotnet_id: &str) -> Option<&'static str> {
    match dotnet_id {
        "StatusText" => Some("StatusIndicator"),
        "SourcePlayButton" | "SourcePlayIcon" => Some("main.quick.play_source"),
        "TranslateButton" | "TranslateIcon" => Some("TranslateButton"),
        "TranslateButtonNarrow" | "TranslateIconNarrow" => Some("TranslateButtonNarrow"),
        "SwapLanguageButton" => Some("SwapLanguageButton"),
        "SwapLanguageButtonNarrow" => Some("SwapLanguageButtonNarrow"),
        _ => None,
    }
}

fn dotnet_floating_window_alias(prefix: &str, dotnet_id: &str) -> Option<&'static str> {
    match (prefix, dotnet_id) {
        ("mini", "WindowSurface") => Some("mini.content"),
        ("fixed", "WindowSurface") => Some("fixed.content"),
        ("mini", "TitleBarRegion") => Some("mini.header"),
        ("fixed", "TitleBarRegion") => Some("fixed.header"),
        ("mini", "PinButton") | ("mini", "PinIcon") => Some("mini.pin"),
        ("mini", "SourceTextContainer") => Some("mini.input_card"),
        ("fixed", "SourceTextContainer") => Some("fixed.input_card"),
        ("mini", "SourcePlayButton") | ("mini", "SourcePlayIcon") => Some("mini.play_source"),
        ("mini", "SwapButton") => Some("mini.swap"),
        ("fixed", "SwapButton") => Some("fixed.swap"),
        ("mini", "TranslateButton") | ("mini", "TranslateIcon") => Some("mini.translate"),
        ("fixed", "TranslateButton") | ("fixed", "TranslateIcon") => Some("fixed.translate"),
        ("mini", "StatusText") => Some("mini.status"),
        ("fixed", "StatusText") => Some("fixed.status"),
        ("mini", "SourceLangCombo") => Some("mini.source_language"),
        ("fixed", "SourceLangCombo") => Some("fixed.source_language"),
        ("mini", "TargetLangCombo") => Some("mini.target_language"),
        ("fixed", "TargetLangCombo") => Some("fixed.target_language"),
        ("mini", "ResultsScrollViewer") | ("mini", "ResultsPanel") => Some("mini.results"),
        ("fixed", "ResultsScrollViewer") | ("fixed", "ResultsPanel") => Some("fixed.results"),
        _ => None,
    }
}

fn dotnet_pop_button_alias(dotnet_id: &str) -> Option<&'static str> {
    match dotnet_id {
        "RootGrid" => Some("pop-button.window"),
        "TranslateButton" | "ModeIcon" => Some("pop-button.translate"),
        _ => None,
    }
}

fn dotnet_to_rust_ui_alias(dotnet_id: &str) -> Option<&'static str> {
    match dotnet_id {
        "LongDocContent" => Some("main.long-doc.scroll"),
        "LongDocContentGrid" => Some("main.long-doc.content"),
        "LongDocInputCard" => Some("main.long-doc.input_card"),
        "LongDocInputCardContent" => Some("LongDocInputCardContent"),
        "LongDocInputTitle" => Some("LongDocInputTitle"),
        "LongDocFilePanel" => Some("LongDocFilePanel"),
        "LongDocFilePathDisplay" => Some("LongDocFilePathDisplay"),
        "LongDocBrowseButton" => Some("main.long-doc.browse"),
        "LongDocControlBar" => Some("main.long-doc.control_bar"),
        "LongDocSourceLangCombo" => Some("main.long-doc.source_language"),
        "LongDocTargetLangCombo" => Some("main.long-doc.target_language"),
        "LongDocServiceCombo" => Some("main.long-doc.service"),
        "LongDocDocumentContextPassCheckBox" => Some("main.long-doc.two_pass"),
        "LongDocTranslateButton" => Some("main.long-doc.translate"),
        "LongDocTranslateIcon" => Some("main.long-doc.translate"),
        "LongDocInputModeCombo" => Some("main.long-doc.input_mode"),
        "LongDocOutputModeCombo" => Some("main.long-doc.output_mode"),
        "LongDocConcurrencyBox" => Some("main.long-doc.concurrency"),
        "LongDocPageRangeBox" => Some("main.long-doc.page_range"),
        "LongDocOutputCard" => Some("main.long-doc.output_card"),
        "LongDocOutputCardContent" => Some("LongDocOutputCardContent"),
        "LongDocOutputTitle" => Some("LongDocOutputTitle"),
        "LongDocRetryButton" => Some("main.long-doc.retry"),
        "LongDocOutputFieldsPanel" => Some("main.long-doc.output_content"),
        "LongDocOutputFolderLabel" => Some("main.long-doc.output_folder_label"),
        "LongDocOutputFolderDisplay" => Some("main.long-doc.output_folder"),
        "LongDocOutputBrowseButton" => Some("main.long-doc.output_browse"),
        "LongDocOutputNamingHint" => Some("main.long-doc.output_naming_hint"),
        "LongDocHistoryExpander" => Some("main.long-doc.history"),
        "LongDocHistoryListView" => Some("main.long-doc.history_list"),
        "LongDocClearHistoryButton" => Some("main.long-doc.clear_history"),
        "SettingsContentRoot" => Some("settings.content"),
        "SettingsTabsHost" => Some("settings.categories"),
        "ServicesTabContent" => Some("settings.services"),
        "ViewsTabContent" => Some("settings.views"),
        _ => None,
    }
}

fn duplicated_dotnet_ids(facts: &[CodeFact]) -> BTreeSet<String> {
    let mut counts = BTreeMap::<String, usize>::new();
    for fact in facts {
        *counts.entry(fact.id.clone()).or_default() += 1;
    }
    counts
        .into_iter()
        .filter_map(|(id, count)| (count > 1).then_some(id))
        .collect()
}

fn read_dotnet_theme_facts(options: &CodeParityOptions) -> Result<CodeThemeFacts, String> {
    let color_regex = Regex::new(r#"(?s)<Color\s+x:Key="([^"]+)">\s*([^<]+)\s*</Color>"#)
        .expect("valid color regex");
    let brush_regex =
        Regex::new(r#"(?s)<SolidColorBrush\b(?P<attrs>[^>]*)>"#).expect("valid brush regex");
    let metric_regex = Regex::new(
        r#"(?s)<(?:Thickness|CornerRadius)\s+x:Key="([^"]+)">\s*([^<]+)\s*</(?:Thickness|CornerRadius)>"#,
    )
    .expect("valid metric regex");
    let mut colors = BTreeMap::new();
    let mut brushes = BTreeMap::new();
    let mut metrics = BTreeMap::new();

    for path in &options.dotnet_resources {
        if !path.exists() {
            continue;
        }
        let mut text = fs::read_to_string(path)
            .map_err(|error| format!("Could not read {}: {error}", path.display()))?;
        if path.file_name().and_then(|name| name.to_str()) == Some("Colors.xaml") {
            if let Some(light) = extract_light_resource_dictionary(&text) {
                text = light;
            }
        }

        for capture in color_regex.captures_iter(&text) {
            let Some(value) =
                normalize_color_literal(capture.get(2).map(|value| value.as_str()).unwrap_or(""))
            else {
                continue;
            };
            insert_if_absent(&mut colors, capture[1].to_string(), value);
        }
        for capture in brush_regex.captures_iter(&text) {
            let attrs = parse_xml_attributes(
                capture
                    .name("attrs")
                    .map(|value| value.as_str())
                    .unwrap_or_default(),
            );
            let (Some(key), Some(color)) = (attrs.get("x:Key"), attrs.get("Color")) else {
                continue;
            };
            insert_if_absent(&mut brushes, key.clone(), color.clone());
        }
        for capture in metric_regex.captures_iter(&text) {
            insert_if_absent(
                &mut metrics,
                capture[1].to_string(),
                capture[2].trim().to_string(),
            );
        }
    }

    for (key, value) in brushes {
        if colors.contains_key(&key) {
            continue;
        }
        if let Some(resource_key) = resource_reference_key(&value) {
            if let Some(color) = colors.get(&resource_key) {
                colors.insert(key, color.clone());
            }
        } else if let Some(color) = normalize_color_literal(&value) {
            colors.insert(key, color);
        }
    }

    Ok(CodeThemeFacts { colors, metrics })
}

fn read_rust_theme_facts(options: &CodeParityOptions) -> Result<CodeRustThemeFacts, String> {
    let mut values = BTreeMap::new();
    if !options.rust_theme.exists() {
        return Ok(CodeRustThemeFacts { values });
    }
    let text = fs::read_to_string(&options.rust_theme)
        .map_err(|error| format!("Could not read {}: {error}", options.rust_theme.display()))?;
    let body =
        extract_between(&text, "fn easydict_light()", "fn easydict_dark").unwrap_or_default();
    let accent_regex =
        Regex::new(r#"(?s)accent:\s*AccentPalette\s*\{(?P<body>.*?)\},\s*typography"#)
            .expect("valid accent regex");
    if let Some(capture) = accent_regex.captures(&body) {
        for (field, color) in parse_rust_color_fields(
            capture
                .name("body")
                .map(|value| value.as_str())
                .unwrap_or_default(),
        ) {
            values.insert(format!("accent.{field}"), color);
        }
    }
    for (field, color) in parse_rust_color_fields(&body) {
        if matches!(
            field.as_str(),
            "base" | "light_1" | "light_2" | "dark_1" | "dark_2"
        ) {
            continue;
        }
        values.insert(field, color);
    }
    Ok(CodeRustThemeFacts { values })
}

fn read_rust_ui_facts(options: &CodeParityOptions) -> Result<BTreeMap<String, CodeFact>, String> {
    let mut facts = BTreeMap::new();
    if !options.rust_ui.exists() {
        return Ok(facts);
    }
    let text = fs::read_to_string(&options.rust_ui)
        .map_err(|error| format!("Could not read {}: {error}", options.rust_ui.display()))?;
    let lines = text.lines().collect::<Vec<_>>();
    let id_regex = Regex::new(r#"\.id\(\s*(?:format!\()?"([^"]+)""#).expect("valid Rust id regex");
    let style_regex = Regex::new(r#"\.tw\("([^"]+)"\)"#).expect("valid style regex");
    let kind_regex =
        Regex::new(r#"(button|combo_box|text_editor|text|card|container|row|column|wrap)\s*\("#)
            .expect("valid widget kind regex");

    for (index, line) in lines.iter().enumerate() {
        for capture in id_regex.captures_iter(line) {
            let id = capture[1].to_string();
            if id.contains('{') {
                continue;
            }
            let start = index.saturating_sub(options.context_lines);
            let end = (index + options.context_lines).min(lines.len().saturating_sub(1));
            let context = lines[start..=end].join("\n");
            let chain_context = rust_id_chain_context(&lines, index);
            let mut properties = BTreeMap::new();
            for property in rust_fluent_methods() {
                if let Some(value) = find_last_method_argument(&chain_context, property) {
                    properties.insert(property.to_string(), value);
                }
            }
            if !properties.contains_key("padding") {
                if let Some(value) = find_last_method_argument(&chain_context, "padding_edges") {
                    properties.insert("padding".to_string(), value);
                }
            }
            if !properties.contains_key("height") {
                let fixed_height =
                    match (properties.get("min_height"), properties.get("max_height")) {
                        (Some(min_height), Some(max_height)) if min_height == max_height => {
                            Some(min_height.clone())
                        }
                        _ => None,
                    };
                if let Some(fixed_height) = fixed_height {
                    properties.insert("height".to_string(), fixed_height);
                }
            }
            if let Some(size) = properties.get("size").cloned() {
                properties
                    .entry("width".to_string())
                    .or_insert_with(|| size.clone());
                properties.entry("height".to_string()).or_insert(size);
            }
            if is_secret_field_editor(&context, &id) {
                properties.insert(
                    "padding".to_string(),
                    "Edges { top: 5, right: 40, bottom: 5, left: 12 }".to_string(),
                );
                properties.insert("secure".to_string(), "true".to_string());
                properties.insert("align_x".to_string(), "Stretch".to_string());
            }
            if !properties.contains_key("width") {
                if let Some(width) = infer_enclosing_field_width(&context, &id) {
                    properties.insert("width".to_string(), width);
                    properties
                        .entry("align_x".to_string())
                        .or_insert_with(|| "Left".to_string());
                }
            }
            if chain_context.contains(".center_x()") {
                properties.insert("align_x".to_string(), "Center".to_string());
            }
            if chain_context.contains(".center_y()") {
                properties.insert("align_y".to_string(), "Center".to_string());
            }
            if is_right_aligned_status_badge(&id, &chain_context) {
                properties.insert("align_x".to_string(), "Right".to_string());
                properties.insert("align_y".to_string(), "Center".to_string());
            }
            if let Some(capture) = style_regex.captures_iter(&chain_context).last() {
                let style = capture[1].to_string();
                apply_rust_style_fact_properties(&style, &mut properties);
                properties.insert("style".to_string(), style);
            }
            let kind = kind_regex
                .captures(&context)
                .map(|capture| capture[1].to_string())
                .unwrap_or_else(|| "unknown".to_string());
            facts.insert(
                id.clone(),
                CodeFact {
                    source: "rust-ui".to_string(),
                    file: repo_relative_path(&options.repo_root, &options.rust_ui),
                    line: index + 1,
                    id,
                    kind,
                    properties,
                },
            );
        }
    }
    add_rust_secret_field_stack_facts(
        &repo_relative_path(&options.repo_root, &options.rust_ui),
        &text,
        &mut facts,
    );
    add_rust_service_expander_facts(
        &repo_relative_path(&options.repo_root, &options.rust_ui),
        &text,
        &mut facts,
    );
    add_rust_styled_text_facts(
        &repo_relative_path(&options.repo_root, &options.rust_ui),
        &text,
        &mut facts,
    );
    add_rust_settings_link_button_facts(
        &repo_relative_path(&options.repo_root, &options.rust_ui),
        &text,
        &mut facts,
    );
    add_rust_no_config_service_row_facts(
        &repo_relative_path(&options.repo_root, &options.rust_ui),
        &text,
        &mut facts,
    );
    add_rust_services_section_header_facts(
        &repo_relative_path(&options.repo_root, &options.rust_ui),
        &text,
        &mut facts,
    );
    add_rust_llm_provider_descriptor_facts(
        &repo_relative_path(&options.repo_root, &options.rust_ui),
        &text,
        &mut facts,
    );
    add_rust_local_ai_action_control_facts(
        &repo_relative_path(&options.repo_root, &options.rust_ui),
        &mut facts,
    );
    add_rust_settings_services_row_context_facts(
        &repo_relative_path(&options.repo_root, &options.rust_ui),
        &text,
        &mut facts,
    );
    add_rust_settings_about_row_context_facts(
        &repo_relative_path(&options.repo_root, &options.rust_ui),
        &text,
        &mut facts,
    );
    add_rust_main_action_bar_context_facts(
        &repo_relative_path(&options.repo_root, &options.rust_ui),
        &text,
        &mut facts,
    );
    add_rust_main_quick_content_context_facts(
        &repo_relative_path(&options.repo_root, &options.rust_ui),
        &text,
        &mut facts,
    );
    add_rust_settings_general_context_facts(
        &repo_relative_path(&options.repo_root, &options.rust_ui),
        &text,
        &mut facts,
    );
    add_rust_settings_shell_context_facts(
        &repo_relative_path(&options.repo_root, &options.rust_ui),
        &text,
        &mut facts,
    );
    add_rust_settings_loading_overlay_context_facts(
        &repo_relative_path(&options.repo_root, &options.rust_ui),
        &text,
        &mut facts,
    );
    add_rust_settings_advanced_context_facts(
        &repo_relative_path(&options.repo_root, &options.rust_ui),
        &text,
        &mut facts,
    );
    add_rust_settings_advanced_section_facts(
        &repo_relative_path(&options.repo_root, &options.rust_ui),
        &text,
        &mut facts,
    );
    add_rust_settings_language_context_facts(
        &repo_relative_path(&options.repo_root, &options.rust_ui),
        &text,
        &mut facts,
    );
    add_rust_floating_window_context_facts(
        &repo_relative_path(&options.repo_root, &options.rust_ui),
        &text,
        &mut facts,
    );
    add_rust_main_input_context_facts(
        &repo_relative_path(&options.repo_root, &options.rust_ui),
        &text,
        &mut facts,
    );
    add_rust_long_doc_card_context_facts(
        &repo_relative_path(&options.repo_root, &options.rust_ui),
        &text,
        &mut facts,
    );
    add_rust_long_doc_hint_header_context_facts(
        &repo_relative_path(&options.repo_root, &options.rust_ui),
        &text,
        &mut facts,
    );
    add_rust_long_doc_control_bar_context_facts(
        &repo_relative_path(&options.repo_root, &options.rust_ui),
        &text,
        &mut facts,
    );
    add_rust_settings_views_context_facts(
        &repo_relative_path(&options.repo_root, &options.rust_ui),
        &text,
        &mut facts,
    );
    add_rust_settings_hotkeys_context_facts(
        &repo_relative_path(&options.repo_root, &options.rust_ui),
        &text,
        &mut facts,
    );
    Ok(facts)
}

fn add_rust_service_expander_facts(
    relative: &str,
    text: &str,
    facts: &mut BTreeMap<String, CodeFact>,
) {
    let needle = "service_expander(";
    let mut search_start = 0;
    while let Some(offset) = text[search_start..].find(needle) {
        let call_start = search_start + offset;
        let argument_start = call_start + needle.len();
        let Some((arguments, call_end)) = read_balanced_argument(text, argument_start) else {
            break;
        };
        let args = split_top_level_arguments(&arguments);
        if args.len() >= 7 {
            let line = line_number(text, call_start);
            if let Some(service_id) = args.get(1).and_then(|value| rust_string_literal(value)) {
                if let Some(title_id) = service_expander_title_id(&service_id) {
                    insert_service_title_fact(relative, line, title_id.to_string(), facts);
                }
            }
            if let Some(expander_id) = args.get(3).and_then(|value| rust_string_literal(value)) {
                insert_service_expander_fact(relative, line, expander_id, facts);
            }
            if let Some(status_id) = args.get(5).and_then(|value| rust_string_literal(value)) {
                if !status_id.trim().is_empty() {
                    insert_service_status_fact(relative, line, status_id, facts);
                }
            }
        }
        search_start = call_end.saturating_add(1);
    }
}

fn add_rust_secret_field_stack_facts(
    relative: &str,
    text: &str,
    facts: &mut BTreeMap<String, CodeFact>,
) {
    let needle = "secret_field_stack(";
    let mut search_start = 0;
    while let Some(offset) = text[search_start..].find(needle) {
        let call_start = search_start + offset;
        let argument_start = call_start + needle.len();
        let Some((arguments, call_end)) = read_balanced_argument(text, argument_start) else {
            break;
        };
        let args = split_top_level_arguments(&arguments);
        if args.len() >= 5 {
            let line = line_number(text, call_start);
            let width = args
                .get(1)
                .and_then(|value| first_number_text(value))
                .unwrap_or_else(|| "350".to_string());
            if let Some(editor_id) = literal_id_in_rust_fragment(&args[3]) {
                insert_synthetic_rust_fact(
                    facts,
                    CodeFact {
                        source: "rust-ui-synthetic".to_string(),
                        file: relative.to_string(),
                        line,
                        id: editor_id,
                        kind: "text_editor".to_string(),
                        properties: BTreeMap::from([
                            ("width".to_string(), width.clone()),
                            (
                                "padding".to_string(),
                                "Edges { top: 5, right: 40, bottom: 5, left: 12 }".to_string(),
                            ),
                            ("secure".to_string(), "true".to_string()),
                            ("max_height".to_string(), "36".to_string()),
                            ("align_x".to_string(), "Stretch".to_string()),
                        ]),
                    },
                );
            }
            if let Some(reveal_id) = rust_string_literal(args[4].trim()) {
                insert_synthetic_rust_fact(
                    facts,
                    CodeFact {
                        source: "rust-ui-synthetic".to_string(),
                        file: relative.to_string(),
                        line,
                        id: reveal_id,
                        kind: "trailing_icon".to_string(),
                        properties: BTreeMap::from([
                            ("width".to_string(), "28".to_string()),
                            ("height".to_string(), "28".to_string()),
                            ("margin".to_string(), "0,0,6,0".to_string()),
                            ("align_x".to_string(), "Right".to_string()),
                            ("align_y".to_string(), "Center".to_string()),
                        ]),
                    },
                );
            }
        }
        search_start = call_end.saturating_add(1);
    }
}

fn add_rust_styled_text_facts(relative: &str, text: &str, facts: &mut BTreeMap<String, CodeFact>) {
    for function_name in [
        "styled_text_id",
        "styled_text_id_with_font_size",
        "single_line_styled_text_id",
        "sized_styled_text_id",
    ] {
        let needle = format!("{function_name}(");
        let mut search_start = 0;
        while let Some(offset) = text[search_start..].find(&needle) {
            let call_start = search_start + offset;
            let argument_start = call_start + needle.len();
            let Some((arguments, call_end)) = read_balanced_argument(text, argument_start) else {
                break;
            };
            let args = split_top_level_arguments(&arguments);
            if args.len() >= 3 {
                let line = line_number(text, call_start);
                if let (Some(id), Some(style)) = (
                    rust_string_literal(args[0].trim()),
                    rust_text_style(&args[2]),
                ) {
                    let mut properties = BTreeMap::from([
                        ("text_style".to_string(), style.clone()),
                        (
                            "font_size".to_string(),
                            explicit_text_font_size(function_name, &args)
                                .unwrap_or_else(|| text_style_font_size(&style).to_string()),
                        ),
                    ]);
                    properties.extend(rust_text_chain_properties(text, call_end));
                    if function_name == "single_line_styled_text_id" {
                        properties.insert("wrapping".to_string(), "None".to_string());
                    }
                    if function_name == "sized_styled_text_id" && args.len() >= 5 {
                        if let Some(width) = first_number_text(&args[3]) {
                            properties.insert("width".to_string(), width);
                        }
                        if let Some(height) = first_number_text(&args[4]) {
                            properties.insert("height".to_string(), height);
                        }
                    }
                    insert_synthetic_rust_fact(
                        facts,
                        CodeFact {
                            source: "rust-ui-synthetic".to_string(),
                            file: relative.to_string(),
                            line,
                            id,
                            kind: "text".to_string(),
                            properties,
                        },
                    );
                }
            }
            search_start = call_end.saturating_add(1);
        }
    }
}

fn add_rust_settings_link_button_facts(
    relative: &str,
    text: &str,
    facts: &mut BTreeMap<String, CodeFact>,
) {
    let needle = "settings_link_button(";
    let mut search_start = 0;
    while let Some(offset) = text[search_start..].find(needle) {
        let call_start = search_start + offset;
        let argument_start = call_start + needle.len();
        let Some((arguments, call_end)) = read_balanced_argument(text, argument_start) else {
            break;
        };
        let args = split_top_level_arguments(&arguments);
        if let Some(link) = args.first().and_then(|value| rust_settings_link(value)) {
            let (id, width, height, text_style, font_size) = settings_link_contract(link);
            insert_synthetic_rust_fact(
                facts,
                CodeFact {
                    source: "rust-ui-synthetic".to_string(),
                    file: relative.to_string(),
                    line: line_number(text, call_start),
                    id: id.to_string(),
                    kind: "button".to_string(),
                    properties: BTreeMap::from([
                        ("kind".to_string(), "Link".to_string()),
                        ("width".to_string(), width.to_string()),
                        ("height".to_string(), height.to_string()),
                        ("text_style".to_string(), text_style.to_string()),
                        ("font_size".to_string(), font_size.to_string()),
                    ]),
                },
            );
        }
        search_start = call_end.saturating_add(1);
    }
}

fn add_rust_no_config_service_row_facts(
    relative: &str,
    text: &str,
    facts: &mut BTreeMap<String, CodeFact>,
) {
    let needle = "no_config_service_row(";
    let mut search_start = 0;
    while let Some(offset) = text[search_start..].find(needle) {
        let call_start = search_start + offset;
        let argument_start = call_start + needle.len();
        let Some((arguments, call_end)) = read_balanced_argument(text, argument_start) else {
            break;
        };
        let args = split_top_level_arguments(&arguments);
        if let Some(id) = args.first().and_then(|value| rust_string_literal(value)) {
            insert_synthetic_rust_fact(
                facts,
                CodeFact {
                    source: "rust-ui-synthetic".to_string(),
                    file: relative.to_string(),
                    line: line_number(text, call_start),
                    id,
                    kind: "row".to_string(),
                    properties: BTreeMap::from([
                        ("spacing".to_string(), "6".to_string()),
                        ("align_y".to_string(), "Center".to_string()),
                    ]),
                },
            );
        }
        search_start = call_end.saturating_add(1);
    }
}

fn rust_settings_link(value: &str) -> Option<&'static str> {
    Regex::new(r#"SettingsLink::([A-Za-z0-9_]+)"#)
        .expect("valid settings link regex")
        .captures(value)
        .and_then(|capture| capture.get(1))
        .and_then(|match_| match match_.as_str() {
            "GitHubRepository" => Some("GitHubRepository"),
            "IssueFeedback" => Some("IssueFeedback"),
            "EasydictForMacOS" => Some("EasydictForMacOS"),
            _ => None,
        })
}

fn settings_link_contract(
    link: &str,
) -> (
    &'static str,
    &'static str,
    &'static str,
    &'static str,
    &'static str,
) {
    match link {
        "GitHubRepository" => ("GitHubRepositoryLink", "116", "21", "none", "14"),
        "IssueFeedback" => ("IssueFeedbackLink", "94", "21", "none", "14"),
        "EasydictForMacOS" => ("InspiredByLink", "106", "18", "Caption", "12"),
        _ => ("", "", "", "none", "14"),
    }
}

fn add_rust_services_section_header_facts(
    relative: &str,
    text: &str,
    facts: &mut BTreeMap<String, CodeFact>,
) {
    let needle = "services_section_header(";
    let mut search_start = 0;
    while let Some(offset) = text[search_start..].find(needle) {
        let call_start = search_start + offset;
        let argument_start = call_start + needle.len();
        let Some((arguments, call_end)) = read_balanced_argument(text, argument_start) else {
            break;
        };
        let args = split_top_level_arguments(&arguments);
        if args.len() >= 3 {
            let line = line_number(text, call_start);
            if let Some(title_id) = args.get(1).and_then(|value| rust_string_literal(value)) {
                insert_synthetic_rust_fact(
                    facts,
                    CodeFact {
                        source: "rust-ui-synthetic".to_string(),
                        file: relative.to_string(),
                        line,
                        id: title_id,
                        kind: "text".to_string(),
                        properties: BTreeMap::from([
                            ("text_style".to_string(), "SectionTitle".to_string()),
                            ("font_size".to_string(), "18".to_string()),
                        ]),
                    },
                );
            }
            if let Some(help_id) = args.get(2).and_then(|value| rust_string_literal(value)) {
                insert_synthetic_rust_fact(
                    facts,
                    CodeFact {
                        source: "rust-ui-synthetic".to_string(),
                        file: relative.to_string(),
                        line,
                        id: help_id,
                        kind: "icon_button".to_string(),
                        properties: BTreeMap::from([
                            ("font_size".to_string(), "14".to_string()),
                            ("align_y".to_string(), "Center".to_string()),
                        ]),
                    },
                );
            }
        }
        search_start = call_end.saturating_add(1);
    }
}

fn add_rust_llm_provider_descriptor_facts(
    relative: &str,
    text: &str,
    facts: &mut BTreeMap<String, CodeFact>,
) {
    let descriptor_regex = Regex::new(r#"(?s)LlmProviderDescriptor\s*\{(?P<body>.*?)\n\s*\},"#)
        .expect("valid LLM provider descriptor regex");
    for descriptor in descriptor_regex.captures_iter(text) {
        let Some(match_) = descriptor.get(0) else {
            continue;
        };
        let body = descriptor
            .name("body")
            .map(|value| value.as_str())
            .unwrap_or_default();
        let line = line_number(text, match_.start());
        let Some(service_id) = rust_string_field(body, "service_id") else {
            continue;
        };

        if let Some(expander_id) = rust_string_field(body, "expander_id") {
            insert_service_expander_fact(relative, line, expander_id, facts);
        }

        if let Some(status_id) = rust_string_field(body, "status_id") {
            insert_service_status_fact(relative, line, status_id, facts);
        }

        if let Some(key_header_id) = rust_string_field(body, "key_header_id") {
            insert_synthetic_rust_fact(
                facts,
                CodeFact {
                    source: "rust-ui-synthetic".to_string(),
                    file: relative.to_string(),
                    line,
                    id: key_header_id,
                    kind: "text".to_string(),
                    properties: BTreeMap::from([
                        ("text_style".to_string(), "Body".to_string()),
                        ("font_size".to_string(), "14".to_string()),
                    ]),
                },
            );
        }

        if let Some(key_box_id) = rust_string_field(body, "key_box_id") {
            insert_synthetic_rust_fact(
                facts,
                CodeFact {
                    source: "rust-ui-synthetic".to_string(),
                    file: relative.to_string(),
                    line,
                    id: key_box_id,
                    kind: "text_editor".to_string(),
                    properties: BTreeMap::from([
                        ("width".to_string(), "350".to_string()),
                        (
                            "padding".to_string(),
                            "Edges { top: 5, right: 40, bottom: 5, left: 12 }".to_string(),
                        ),
                        ("secure".to_string(), "true".to_string()),
                        ("max_height".to_string(), "36".to_string()),
                        ("align_x".to_string(), "Stretch".to_string()),
                    ]),
                },
            );
        }

        if let Some(key_reveal_id) = rust_string_field(body, "key_reveal_id") {
            insert_synthetic_rust_fact(
                facts,
                CodeFact {
                    source: "rust-ui-synthetic".to_string(),
                    file: relative.to_string(),
                    line,
                    id: key_reveal_id,
                    kind: "trailing_icon".to_string(),
                    properties: BTreeMap::from([
                        ("width".to_string(), "28".to_string()),
                        ("height".to_string(), "28".to_string()),
                        ("margin".to_string(), "0,0,6,0".to_string()),
                        ("align_x".to_string(), "Right".to_string()),
                        ("align_y".to_string(), "Center".to_string()),
                    ]),
                },
            );
        }

        if let Some(endpoint_box_id) = rust_option_string_field(body, "endpoint_box_id") {
            insert_synthetic_rust_fact(
                facts,
                CodeFact {
                    source: "rust-ui-synthetic".to_string(),
                    file: relative.to_string(),
                    line,
                    id: endpoint_box_id,
                    kind: "text_editor".to_string(),
                    properties: BTreeMap::from([
                        ("width".to_string(), "450".to_string()),
                        ("max_height".to_string(), "36".to_string()),
                        ("align_x".to_string(), "Left".to_string()),
                    ]),
                },
            );
        }

        if let Some(model_box_id) = rust_string_field(body, "model_box_id") {
            let model_width = provider_descriptor_model_width(&service_id);
            let model_kind = if matches!(service_id.as_str(), "custom-openai" | "doubao") {
                "text_editor"
            } else {
                "combo_box"
            };
            insert_synthetic_rust_fact(
                facts,
                CodeFact {
                    source: "rust-ui-synthetic".to_string(),
                    file: relative.to_string(),
                    line,
                    id: model_box_id,
                    kind: model_kind.to_string(),
                    properties: BTreeMap::from([
                        ("width".to_string(), model_width.to_string()),
                        ("align_x".to_string(), "Left".to_string()),
                    ]),
                },
            );
        }

        if let Some(test_button_id) = rust_string_field(body, "test_button_id") {
            insert_synthetic_rust_fact(
                facts,
                CodeFact {
                    source: "rust-ui-synthetic".to_string(),
                    file: relative.to_string(),
                    line,
                    id: test_button_id,
                    kind: "button".to_string(),
                    properties: BTreeMap::from([
                        ("height".to_string(), "29".to_string()),
                        ("padding".to_string(), "8,4".to_string()),
                    ]),
                },
            );
        }
    }
}

fn add_rust_local_ai_action_control_facts(relative: &str, facts: &mut BTreeMap<String, CodeFact>) {
    for id in [
        "WindowsLocalAIPrepareButton",
        "FoundryLocalStartButton",
        "FoundryLocalInstallLink",
        "OpenVinoDownloadButton",
    ] {
        let line = facts.get(id).map(|fact| fact.line).unwrap_or(1);
        insert_synthetic_rust_fact(
            facts,
            CodeFact {
                source: "rust-ui-synthetic".to_string(),
                file: relative.to_string(),
                line,
                id: id.to_string(),
                kind: "button".to_string(),
                properties: BTreeMap::from([("align_x".to_string(), "Left".to_string())]),
            },
        );
    }

    let line = facts
        .get("WindowsLocalAIWindowsUpdateLink")
        .map(|fact| fact.line)
        .unwrap_or(1);
    insert_synthetic_rust_fact(
        facts,
        CodeFact {
            source: "rust-ui-synthetic".to_string(),
            file: relative.to_string(),
            line,
            id: "WindowsLocalAIWindowsUpdateLink".to_string(),
            kind: "button".to_string(),
            properties: BTreeMap::from([
                ("align_x".to_string(), "Left".to_string()),
                ("font_size".to_string(), "12".to_string()),
                ("min_height".to_string(), "0".to_string()),
            ]),
        },
    );
}

fn add_rust_settings_services_row_context_facts(
    relative: &str,
    text: &str,
    facts: &mut BTreeMap<String, CodeFact>,
) {
    if text.contains("\"ImportedMdxSummaryText\"")
        && text.contains(".id(\"settings.services.mdx\")")
        && text.contains(".align(Alignment::Center)")
    {
        insert_synthetic_properties(
            relative,
            facts,
            "ImportedMdxSummaryText",
            "text",
            BTreeMap::from([("align_y".to_string(), "Center".to_string())]),
        );
    }

    if text.contains("\"EnableInternationalServicesHeaderText\"")
        && text.contains(".id(\"settings.services.international.header\")")
        && text.contains(".align(Alignment::Center)")
    {
        insert_synthetic_properties(
            relative,
            facts,
            "EnableInternationalServicesHeaderText",
            "text",
            BTreeMap::from([("align_y".to_string(), "Center".to_string())]),
        );
        insert_synthetic_properties(
            relative,
            facts,
            "EnableInternationalServicesToggle",
            "toggle_switch",
            BTreeMap::from([
                ("align_y".to_string(), "Center".to_string()),
                ("grid_column".to_string(), "1".to_string()),
                ("min_width".to_string(), "0".to_string()),
            ]),
        );
    }

    if text.contains("\"RefreshOllamaButton\"")
        && text.contains("\"TestOllamaButton\"")
        && text.contains(".align(Alignment::End)")
    {
        for id in ["RefreshOllamaButton", "TestOllamaButton"] {
            insert_synthetic_properties(
                relative,
                facts,
                id,
                "button",
                BTreeMap::from([("align_y".to_string(), "End".to_string())]),
            );
        }
    }
}

fn add_rust_settings_about_row_context_facts(
    relative: &str,
    text: &str,
    facts: &mut BTreeMap<String, CodeFact>,
) {
    if text.contains("\"AboutInspiredByText\"")
        && text.contains(".id(\"settings.about.inspired_by\")")
        && text.contains(".align(Alignment::Center)")
    {
        insert_synthetic_properties(
            relative,
            facts,
            "AboutInspiredByText",
            "text",
            BTreeMap::from([("align_y".to_string(), "Center".to_string())]),
        );
    }
}

fn add_rust_main_action_bar_context_facts(
    relative: &str,
    text: &str,
    facts: &mut BTreeMap<String, CodeFact>,
) {
    if text.contains("source_text_card(state)")
        && text.contains("main_translate_action_bar(state)")
        && text.contains("main_results_card(state)")
    {
        for id in ["ActionBarWide", "ActionBarNarrow"] {
            insert_synthetic_properties(
                relative,
                facts,
                id,
                "layout",
                BTreeMap::from([("grid_row".to_string(), "1".to_string())]),
            );
        }
    }

    if text.contains("fn main_translate_button(") {
        insert_synthetic_properties(
            relative,
            facts,
            "TranslateButton",
            "button",
            BTreeMap::from([
                ("grid_column".to_string(), "4".to_string()),
                ("align_y".to_string(), "Center".to_string()),
            ]),
        );
        insert_synthetic_properties(
            relative,
            facts,
            "TranslateButtonNarrow",
            "button",
            BTreeMap::from([("align_x".to_string(), "Center".to_string())]),
        );
    }
}

fn add_rust_settings_general_context_facts(
    relative: &str,
    text: &str,
    facts: &mut BTreeMap<String, CodeFact>,
) {
    if text.contains("\"AppThemeCombo\"")
        && text.contains(".id(\"settings.general.theme\")")
        && text.contains(".align(Alignment::Start)")
    {
        insert_synthetic_properties(
            relative,
            facts,
            "AppThemeCombo",
            "combo_box",
            BTreeMap::from([("align_x".to_string(), "Left".to_string())]),
        );
    }
}

fn add_rust_settings_loading_overlay_context_facts(
    relative: &str,
    text: &str,
    facts: &mut BTreeMap<String, CodeFact>,
) {
    if text.contains("OverlayLayer::new(settings_loading_indicator())")
        && text.contains(".id(\"LoadingOverlay\")")
    {
        insert_synthetic_properties(
            relative,
            facts,
            "LoadingOverlay",
            "layout",
            BTreeMap::from([
                ("align_x".to_string(), "Center".to_string()),
                ("align_y".to_string(), "Center".to_string()),
            ]),
        );
    }
}

fn add_rust_settings_advanced_context_facts(
    relative: &str,
    text: &str,
    facts: &mut BTreeMap<String, CodeFact>,
) {
    if text.contains("\"AdvancedTabContent\"") {
        for id in [
            "OcrEndpointBox",
            "OcrModelBox",
            "OcrSystemPromptBox",
            "OcrTestStatusBox",
            "FormulaFontPatternBox",
            "FormulaCharPatternBox",
            "CustomPromptBox",
            "ProxyUriBox",
        ] {
            insert_synthetic_properties(
                relative,
                facts,
                id,
                "text_editor",
                BTreeMap::from([("align_x".to_string(), "Left".to_string())]),
            );
        }
    }

    if text.contains("\"CacheStatusText\"")
        && text.contains("\"TranslationCacheActionRow\"")
        && text.contains(".align(Alignment::Center)")
    {
        insert_synthetic_properties(
            relative,
            facts,
            "CacheStatusText",
            "text",
            BTreeMap::from([("align_y".to_string(), "Center".to_string())]),
        );
    }
}

fn add_rust_settings_advanced_section_facts(
    relative: &str,
    text: &str,
    facts: &mut BTreeMap<String, CodeFact>,
) {
    let needle = "settings_advanced_section(";
    let mut search_start = 0;
    while let Some(offset) = text[search_start..].find(needle) {
        let call_start = search_start + offset;
        let argument_start = call_start + needle.len();
        let Some((arguments, call_end)) = read_balanced_argument(text, argument_start) else {
            break;
        };
        let args = split_top_level_arguments(&arguments);
        let line = line_number(text, call_start);
        if args.len() >= 5 {
            if let Some(section_id) = args.first().and_then(|value| rust_string_literal(value)) {
                insert_synthetic_rust_fact(
                    facts,
                    CodeFact {
                        source: "rust-ui-synthetic".to_string(),
                        file: relative.to_string(),
                        line,
                        id: section_id,
                        kind: "layout".to_string(),
                        properties: BTreeMap::from([
                            ("spacing".to_string(), "12".to_string()),
                            ("width".to_string(), "Fill".to_string()),
                        ]),
                    },
                );
            }
            if let Some(header_id) = args.get(1).and_then(|value| rust_string_literal(value)) {
                insert_synthetic_rust_fact(
                    facts,
                    CodeFact {
                        source: "rust-ui-synthetic".to_string(),
                        file: relative.to_string(),
                        line,
                        id: header_id,
                        kind: "text".to_string(),
                        properties: BTreeMap::from([
                            ("text_style".to_string(), "SectionTitle".to_string()),
                            ("font_size".to_string(), "18".to_string()),
                        ]),
                    },
                );
            }
            if let Some(description_id) = advanced_section_description_id(&args[3]) {
                insert_synthetic_rust_fact(
                    facts,
                    CodeFact {
                        source: "rust-ui-synthetic".to_string(),
                        file: relative.to_string(),
                        line,
                        id: description_id,
                        kind: "text".to_string(),
                        properties: BTreeMap::from([
                            ("text_style".to_string(), "Caption".to_string()),
                            ("font_size".to_string(), "12".to_string()),
                        ]),
                    },
                );
            }
        }
        search_start = call_end.saturating_add(1);
    }
}

fn advanced_section_description_id(argument: &str) -> Option<String> {
    let trimmed = argument.trim();
    if trimmed == "None" {
        return None;
    }
    Regex::new(r#""([^"]+)""#)
        .expect("valid description id regex")
        .captures(trimmed)
        .map(|capture| capture[1].to_string())
}

fn add_rust_settings_language_context_facts(
    relative: &str,
    text: &str,
    facts: &mut BTreeMap<String, CodeFact>,
) {
    if text.contains("\"settings.language.translation_languages\"")
        && text.contains("ToggleTranslationLanguagesExpanded")
    {
        let line = facts
            .get("settings.language.translation_languages")
            .map(|fact| fact.line)
            .unwrap_or(1);
        insert_synthetic_rust_fact(
            facts,
            CodeFact {
                source: "rust-ui-synthetic".to_string(),
                file: relative.to_string(),
                line,
                id: "AvailableLanguagesExpander".to_string(),
                kind: "expander".to_string(),
                properties: BTreeMap::from([
                    ("align_x".to_string(), "Stretch".to_string()),
                    ("content_align_x".to_string(), "Stretch".to_string()),
                ]),
            },
        );
    }

    if text.contains("\"FirstLanguageCombo\"")
        && text.contains("\"settings.language.first\"")
        && text.contains(".align(Alignment::Start)")
    {
        insert_synthetic_properties(
            relative,
            facts,
            "FirstLanguageCombo",
            "combo_box",
            BTreeMap::from([("align_x".to_string(), "Left".to_string())]),
        );
    }

    if text.contains("\"LanguagePreferencesDescriptionText\"")
        && text.contains("\"LanguagePreferencesSection\"")
    {
        insert_synthetic_properties(
            relative,
            facts,
            "LanguagePreferencesDescriptionText",
            "text",
            BTreeMap::from([("margin".to_string(), "0,4,0,0".to_string())]),
        );
    }
}

fn add_rust_floating_window_context_facts(
    relative: &str,
    text: &str,
    facts: &mut BTreeMap<String, CodeFact>,
) {
    if text.contains("fn floating_translate_view(") {
        for (prefix, content_spacing) in [("mini", "4"), ("fixed", "6")] {
            insert_synthetic_properties(
                relative,
                facts,
                &format!("{prefix}.content"),
                "layout",
                BTreeMap::from([
                    ("padding".to_string(), "16".to_string()),
                    ("spacing".to_string(), content_spacing.to_string()),
                    ("width".to_string(), "Fill".to_string()),
                    ("height".to_string(), "Fill".to_string()),
                    ("border_width".to_string(), "0".to_string()),
                    ("radius".to_string(), "0".to_string()),
                ]),
            );
            insert_synthetic_properties(
                relative,
                facts,
                &format!("{prefix}.source_language"),
                "combo_box",
                BTreeMap::from([
                    ("grid_column".to_string(), "0".to_string()),
                    ("align_x".to_string(), "Stretch".to_string()),
                ]),
            );
            insert_synthetic_properties(
                relative,
                facts,
                &format!("{prefix}.target_language"),
                "combo_box",
                BTreeMap::from([
                    ("grid_column".to_string(), "2".to_string()),
                    ("align_x".to_string(), "Stretch".to_string()),
                ]),
            );
            insert_synthetic_properties(
                relative,
                facts,
                &format!("{prefix}.header"),
                "layout",
                BTreeMap::from([
                    ("grid_row".to_string(), "0".to_string()),
                    ("margin".to_string(), "0,0,0,4".to_string()),
                    ("align_y".to_string(), "Center".to_string()),
                ]),
            );
            insert_synthetic_properties(
                relative,
                facts,
                &format!("{prefix}.input_card"),
                "card",
                BTreeMap::from([
                    ("grid_row".to_string(), "1".to_string()),
                    ("padding".to_string(), "12,10".to_string()),
                    ("margin".to_string(), "0,2,0,6".to_string()),
                    ("radius".to_string(), "18".to_string()),
                ]),
            );
            insert_synthetic_properties(
                relative,
                facts,
                &format!("{prefix}.swap"),
                "button",
                BTreeMap::from([
                    ("grid_column".to_string(), "1".to_string()),
                    ("width".to_string(), "28".to_string()),
                    ("height".to_string(), "28".to_string()),
                    ("padding".to_string(), "0".to_string()),
                    ("font_size".to_string(), "12".to_string()),
                    ("margin".to_string(), "4,0".to_string()),
                ]),
            );
            insert_synthetic_properties(
                relative,
                facts,
                &format!("{prefix}.translate"),
                "button",
                BTreeMap::from([
                    ("grid_column".to_string(), "3".to_string()),
                    ("width".to_string(), "32".to_string()),
                    ("height".to_string(), "32".to_string()),
                    ("padding".to_string(), "0".to_string()),
                    ("font_size".to_string(), "14".to_string()),
                    ("margin".to_string(), "4,0,0,0".to_string()),
                ]),
            );
            insert_synthetic_properties(
                relative,
                facts,
                &format!("{prefix}.status"),
                "text",
                BTreeMap::from([
                    ("grid_row".to_string(), "5".to_string()),
                    ("font_size".to_string(), "10".to_string()),
                    ("align_x".to_string(), "Right".to_string()),
                    ("margin".to_string(), "0,4,0,0".to_string()),
                ]),
            );
        }

        insert_synthetic_properties(
            relative,
            facts,
            "mini.play_source",
            "button",
            BTreeMap::from([
                ("grid_column".to_string(), "1".to_string()),
                ("width".to_string(), "28".to_string()),
                ("height".to_string(), "28".to_string()),
                ("padding".to_string(), "0".to_string()),
                ("font_size".to_string(), "14".to_string()),
                ("margin".to_string(), "4,0,0,0".to_string()),
                ("align_x".to_string(), "Right".to_string()),
                ("align_y".to_string(), "Top".to_string()),
            ]),
        );
        insert_synthetic_properties(
            relative,
            facts,
            "mini.pin",
            "button",
            BTreeMap::from([
                ("grid_column".to_string(), "0".to_string()),
                ("width".to_string(), "28".to_string()),
                ("height".to_string(), "28".to_string()),
                ("padding".to_string(), "0".to_string()),
                ("font_size".to_string(), "14".to_string()),
                ("border_width".to_string(), "0".to_string()),
                ("radius".to_string(), "10".to_string()),
            ]),
        );
    }

    if text.contains("\"StatusIndicator\"") {
        insert_synthetic_properties(
            relative,
            facts,
            "StatusIndicator",
            "status_badge",
            BTreeMap::from([
                ("font_size".to_string(), "12".to_string()),
                ("align_y".to_string(), "Center".to_string()),
                ("padding".to_string(), "12,6".to_string()),
                ("margin".to_string(), "0,0,8,0".to_string()),
            ]),
        );
    }

    if text.contains("\"pop-button.translate\"") {
        insert_synthetic_properties(
            relative,
            facts,
            "pop-button.translate",
            "button",
            BTreeMap::from([
                ("border_width".to_string(), "0".to_string()),
                ("radius".to_string(), "10".to_string()),
                ("align_x".to_string(), "Center".to_string()),
                ("align_y".to_string(), "Center".to_string()),
            ]),
        );
    }

    if text.contains("\"MiniWindowCloseButton\"") {
        insert_synthetic_properties(
            relative,
            facts,
            "MiniWindowCloseButton",
            "button",
            BTreeMap::from([
                ("grid_column".to_string(), "2".to_string()),
                ("font_size".to_string(), "14".to_string()),
            ]),
        );
    }

    if text.contains("\"CloseButton\"") {
        insert_synthetic_properties(
            relative,
            facts,
            "CloseButton",
            "button",
            BTreeMap::from([
                ("grid_column".to_string(), "1".to_string()),
                ("font_size".to_string(), "14".to_string()),
            ]),
        );
    }

    if text.contains("\"DetectedLangText\"") {
        insert_synthetic_properties(
            relative,
            facts,
            "DetectedLangText",
            "text",
            BTreeMap::from([
                ("grid_row".to_string(), "3".to_string()),
                ("margin".to_string(), "0,0,0,2".to_string()),
            ]),
        );
    }
}

fn add_rust_main_input_context_facts(
    relative: &str,
    text: &str,
    facts: &mut BTreeMap<String, CodeFact>,
) {
    if text.contains("\"DetectedLanguageText\"") {
        insert_synthetic_properties(
            relative,
            facts,
            "DetectedLanguageText",
            "text",
            BTreeMap::from([
                ("grid_row".to_string(), "1".to_string()),
                ("margin".to_string(), "4,0,0,0".to_string()),
                ("align_y".to_string(), "Top".to_string()),
            ]),
        );
    }

    if text.contains("\"InputTextContainer\"") {
        insert_synthetic_properties(
            relative,
            facts,
            "InputTextContainer",
            "layout",
            BTreeMap::from([
                ("grid_row".to_string(), "2".to_string()),
                ("border_width".to_string(), "0".to_string()),
                ("radius".to_string(), "0".to_string()),
            ]),
        );
    }

    if text.contains("\"SuggestionPopupBorder\"")
        && text.contains("\"surface-card border rounded-[10px]\"")
    {
        insert_synthetic_properties(
            relative,
            facts,
            "SuggestionPopupBorder",
            "layout",
            BTreeMap::from([
                ("min_width".to_string(), "180".to_string()),
                ("border_width".to_string(), "1".to_string()),
                ("radius".to_string(), "10".to_string()),
            ]),
        );
    }

    if text.contains("\"InputHelpIcon\"") {
        insert_synthetic_properties(
            relative,
            facts,
            "InputHelpIcon",
            "text",
            BTreeMap::from([
                ("grid_column".to_string(), "1".to_string()),
                ("margin".to_string(), "4,0,0,0".to_string()),
                ("align_y".to_string(), "Center".to_string()),
            ]),
        );
    }

    if text.contains("\"LangHelpIcon\"") {
        insert_synthetic_properties(
            relative,
            facts,
            "LangHelpIcon",
            "text",
            BTreeMap::from([
                ("font_size".to_string(), "14".to_string()),
                ("grid_column".to_string(), "3".to_string()),
                ("margin".to_string(), "6,0,0,0".to_string()),
                ("align_y".to_string(), "Center".to_string()),
            ]),
        );
    }

    if text.contains("\"LangHelpIconNarrow\"") {
        insert_synthetic_properties(
            relative,
            facts,
            "LangHelpIconNarrow",
            "text",
            BTreeMap::from([
                ("font_size".to_string(), "14".to_string()),
                ("grid_column".to_string(), "3".to_string()),
                ("margin".to_string(), "4,0,0,0".to_string()),
                ("align_y".to_string(), "Center".to_string()),
            ]),
        );
    }
}

fn add_rust_main_quick_content_context_facts(
    relative: &str,
    text: &str,
    facts: &mut BTreeMap<String, CodeFact>,
) {
    if text.contains("\"QuickTranslateContent\"") {
        insert_synthetic_properties(
            relative,
            facts,
            "QuickTranslateContent",
            "scroll_view",
            BTreeMap::from([("grid_row".to_string(), "1".to_string())]),
        );
    }

    if text.contains("\"QuickInputCard\"") {
        insert_synthetic_properties(
            relative,
            facts,
            "QuickInputCard",
            "card",
            BTreeMap::from([("grid_row".to_string(), "0".to_string())]),
        );
    }

    if text.contains("\"QuickOutputCard\"") {
        insert_synthetic_properties(
            relative,
            facts,
            "QuickOutputCard",
            "card",
            BTreeMap::from([("grid_row".to_string(), "2".to_string())]),
        );
    }

    if text.contains("\"main.quick.play_source\"") {
        insert_synthetic_properties(
            relative,
            facts,
            "main.quick.play_source",
            "button",
            BTreeMap::from([
                ("grid_column".to_string(), "2".to_string()),
                ("align_x".to_string(), "Right".to_string()),
                ("align_y".to_string(), "Center".to_string()),
            ]),
        );
    }
}

fn add_rust_long_doc_control_bar_context_facts(
    relative: &str,
    text: &str,
    facts: &mut BTreeMap<String, CodeFact>,
) {
    if !text.contains("\"main.long-doc.control_bar\"")
        || !text.contains("Length::FillPortion(2)")
        || !text.contains("Length::FillPortion(3)")
        || !text.contains("Length::Fixed(110)")
    {
        return;
    }

    insert_synthetic_properties(
        relative,
        facts,
        "main.long-doc.control_bar",
        "layout",
        BTreeMap::from([
            ("grid_row".to_string(), "1".to_string()),
            ("column_spacing".to_string(), "8".to_string()),
            ("row_spacing".to_string(), "4".to_string()),
            ("margin".to_string(), "0,4,0,4".to_string()),
        ]),
    );

    for (id, row, column, span, align_x, align_y) in [
        (
            "main.long-doc.source_language",
            "0",
            "0",
            None,
            "Stretch",
            None,
        ),
        (
            "main.long-doc.target_language",
            "0",
            "1",
            None,
            "Stretch",
            None,
        ),
        (
            "main.long-doc.service",
            "0",
            "2",
            Some("2"),
            "Stretch",
            None,
        ),
        ("main.long-doc.input_mode", "1", "0", None, "Stretch", None),
        ("main.long-doc.output_mode", "1", "1", None, "Stretch", None),
        ("main.long-doc.concurrency", "1", "2", None, "Stretch", None),
        ("main.long-doc.page_range", "1", "3", None, "Stretch", None),
        (
            "main.long-doc.two_pass",
            "2",
            "0",
            Some("3"),
            "Stretch",
            None,
        ),
        (
            "main.long-doc.translate",
            "2",
            "3",
            None,
            "Right",
            Some("Center"),
        ),
    ] {
        let mut properties = BTreeMap::from([
            ("grid_row".to_string(), row.to_string()),
            ("grid_column".to_string(), column.to_string()),
            ("align_x".to_string(), align_x.to_string()),
        ]);
        if let Some(span) = span {
            properties.insert("grid_column_span".to_string(), span.to_string());
        }
        if let Some(align_y) = align_y {
            properties.insert("align_y".to_string(), align_y.to_string());
        }
        if id == "main.long-doc.page_range" {
            properties.insert("min_width".to_string(), "110".to_string());
        }
        if id == "main.long-doc.translate" && text.contains(".font_size(16)") {
            properties.insert("font_size".to_string(), "16".to_string());
        }
        insert_synthetic_properties(relative, facts, id, "layout", properties);
    }
}

fn add_rust_long_doc_card_context_facts(
    relative: &str,
    text: &str,
    facts: &mut BTreeMap<String, CodeFact>,
) {
    if text.contains("long_document_card_shell(") && text.contains("\"main.long-doc.input_card\"") {
        insert_synthetic_properties(
            relative,
            facts,
            "main.long-doc.input_card",
            "layout",
            BTreeMap::from([
                ("grid_row".to_string(), "0".to_string()),
                ("margin".to_string(), "0,0,0,2".to_string()),
            ]),
        );
    }

    if text.contains("\"LongDocInputTitle\"") {
        insert_synthetic_properties(
            relative,
            facts,
            "LongDocInputTitle",
            "text",
            BTreeMap::from([("grid_row".to_string(), "0".to_string())]),
        );
    }

    if text.contains("\"LongDocFilePanel\"") {
        insert_synthetic_properties(
            relative,
            facts,
            "LongDocFilePanel",
            "layout",
            BTreeMap::from([
                ("grid_row".to_string(), "1".to_string()),
                ("align_y".to_string(), "Top".to_string()),
            ]),
        );
    }

    if text.contains("\"LongDocFilePathDisplay\"") {
        insert_synthetic_properties(
            relative,
            facts,
            "LongDocFilePathDisplay",
            "text",
            BTreeMap::from([("grid_column".to_string(), "0".to_string())]),
        );
    }

    if text.contains("\"main.long-doc.browse\"") {
        insert_synthetic_properties(
            relative,
            facts,
            "main.long-doc.browse",
            "button",
            BTreeMap::from([("grid_column".to_string(), "1".to_string())]),
        );
    }

    if text.contains("long_document_card_shell(") && text.contains("\"main.long-doc.output_card\"")
    {
        insert_synthetic_properties(
            relative,
            facts,
            "main.long-doc.output_card",
            "layout",
            BTreeMap::from([
                ("grid_row".to_string(), "2".to_string()),
                ("margin".to_string(), "0,2,0,0".to_string()),
            ]),
        );
    }

    if text.contains("\"LongDocOutputTitle\"") {
        insert_synthetic_properties(
            relative,
            facts,
            "LongDocOutputTitle",
            "text",
            BTreeMap::from([("grid_column".to_string(), "0".to_string())]),
        );
    }

    if text.contains("\"main.long-doc.retry\"") {
        insert_synthetic_properties(
            relative,
            facts,
            "main.long-doc.retry",
            "button",
            BTreeMap::from([("grid_column".to_string(), "1".to_string())]),
        );
    }

    if text.contains("\"main.long-doc.output_content\"") {
        insert_synthetic_properties(
            relative,
            facts,
            "main.long-doc.output_content",
            "layout",
            BTreeMap::from([
                ("grid_row".to_string(), "1".to_string()),
                ("align_y".to_string(), "Top".to_string()),
            ]),
        );
    }

    if text.contains("\"main.long-doc.scroll\"") {
        insert_synthetic_properties(
            relative,
            facts,
            "main.long-doc.scroll",
            "scroll_view",
            BTreeMap::from([("grid_row".to_string(), "1".to_string())]),
        );
    }

    if text.contains("\"LongDocStatusText\"") {
        insert_synthetic_properties(
            relative,
            facts,
            "LongDocStatusText",
            "text",
            BTreeMap::from([("grid_row".to_string(), "3".to_string())]),
        );
    }

    if text.contains("\"main.long-doc.history\"") {
        insert_synthetic_properties(
            relative,
            facts,
            "main.long-doc.history",
            "settings_row",
            BTreeMap::from([
                ("grid_row".to_string(), "4".to_string()),
                ("margin".to_string(), "0,8,0,0".to_string()),
                ("align_x".to_string(), "Stretch".to_string()),
                ("content_align_x".to_string(), "Stretch".to_string()),
            ]),
        );
    }

    if text.contains("\"main.long-doc.history_list\"") {
        insert_synthetic_properties(
            relative,
            facts,
            "main.long-doc.history_list",
            "result_list",
            BTreeMap::from([
                ("max_height".to_string(), "200".to_string()),
                ("padding".to_string(), "Edges::ZERO".to_string()),
                ("border_width".to_string(), "0".to_string()),
            ]),
        );
    }

    if text.contains("\"main.long-doc.clear_history\"") {
        insert_synthetic_properties(
            relative,
            facts,
            "main.long-doc.clear_history",
            "button",
            BTreeMap::from([("grid_column".to_string(), "1".to_string())]),
        );
    }

    if text.contains("\"main.long-doc.output_folder\"") {
        insert_synthetic_properties(
            relative,
            facts,
            "main.long-doc.output_folder",
            "text",
            BTreeMap::from([
                ("grid_column".to_string(), "0".to_string()),
                ("align_y".to_string(), "Center".to_string()),
            ]),
        );
    }

    if text.contains("\"main.long-doc.output_browse\"") {
        insert_synthetic_properties(
            relative,
            facts,
            "main.long-doc.output_browse",
            "button",
            BTreeMap::from([("grid_column".to_string(), "1".to_string())]),
        );
    }

    if text.contains("\"main.long-doc.page_range\"") {
        insert_synthetic_properties(
            relative,
            facts,
            "main.long-doc.page_range",
            "text_editor",
            BTreeMap::from([
                (
                    "border_width".to_string(),
                    "{ThemeResource EasydictCardBorderThickness}".to_string(),
                ),
                (
                    "radius".to_string(),
                    "{ThemeResource EasydictControlCornerRadius}".to_string(),
                ),
            ]),
        );
    }
}

fn add_rust_long_doc_hint_header_context_facts(
    relative: &str,
    text: &str,
    facts: &mut BTreeMap<String, CodeFact>,
) {
    let needle = "long_document_hint_header(";
    let mut search_start = 0;
    while let Some(offset) = text[search_start..].find(needle) {
        let call_start = search_start + offset;
        let argument_start = call_start + needle.len();
        let Some((arguments, call_end)) = read_balanced_argument(text, argument_start) else {
            break;
        };
        let args = split_top_level_arguments(&arguments);
        if let Some(hint_id) = args
            .get(2)
            .and_then(|value| rust_string_literal(value.trim()))
        {
            insert_synthetic_rust_fact(
                facts,
                CodeFact {
                    source: "rust-ui-synthetic".to_string(),
                    file: relative.to_string(),
                    line: line_number(text, call_start),
                    id: hint_id,
                    kind: "text".to_string(),
                    properties: BTreeMap::from([
                        ("text_style".to_string(), "Caption".to_string()),
                        ("font_size".to_string(), "11".to_string()),
                    ]),
                },
            );
        }
        search_start = call_end.saturating_add(1);
    }
}

fn add_rust_settings_views_context_facts(
    relative: &str,
    text: &str,
    facts: &mut BTreeMap<String, CodeFact>,
) {
    if !text.contains("settings_view_window_results_section")
        && !text.contains("\"FixedWindowReorderModeButton\"")
    {
        return;
    }

    for id in [
        "MainWindowSection",
        "MiniWindowSection",
        "FixedWindowSection",
    ] {
        insert_synthetic_properties(
            relative,
            facts,
            id,
            "layout",
            BTreeMap::from([("spacing".to_string(), "6".to_string())]),
        );
    }

    for id in [
        "MainWindowHeaderText",
        "MiniWindowHeaderText",
        "FixedWindowHeaderText",
    ] {
        insert_synthetic_properties(
            relative,
            facts,
            id,
            "text",
            BTreeMap::from([
                ("font_size".to_string(), "13".to_string()),
                ("align_y".to_string(), "Center".to_string()),
            ]),
        );
    }

    for id in [
        "MainWindowReorderModeButton",
        "MiniWindowReorderModeButton",
        "FixedWindowReorderModeButton",
    ] {
        insert_synthetic_properties(
            relative,
            facts,
            id,
            "button",
            BTreeMap::from([
                ("font_size".to_string(), "11".to_string()),
                ("grid_column".to_string(), "1".to_string()),
                ("min_height".to_string(), "24".to_string()),
                ("padding".to_string(), "6,1".to_string()),
                ("align_y".to_string(), "Center".to_string()),
            ]),
        );
    }
}

fn add_rust_settings_hotkeys_context_facts(
    relative: &str,
    text: &str,
    facts: &mut BTreeMap<String, CodeFact>,
) {
    if text.contains("\"HotkeysHelpIcon\"") && text.contains(".align(Alignment::Center)") {
        insert_synthetic_properties(
            relative,
            facts,
            "HotkeysHelpIcon",
            "button",
            BTreeMap::from([
                ("font_size".to_string(), "14".to_string()),
                ("align_y".to_string(), "Center".to_string()),
            ]),
        );
    }

    let row_sets_box_width = text.contains(".width(Length::Fixed(200))");
    let row_sets_toggle_bottom_margin = text.contains("bottom: 4");
    let row_sets_toggle_bottom_alignment = text.contains(".align_y(Alignment::End)");
    if !text.contains("hotkey_row(")
        || !row_sets_box_width
        || !row_sets_toggle_bottom_margin
        || !row_sets_toggle_bottom_alignment
    {
        return;
    }

    let hotkey_call_regex = Regex::new(
        r#"(?s)hotkey_row\s*\(\s*[^,]+,\s*"[^"]*"\s*,\s*"[^"]*"\s*,\s*"[^"]*"\s*,\s*"(?P<box>[^"]+)"\s*,\s*"(?P<toggle>[^"]+)""#,
    )
    .expect("valid hotkey row regex");
    for capture in hotkey_call_regex.captures_iter(text) {
        let line = line_number(
            text,
            capture.get(0).map(|match_| match_.start()).unwrap_or(0),
        );
        insert_synthetic_rust_fact(
            facts,
            CodeFact {
                source: "rust-ui-synthetic".to_string(),
                file: relative.to_string(),
                line,
                id: capture["box"].to_string(),
                kind: "text_editor".to_string(),
                properties: BTreeMap::from([("width".to_string(), "200".to_string())]),
            },
        );
        insert_synthetic_rust_fact(
            facts,
            CodeFact {
                source: "rust-ui-synthetic".to_string(),
                file: relative.to_string(),
                line,
                id: capture["toggle"].to_string(),
                kind: "toggle_switch".to_string(),
                properties: BTreeMap::from([
                    ("margin".to_string(), "0,0,0,4".to_string()),
                    ("align_y".to_string(), "End".to_string()),
                ]),
            },
        );
    }

    let needle = "hotkey_row(";
    let mut search_start = 0;
    while let Some(offset) = text[search_start..].find(needle) {
        let call_start = search_start + offset;
        let argument_start = call_start + needle.len();
        let Some((arguments, call_end)) = read_balanced_argument(text, argument_start) else {
            break;
        };
        let args = split_top_level_arguments(&arguments);
        if args.len() >= 6 {
            let line = line_number(text, call_start);
            if let Some(box_id) = args.get(4).and_then(|value| rust_string_literal(value)) {
                insert_synthetic_rust_fact(
                    facts,
                    CodeFact {
                        source: "rust-ui-synthetic".to_string(),
                        file: relative.to_string(),
                        line,
                        id: box_id,
                        kind: "text_editor".to_string(),
                        properties: BTreeMap::from([(
                            "width".to_string(),
                            "Length::Fixed(200)".to_string(),
                        )]),
                    },
                );
            }
            if let Some(toggle_id) = args.get(5).and_then(|value| rust_string_literal(value)) {
                insert_synthetic_rust_fact(
                    facts,
                    CodeFact {
                        source: "rust-ui-synthetic".to_string(),
                        file: relative.to_string(),
                        line,
                        id: toggle_id,
                        kind: "toggle_switch".to_string(),
                        properties: BTreeMap::from([
                            ("margin".to_string(), "0,0,0,4".to_string()),
                            ("align_y".to_string(), "End".to_string()),
                        ]),
                    },
                );
            }
        }
        search_start = call_end.saturating_add(1);
    }
}

fn add_rust_settings_shell_context_facts(
    relative: &str,
    text: &str,
    facts: &mut BTreeMap<String, CodeFact>,
) {
    if text.contains("\"settings.content\"")
        && text.contains("max-w-[1040px]")
        && text.contains("mx-auto")
    {
        insert_synthetic_properties(
            relative,
            facts,
            "settings.content",
            "layout",
            BTreeMap::from([
                (
                    "width".to_string(),
                    "{Binding ViewportWidth, ElementName=MainScrollViewer}".to_string(),
                ),
                ("max_width".to_string(), "1040".to_string()),
                ("spacing".to_string(), "24".to_string()),
                ("align_x".to_string(), "Center".to_string()),
            ]),
        );
    }

    if text.contains("\"settings.categories\"") {
        insert_synthetic_properties(
            relative,
            facts,
            "settings.categories",
            "wrap",
            BTreeMap::from([("align_x".to_string(), "Stretch".to_string())]),
        );
    }

    if text.contains("\"settings.services\"") {
        insert_synthetic_properties(
            relative,
            facts,
            "settings.services",
            "layout",
            BTreeMap::from([("spacing".to_string(), "24".to_string())]),
        );
    }

    if text.contains("\"settings.views\"") {
        insert_synthetic_properties(
            relative,
            facts,
            "settings.views",
            "layout",
            BTreeMap::from([("spacing".to_string(), "12".to_string())]),
        );
    }

    if text.contains("\"SettingsHeaderText\"") {
        insert_synthetic_properties(
            relative,
            facts,
            "SettingsHeaderText",
            "text",
            BTreeMap::from([("align_y".to_string(), "Center".to_string())]),
        );
    }

    if text.contains("\"SettingsTabSwitchRing\"") {
        insert_synthetic_properties(
            relative,
            facts,
            "SettingsTabSwitchRing",
            "progress_ring",
            BTreeMap::from([
                ("align_x".to_string(), "Right".to_string()),
                ("align_y".to_string(), "Top".to_string()),
                ("margin".to_string(), "0,6,4,0".to_string()),
            ]),
        );
    }

    if text.contains("\"SaveButton\"")
        && text.contains("OverlayLayer::new(settings_save_bar")
        && text.contains(".align(Alignment::End, Alignment::End)")
    {
        insert_synthetic_properties(
            relative,
            facts,
            "SaveButton",
            "button",
            BTreeMap::from([
                ("align_x".to_string(), "Right".to_string()),
                ("align_y".to_string(), "Bottom".to_string()),
                ("margin".to_string(), "0,0,32,32".to_string()),
                ("padding".to_string(), "24,12".to_string()),
            ]),
        );
    }
}

fn insert_synthetic_properties(
    relative: &str,
    facts: &mut BTreeMap<String, CodeFact>,
    id: &str,
    kind: &str,
    properties: BTreeMap<String, String>,
) {
    let line = facts.get(id).map(|fact| fact.line).unwrap_or(1);
    insert_synthetic_rust_fact(
        facts,
        CodeFact {
            source: "rust-ui-synthetic".to_string(),
            file: relative.to_string(),
            line,
            id: id.to_string(),
            kind: kind.to_string(),
            properties,
        },
    );
}

fn insert_service_expander_fact(
    relative: &str,
    line: usize,
    id: String,
    facts: &mut BTreeMap<String, CodeFact>,
) {
    insert_synthetic_rust_fact(
        facts,
        CodeFact {
            source: "rust-ui-synthetic".to_string(),
            file: relative.to_string(),
            line,
            id,
            kind: "expander".to_string(),
            properties: BTreeMap::from([
                ("width".to_string(), "Fill".to_string()),
                ("align_x".to_string(), "Stretch".to_string()),
                ("content_align_x".to_string(), "Stretch".to_string()),
            ]),
        },
    );
}

fn service_expander_title_id(service_id: &str) -> Option<&'static str> {
    match service_id {
        "windows-local-ai" => Some("WindowsLocalAITitleText"),
        _ => None,
    }
}

fn insert_service_title_fact(
    relative: &str,
    line: usize,
    id: String,
    facts: &mut BTreeMap<String, CodeFact>,
) {
    insert_synthetic_rust_fact(
        facts,
        CodeFact {
            source: "rust-ui-synthetic".to_string(),
            file: relative.to_string(),
            line,
            id,
            kind: "text".to_string(),
            properties: BTreeMap::from([
                ("text_style".to_string(), "BodyStrong".to_string()),
                ("font_size".to_string(), "14".to_string()),
                ("align_x".to_string(), "Left".to_string()),
            ]),
        },
    );
}

fn insert_service_status_fact(
    relative: &str,
    line: usize,
    id: String,
    facts: &mut BTreeMap<String, CodeFact>,
) {
    insert_synthetic_rust_fact(
        facts,
        CodeFact {
            source: "rust-ui-synthetic".to_string(),
            file: relative.to_string(),
            line,
            id,
            kind: "text".to_string(),
            properties: BTreeMap::from([
                ("align_x".to_string(), "Right".to_string()),
                ("align_y".to_string(), "Center".to_string()),
                ("margin".to_string(), "0,0,8,0".to_string()),
            ]),
        },
    );
}

fn insert_synthetic_rust_fact(facts: &mut BTreeMap<String, CodeFact>, synthetic: CodeFact) {
    facts
        .entry(synthetic.id.clone())
        .and_modify(|existing| {
            for (property, value) in &synthetic.properties {
                existing
                    .properties
                    .entry(property.clone())
                    .or_insert_with(|| value.clone());
            }
        })
        .or_insert(synthetic);
}

fn split_top_level_arguments(text: &str) -> Vec<String> {
    let mut args = Vec::new();
    let mut start = 0usize;
    let mut paren_depth = 0usize;
    let mut brace_depth = 0usize;
    let mut bracket_depth = 0usize;
    let mut in_string = false;
    let mut escaped = false;
    for (offset, ch) in text.char_indices() {
        if in_string {
            if escaped {
                escaped = false;
            } else if ch == '\\' {
                escaped = true;
            } else if ch == '"' {
                in_string = false;
            }
            continue;
        }
        match ch {
            '"' => in_string = true,
            '(' => paren_depth += 1,
            ')' => paren_depth = paren_depth.saturating_sub(1),
            '{' => brace_depth += 1,
            '}' => brace_depth = brace_depth.saturating_sub(1),
            '[' => bracket_depth += 1,
            ']' => bracket_depth = bracket_depth.saturating_sub(1),
            ',' if paren_depth == 0 && brace_depth == 0 && bracket_depth == 0 => {
                args.push(text[start..offset].trim().to_string());
                start = offset + ch.len_utf8();
            }
            _ => {}
        }
    }
    let tail = text[start..].trim();
    if !tail.is_empty() {
        args.push(tail.to_string());
    }
    args
}

fn first_number_text(text: &str) -> Option<String> {
    Regex::new(r#"[-+]?\d+(?:\.\d+)?"#)
        .expect("valid number regex")
        .find(text)
        .map(|match_| match_.as_str().to_string())
}

fn literal_id_in_rust_fragment(text: &str) -> Option<String> {
    Regex::new(r#"\.id\(\s*"([^"]+)""#)
        .expect("valid Rust literal id regex")
        .captures(text)
        .and_then(|capture| capture.get(1))
        .map(|match_| match_.as_str().to_string())
}

fn rust_string_literal(text: &str) -> Option<String> {
    let trimmed = text.trim();
    trimmed
        .strip_prefix('"')
        .and_then(|value| value.strip_suffix('"'))
        .map(|value| value.to_string())
}

fn rust_text_style(text: &str) -> Option<String> {
    Regex::new(r#"TextStyle::([A-Za-z0-9_]+)"#)
        .expect("valid Rust TextStyle regex")
        .captures(text)
        .and_then(|capture| capture.get(1))
        .map(|match_| match_.as_str().to_string())
}

fn explicit_text_font_size(function_name: &str, args: &[String]) -> Option<String> {
    match function_name {
        "styled_text_id_with_font_size" => args.get(3).and_then(|value| first_number_text(value)),
        _ => None,
    }
}

fn rust_text_chain_properties(text: &str, call_end: usize) -> BTreeMap<String, String> {
    let mut properties = BTreeMap::new();
    let Some(chain) = rust_method_chain_after_call(text, call_end) else {
        return properties;
    };
    if let Some(margin) = find_last_method_argument(&chain, "text_margin") {
        properties.insert("margin".to_string(), margin);
    }
    if let Some(align_x) =
        find_last_method_argument(&chain, "text_align_x").and_then(|value| rust_alignment(&value))
    {
        properties.insert("align_x".to_string(), align_x);
    }
    if let Some(align_y) =
        find_last_method_argument(&chain, "text_align_y").and_then(|value| rust_alignment(&value))
    {
        properties.insert("align_y".to_string(), align_y);
    }
    if let Some(align) = find_last_method_argument(&chain, "text_align") {
        let args = split_top_level_arguments(&align);
        if let Some(align_x) = args.first().and_then(|value| rust_alignment(value)) {
            properties.insert("align_x".to_string(), align_x);
        }
        if let Some(align_y) = args.get(1).and_then(|value| rust_alignment(value)) {
            properties.insert("align_y".to_string(), align_y);
        }
    }
    properties
}

fn rust_method_chain_after_call(text: &str, call_end: usize) -> Option<String> {
    let start = call_end.checked_add(1)?;
    let tail = text.get(start..)?;
    let mut end = 0usize;
    let mut paren_depth = 0usize;
    let mut brace_depth = 0usize;
    let mut bracket_depth = 0usize;
    let mut in_string = false;
    let mut escaped = false;
    let mut saw_method = false;
    for (offset, ch) in tail.char_indices() {
        if in_string {
            if escaped {
                escaped = false;
            } else if ch == '\\' {
                escaped = true;
            } else if ch == '"' {
                in_string = false;
            }
            end = offset + ch.len_utf8();
            continue;
        }
        match ch {
            '"' => in_string = true,
            '(' => paren_depth += 1,
            ')' => paren_depth = paren_depth.saturating_sub(1),
            '{' => brace_depth += 1,
            '}' => brace_depth = brace_depth.saturating_sub(1),
            '[' => bracket_depth += 1,
            ']' => bracket_depth = bracket_depth.saturating_sub(1),
            '.' if paren_depth == 0 && brace_depth == 0 && bracket_depth == 0 => saw_method = true,
            ',' | ';' if paren_depth == 0 && brace_depth == 0 && bracket_depth == 0 => break,
            _ => {}
        }
        end = offset + ch.len_utf8();
    }
    saw_method.then(|| tail[..end].to_string())
}

fn rust_alignment(text: &str) -> Option<String> {
    Regex::new(r#"Alignment::([A-Za-z0-9_]+)"#)
        .expect("valid Rust alignment regex")
        .captures(text)
        .and_then(|capture| capture.get(1))
        .map(|match_| match_.as_str().to_string())
}

fn text_style_font_size(style: &str) -> &'static str {
    match style {
        "Caption" => "12",
        "CaptionSmall" => "11",
        "Body" => "14",
        "BodyLarge" => "15",
        "BodyStrong" | "Success" | "Warning" => "14",
        "SectionTitle" => "18",
        "Subtitle" => "20",
        "Title" => "28",
        "TitleLarge" => "40",
        _ => "14",
    }
}

fn rust_id_chain_context(lines: &[&str], index: usize) -> String {
    if lines.is_empty() {
        return String::new();
    }
    let start = index;
    let mut end = index;
    for (line_index, line) in lines.iter().enumerate().skip(index).take(13) {
        end = line_index;
        let trimmed = line.trim();
        if line_index == index && trimmed.ends_with(';') {
            break;
        }
        if line_index == index {
            continue;
        }
        if trimmed.ends_with(';')
            || trimmed.starts_with('.') && (trimmed.ends_with(',') || trimmed.ends_with("),"))
            || trimmed.contains(".into_view(),")
        {
            break;
        }
    }
    lines[start..=end].join("\n")
}

fn parse_xml_attributes(text: &str) -> BTreeMap<String, String> {
    let attr_regex = Regex::new(r#"([\w:.]+)\s*=\s*"([^"]*)""#).expect("valid XML attribute regex");
    attr_regex
        .captures_iter(text)
        .map(|capture| (capture[1].to_string(), capture[2].to_string()))
        .collect()
}

fn extract_light_resource_dictionary(text: &str) -> Option<String> {
    let light_regex =
        Regex::new(r#"<ResourceDictionary\s+x:Key="Light">"#).expect("valid Light regex");
    let dark_regex =
        Regex::new(r#"<ResourceDictionary\s+x:Key="Dark">"#).expect("valid Dark regex");
    let light = light_regex.find(text)?;
    let rest = &text[light.end()..];
    let end = dark_regex
        .find(rest)
        .map(|match_| match_.start())
        .unwrap_or(rest.len());
    Some(rest[..end].to_string())
}

fn resource_reference_key(value: &str) -> Option<String> {
    let regex =
        Regex::new(r#"\{(?:StaticResource|ThemeResource)\s+([^}]+)\}"#).expect("valid ref regex");
    regex
        .captures(value)
        .map(|capture| capture[1].trim().to_string())
}

fn parse_rust_color_fields(body: &str) -> Vec<(String, String)> {
    let rgb_regex = Regex::new(
        r#"(?m)(?P<field>[a-zA-Z0-9_]+):\s*Color::rgb\(\s*(?P<r>\d+),\s*(?P<g>\d+),\s*(?P<b>\d+)\s*\)"#,
    )
    .expect("valid Rust rgb regex");
    let rgba_regex = Regex::new(
        r#"(?m)(?P<field>[a-zA-Z0-9_]+):\s*Color::rgba\(\s*(?P<r>\d+),\s*(?P<g>\d+),\s*(?P<b>\d+),\s*(?P<a>\d+)\s*\)"#,
    )
    .expect("valid Rust rgba regex");
    let mut values = Vec::new();
    for capture in rgb_regex.captures_iter(body) {
        values.push((
            capture["field"].to_string(),
            rgba_hex(
                parse_u8(&capture["r"]),
                parse_u8(&capture["g"]),
                parse_u8(&capture["b"]),
                255,
            ),
        ));
    }
    for capture in rgba_regex.captures_iter(body) {
        values.push((
            capture["field"].to_string(),
            rgba_hex(
                parse_u8(&capture["r"]),
                parse_u8(&capture["g"]),
                parse_u8(&capture["b"]),
                parse_u8(&capture["a"]),
            ),
        ));
    }
    values
}

fn infer_enclosing_field_width(context: &str, id: &str) -> Option<String> {
    let id_marker = format!(r#".id("{id}")"#);
    let id_index = context.find(&id_marker)?;
    let before_id = &context[..id_index];
    let field_regex = Regex::new(
        r#"(?s)(?:fixed_width_field|settings_field_stack|secret_field_stack|settings_labeled_control_field)\s*\(\s*"[^"]+"\s*,\s*(?P<width>\d+(?:\.\d+)?)"#,
    )
    .expect("valid field width regex");
    field_regex
        .captures_iter(before_id)
        .last()
        .map(|capture| capture["width"].to_string())
}

fn is_secret_field_editor(context: &str, id: &str) -> bool {
    let id_marker = format!(r#".id("{id}")"#);
    let Some(id_index) = context.find(&id_marker) else {
        return false;
    };
    context[..id_index].rfind("secret_field_stack(").is_some()
}

fn is_right_aligned_status_badge(id: &str, chain_context: &str) -> bool {
    id == "OpenVinoStatusBadge"
        && chain_context.contains(".margin(")
        && chain_context.contains("right: 8")
}

fn apply_rust_style_fact_properties(style: &str, properties: &mut BTreeMap<String, String>) {
    let classes = style.split_whitespace().collect::<Vec<_>>();
    if classes
        .iter()
        .any(|class| *class == "border" || *class == "surface-card")
    {
        properties
            .entry("border_width".to_string())
            .or_insert_with(|| "1".to_string());
    }
    if let Some(radius) = classes
        .iter()
        .rev()
        .find_map(|class| rust_style_radius(class))
    {
        properties.entry("radius".to_string()).or_insert(radius);
    }
}

fn rust_style_radius(class: &str) -> Option<String> {
    match class {
        "rounded-none" => Some("0".to_string()),
        "rounded-sm" => Some("4".to_string()),
        "rounded" | "rounded-md" => Some("4".to_string()),
        "rounded-lg" => Some("8".to_string()),
        "rounded-xl" => Some("12".to_string()),
        "rounded-2xl" => Some("16".to_string()),
        "rounded-full" => Some("999".to_string()),
        _ => class
            .strip_prefix("rounded-[")
            .and_then(|value| value.strip_suffix(']'))
            .map(|value| value.strip_suffix("px").unwrap_or(value).to_string()),
    }
}

fn rust_string_field(block: &str, field: &str) -> Option<String> {
    let regex = Regex::new(&format!(r#"\b{}\s*:\s*"([^"]*)""#, regex::escape(field)))
        .expect("valid Rust string field regex");
    regex
        .captures(block)
        .and_then(|capture| capture.get(1))
        .map(|match_| match_.as_str().to_string())
}

fn rust_option_string_field(block: &str, field: &str) -> Option<String> {
    let regex = Regex::new(&format!(
        r#"\b{}\s*:\s*Some\("([^"]+)"\)"#,
        regex::escape(field)
    ))
    .expect("valid Rust option string field regex");
    regex
        .captures(block)
        .and_then(|capture| capture.get(1))
        .map(|match_| match_.as_str().to_string())
}

fn provider_descriptor_model_width(service_id: &str) -> u16 {
    match service_id {
        "custom-openai" => 200,
        "doubao" => 300,
        _ => 280,
    }
}

fn find_last_method_argument(text: &str, method: &str) -> Option<String> {
    let needle = format!(".{method}(");
    let mut search_start = 0;
    let mut found = None;
    let mut paren_depth = 0usize;
    let mut brace_depth = 0usize;
    let mut bracket_depth = 0usize;
    let mut in_string = false;
    let mut escaped = false;

    while search_start < text.len() {
        let Some((offset, ch)) = text[search_start..].char_indices().next() else {
            break;
        };
        let absolute = search_start + offset;
        if in_string {
            if escaped {
                escaped = false;
            } else if ch == '\\' {
                escaped = true;
            } else if ch == '"' {
                in_string = false;
            }
            search_start = absolute + ch.len_utf8();
            continue;
        }
        match ch {
            '"' => in_string = true,
            '(' => paren_depth += 1,
            ')' => paren_depth = paren_depth.saturating_sub(1),
            '{' => brace_depth += 1,
            '}' => brace_depth = brace_depth.saturating_sub(1),
            '[' => bracket_depth += 1,
            ']' => bracket_depth = bracket_depth.saturating_sub(1),
            '.' if paren_depth == 0
                && brace_depth == 0
                && bracket_depth == 0
                && text[absolute..].starts_with(&needle) =>
            {
                let argument_start = absolute + needle.len();
                let Some((argument, end)) = read_balanced_argument(text, argument_start) else {
                    break;
                };
                found = Some(argument.trim().to_string());
                search_start = end.saturating_add(1);
                continue;
            }
            _ => {}
        }
        search_start = absolute + ch.len_utf8();
    }

    found
}

fn read_balanced_argument(text: &str, start: usize) -> Option<(String, usize)> {
    let mut depth = 0usize;
    let mut in_string = false;
    let mut escaped = false;
    for (offset, ch) in text[start..].char_indices() {
        if in_string {
            if escaped {
                escaped = false;
            } else if ch == '\\' {
                escaped = true;
            } else if ch == '"' {
                in_string = false;
            }
            continue;
        }
        match ch {
            '"' => in_string = true,
            '(' => depth += 1,
            ')' if depth == 0 => {
                let end = start + offset;
                return Some((text[start..end].to_string(), end));
            }
            ')' => depth = depth.saturating_sub(1),
            _ => {}
        }
    }
    None
}

fn compare_code_property_value(property: &str, reference: &str, candidate: Option<&str>) -> String {
    let Some(candidate) = candidate.filter(|value| !value.trim().is_empty()) else {
        return "missing".to_string();
    };
    if let (Some(reference_color), Some(candidate_color)) =
        (parse_color_value(reference), parse_color_value(candidate))
    {
        return if reference_color == candidate_color {
            "pass".to_string()
        } else {
            "drift".to_string()
        };
    }
    if let (Some(reference_alignment), Some(candidate_alignment)) = (
        normalize_alignment(reference),
        normalize_alignment(candidate),
    ) {
        return if reference_alignment == candidate_alignment {
            "pass".to_string()
        } else {
            "different".to_string()
        };
    }
    if property == "Width"
        && reference.contains("ViewportWidth")
        && (candidate.contains("Length::Fill") || candidate.trim() == "Fill")
    {
        return "pass".to_string();
    }
    if let Some(status) = compare_box_values(reference, candidate) {
        return status.to_string();
    }
    let reference_numbers = number_list(reference);
    let candidate_numbers = number_list(candidate);
    if !reference_numbers.is_empty() && !candidate_numbers.is_empty() {
        let count = reference_numbers.len().min(candidate_numbers.len());
        let max_delta = (0..count)
            .map(|index| (reference_numbers[index] - candidate_numbers[index]).abs())
            .fold(0.0_f64, f64::max);
        return if max_delta <= 0.01 {
            "pass".to_string()
        } else {
            "drift".to_string()
        };
    }
    if reference.trim() == candidate.trim() {
        return "pass".to_string();
    }
    if property.contains("Alignment") {
        return "different".to_string();
    }
    "different".to_string()
}

fn compare_box_values(reference: &str, candidate: &str) -> Option<&'static str> {
    if !is_box_like_value(reference) && !is_box_like_value(candidate) {
        return None;
    }
    let reference = parse_box_value(reference)?;
    let candidate = parse_box_value(candidate)?;
    let max_delta = reference
        .iter()
        .zip(candidate.iter())
        .map(|(left, right)| (left - right).abs())
        .fold(0.0_f64, f64::max);
    Some(if max_delta <= 0.01 { "pass" } else { "drift" })
}

fn is_box_like_value(value: &str) -> bool {
    value.contains(',')
        || value.contains("Edges")
        || ["left:", "top:", "right:", "bottom:"]
            .iter()
            .any(|needle| value.contains(needle))
}

fn parse_box_value(value: &str) -> Option<[f64; 4]> {
    if value.contains("Edges")
        || ["left:", "top:", "right:", "bottom:"]
            .iter()
            .any(|needle| value.contains(needle))
    {
        return Some(parse_rust_edges(value));
    }

    let values = number_list(value);
    match values.as_slice() {
        [] => None,
        [all] => Some([*all, *all, *all, *all]),
        [horizontal, vertical] => Some([*horizontal, *vertical, *horizontal, *vertical]),
        [left, top, right] => Some([*left, *top, *right, *top]),
        [left, top, right, bottom, ..] => Some([*left, *top, *right, *bottom]),
    }
}

fn parse_rust_edges(value: &str) -> [f64; 4] {
    let mut edges = [0.0, 0.0, 0.0, 0.0];
    for (field, index) in [("left", 0), ("top", 1), ("right", 2), ("bottom", 3)] {
        if let Some(number) = rust_struct_field_number(value, field) {
            edges[index] = number;
        }
    }
    edges
}

fn rust_struct_field_number(value: &str, field: &str) -> Option<f64> {
    let regex =
        Regex::new(&format!(r#"{field}\s*:\s*([-+]?\d+(?:\.\d+)?)"#)).expect("valid field regex");
    regex
        .captures(value)
        .and_then(|capture| capture.get(1))
        .and_then(|match_| match_.as_str().parse::<f64>().ok())
}

fn code_layout_recommendation(
    id: &str,
    dotnet_property: &str,
    rust_property: &str,
    dotnet_value: &str,
) -> String {
    format!(
        "For `{id}`, express .NET `{dotnet_property}={dotnet_value}` in Rust as `{rust_property}` before screenshot tuning."
    )
}

fn resolve_code_layout_value(property: &str, value: &str, theme: &CodeThemeFacts) -> String {
    let Some(key) = resource_reference_key(value) else {
        return value.to_string();
    };

    if is_color_layout_property(property) {
        return theme
            .colors
            .get(&key)
            .cloned()
            .unwrap_or_else(|| value.to_string());
    }

    theme
        .metrics
        .get(&key)
        .cloned()
        .unwrap_or_else(|| value.to_string())
}

fn is_color_layout_property(property: &str) -> bool {
    matches!(
        property,
        "Background" | "Foreground" | "BorderBrush" | "Fill" | "Stroke"
    )
}

fn code_layout_attributes() -> &'static [&'static str] {
    &[
        "Width",
        "Height",
        "MinWidth",
        "MaxWidth",
        "MinHeight",
        "MaxHeight",
        "Padding",
        "Margin",
        "Spacing",
        "ColumnSpacing",
        "RowSpacing",
        "CornerRadius",
        "BorderThickness",
        "FontSize",
        "Background",
        "Foreground",
        "BorderBrush",
        "Fill",
        "Stroke",
        "HorizontalAlignment",
        "VerticalAlignment",
        "HorizontalContentAlignment",
        "VerticalContentAlignment",
        "Grid.Row",
        "Grid.Column",
        "Grid.RowSpan",
        "Grid.ColumnSpan",
        "Canvas.Left",
        "Canvas.Top",
    ]
}

fn rust_fluent_methods() -> &'static [&'static str] {
    &[
        "width",
        "height",
        "min_width",
        "max_width",
        "min_height",
        "max_height",
        "padding",
        "secure",
        "margin",
        "spacing",
        "column_spacing",
        "row_spacing",
        "size",
        "font_size",
        "align_x",
        "align_y",
        "content_align_x",
        "border_width",
        "radius",
    ]
}

fn rust_property_for_dotnet_attribute(attribute: &str) -> Option<&'static str> {
    match attribute {
        "Width" => Some("width"),
        "Height" => Some("height"),
        "MinWidth" => Some("min_width"),
        "MaxWidth" => Some("max_width"),
        "MinHeight" => Some("min_height"),
        "MaxHeight" => Some("max_height"),
        "Padding" => Some("padding"),
        "Margin" => Some("margin"),
        "Spacing" => Some("spacing"),
        "ColumnSpacing" => Some("column_spacing"),
        "RowSpacing" => Some("row_spacing"),
        "CornerRadius" => Some("radius"),
        "BorderThickness" => Some("border_width"),
        "FontSize" => Some("font_size"),
        "HorizontalAlignment" => Some("align_x"),
        "VerticalAlignment" => Some("align_y"),
        "HorizontalContentAlignment" => Some("content_align_x"),
        "VerticalContentAlignment" => Some("content_align_y"),
        "Grid.Row" => Some("grid_row"),
        "Grid.Column" => Some("grid_column"),
        "Grid.RowSpan" => Some("grid_row_span"),
        "Grid.ColumnSpan" => Some("grid_column_span"),
        "Canvas.Left" => Some("x"),
        "Canvas.Top" => Some("y"),
        _ => None,
    }
}

fn code_theme_key_map() -> &'static [(&'static str, &'static str)] {
    &[
        ("MainViewBackgroundColor", "background"),
        ("ResultViewBackgroundColor", "result_surface"),
        ("ServiceResultHeaderBackgroundColor", "result_header"),
        (
            "ServiceResultHeaderHoverBackgroundColor",
            "result_header_hover",
        ),
        ("SettingsTabBackgroundColor", "tile_surface"),
        ("SettingsTabBorderColor", "tile_border"),
        ("SettingsTabSelectedBackgroundColor", "selected_surface"),
        ("SettingsTabSelectedBorderColor", "selected_border"),
        ("SettingsTabSelectedForegroundColor", "selected_foreground"),
        ("QueryViewBackgroundColor", "input_surface"),
        ("FloatingInputBackgroundColor", "floating_input_surface"),
        ("FloatingInputBorderColor", "floating_input_border"),
        ("PopButtonBackgroundColor", "floating_action_surface"),
        ("PopButtonBorderColor", "floating_action_border"),
        ("AccentColor", "accent.base"),
        ("AccentPointerOverColor", "accent_hover"),
        ("AccentPressedColor", "accent_pressed"),
        ("AccentForegroundColor", "accent_foreground"),
        ("MainBorderColor", "border"),
        ("QueryTextColor", "text_primary"),
        (
            "ServiceResultHeaderForegroundColor",
            "result_header_foreground",
        ),
        (
            "ServiceResultHeaderSecondaryForegroundColor",
            "text_secondary",
        ),
        ("StatusConnectedColor", "status_connected"),
        ("StatusDisconnectedColor", "status_disconnected"),
        ("StatusErrorColor", "status_error"),
        ("ButtonHoverColor", "button_hover"),
        ("ButtonBackgroundPressed", "button_pressed"),
    ]
}

fn default_dotnet_xaml_paths() -> Vec<PathBuf> {
    [
        "dotnet/src/Easydict.WinUI/Views/MainPage.xaml",
        "dotnet/src/Easydict.WinUI/Views/SettingsPage.xaml",
        "dotnet/src/Easydict.WinUI/Views/MiniWindow.xaml",
        "dotnet/src/Easydict.WinUI/Views/FixedWindow.xaml",
        "dotnet/src/Easydict.WinUI/Views/PopButtonWindow.xaml",
    ]
    .into_iter()
    .map(PathBuf::from)
    .collect()
}

fn default_dotnet_resource_paths() -> Vec<PathBuf> {
    [
        "dotnet/src/Easydict.WinUI/Themes/Colors.xaml",
        "dotnet/src/Easydict.WinUI/Themes/SettingsPageResources.xaml",
        "dotnet/src/Easydict.WinUI/Themes/MinimalResources.xaml",
    ]
    .into_iter()
    .map(PathBuf::from)
    .collect()
}

fn normalize_color_literal(value: &str) -> Option<String> {
    parse_color_value(value).map(|(red, green, blue, alpha)| rgba_hex(red, green, blue, alpha))
}

fn parse_color_value(value: &str) -> Option<(u8, u8, u8, u8)> {
    let regex = Regex::new(r#"#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})"#).expect("valid hex regex");
    let capture = regex.captures(value)?;
    let hex = &capture[1];
    if hex.len() == 6 {
        Some((
            u8::from_str_radix(&hex[0..2], 16).ok()?,
            u8::from_str_radix(&hex[2..4], 16).ok()?,
            u8::from_str_radix(&hex[4..6], 16).ok()?,
            255,
        ))
    } else {
        Some((
            u8::from_str_radix(&hex[2..4], 16).ok()?,
            u8::from_str_radix(&hex[4..6], 16).ok()?,
            u8::from_str_radix(&hex[6..8], 16).ok()?,
            u8::from_str_radix(&hex[0..2], 16).ok()?,
        ))
    }
}

fn rgba_hex(red: u8, green: u8, blue: u8, alpha: u8) -> String {
    if alpha == 255 {
        format!("#{red:02X}{green:02X}{blue:02X}")
    } else {
        format!("#{alpha:02X}{red:02X}{green:02X}{blue:02X}")
    }
}

fn normalize_alignment(value: &str) -> Option<&'static str> {
    let value = value.to_ascii_lowercase();
    if value.contains("left") || value.contains("top") || value.contains("start") {
        Some("start")
    } else if value.contains("right") || value.contains("bottom") || value.contains("end") {
        Some("end")
    } else if value.contains("center") {
        Some("center")
    } else if value.contains("stretch") || value.contains("fill") {
        Some("stretch")
    } else {
        None
    }
}

fn number_list(value: &str) -> Vec<f64> {
    let regex = Regex::new(r#"[-+]?\d+(?:\.\d+)?"#).expect("valid number regex");
    regex
        .find_iter(value)
        .filter_map(|match_| match_.as_str().parse::<f64>().ok())
        .collect()
}

fn parse_u8(value: &str) -> u8 {
    value.parse::<u8>().unwrap_or_default()
}

fn insert_if_absent(map: &mut BTreeMap<String, String>, key: String, value: String) {
    if key.trim().is_empty() || value.trim().is_empty() {
        return;
    }
    map.entry(key).or_insert(value);
}

fn extract_between(text: &str, start_marker: &str, end_marker: &str) -> Option<String> {
    let start = text.find(start_marker)?;
    let end = text[start..]
        .find(end_marker)
        .map(|offset| start + offset)
        .unwrap_or(text.len());
    Some(text[start..end].to_string())
}

fn line_number(text: &str, index: usize) -> usize {
    text[..index.min(text.len())]
        .bytes()
        .filter(|byte| *byte == b'\n')
        .count()
        + 1
}

fn repo_relative_path(repo_root: &Path, path: &Path) -> String {
    path.strip_prefix(repo_root)
        .unwrap_or(path)
        .display()
        .to_string()
}

fn absolutize_against(root: &Path, path: PathBuf) -> PathBuf {
    if path.is_absolute() {
        path
    } else {
        root.join(path)
    }
}

fn timestamp_millis() -> u128 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_millis())
        .unwrap_or_default()
}

fn code_settings_services_focus(
    comparisons: &[CodeLayoutComparison],
) -> Vec<CodeSettingsServicesFocusItem> {
    let mut focus = comparisons
        .iter()
        .filter(|item| is_settings_services_code_comparison(item))
        .map(|item| CodeSettingsServicesFocusItem {
            priority: settings_services_code_priority(item).to_string(),
            component: settings_services_code_component(item).to_string(),
            id: item.id.clone(),
            property: item.property.clone(),
            dotnet_value: item.dotnet_value.clone(),
            rust_value: item.rust_value.clone(),
            dotnet_location: item.dotnet_location.clone(),
            rust_location: item.rust_location.clone(),
            recommendation: settings_services_code_recommendation(item),
        })
        .collect::<Vec<_>>();
    focus.sort_by(|left, right| {
        settings_services_priority_rank(&left.priority)
            .cmp(&settings_services_priority_rank(&right.priority))
            .then_with(|| left.component.cmp(&right.component))
            .then_with(|| left.id.cmp(&right.id))
            .then_with(|| left.property.cmp(&right.property))
    });
    focus
}

fn is_settings_services_code_comparison(item: &CodeLayoutComparison) -> bool {
    if !item.dotnet_location.contains("SettingsPage.xaml:") {
        return false;
    }
    let Some(line) = location_line_number(&item.dotnet_location) else {
        return false;
    };
    (180..=1382).contains(&line)
}

fn location_line_number(location: &str) -> Option<usize> {
    location.rsplit(':').next()?.parse().ok()
}

fn settings_services_code_priority(item: &CodeLayoutComparison) -> &'static str {
    if matches!(
        settings_services_code_component(item),
        "expander bar" | "expander status"
    ) {
        "P0"
    } else if item.property == "Padding" && item.id.ends_with("Button") {
        "P1"
    } else if item.id.ends_with("EndpointBox") || item.id.ends_with("ModelBox") {
        "P1"
    } else {
        "P2"
    }
}

fn settings_services_priority_rank(priority: &str) -> u8 {
    match priority {
        "P0" => 0,
        "P1" => 1,
        _ => 2,
    }
}

fn settings_services_code_component(item: &CodeLayoutComparison) -> &'static str {
    if item.id.ends_with("ServiceExpander") || item.id.ends_with("Expander") {
        "expander bar"
    } else if item.id.ends_with("StatusText") || item.id.ends_with("StatusBadge") {
        "expander status"
    } else if item.id.ends_with("Button") {
        "action button"
    } else if item.id.ends_with("EndpointBox") || item.id.ends_with("ModelBox") {
        "field sizing"
    } else if item.id.ends_with("HeaderText") || item.id.ends_with("HelpIcon") {
        "section/header text"
    } else if item.property == "FontSize" {
        "text density"
    } else {
        "settings layout"
    }
}

fn settings_services_code_recommendation(item: &CodeLayoutComparison) -> String {
    match settings_services_code_component(item) {
        "expander bar" => format!(
            "Align `{}` with the WinUI Expander bar stretch/content alignment before pixel tuning the Services page.",
            item.id
        ),
        "expander status" => format!(
            "Place `{}` like the WinUI status glyph: right aligned with the same 8 DIP trailing margin in the service expander header.",
            item.id
        ),
        "action button" if item.property == "Padding" => format!(
            "Give `{}` the WinUI service action button padding `{}` or encode an equivalent compact button token.",
            item.id, item.dotnet_value
        ),
        "field sizing" => format!(
            "Express `{}` field sizing/alignment from .NET in Rust before screenshot tuning.",
            item.id
        ),
        _ => item.recommendation.clone(),
    }
}

fn code_settings_services_expander_scheme(
    options: &CodeParityOptions,
) -> Result<Vec<CodeSettingsServicesExpanderSchemeCheck>, String> {
    let dotnet_settings_xaml = options
        .dotnet_xaml
        .iter()
        .find(|path| {
            path.file_name()
                .and_then(|name| name.to_str())
                .is_some_and(|name| name.eq_ignore_ascii_case("SettingsPage.xaml"))
        })
        .and_then(|path| fs::read_to_string(path).ok())
        .unwrap_or_default();
    let rust_ui = fs::read_to_string(&options.rust_ui)
        .map_err(|error| format!("Could not read {}: {error}", options.rust_ui.display()))?;
    let dotnet_scheme = code_dotnet_service_expander_content_scheme(&dotnet_settings_xaml);
    let header_style =
        rust_service_expander_style_status(&rust_ui, "settings_service_expander_header_style");
    let content_style =
        rust_service_expander_style_status(&rust_ui, "settings_service_expander_content_style");
    let style_status = if header_style == "default" && content_style == "default" {
        "pass"
    } else {
        "drift"
    };
    let dotnet_spacing = dotnet_scheme.spacing.unwrap_or_else(|| "12".to_string());
    let dotnet_padding = dotnet_scheme.padding.unwrap_or_else(|| "0,8".to_string());

    Ok(vec![
        CodeSettingsServicesExpanderSchemeCheck {
            status: style_status.to_string(),
            area: "bar/content color scheme".to_string(),
            dotnet_value: "WinUI default Expander brushes from SettingsPage resources".to_string(),
            rust_value: format!("header_style={header_style}, content_style={content_style}"),
            recommendation:
                "Keep service expander bars and expanded content on the default WinUI Expander palette; custom styles should be justified by screenshot evidence."
                    .to_string(),
        },
        CodeSettingsServicesExpanderSchemeCheck {
            status: compare_code_property_value("Spacing", &dotnet_spacing, Some("12")),
            area: "expanded content spacing".to_string(),
            dotnet_value: dotnet_spacing,
            rust_value: "12".to_string(),
            recommendation:
                "Expanded service content should keep 12 DIP vertical spacing like the WinUI StackPanel."
                    .to_string(),
        },
        CodeSettingsServicesExpanderSchemeCheck {
            status: compare_code_property_value(
                "Padding",
                &dotnet_padding,
                Some(
                    r#"Edges {
                        top: 8,
                        bottom: 8,
                        ..Edges::ZERO
                    }"#,
                ),
            ),
            area: "expanded content padding".to_string(),
            dotnet_value: dotnet_padding,
            rust_value: "Edges { top: 8, bottom: 8, ..Edges::ZERO }".to_string(),
            recommendation:
                "Expanded service content should keep the same 0,8 DIP inset as the WinUI StackPanel."
                    .to_string(),
        },
    ])
}

fn code_interaction_contracts(
    options: &CodeParityOptions,
) -> Result<Vec<CodeInteractionContractCheck>, String> {
    let ui_contract = code_contract_source(options, "rs/crates/easydict_app/tests/ui_contract.rs")?;
    let quick_behavior = code_contract_source(
        options,
        "rs/crates/easydict_app/tests/quick_translate_behavior.rs",
    )?;
    let tray_platform = code_contract_source(
        options,
        "lib/winfluent-rs/crates/win_fluent/src/platform.rs",
    )?;
    let tray_native_runtime = code_contract_source(
        options,
        "lib/winfluent-rs/crates/win_fluent_platform_win/src/lib.rs",
    )?;
    let dotnet_tray = code_contract_source(
        options,
        "dotnet/src/Easydict.WinUI/Services/TrayIconService.cs",
    )?;

    let mut checks = Vec::new();
    push_source_interaction_contract(
        &mut checks,
        "hover/effects",
        "main primary action hover and pressed states",
        "WinUI Button pointer states from MainPage templates/resources".to_string(),
        &ui_contract,
        &[
            "PreviewScenario::PrimaryHover",
            "\"TranslateButton\", \"hovered=true\"",
            "PreviewScenario::PrimaryPressed",
            "\"TranslateButton\", \"pressed=true\"",
        ],
        "Keep primary action hover/pressed state propagation green before tuning pixels.",
    );
    push_source_interaction_contract(
        &mut checks,
        "hover/effects",
        "source input hover and keyboard focus states",
        "WinUI TextBox pointer/focus states from MainPage templates/resources".to_string(),
        &ui_contract,
        &[
            "PreviewScenario::SourceInputHover",
            "\"InputTextBox\", \"hovered=true\"",
            "PreviewScenario::SourceInputFocused",
            "\"InputTextBox\", \"focused=true\"",
        ],
        "Preserve separate hovered and focused source input states; screenshot parity should only tune colors and stroke weight.",
    );
    push_source_interaction_contract(
        &mut checks,
        "hover/effects",
        "result header hover exposes actions and stable collapse behavior",
        "WinUI result cards expose hover commands and collapse without layout jump".to_string(),
        &ui_contract,
        &[
            "PreviewScenario::ResultHeaderHover",
            "\"bing\", \"actions_visible=true\"",
            "\"main.quick.results\"",
            "collapse_transition_ms=0",
        ],
        "Use this contract to distinguish result-card behavior regressions from ordinary pixel drift.",
    );
    push_source_interaction_contract(
        &mut checks,
        "settings",
        "top settings tabs expose hover and pressed visual states",
        "SettingsPage uses top icon tabs as the .NET navigation source of truth".to_string(),
        &ui_contract,
        &[
            "\"SettingsTab_Services\", \"hovered=true\"",
            "\"SettingsTab_Views\", \"pressed=true\"",
        ],
        "Keep top-tab interaction states explicit; do not replace this evidence with NavigationView assumptions.",
    );
    push_source_interaction_contract(
        &mut checks,
        "settings/services",
        "service rows expose hover, pressed, and status badge states",
        "SettingsPage Services expanders use WinUI Expander rows and compact command buttons".to_string(),
        &ui_contract,
        &[
            "import_mdx_button_state = ControlState::default().hovered(true)",
            "international_services_toggle_state = ControlState::default().hovered(true).pressed(true)",
            "deepl_service_expander_state = ControlState::default().hovered(true)",
            "\"OllamaServiceExpander\",",
            "\"pressed=true\"",
            "\"WindowsLocalAIStatusBadge\"",
            "style=Success",
        ],
        "Services-page color/layout work should keep these per-row interaction states visible in the static contract.",
    );
    push_source_interaction_contract(
        &mut checks,
        "floating",
        "mini, fixed, and PopButton floating actions expose hover and pressed states",
        "MiniWindow, FixedWindow, and PopButton use compact WinUI-like floating action surfaces".to_string(),
        &ui_contract,
        &[
            "\"mini.translate\", \"hovered=true\"",
            "\"fixed.translate\", \"pressed=true\"",
            "\"pop-button.translate\", \"hovered=true\"",
            "\"pop-button.translate\", \"pressed=true\"",
        ],
        "Floating windows should share the same action-state semantics before their chrome and bounds are tuned.",
    );
    push_source_interaction_contract(
        &mut checks,
        "animation",
        "mode overlay fade and result collapse timings stay explicit",
        "WinUI mode switching uses a visible overlay while result collapse remains deterministic".to_string(),
        &ui_contract,
        &[
            "\"ModeSwitchOverlay\", \"fade_transition_ms=180\"",
            "\"main.quick.results\"",
            "collapse_transition_ms=0",
        ],
        "Treat timing changes as intentional UX decisions; keep animation durations visible in contract output.",
    );
    push_source_interaction_contract(
        &mut checks,
        "theme/effects",
        "hover and pressed palette tokens are asserted across light, dark, and minimal themes",
        "Colors.xaml resources remain the .NET source for hover, pressed, and accent states".to_string(),
        &ui_contract,
        &[
            "button_hover=#f1f4f8",
            "button_pressed=#ecece9",
            "accent_hover=#106ebe",
            "floating_action_hover_opacity=1",
            "floating_action_pressed_opacity=0.85",
            "button_hover=#323946",
        ],
        "When screenshot deltas are color-only, fix the theme token map before hand-tuning individual controls.",
    );
    push_source_interaction_contract(
        &mut checks,
        "window-runtime",
        "capture overlay and PopButton keep utility-window chrome semantics",
        "WinUI capture/selection surfaces are topmost utility windows that avoid taskbar and activation side effects".to_string(),
        &ui_contract,
        &[
            "assert_eq!(capture_options.level, WindowLevel::TopMost)",
            "assert_eq!(pop_options.level, WindowLevel::ToolWindow)",
            "assert!(pop_options.skip_taskbar)",
            "assert!(pop_options.no_activate)",
        ],
        "Window-runtime parity should stay green before comparing overlay or PopButton screenshots.",
    );
    push_source_interaction_contract(
        &mut checks,
        "tray",
        "tray menu topology, default action, icon, and browser submenu match the WinUI menu",
        code_contract_source_evidence(
            &dotnet_tray,
            &[
                "var showItem = new MenuFlyoutItem",
                "CreateBrowserSupportSubmenu",
                "var exitItem = new MenuFlyoutItem",
            ],
        )
        .1,
        &quick_behavior,
        &[
            "fn default_tray_menu_covers_migration_contract()",
            "icon_path.ends_with(\"AppIcon.ico\")",
            "menu.default_item_id.as_deref(), Some(TRAY_SHOW_MAIN)",
            "vec![\n            TRAY_SHOW_MAIN",
            "\"browser-support\"",
            "TRAY_EXIT",
        ],
        "Keep tray structure checks close to the .NET order so menu regressions are caught without opening the app.",
    );
    push_source_interaction_contract(
        &mut checks,
        "tray",
        "tray labels and browser install state follow localization and installation status",
        "TrayIconService localizes every MenuFlyoutItem through L(...).".to_string(),
        &quick_behavior,
        &[
            "fn tray_menu_labels_follow_ui_language_like_winui_tray()",
            "\"显示 Easydict\"",
            "fn tray_browser_support_menu_reflects_installation_status()",
            "chrome_installed: true",
            "firefox_installed: false",
        ],
        "Use this contract when adding languages or browser-status logic; visual parity should not hide text/state drift.",
    );
    push_source_interaction_contract(
        &mut checks,
        "tray",
        "tray commands route to the same desktop actions as WinUI",
        code_contract_source_evidence(
            &dotnet_tray,
            &[
                "OnTranslateClipboard?.Invoke()",
                "OnOcrTranslate?.Invoke()",
                "OnOpenSettings?.Invoke()",
                "ExitApplication()",
            ],
        )
        .1,
        &quick_behavior,
        &[
            "fn tray_commands_route_to_existing_desktop_actions()",
            "TRAY_TRANSLATE_CLIPBOARD",
            "WindowCommand::Show(id) if id.as_str() == \"capture-overlay\"",
            "TRAY_OPEN_SETTINGS",
            "TRAY_EXIT",
        ],
        "Route new tray actions through the app command layer before adding native adapter code.",
    );
    push_tray_schema_gap_contracts(&mut checks, &dotnet_tray, &tray_platform);
    push_tray_native_runtime_contracts(&mut checks, &dotnet_tray, &tray_native_runtime);
    Ok(checks)
}

#[derive(Debug, Clone)]
struct CodeContractSource {
    relative: String,
    text: String,
}

fn code_contract_source(
    options: &CodeParityOptions,
    relative: &str,
) -> Result<CodeContractSource, String> {
    let path = options.repo_root.join(relative);
    let text = if path.exists() {
        fs::read_to_string(&path)
            .map_err(|error| format!("Could not read {}: {error}", path.display()))?
    } else {
        String::new()
    };
    Ok(CodeContractSource {
        relative: relative.replace('\\', "/"),
        text,
    })
}

fn push_source_interaction_contract(
    checks: &mut Vec<CodeInteractionContractCheck>,
    area: &str,
    contract: &str,
    dotnet_evidence: String,
    rust_source: &CodeContractSource,
    rust_needles: &[&str],
    recommendation: &str,
) {
    let (all_found, rust_evidence) = code_contract_source_evidence(rust_source, rust_needles);
    checks.push(CodeInteractionContractCheck {
        status: if all_found { "pass" } else { "missing" }.to_string(),
        area: area.to_string(),
        contract: contract.to_string(),
        dotnet_evidence,
        rust_evidence,
        recommendation: recommendation.to_string(),
    });
}

fn push_tray_schema_gap_contracts(
    checks: &mut Vec<CodeInteractionContractCheck>,
    dotnet_tray: &CodeContractSource,
    tray_platform: &CodeContractSource,
) {
    let (_, dotnet_tooltip_evidence) = code_contract_source_evidence(
        dotnet_tray,
        &[
            "ToolTipService.SetToolTip",
            "SetTip(showItem)",
            "SetTip(browserMenu)",
        ],
    );
    let tray_item_block = extract_between(
        &tray_platform.text,
        "pub struct TrayMenuItem",
        "impl<Message> TrayMenuItem",
    )
    .unwrap_or_default();
    let item_tooltip_supported = tray_item_block.contains("tooltip");
    checks.push(CodeInteractionContractCheck {
        status: if item_tooltip_supported {
            "pass"
        } else {
            "missing"
        }
        .to_string(),
        area: "tray".to_string(),
        contract: "tray menu item hover tooltips are expressible in the Rust front-end schema"
            .to_string(),
        dotnet_evidence: dotnet_tooltip_evidence,
        rust_evidence: if item_tooltip_supported {
            code_contract_source_evidence(tray_platform, &["pub struct TrayMenuItem", "tooltip"]).1
        } else {
            format!(
                "{}: TrayMenuItem has no per-item tooltip field",
                tray_platform.relative
            )
        },
        recommendation:
            "Extend TrayMenuItem with tooltip text, or document a native-menu fallback if Win32 cannot show per-item hover tips."
                .to_string(),
    });

    let (_, dotnet_width_evidence) = code_contract_source_evidence(
        dotnet_tray,
        &[
            "MenuFlyoutPresenter",
            "FrameworkElement.MinWidthProperty, 300d",
        ],
    );
    let tray_menu_block = extract_between(
        &tray_platform.text,
        "pub struct TrayMenu",
        "impl<Message> TrayMenu",
    )
    .unwrap_or_default();
    let presenter_width_supported =
        tray_menu_block.contains("min_width") || tray_menu_block.contains("presenter_min_width");
    checks.push(CodeInteractionContractCheck {
        status: if presenter_width_supported {
            "pass"
        } else {
            "missing"
        }
        .to_string(),
        area: "tray".to_string(),
        contract: "tray menu presenter minimum width is expressible in the Rust front-end schema"
            .to_string(),
        dotnet_evidence: dotnet_width_evidence,
        rust_evidence: if presenter_width_supported {
            code_contract_source_evidence(tray_platform, &["pub struct TrayMenu", "min_width"]).1
        } else {
            format!(
                "{}: TrayMenu has no presenter/min_width field",
                tray_platform.relative
            )
        },
        recommendation:
            "Add a TrayMenu presenter_min_width/min_width option and map it in the Win32 adapter so long localized labels match the .NET first-open width."
                .to_string(),
    });
}

fn push_tray_native_runtime_contracts(
    checks: &mut Vec<CodeInteractionContractCheck>,
    dotnet_tray: &CodeContractSource,
    tray_platform: &CodeContractSource,
) {
    let menu_runtime = extract_between(
        &tray_platform.text,
        "fn show_tray_menu",
        "fn first_enabled_tray_command",
    )
    .unwrap_or_default();
    let has_native_menu_tooltips = tray_platform.text.contains("TOOLINFO")
        || tray_platform.text.contains("TTM_ADDTOOL")
        || (tray_platform.text.contains("WM_MENUSELECT")
            && tray_platform.text.contains("tray_menu_selected_tooltip")
            && tray_platform.text.contains("show_tray_menu_tooltip"));
    let has_native_min_width = menu_runtime.contains("MEASUREITEMSTRUCT")
        || menu_runtime.contains("MFT_OWNERDRAW")
        || (menu_runtime.contains("handle.presenter_min_width")
            && menu_runtime.contains("tray_menu_label_text"));

    let (_, dotnet_tooltip_evidence) = code_contract_source_evidence(
        dotnet_tray,
        &[
            "ToolTipService.SetToolTip",
            "SetTip(showItem)",
            "SetTip(browserMenu)",
        ],
    );
    checks.push(CodeInteractionContractCheck {
        status: if has_native_menu_tooltips {
            "pass"
        } else {
            "partial"
        }
        .to_string(),
        area: "tray/runtime".to_string(),
        contract: "native tray menu hover tooltips are rendered by the Windows adapter".to_string(),
        dotnet_evidence: dotnet_tooltip_evidence,
        rust_evidence: if has_native_menu_tooltips {
            code_contract_source_evidence(
                tray_platform,
                &[
                    "WM_MENUSELECT",
                    "tray_menu_selected_tooltip",
                    "show_tray_menu_tooltip",
                ],
            )
            .1
        } else {
            format!(
                "{}: native popup menu still uses HMENU/AppendMenuW without tooltip runtime rendering",
                tray_platform.relative
            )
        },
        recommendation:
            "After schema parity, either implement real native menu tooltip rendering or record a deliberate HMENU fallback with screenshot/manual evidence."
                .to_string(),
    });

    let (_, dotnet_width_evidence) = code_contract_source_evidence(
        dotnet_tray,
        &[
            "MenuFlyoutPresenter",
            "FrameworkElement.MinWidthProperty, 300d",
        ],
    );
    checks.push(CodeInteractionContractCheck {
        status: if has_native_min_width {
            "pass"
        } else {
            "partial"
        }
        .to_string(),
        area: "tray/runtime".to_string(),
        contract: "native tray menu presenter minimum width is applied by the Windows adapter"
            .to_string(),
        dotnet_evidence: dotnet_width_evidence,
        rust_evidence: if has_native_min_width {
            code_contract_source_evidence(
                tray_platform,
                &["handle.presenter_min_width", "tray_menu_label_text"],
            )
            .1
        } else {
            format!(
                "{}: native popup menu still uses default TrackPopupMenu sizing",
                tray_platform.relative
            )
        },
        recommendation:
            "Keep presenter_min_width applied in the native popup width hint; switch to owner-draw measurement if screenshot evidence shows the hint is insufficient."
                .to_string(),
    });
}

fn code_contract_source_evidence(source: &CodeContractSource, needles: &[&str]) -> (bool, String) {
    if source.text.is_empty() {
        return (false, format!("{}: file missing or empty", source.relative));
    }
    let mut found_lines = Vec::new();
    let mut missing = Vec::new();
    for needle in needles {
        if let Some(index) = source.text.find(needle) {
            found_lines.push(line_number(&source.text, index));
        } else {
            missing.push(shorten_contract_needle(needle));
        }
    }
    if missing.is_empty() {
        let first = found_lines.first().copied().unwrap_or(1);
        (
            true,
            format!("{}:{} (+{} checks)", source.relative, first, needles.len()),
        )
    } else {
        (
            false,
            format!("{} missing `{}`", source.relative, missing.join("`, `")),
        )
    }
}

fn shorten_contract_needle(needle: &str) -> String {
    let compact = needle.split_whitespace().collect::<Vec<_>>().join(" ");
    if compact.len() > 96 {
        format!("{}...", &compact[..96])
    } else {
        compact
    }
}

fn code_dotnet_service_expander_content_scheme(
    text: &str,
) -> CodeDotnetServiceExpanderContentScheme {
    let expander_regex =
        Regex::new(r#"(?s)<Expander\b.*?</Expander>"#).expect("valid expander regex");
    let stack_panel_regex =
        Regex::new(r#"(?s)<StackPanel\b(?P<attrs>[^>]*)>"#).expect("valid stack panel regex");
    let mut padding_values = BTreeMap::new();
    let mut spacing_values = BTreeMap::new();
    for expander in expander_regex.find_iter(text) {
        let block = expander.as_str();
        let content_start = block
            .find("</Expander.Header>")
            .map(|index| index + "</Expander.Header>".len())
            .unwrap_or(0);
        let content = &block[content_start..];
        let Some(stack_panel) = stack_panel_regex.captures(content) else {
            continue;
        };
        let attrs = parse_xml_attributes(
            stack_panel
                .name("attrs")
                .map(|value| value.as_str())
                .unwrap_or_default(),
        );
        if let Some(value) = attrs.get("Padding") {
            *padding_values.entry(value.clone()).or_insert(0usize) += 1;
        }
        if let Some(value) = attrs.get("Spacing") {
            *spacing_values.entry(value.clone()).or_insert(0usize) += 1;
        }
    }

    CodeDotnetServiceExpanderContentScheme {
        padding: dominant_string_value(padding_values),
        spacing: dominant_string_value(spacing_values),
    }
}

fn dominant_string_value(values: BTreeMap<String, usize>) -> Option<String> {
    values
        .into_iter()
        .max_by(|left, right| left.1.cmp(&right.1).then_with(|| right.0.cmp(&left.0)))
        .map(|(value, _)| value)
}

fn rust_service_expander_style_status(text: &str, function_name: &str) -> &'static str {
    let Some(body) = extract_between(text, &format!("fn {function_name}("), "\nfn ") else {
        return "unknown";
    };
    if body.contains("Some(") {
        "custom"
    } else if body.contains("None") {
        "default"
    } else {
        "unknown"
    }
}

fn compare_code_layout_comparisons(
    left: &CodeLayoutComparison,
    right: &CodeLayoutComparison,
) -> std::cmp::Ordering {
    code_status_rank(&right.status)
        .cmp(&code_status_rank(&left.status))
        .then_with(|| left.id.cmp(&right.id))
        .then_with(|| left.property.cmp(&right.property))
}

fn compare_code_theme_comparisons(
    left: &CodeThemeComparison,
    right: &CodeThemeComparison,
) -> std::cmp::Ordering {
    code_status_rank(&right.status)
        .cmp(&code_status_rank(&left.status))
        .then_with(|| left.dotnet_key.cmp(&right.dotnet_key))
}

fn code_status_rank(status: &str) -> u8 {
    match status {
        "drift" => 3,
        "different" => 2,
        "missing" => 1,
        _ => 0,
    }
}

fn code_layout_gap_buckets(comparisons: &[CodeLayoutComparison]) -> Vec<CodeLayoutGapBucket> {
    let mut buckets = BTreeMap::<String, CodeLayoutGapBucket>::new();
    for item in comparisons {
        let area = code_layout_gap_area(&item.id);
        let bucket = buckets
            .entry(area.clone())
            .or_insert_with(|| CodeLayoutGapBucket::new(area));
        bucket.record(&item.status);
    }
    let mut buckets = buckets.into_values().collect::<Vec<_>>();
    buckets.sort_by(|left, right| {
        right
            .total
            .cmp(&left.total)
            .then_with(|| left.area.cmp(&right.area))
    });
    buckets
}

fn code_layout_gap_components(comparisons: &[CodeLayoutComparison]) -> Vec<CodeLayoutGapComponent> {
    let mut components = BTreeMap::<String, CodeLayoutGapComponent>::new();
    for item in comparisons {
        let area = code_layout_gap_area(&item.id);
        let component = components
            .entry(item.id.clone())
            .or_insert_with(|| CodeLayoutGapComponent::new(area, item.id.clone()));
        component.record(&item.status, &item.property);
    }
    let mut components = components.into_values().collect::<Vec<_>>();
    components.sort_by(|left, right| {
        right
            .total
            .cmp(&left.total)
            .then_with(|| left.area.cmp(&right.area))
            .then_with(|| left.id.cmp(&right.id))
    });
    components
}

fn code_layout_gap_area(id: &str) -> String {
    if id.starts_with("LongDoc") || id.starts_with("main.long-doc") {
        "LongDoc".to_string()
    } else if id.starts_with("Settings")
        || id.contains("Service")
        || id.contains("Hotkey")
        || id.contains("Language")
        || id.starts_with("Ocr")
        || id.starts_with("MouseSelection")
    {
        "Settings".to_string()
    } else if id.starts_with("Mini") || id.starts_with("Fixed") || id.starts_with("Pop") {
        "Floating".to_string()
    } else if id.starts_with("Quick")
        || id.starts_with("Source")
        || id.starts_with("Result")
        || id.starts_with("Translate")
        || id.starts_with("Main")
        || id.starts_with("Mode")
        || id.starts_with("Swap")
        || id.starts_with("Status")
    {
        "Main".to_string()
    } else if id.starts_with("About") {
        "About".to_string()
    } else {
        "Other".to_string()
    }
}

#[derive(Debug, Clone)]
struct CodeThemeFacts {
    colors: BTreeMap<String, String>,
    metrics: BTreeMap<String, String>,
}

#[derive(Debug, Clone)]
struct CodeRustThemeFacts {
    values: BTreeMap<String, String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct CodeParityOutput {
    schema_version: String,
    generated_at_utc: String,
    repo_root: String,
    summary: CodeParitySummary,
    theme_comparisons: Vec<CodeThemeComparison>,
    layout_comparisons: Vec<CodeLayoutComparison>,
    layout_gap_buckets: Vec<CodeLayoutGapBucket>,
    layout_gap_components: Vec<CodeLayoutGapComponent>,
    settings_services_focus: Vec<CodeSettingsServicesFocusItem>,
    settings_services_expander_scheme: Vec<CodeSettingsServicesExpanderSchemeCheck>,
    interaction_contracts: Vec<CodeInteractionContractCheck>,
    ambiguous_dotnet_ids: Vec<String>,
    dotnet_facts: Vec<CodeFact>,
    rust_facts: BTreeMap<String, CodeFact>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct CodeParitySummary {
    dotnet_layout_fact_count: usize,
    rust_static_ui_id_count: usize,
    dotnet_theme_fact_count: usize,
    rust_theme_fact_count: usize,
    ambiguous_dotnet_id_count: usize,
    theme_comparison_count: usize,
    layout_comparison_count: usize,
    theme_drift_count: usize,
    theme_missing_count: usize,
    theme_different_count: usize,
    layout_drift_count: usize,
    layout_missing_count: usize,
    layout_different_count: usize,
    interaction_contract_count: usize,
    interaction_contract_missing_count: usize,
    interaction_contract_partial_count: usize,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct CodeThemeComparison {
    status: String,
    dotnet_key: String,
    rust_token: String,
    dotnet_value: String,
    rust_value: Option<String>,
    recommendation: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct CodeLayoutComparison {
    status: String,
    id: String,
    dotnet_kind: String,
    rust_kind: Option<String>,
    property: String,
    rust_property: String,
    dotnet_value: String,
    rust_value: Option<String>,
    dotnet_location: String,
    rust_location: Option<String>,
    recommendation: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct CodeLayoutGapBucket {
    area: String,
    total: usize,
    missing: usize,
    drift: usize,
    different: usize,
}

impl CodeLayoutGapBucket {
    fn new(area: String) -> Self {
        Self {
            area,
            total: 0,
            missing: 0,
            drift: 0,
            different: 0,
        }
    }

    fn record(&mut self, status: &str) {
        self.total += 1;
        match status {
            "missing" => self.missing += 1,
            "drift" => self.drift += 1,
            "different" => self.different += 1,
            _ => {}
        }
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct CodeLayoutGapComponent {
    area: String,
    id: String,
    total: usize,
    missing: usize,
    drift: usize,
    different: usize,
    properties: Vec<String>,
}

impl CodeLayoutGapComponent {
    fn new(area: String, id: String) -> Self {
        Self {
            area,
            id,
            total: 0,
            missing: 0,
            drift: 0,
            different: 0,
            properties: Vec::new(),
        }
    }

    fn record(&mut self, status: &str, property: &str) {
        self.total += 1;
        match status {
            "missing" => self.missing += 1,
            "drift" => self.drift += 1,
            "different" => self.different += 1,
            _ => {}
        }
        if !self.properties.iter().any(|value| value == property) {
            self.properties.push(property.to_string());
            self.properties.sort();
        }
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct CodeSettingsServicesFocusItem {
    priority: String,
    component: String,
    id: String,
    property: String,
    dotnet_value: String,
    rust_value: Option<String>,
    dotnet_location: String,
    rust_location: Option<String>,
    recommendation: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct CodeSettingsServicesExpanderSchemeCheck {
    status: String,
    area: String,
    dotnet_value: String,
    rust_value: String,
    recommendation: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct CodeInteractionContractCheck {
    status: String,
    area: String,
    contract: String,
    dotnet_evidence: String,
    rust_evidence: String,
    recommendation: String,
}

#[derive(Debug, Clone, Default)]
struct CodeDotnetServiceExpanderContentScheme {
    padding: Option<String>,
    spacing: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct CodeFact {
    source: String,
    file: String,
    line: usize,
    id: String,
    kind: String,
    properties: BTreeMap<String, String>,
}

#[derive(Debug, Clone)]
struct TriageReportCollection {
    reports: Vec<TriageReportFile>,
    skipped: Vec<TriageSkippedReport>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct TriageSkippedReport {
    report_path: String,
    reason: String,
}

#[derive(Debug, Clone)]
struct TriageReportFile {
    path: PathBuf,
    modified_millis: u128,
    artifact_name: String,
    value: Value,
}

#[derive(Debug, Clone)]
struct TriageScenarioSample {
    scenario_id: String,
    artifact_name: String,
    status: String,
    score: f64,
    pass_score: f64,
    deficit: f64,
    primary_driver: String,
    action: String,
    regions: Vec<TriageRegionSample>,
    findings: Vec<TriageFindingSample>,
    max_control_dimension_delta_dips: Option<f64>,
}

impl TriageScenarioSample {
    fn from_value(report: &TriageReportFile, scenario: &Value) -> Self {
        let scenario_id = get_string(scenario, "ScenarioId").unwrap_or_default();
        let status = get_string(scenario, "Status").unwrap_or_else(|| "unknown".to_string());
        let score = get_f64(scenario, "Score").map(round2).unwrap_or_default();
        let gate = get_object(scenario, "Gate");
        let pass_score = gate
            .and_then(|value| get_f64(value, "PassScore"))
            .unwrap_or(85.0);
        let metrics = get_object(scenario, "Metrics");
        let regions = triage_region_samples(scenario);
        let findings = triage_finding_samples(scenario);
        let max_control_dimension_delta_dips =
            metrics.and_then(|value| get_f64(value, "MaxControlDimensionDeltaDips"));
        let primary_driver = triage_primary_driver(&findings, &regions, metrics);
        let action = triage_action(&findings, &regions, metrics);
        Self {
            scenario_id,
            artifact_name: report.artifact_name.clone(),
            status,
            score,
            pass_score,
            deficit: (pass_score - score).max(0.0),
            primary_driver,
            action,
            regions,
            findings,
            max_control_dimension_delta_dips,
        }
    }
}

fn triage_region_samples(scenario: &Value) -> Vec<TriageRegionSample> {
    get_array(scenario, "Regions")
        .into_iter()
        .flatten()
        .filter_map(|region| {
            Some(TriageRegionSample {
                name: get_string(region, "Name")?,
                score: get_f64(region, "Score").map(round2)?,
            })
        })
        .collect()
}

fn triage_finding_samples(scenario: &Value) -> Vec<TriageFindingSample> {
    get_array(scenario, "Findings")
        .into_iter()
        .flatten()
        .filter_map(|finding| {
            Some(TriageFindingSample {
                severity: get_string(finding, "Severity")?,
                layer_hint: get_string(finding, "LayerHint")?,
                metric: get_string(finding, "Metric")?,
                value: get_f64(finding, "Value").unwrap_or_default(),
                message: get_string(finding, "Message").unwrap_or_default(),
            })
        })
        .collect()
}

fn triage_primary_driver(
    findings: &[TriageFindingSample],
    regions: &[TriageRegionSample],
    metrics: Option<&Value>,
) -> String {
    if let Some(finding) = findings.iter().max_by(|left, right| {
        triage_severity_rank(&left.severity)
            .cmp(&triage_severity_rank(&right.severity))
            .then_with(|| {
                left.value
                    .partial_cmp(&right.value)
                    .unwrap_or(std::cmp::Ordering::Equal)
            })
    }) {
        return format!("{} / {}", finding.layer_hint, finding.metric);
    }
    if let Some(delta) = metrics.and_then(|value| get_f64(value, "MaxControlDimensionDeltaDips")) {
        if delta > 2.0 {
            return format!("control dimensions {:.2} DIP", delta);
        }
    }
    regions
        .iter()
        .min_by(|left, right| {
            left.score
                .partial_cmp(&right.score)
                .unwrap_or(std::cmp::Ordering::Equal)
        })
        .map(|region| format!("region {}", region.name))
        .unwrap_or_else(|| "score".to_string())
}

fn triage_action(
    findings: &[TriageFindingSample],
    regions: &[TriageRegionSample],
    metrics: Option<&Value>,
) -> String {
    if findings.iter().any(is_recapture_evidence_finding) {
        return "recapture/evidence".to_string();
    }
    if metrics
        .and_then(|value| get_f64(value, "AbsoluteSizeScoreCap"))
        .is_some_and(|value| value < 100.0)
        || metrics
            .and_then(|value| get_f64(value, "AbsoluteWindowSizeDeltaPercent"))
            .is_some_and(|value| value > 1.0)
    {
        return "window runtime / absolute size".to_string();
    }
    if metrics
        .and_then(|value| get_f64(value, "InteractionEffectDeltaScore"))
        .or_else(|| metrics.and_then(|value| get_f64(value, "InteractionEffectRoiDeltaScore")))
        .is_some_and(|value| value < 70.0)
    {
        return "hover/pressed/focus effects".to_string();
    }
    if metrics
        .and_then(|value| get_f64(value, "ControlDimensionScoreCap"))
        .is_some_and(|value| value < 100.0)
        || metrics
            .and_then(|value| get_f64(value, "MaxControlDimensionDeltaDips"))
            .is_some_and(|value| value > 2.0)
    {
        return "control dimensions / layout".to_string();
    }
    if metrics
        .and_then(|value| get_f64(value, "SemanticScore"))
        .is_some_and(|value| value < 80.0)
    {
        return "semantic tree / required controls".to_string();
    }
    if let Some(region) = regions.iter().min_by(|left, right| {
        left.score
            .partial_cmp(&right.score)
            .unwrap_or(std::cmp::Ordering::Equal)
    }) {
        return format!("visual region {}", region.name);
    }
    "visual parity".to_string()
}

fn is_recapture_evidence_finding(finding: &TriageFindingSample) -> bool {
    if finding.layer_hint != "evidence_quality" && finding.layer_hint != "window_runtime" {
        return false;
    }
    let metric = finding.metric.to_ascii_lowercase();
    let message = finding.message.to_ascii_lowercase();
    metric.contains("clipped")
        || metric.contains("fallback")
        || metric.contains("missingcontrolbounds")
        || metric.contains("referencecandidate")
        || message.contains("clipped")
        || message.contains("fallback")
        || message.contains("rerun")
        || message.contains("missing bounds")
}

#[derive(Debug, Clone)]
struct TriageRegionSample {
    name: String,
    score: f64,
}

#[derive(Debug, Clone)]
struct TriageFindingSample {
    severity: String,
    layer_hint: String,
    metric: String,
    value: f64,
    message: String,
}

#[derive(Debug, Clone)]
struct TriageScenarioAggregate {
    scenario_id: String,
    occurrences: usize,
    failing_count: usize,
    score_sum: f64,
    minimum_score: f64,
    deficit_sum: f64,
    latest: Option<TriageScenarioSample>,
}

impl TriageScenarioAggregate {
    fn new(scenario_id: &str) -> Self {
        Self {
            scenario_id: scenario_id.to_string(),
            occurrences: 0,
            failing_count: 0,
            score_sum: 0.0,
            minimum_score: 100.0,
            deficit_sum: 0.0,
            latest: None,
        }
    }

    fn push(&mut self, sample: TriageScenarioSample) {
        if self.latest.is_none() {
            self.latest = Some(sample.clone());
        }
        self.occurrences += 1;
        if sample.status != "pass" {
            self.failing_count += 1;
        }
        self.score_sum += sample.score;
        self.minimum_score = self.minimum_score.min(sample.score);
        self.deficit_sum += sample.deficit;
    }

    fn into_queue_item(self) -> Option<TriageQueueItem> {
        let latest = self.latest?;
        let average_deficit = self.deficit_sum / self.occurrences.max(1) as f64;
        let worst_region = latest
            .regions
            .iter()
            .min_by(|left, right| {
                left.score
                    .partial_cmp(&right.score)
                    .unwrap_or(std::cmp::Ordering::Equal)
            })
            .map(|region| format!("{} {:.2}", region.name, region.score));
        let control_delta_bonus = latest.max_control_dimension_delta_dips.unwrap_or_default();
        let priority = round2(
            latest.deficit * 2.0
                + average_deficit
                + (self.failing_count as f64 * 3.0)
                + ((100.0 - self.minimum_score).max(0.0) * 0.15)
                + control_delta_bonus,
        );
        Some(TriageQueueItem {
            scenario_id: self.scenario_id,
            latest_artifact: latest.artifact_name,
            latest_status: latest.status,
            latest_score: latest.score,
            pass_score: round2(latest.pass_score),
            minimum_score: round2(self.minimum_score),
            average_score: round2(self.score_sum / self.occurrences.max(1) as f64),
            occurrences: self.occurrences,
            failing_count: self.failing_count,
            average_deficit: round2(average_deficit),
            priority,
            action: latest.action,
            primary_driver: latest.primary_driver,
            worst_region,
        })
    }
}

#[derive(Debug, Clone)]
struct TriageHotspotAggregate {
    name: String,
    occurrences: usize,
    score_sum: f64,
    minimum_score: f64,
    deficit_sum: f64,
    example_artifact: String,
    example_scenario: String,
}

impl TriageHotspotAggregate {
    fn new(name: &str) -> Self {
        Self {
            name: name.to_string(),
            occurrences: 0,
            score_sum: 0.0,
            minimum_score: 100.0,
            deficit_sum: 0.0,
            example_artifact: String::new(),
            example_scenario: String::new(),
        }
    }

    fn push(&mut self, score: f64, deficit: f64, sample: &TriageScenarioSample) {
        if self.occurrences == 0 || score < self.minimum_score {
            self.example_artifact = sample.artifact_name.clone();
            self.example_scenario = sample.scenario_id.clone();
        }
        self.occurrences += 1;
        self.score_sum += score;
        self.minimum_score = self.minimum_score.min(score);
        self.deficit_sum += deficit;
    }

    fn into_hotspot(self) -> TriageHotspot {
        TriageHotspot {
            name: self.name,
            occurrences: self.occurrences,
            average_score: round2(self.score_sum / self.occurrences.max(1) as f64),
            minimum_score: round2(self.minimum_score),
            average_deficit: round2(self.deficit_sum / self.occurrences.max(1) as f64),
            example_artifact: self.example_artifact,
            example_scenario: self.example_scenario,
        }
    }
}

#[derive(Debug, Clone)]
struct TriageFindingAggregate {
    key: String,
    occurrences: usize,
    value_sum: f64,
    max_severity: String,
    layer_hint: String,
    metric: String,
    example_artifact: String,
    example_scenario: String,
    example_message: String,
}

impl TriageFindingAggregate {
    fn new(key: &str) -> Self {
        Self {
            key: key.to_string(),
            occurrences: 0,
            value_sum: 0.0,
            max_severity: String::new(),
            layer_hint: String::new(),
            metric: String::new(),
            example_artifact: String::new(),
            example_scenario: String::new(),
            example_message: String::new(),
        }
    }

    fn push(&mut self, finding: &TriageFindingSample, sample: &TriageScenarioSample) {
        if self.occurrences == 0
            || triage_severity_rank(&finding.severity) > triage_severity_rank(&self.max_severity)
        {
            self.max_severity = finding.severity.clone();
            self.layer_hint = finding.layer_hint.clone();
            self.metric = finding.metric.clone();
            self.example_artifact = sample.artifact_name.clone();
            self.example_scenario = sample.scenario_id.clone();
            self.example_message = finding.message.clone();
        }
        self.occurrences += 1;
        self.value_sum += finding.value;
    }

    fn into_hotspot(self) -> TriageFindingHotspot {
        TriageFindingHotspot {
            key: self.key,
            layer_hint: self.layer_hint,
            metric: self.metric,
            max_severity: self.max_severity,
            occurrences: self.occurrences,
            average_value: round2(self.value_sum / self.occurrences.max(1) as f64),
            example_artifact: self.example_artifact,
            example_scenario: self.example_scenario,
            example_message: self.example_message,
        }
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct TriageOutput {
    schema_version: String,
    generated_at_utc: String,
    artifact_root: String,
    report_count: usize,
    skipped_reports: Vec<TriageSkippedReport>,
    artifact_summaries: Vec<TriageArtifactSummary>,
    next_iteration_queue: Vec<TriageQueueItem>,
    region_hotspots: Vec<TriageHotspot>,
    finding_hotspots: Vec<TriageFindingHotspot>,
    layer_hotspots: Vec<TriageHotspot>,
    evidence_gaps: Vec<TriageFindingHotspot>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct TriageArtifactSummary {
    artifact_name: String,
    generated_at_utc: String,
    report_path: String,
    total_scenarios: usize,
    pass_count: usize,
    warn_count: usize,
    fail_count: usize,
    average_score: f64,
    minimum_score: f64,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct TriageQueueItem {
    scenario_id: String,
    latest_artifact: String,
    latest_status: String,
    latest_score: f64,
    pass_score: f64,
    minimum_score: f64,
    average_score: f64,
    occurrences: usize,
    failing_count: usize,
    average_deficit: f64,
    priority: f64,
    action: String,
    primary_driver: String,
    worst_region: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct TriageHotspot {
    name: String,
    occurrences: usize,
    average_score: f64,
    minimum_score: f64,
    average_deficit: f64,
    example_artifact: String,
    example_scenario: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct TriageFindingHotspot {
    key: String,
    layer_hint: String,
    metric: String,
    max_severity: String,
    occurrences: usize,
    average_value: f64,
    example_artifact: String,
    example_scenario: String,
    example_message: String,
}

fn is_png_file(path: &Path) -> bool {
    path.extension()
        .and_then(|extension| extension.to_str())
        .map(|extension| extension.eq_ignore_ascii_case("png"))
        .unwrap_or(false)
}

fn file_name_contains(path: &Path, needle: &str) -> bool {
    path.file_name()
        .and_then(|name| name.to_str())
        .map(|name| name.contains(needle))
        .unwrap_or(false)
}

fn validate_percentage(value: Option<f64>, name: &str) -> Result<(), String> {
    if let Some(value) = value {
        if !(0.0..=100.0).contains(&value) {
            return Err(format!(
                "{name} requires a percentage from 0 to 100, got '{value}'."
            ));
        }
    }
    Ok(())
}

fn parse_score_gate_rule(value: &str) -> Result<ScoreGateRule, String> {
    let (target, thresholds) = value.split_once('=').ok_or_else(|| {
        format!("--score-gate requires format layer/case=pass,warn, got '{value}'.")
    })?;
    let (layer, case) = target.split_once('/').ok_or_else(|| {
        format!("--score-gate requires a layer/case target before '=', got '{target}'.")
    })?;
    let values = thresholds.split(',').map(str::trim).collect::<Vec<_>>();
    if values.len() != 2 {
        return Err(format!(
            "--score-gate requires pass,warn thresholds after '=', got '{thresholds}'."
        ));
    }
    let pass_score = values[0].parse::<f64>().map_err(|_| {
        format!(
            "--score-gate requires numeric pass threshold, got '{}'.",
            values[0]
        )
    })?;
    let warn_score = values[1].parse::<f64>().map_err(|_| {
        format!(
            "--score-gate requires numeric warn threshold, got '{}'.",
            values[1]
        )
    })?;
    if pass_score < warn_score {
        return Err(format!(
            "--score-gate pass threshold must be greater than or equal to warn threshold, got '{thresholds}'."
        ));
    }
    Ok(ScoreGateRule {
        layer: layer.trim().to_string(),
        case: case.trim().to_string(),
        pass_score,
        warn_score,
    })
}

fn absolutize(path: PathBuf) -> PathBuf {
    if path.is_absolute() {
        path
    } else {
        std::env::current_dir()
            .unwrap_or_else(|_| PathBuf::from("."))
            .join(path)
    }
}

fn load_pairs(options: &CliOptions) -> Result<Vec<ScreenshotPair>, String> {
    let mut pairs = Vec::new();
    let mut seen = BTreeSet::new();

    if let Some(manifest_path) = &options.manifest_path {
        for pair in load_pairs_from_manifest(manifest_path)? {
            seen.insert(pair.scenario_id.to_ascii_lowercase());
            pairs.push(pair);
        }
    }

    if !options.manifest_only {
        for pair in discover_pairs(&options.screenshot_root)? {
            if seen.insert(pair.scenario_id.to_ascii_lowercase()) {
                pairs.push(pair);
            }
        }
    }

    Ok(pairs)
}

fn load_pairs_from_manifest(path: &Path) -> Result<Vec<ScreenshotPair>, String> {
    let manifest_dir = path
        .parent()
        .map(Path::to_path_buf)
        .unwrap_or_else(|| PathBuf::from("."));
    let text = fs::read_to_string(path).map_err(|error| error.to_string())?;
    let value = serde_json::from_str::<Value>(&text).map_err(|error| error.to_string())?;
    let scenarios = get_array(&value, "Scenarios").cloned().unwrap_or_default();
    let mut pairs = Vec::new();

    for scenario_value in scenarios {
        let scenario = parse_manifest_scenario(&scenario_value)?;
        let reference_path = resolve_manifest_path(&manifest_dir, &scenario.reference_screenshot);
        let candidate_path = resolve_manifest_path(&manifest_dir, &scenario.candidate_screenshot);
        if !reference_path.exists() || !candidate_path.exists() {
            continue;
        }
        let scenario_id = if scenario.scenario_id.trim().is_empty() {
            reference_path
                .file_stem()
                .and_then(|value| value.to_str())
                .unwrap_or("scenario")
                .to_string()
        } else {
            scenario.scenario_id.clone()
        };
        pairs.push(ScreenshotPair {
            scenario_id,
            reference_path,
            candidate_path,
            metadata: Some(scenario),
        });
    }

    Ok(pairs)
}

fn discover_pairs(root: &Path) -> Result<Vec<ScreenshotPair>, String> {
    let mut pairs = Vec::new();
    for entry in WalkDir::new(root) {
        let entry = entry.map_err(|error| error.to_string())?;
        if !entry.file_type().is_file() {
            continue;
        }
        let path = entry.path();
        let Some(file_name) = path.file_name().and_then(|value| value.to_str()) else {
            continue;
        };
        if !file_name
            .to_ascii_lowercase()
            .ends_with(&DOTNET_SUFFIX.to_ascii_lowercase())
        {
            continue;
        }
        let scenario_id = file_name[..file_name.len() - DOTNET_SUFFIX.len()].to_string();
        let candidate_path = path
            .parent()
            .unwrap_or(root)
            .join(format!("{scenario_id}{RUST_SUFFIX}"));
        if candidate_path.exists() {
            pairs.push(ScreenshotPair {
                scenario_id,
                reference_path: path.to_path_buf(),
                candidate_path,
                metadata: None,
            });
        }
    }
    Ok(pairs)
}

fn resolve_manifest_path(base: &Path, path: &str) -> PathBuf {
    let path = path.replace('/', std::path::MAIN_SEPARATOR_STR);
    let candidate = PathBuf::from(&path);
    if candidate.is_absolute() {
        candidate
    } else {
        base.join(candidate)
    }
}

#[derive(Debug, Clone)]
struct ScreenshotPair {
    scenario_id: String,
    reference_path: PathBuf,
    candidate_path: PathBuf,
    metadata: Option<ManifestScenario>,
}

fn analyze_pair(pair: &ScreenshotPair, options: &CliOptions) -> Result<ScenarioResult, String> {
    let reference = image::open(&pair.reference_path)
        .map_err(|error| error.to_string())?
        .to_rgba8();
    let candidate_original = image::open(&pair.candidate_path)
        .map_err(|error| error.to_string())?
        .to_rgba8();
    let candidate =
        normalize_to_reference(&candidate_original, reference.width(), reference.height());

    let scenario_dir = options
        .output_dir
        .join("scenarios")
        .join(sanitize_file_name(&pair.scenario_id));
    fs::create_dir_all(&scenario_dir).map_err(|error| error.to_string())?;
    let normalized_reference_path = scenario_dir.join("reference-normalized.png");
    let normalized_candidate_path = scenario_dir.join("candidate-normalized.png");
    let dip_normalized_reference_path = scenario_dir.join("reference-dip-normalized.png");
    let dip_normalized_candidate_path = scenario_dir.join("candidate-dip-normalized.png");
    let dip_normalized_contact_sheet_path = scenario_dir.join("side-by-side-dip-normalized.png");
    let diff_heatmap_path = scenario_dir.join("diff-heatmap.png");
    let contact_sheet_path = scenario_dir.join("side-by-side-normalized.png");

    reference
        .save(&normalized_reference_path)
        .map_err(|error| error.to_string())?;
    candidate
        .save(&normalized_candidate_path)
        .map_err(|error| error.to_string())?;
    save_diff_heatmap(&reference, &candidate, &diff_heatmap_path)?;
    save_contact_sheet(&reference, &candidate, &contact_sheet_path)?;
    let dip_normalized = compare_dip_normalized_shared_viewport(
        &reference,
        &candidate_original,
        pair.metadata.as_ref(),
    );
    if let Some(dip_normalized) = &dip_normalized {
        dip_normalized
            .reference
            .save(&dip_normalized_reference_path)
            .map_err(|error| error.to_string())?;
        dip_normalized
            .candidate
            .save(&dip_normalized_candidate_path)
            .map_err(|error| error.to_string())?;
        save_contact_sheet(
            &dip_normalized.reference,
            &dip_normalized.candidate,
            &dip_normalized_contact_sheet_path,
        )?;
    }

    let full_region = RegionSpec::full(reference.width(), reference.height());
    let full_pixel = compare_pixels(&reference, &candidate, &full_region);
    let full_ssim = calculate_ssim(&reference, &candidate, &full_region);
    let dhash_distance =
        (calculate_dhash(&reference) ^ calculate_dhash(&candidate)).count_ones() as i32;
    let phash_distance =
        (calculate_phash(&reference) ^ calculate_phash(&candidate)).count_ones() as i32;
    let hash_score =
        clamp_score(100.0 - (((dhash_distance + phash_distance) as f64 / 128.0) * 100.0));
    let raw_size_score = calculate_size_score(
        reference.width(),
        reference.height(),
        candidate_original.width(),
        candidate_original.height(),
    );
    let size_score = calculate_effective_size_score(pair).unwrap_or(raw_size_score);
    let palette = compare_palette(&reference, &candidate);
    let palette_score = clamp_score(100.0 - f64::min(100.0, palette.average_color_delta / 1.35));
    let ui_summary = compare_ui_summaries(pair.metadata.as_ref());
    let evidence_audit = build_evidence_audit(pair.metadata.as_ref());
    let semantic_score = ui_summary.as_ref().map(|value| value.score);
    let scoring_profile = ScenarioScoringProfile::for_pair(pair, semantic_score.is_some());

    let region_specs = if let Some(metadata) = &pair.metadata {
        if !metadata.regions.is_empty() {
            regions_from_manifest(reference.width(), reference.height(), &metadata.regions)
        } else {
            default_regions(
                reference.width(),
                reference.height(),
                Some(metadata.window_kind.as_str()),
            )
        }
    } else {
        default_regions(reference.width(), reference.height(), None)
    };
    let regions = region_specs
        .iter()
        .map(|region| analyze_region(&reference, &candidate, region))
        .collect::<Vec<_>>();
    let region_score = if regions.is_empty() {
        100.0
    } else {
        regions.iter().map(|r| r.score * r.weight).sum::<f64>()
            / regions.iter().map(|r| r.weight).sum::<f64>().max(1.0)
    };
    let ssim_score = clamp_score(full_ssim * 100.0);
    let visual_score = scoring_profile.score(
        ssim_score,
        hash_score,
        region_score,
        semantic_score,
        size_score,
        palette_score,
    );
    let runtime_score_cap = calculate_window_runtime_score_cap(pair);
    let visual_absolute_match = has_perfect_visual_absolute_evidence(
        &full_pixel,
        full_ssim,
        hash_score,
        reference.width(),
        reference.height(),
        candidate_original.width(),
        candidate_original.height(),
        pair,
    );
    let control_dimension_missing_evidence_visual_verified =
        is_missing_dimension_evidence_visually_verified(ui_summary.as_ref(), visual_absolute_match);
    let control_dimension_score_cap = calculate_control_dimension_score_cap(
        ui_summary.as_ref(),
        control_dimension_missing_evidence_visual_verified,
    );
    let absolute_image_size_delta_percent = max_axis_delta_percent(
        reference.width() as f64,
        reference.height() as f64,
        candidate_original.width() as f64,
        candidate_original.height() as f64,
    );
    let absolute_window_size_delta_percent = pair.metadata.as_ref().and_then(|metadata| {
        absolute_window_delta_percent(
            metadata.reference_window.as_ref(),
            metadata.candidate_window.as_ref(),
        )
    });
    let dpi_scale_delta = pair
        .metadata
        .as_ref()
        .and_then(reference_candidate_dpi_scale_delta);
    let absolute_size_score_cap = calculate_absolute_size_score_cap(
        reference.width(),
        reference.height(),
        candidate_original.width(),
        candidate_original.height(),
        pair,
    );
    let semantic_contract_score_floor = calculate_semantic_contract_score_floor(
        &scoring_profile,
        ui_summary.as_ref(),
        pair.metadata.as_ref(),
        runtime_score_cap,
        control_dimension_score_cap,
        absolute_size_score_cap,
    );
    let final_score = visual_score
        .max(semantic_contract_score_floor.unwrap_or(0.0))
        .min(runtime_score_cap)
        .min(control_dimension_score_cap)
        .min(absolute_size_score_cap);
    let gate = resolve_score_gate(pair, &regions, runtime_score_cap, options);
    let status = ScoreStatus::from_score(final_score, gate.pass_score, gate.warn_score);

    let mut findings = build_findings(
        pair,
        reference.width(),
        reference.height(),
        candidate_original.width(),
        candidate_original.height(),
        &scoring_profile,
        &gate,
        status,
        final_score,
        visual_score,
        runtime_score_cap,
        control_dimension_score_cap,
        absolute_size_score_cap,
        &full_pixel,
        full_ssim,
        hash_score,
        size_score,
        &palette,
        ui_summary.as_ref(),
        semantic_contract_score_floor,
        control_dimension_missing_evidence_visual_verified,
        &regions,
    );
    add_dip_normalized_viewport_finding(
        pair.metadata.as_ref(),
        dip_normalized.as_ref(),
        &mut findings,
    );

    Ok(ScenarioResult {
        scenario_id: pair.scenario_id.clone(),
        status,
        score: round2(final_score),
        reference_path: relative_path(&options.output_dir, &pair.reference_path),
        candidate_path: relative_path(&options.output_dir, &pair.candidate_path),
        normalized_reference_path: relative_path(&options.output_dir, &normalized_reference_path),
        normalized_candidate_path: relative_path(&options.output_dir, &normalized_candidate_path),
        dip_normalized_reference_path: dip_normalized
            .as_ref()
            .map(|_| relative_path(&options.output_dir, &dip_normalized_reference_path)),
        dip_normalized_candidate_path: dip_normalized
            .as_ref()
            .map(|_| relative_path(&options.output_dir, &dip_normalized_candidate_path)),
        dip_normalized_contact_sheet_path: dip_normalized
            .as_ref()
            .map(|_| relative_path(&options.output_dir, &dip_normalized_contact_sheet_path)),
        diff_heatmap_path: relative_path(&options.output_dir, &diff_heatmap_path),
        contact_sheet_path: relative_path(&options.output_dir, &contact_sheet_path),
        reference_size: ImageSize {
            width: reference.width(),
            height: reference.height(),
        },
        candidate_size: ImageSize {
            width: candidate_original.width(),
            height: candidate_original.height(),
        },
        metadata: pair.metadata.clone(),
        gate,
        evidence_audit,
        metrics: ScenarioMetrics {
            pixel_error_percent: round4(full_pixel.pixel_error_percent),
            mean_channel_delta: round4(full_pixel.mean_channel_delta),
            max_channel_delta: full_pixel.max_channel_delta,
            ssim: round5(full_ssim),
            dhash_distance,
            phash_distance,
            hash_score: round2(hash_score),
            size_score: round2(size_score),
            palette_score: round2(palette_score),
            average_color_delta: round2(palette.average_color_delta),
            semantic_score: semantic_score.map(round2),
            control_count_delta_percent: ui_summary
                .as_ref()
                .map(|value| round2(value.control_count_delta_percent)),
            control_count_delta_count: ui_summary
                .as_ref()
                .map(|value| value.control_count_deltas.len()),
            automation_id_jaccard: ui_summary
                .as_ref()
                .and_then(|value| value.automation_id_jaccard.map(round2)),
            visible_text_jaccard: ui_summary
                .as_ref()
                .and_then(|value| value.visible_text_jaccard.map(round2)),
            visible_text_delta_count: ui_summary.as_ref().map(|value| {
                value.missing_reference_visible_texts.len()
                    + value.extra_candidate_visible_texts.len()
            }),
            missing_required_semantic_tag_count: ui_summary
                .as_ref()
                .map(|value| value.missing_required_semantic_tags.len()),
            missing_required_visible_text_count: ui_summary
                .as_ref()
                .map(|value| value.missing_required_visible_texts.len()),
            missing_required_control_state_count: ui_summary
                .as_ref()
                .map(|value| value.missing_required_control_states.len()),
            missing_control_bounds_evidence_count: ui_summary
                .as_ref()
                .map(|value| value.missing_control_bounds_evidence.len()),
            control_dimension_delta_count: ui_summary
                .as_ref()
                .map(|value| value.control_dimension_delta_count),
            max_control_dimension_delta_dips: ui_summary
                .as_ref()
                .map(|value| round2(value.max_control_dimension_delta_dips)),
            scoring_profile: scoring_profile.id.clone(),
            region_score: round2(region_score),
            visual_score: round2(visual_score),
            semantic_contract_score_floor: semantic_contract_score_floor.map(round2),
            window_runtime_score_cap: (runtime_score_cap < 100.0)
                .then_some(round2(runtime_score_cap)),
            control_dimension_score_cap: (control_dimension_score_cap < 100.0)
                .then_some(round2(control_dimension_score_cap)),
            absolute_image_size_delta_percent: round2(absolute_image_size_delta_percent),
            absolute_window_size_delta_percent: absolute_window_size_delta_percent.map(round2),
            dpi_scale_delta: dpi_scale_delta.map(round2),
            dip_normalized_pixel_error_percent: dip_normalized
                .as_ref()
                .map(|value| round4(value.pixel.pixel_error_percent)),
            dip_normalized_ssim: dip_normalized.as_ref().map(|value| round5(value.ssim)),
            dip_normalized_viewport_width: dip_normalized.as_ref().map(|value| value.width),
            dip_normalized_viewport_height: dip_normalized.as_ref().map(|value| value.height),
            absolute_size_score_cap: (absolute_size_score_cap < 100.0)
                .then_some(round2(absolute_size_score_cap)),
            effect_baseline_scenario_id: None,
            reference_effect_pixel_error_percent: None,
            candidate_effect_pixel_error_percent: None,
            effect_delta_magnitude_delta_percent: None,
            interaction_effect_delta_score: None,
            interaction_effect_roi_target_ids: None,
            interaction_effect_roi_bounds: None,
            reference_effect_roi_pixel_error_percent: None,
            candidate_effect_roi_pixel_error_percent: None,
            effect_roi_delta_magnitude_delta_percent: None,
            interaction_effect_roi_delta_score: None,
        },
        regions,
        findings,
    })
}

fn annotate_interaction_effect_metrics(
    scenarios: &mut [ScenarioResult],
    pairs: &[ScreenshotPair],
) -> Result<(), String> {
    let pair_by_id = pairs
        .iter()
        .map(|pair| (pair.scenario_id.to_ascii_lowercase(), pair))
        .collect::<BTreeMap<_, _>>();

    for scenario in scenarios.iter_mut() {
        let Some(baseline_id) = scenario
            .metadata
            .as_ref()
            .and_then(|metadata| metadata.baseline_scenario_id.as_ref())
            .filter(|value| !value.trim().is_empty())
            .cloned()
        else {
            continue;
        };
        let Some(current_pair) = pair_by_id.get(&scenario.scenario_id.to_ascii_lowercase()) else {
            continue;
        };
        let Some(baseline_pair) = pair_by_id.get(&baseline_id.to_ascii_lowercase()) else {
            scenario.findings.push(Finding {
                severity: "warning".to_string(),
                layer_hint: "evidence_quality".to_string(),
                message: format!(
                    "Interaction baseline scenario `{baseline_id}` was declared but not found; hover/focus effect delta could not be measured."
                ),
                metric: "missingInteractionBaseline".to_string(),
                value: 1.0,
            });
            continue;
        };

        let effect = compare_interaction_effect_delta(
            baseline_pair,
            current_pair,
            scenario.metadata.as_ref(),
        )?;
        scenario.metrics.effect_baseline_scenario_id = Some(baseline_id.clone());
        scenario.metrics.reference_effect_pixel_error_percent =
            Some(round4(effect.reference_pixel_error_percent));
        scenario.metrics.candidate_effect_pixel_error_percent =
            Some(round4(effect.candidate_pixel_error_percent));
        scenario.metrics.effect_delta_magnitude_delta_percent =
            Some(round4(effect.magnitude_delta_percent));
        scenario.metrics.interaction_effect_delta_score = Some(round2(effect.score));
        apply_interaction_effect_score_cap(
            scenario,
            effect.score,
            "interactionEffectScoreCap",
            "full-window interaction effect",
        );

        if effect.score < 70.0 {
            scenario.findings.push(Finding {
                severity: "warning".to_string(),
                layer_hint: "final_visual_effect".to_string(),
                message: format!(
                    "Interaction effect differs from baseline `{baseline_id}`: WinUI changed {:.2}% of pixels, Rust changed {:.2}% (delta {:.2}%).",
                    effect.reference_pixel_error_percent,
                    effect.candidate_pixel_error_percent,
                    effect.magnitude_delta_percent
                ),
                metric: "interactionEffectDeltaScore".to_string(),
                value: round2(effect.score),
            });
        }
        if let Some(roi) = effect.roi.as_ref() {
            scenario.metrics.interaction_effect_roi_target_ids = Some(roi.target_ids.clone());
            scenario.metrics.interaction_effect_roi_bounds = Some(roi.bounds.clone());
            scenario.metrics.reference_effect_roi_pixel_error_percent =
                Some(round4(roi.reference_pixel_error_percent));
            scenario.metrics.candidate_effect_roi_pixel_error_percent =
                Some(round4(roi.candidate_pixel_error_percent));
            scenario.metrics.effect_roi_delta_magnitude_delta_percent =
                Some(round4(roi.magnitude_delta_percent));
            scenario.metrics.interaction_effect_roi_delta_score = Some(round2(roi.score));
            apply_interaction_effect_score_cap(
                scenario,
                roi.score,
                "interactionEffectRoiScoreCap",
                "interaction effect ROI",
            );

            if roi.score < 70.0 {
                scenario.findings.push(Finding {
                    severity: "warning".to_string(),
                    layer_hint: "final_visual_effect".to_string(),
                    message: format!(
                        "Interaction effect in ROI `{}` differs from baseline `{baseline_id}`: WinUI changed {:.2}% of ROI pixels, Rust changed {:.2}% (delta {:.2}%).",
                        roi.target_ids.join(","),
                        roi.reference_pixel_error_percent,
                        roi.candidate_pixel_error_percent,
                        roi.magnitude_delta_percent
                    ),
                    metric: "interactionEffectRoiDeltaScore".to_string(),
                    value: round2(roi.score),
                });
            }
        }
    }

    Ok(())
}

fn apply_interaction_effect_score_cap(
    scenario: &mut ScenarioResult,
    cap_score: f64,
    metric: &str,
    source: &str,
) {
    let cap_score = round2(cap_score);
    if cap_score >= scenario.score {
        return;
    }

    let previous_score = scenario.score;
    let previous_status = scenario.status;
    scenario.score = cap_score;
    scenario.status = ScoreStatus::from_score(
        scenario.score,
        scenario.gate.pass_score,
        scenario.gate.warn_score,
    );
    scenario.findings.push(Finding {
        severity: if scenario.status == ScoreStatus::Fail {
            "error".to_string()
        } else {
            "warning".to_string()
        },
        layer_hint: "final_visual_effect".to_string(),
        message: format!(
            "{source} capped scenario score from {:.2} ({:?}) to {:.2} ({:?}); hover, pressed, and focus parity must match the WinUI reference before this scenario can pass.",
            previous_score,
            previous_status,
            scenario.score,
            scenario.status
        ),
        metric: metric.to_string(),
        value: scenario.score,
    });
}

fn compare_interaction_effect_delta(
    baseline_pair: &ScreenshotPair,
    current_pair: &ScreenshotPair,
    metadata: Option<&ManifestScenario>,
) -> Result<InteractionEffectDelta, String> {
    let reference_baseline = image::open(&baseline_pair.reference_path)
        .map_err(|error| error.to_string())?
        .to_rgba8();
    let reference_current = image::open(&current_pair.reference_path)
        .map_err(|error| error.to_string())?
        .to_rgba8();
    let reference_baseline = normalize_to_reference(
        &reference_baseline,
        reference_current.width(),
        reference_current.height(),
    );
    let reference_region = RegionSpec::full(reference_current.width(), reference_current.height());
    let reference_effect =
        compare_pixels(&reference_baseline, &reference_current, &reference_region);

    let candidate_baseline = image::open(&baseline_pair.candidate_path)
        .map_err(|error| error.to_string())?
        .to_rgba8();
    let candidate_current = image::open(&current_pair.candidate_path)
        .map_err(|error| error.to_string())?
        .to_rgba8();
    let candidate_baseline = normalize_to_reference(
        &candidate_baseline,
        candidate_current.width(),
        candidate_current.height(),
    );
    let candidate_region = RegionSpec::full(candidate_current.width(), candidate_current.height());
    let candidate_effect =
        compare_pixels(&candidate_baseline, &candidate_current, &candidate_region);

    let (magnitude_delta_percent, score) =
        score_interaction_effect_delta(&reference_effect, &candidate_effect);

    let roi = interaction_effect_roi(
        metadata,
        reference_current.width().min(candidate_current.width()),
        reference_current.height().min(candidate_current.height()),
    )
    .map(|roi| {
        let reference_effect = compare_pixels(&reference_baseline, &reference_current, &roi.region);
        let candidate_effect = compare_pixels(&candidate_baseline, &candidate_current, &roi.region);
        let (magnitude_delta_percent, score) =
            score_interaction_effect_delta(&reference_effect, &candidate_effect);
        InteractionEffectRoiDelta {
            target_ids: roi.target_ids,
            bounds: RegionBounds {
                x: roi.region.x,
                y: roi.region.y,
                width: roi.region.width,
                height: roi.region.height,
            },
            reference_pixel_error_percent: reference_effect.pixel_error_percent,
            candidate_pixel_error_percent: candidate_effect.pixel_error_percent,
            magnitude_delta_percent,
            score,
        }
    });

    Ok(InteractionEffectDelta {
        reference_pixel_error_percent: reference_effect.pixel_error_percent,
        candidate_pixel_error_percent: candidate_effect.pixel_error_percent,
        magnitude_delta_percent,
        score,
        roi,
    })
}

#[derive(Debug, Clone)]
struct InteractionEffectDelta {
    reference_pixel_error_percent: f64,
    candidate_pixel_error_percent: f64,
    magnitude_delta_percent: f64,
    score: f64,
    roi: Option<InteractionEffectRoiDelta>,
}

#[derive(Debug, Clone)]
struct InteractionEffectRoiDelta {
    target_ids: Vec<String>,
    bounds: RegionBounds,
    reference_pixel_error_percent: f64,
    candidate_pixel_error_percent: f64,
    magnitude_delta_percent: f64,
    score: f64,
}

#[derive(Debug, Clone)]
struct InteractionEffectRoi {
    target_ids: Vec<String>,
    region: RegionSpec,
}

fn score_interaction_effect_delta(
    reference_effect: &PixelComparison,
    candidate_effect: &PixelComparison,
) -> (f64, f64) {
    let magnitude_delta_percent =
        (reference_effect.pixel_error_percent - candidate_effect.pixel_error_percent).abs();
    let mean_delta_distance =
        (reference_effect.mean_channel_delta - candidate_effect.mean_channel_delta).abs();
    let score = clamp_score(100.0 - (magnitude_delta_percent * 4.0) - (mean_delta_distance * 2.0));
    (magnitude_delta_percent, score)
}

fn interaction_effect_roi(
    metadata: Option<&ManifestScenario>,
    image_width: u32,
    image_height: u32,
) -> Option<InteractionEffectRoi> {
    let metadata = metadata?;
    if image_width == 0 || image_height == 0 {
        return None;
    }
    let dimensions = metadata
        .reference_ui_summary
        .as_ref()
        .and_then(|summary| summary.visible_control_dimensions.as_ref())?;
    let dpi_scale = metadata
        .reference_window
        .as_ref()
        .map(|window| window.dpi_scale)
        .filter(|value| value.is_finite() && *value > 0.0)
        .unwrap_or(1.0);

    let mut target_ids = Vec::new();
    let mut union: Option<(u32, u32, u32, u32)> = None;
    for (id, states) in &metadata.required_control_states {
        if !states.iter().any(|state| is_effect_state(state)) {
            continue;
        }
        let Some(bounds) = get_case_insensitive_dimension(dimensions, id)
            .and_then(|dimension| dimension.bounds_dips.as_ref())
        else {
            continue;
        };
        let Some((x, y, width, height)) =
            control_bounds_dips_to_pixel_roi(bounds, dpi_scale, image_width, image_height)
        else {
            continue;
        };
        target_ids.push(id.clone());
        union = Some(match union {
            Some((left, top, current_width, current_height)) => {
                let right = left
                    .saturating_add(current_width)
                    .max(x.saturating_add(width));
                let bottom = top
                    .saturating_add(current_height)
                    .max(y.saturating_add(height));
                let left = left.min(x);
                let top = top.min(y);
                (
                    left,
                    top,
                    right.saturating_sub(left),
                    bottom.saturating_sub(top),
                )
            }
            None => (x, y, width, height),
        });
    }

    let (x, y, width, height) = union?;
    Some(InteractionEffectRoi {
        target_ids,
        region: RegionSpec::new("interaction-effect-roi", x, y, width, height, 1.0),
    })
}

fn is_effect_state(state: &str) -> bool {
    let state = state.trim();
    ["hovered", "pressed", "focused"]
        .iter()
        .any(|candidate| candidate.eq_ignore_ascii_case(state))
}

fn control_bounds_dips_to_pixel_roi(
    bounds: &ManifestControlBoundsDips,
    dpi_scale: f64,
    image_width: u32,
    image_height: u32,
) -> Option<(u32, u32, u32, u32)> {
    if !bounds.left.is_finite()
        || !bounds.top.is_finite()
        || !bounds.width.is_finite()
        || !bounds.height.is_finite()
        || bounds.width <= 0.0
        || bounds.height <= 0.0
    {
        return None;
    }

    let padding_dips = 8.0;
    let scale = if dpi_scale.is_finite() && dpi_scale > 0.0 {
        dpi_scale
    } else {
        1.0
    };
    let image_width = image_width as f64;
    let image_height = image_height as f64;
    let left = ((bounds.left - padding_dips) * scale)
        .floor()
        .clamp(0.0, image_width);
    let top = ((bounds.top - padding_dips) * scale)
        .floor()
        .clamp(0.0, image_height);
    let right = ((bounds.left + bounds.width + padding_dips) * scale)
        .ceil()
        .clamp(0.0, image_width);
    let bottom = ((bounds.top + bounds.height + padding_dips) * scale)
        .ceil()
        .clamp(0.0, image_height);
    if right <= left || bottom <= top {
        return None;
    }

    Some((
        left as u32,
        top as u32,
        (right - left) as u32,
        (bottom - top) as u32,
    ))
}

fn normalize_to_reference(image: &RgbaImage, width: u32, height: u32) -> RgbaImage {
    if image.width() == width && image.height() == height {
        image.clone()
    } else {
        imageops::resize(image, width, height, FilterType::Lanczos3)
    }
}

fn compare_dip_normalized_shared_viewport(
    reference: &RgbaImage,
    candidate: &RgbaImage,
    metadata: Option<&ManifestScenario>,
) -> Option<DipNormalizedViewportComparison> {
    let metadata = metadata?;
    let reference = normalize_window_image_to_dips(reference, metadata.reference_window.as_ref())?;
    let candidate = normalize_window_image_to_dips(candidate, metadata.candidate_window.as_ref())?;
    let width = reference.width().min(candidate.width());
    let height = reference.height().min(candidate.height());
    if width == 0 || height == 0 {
        return None;
    }

    let reference = crop_top_left(&reference, width, height);
    let candidate = crop_top_left(&candidate, width, height);
    let region = RegionSpec::full(width, height);
    let pixel = compare_pixels(&reference, &candidate, &region);
    let ssim = calculate_ssim(&reference, &candidate, &region);

    Some(DipNormalizedViewportComparison {
        reference,
        candidate,
        width,
        height,
        pixel,
        ssim,
    })
}

fn normalize_window_image_to_dips(
    image: &RgbaImage,
    window: Option<&ManifestWindow>,
) -> Option<RgbaImage> {
    let scale = window?.dpi_scale;
    if !scale.is_finite() || scale <= 0.0 {
        return None;
    }

    let width = ((image.width() as f64) / scale).round().max(1.0) as u32;
    let height = ((image.height() as f64) / scale).round().max(1.0) as u32;
    Some(normalize_to_reference(image, width, height))
}

fn crop_top_left(image: &RgbaImage, width: u32, height: u32) -> RgbaImage {
    imageops::crop_imm(image, 0, 0, width, height).to_image()
}

fn analyze_region(
    reference: &RgbaImage,
    candidate: &RgbaImage,
    region: &RegionSpec,
) -> RegionResult {
    let pixel = compare_pixels(reference, candidate, region);
    let ssim = calculate_ssim(reference, candidate, region);
    let score =
        clamp_score((ssim * 100.0 * 0.68) + (pixel_score(pixel.pixel_error_percent) * 0.32));
    RegionResult {
        name: region.name.clone(),
        weight: region.weight,
        bounds: RegionBounds {
            x: region.x,
            y: region.y,
            width: region.width,
            height: region.height,
        },
        score: round2(score),
        pixel_error_percent: round4(pixel.pixel_error_percent),
        ssim: round5(ssim),
        mean_channel_delta: round4(pixel.mean_channel_delta),
        max_channel_delta: pixel.max_channel_delta,
    }
}

fn compare_pixels(
    reference: &RgbaImage,
    candidate: &RgbaImage,
    region: &RegionSpec,
) -> PixelComparison {
    let mut changed = 0_u64;
    let mut total = 0_u64;
    let mut total_delta = 0.0;
    let mut max_delta = 0_i32;
    for y in region.y..region.y + region.height {
        for x in region.x..region.x + region.width {
            let before = reference.get_pixel(x, y);
            let after = candidate.get_pixel(x, y);
            let red = (before[0] as i16 - after[0] as i16).abs();
            let green = (before[1] as i16 - after[1] as i16).abs();
            let blue = (before[2] as i16 - after[2] as i16).abs();
            let alpha = (before[3] as i16 - after[3] as i16).abs();
            let pixel_max = red.max(green).max(blue).max(alpha);
            if pixel_max > PIXEL_DELTA_TOLERANCE {
                changed += 1;
            }
            max_delta = max_delta.max(pixel_max as i32);
            total_delta += red as f64 + green as f64 + blue as f64;
            total += 1;
        }
    }
    PixelComparison {
        pixel_error_percent: if total == 0 {
            0.0
        } else {
            changed as f64 * 100.0 / total as f64
        },
        mean_channel_delta: if total == 0 {
            0.0
        } else {
            total_delta / (total as f64 * 3.0)
        },
        max_channel_delta: max_delta,
    }
}

fn calculate_ssim(reference: &RgbaImage, candidate: &RgbaImage, region: &RegionSpec) -> f64 {
    let mut count = 0.0;
    let mut sum_x = 0.0;
    let mut sum_y = 0.0;
    let mut sum_x2 = 0.0;
    let mut sum_y2 = 0.0;
    let mut sum_xy = 0.0;
    let step_x = (region.width / 320).max(1);
    let step_y = (region.height / 320).max(1);
    let mut y = region.y;
    while y < region.y + region.height {
        let mut x = region.x;
        while x < region.x + region.width {
            let lx = luminance(reference.get_pixel(x, y));
            let ly = luminance(candidate.get_pixel(x, y));
            sum_x += lx;
            sum_y += ly;
            sum_x2 += lx * lx;
            sum_y2 += ly * ly;
            sum_xy += lx * ly;
            count += 1.0;
            x += step_x;
        }
        y += step_y;
    }
    if count <= 1.0 {
        return 1.0;
    }
    let mu_x = sum_x / count;
    let mu_y = sum_y / count;
    let sigma_x = (sum_x2 / count) - (mu_x * mu_x);
    let sigma_y = (sum_y2 / count) - (mu_y * mu_y);
    let sigma_xy = (sum_xy / count) - (mu_x * mu_y);
    let c1 = 6.5025;
    let c2 = 58.5225;
    let numerator = ((2.0 * mu_x * mu_y) + c1) * ((2.0 * sigma_xy) + c2);
    let denominator = ((mu_x * mu_x) + (mu_y * mu_y) + c1) * (sigma_x + sigma_y + c2);
    if denominator == 0.0 {
        1.0
    } else {
        (numerator / denominator).clamp(0.0, 1.0)
    }
}

fn calculate_dhash(image: &RgbaImage) -> u64 {
    let resized = imageops::resize(image, 9, 8, FilterType::Triangle);
    let mut hash = 0_u64;
    let mut bit = 0;
    for y in 0..8 {
        for x in 0..8 {
            if luminance(resized.get_pixel(x, y)) > luminance(resized.get_pixel(x + 1, y)) {
                hash |= 1_u64 << bit;
            }
            bit += 1;
        }
    }
    hash
}

fn calculate_phash(image: &RgbaImage) -> u64 {
    const SOURCE: usize = 32;
    const HASH: usize = 8;
    let resized = imageops::resize(image, SOURCE as u32, SOURCE as u32, FilterType::Triangle);
    let mut pixels = [[0.0_f64; SOURCE]; SOURCE];
    for y in 0..SOURCE {
        for x in 0..SOURCE {
            pixels[x][y] = luminance(resized.get_pixel(x as u32, y as u32));
        }
    }
    let mut coefficients = [[0.0_f64; HASH]; HASH];
    for u in 0..HASH {
        for v in 0..HASH {
            let mut sum = 0.0;
            for x in 0..SOURCE {
                for y in 0..SOURCE {
                    sum += pixels[x][y]
                        * (((2.0 * x as f64) + 1.0) * u as f64 * std::f64::consts::PI
                            / (2.0 * SOURCE as f64))
                            .cos()
                        * (((2.0 * y as f64) + 1.0) * v as f64 * std::f64::consts::PI
                            / (2.0 * SOURCE as f64))
                            .cos();
                }
            }
            coefficients[u][v] = sum;
        }
    }
    let mut values = Vec::with_capacity(HASH * HASH - 1);
    for (u, row) in coefficients.iter().enumerate().take(HASH) {
        for (v, value) in row.iter().enumerate().take(HASH) {
            if u != 0 || v != 0 {
                values.push(*value);
            }
        }
    }
    values.sort_by(|a, b| a.partial_cmp(b).unwrap_or(std::cmp::Ordering::Equal));
    let median = values[values.len() / 2];
    let mut hash = 0_u64;
    let mut bit = 0;
    for (u, row) in coefficients.iter().enumerate().take(HASH) {
        for (v, value) in row.iter().enumerate().take(HASH) {
            if u == 0 && v == 0 {
                continue;
            }
            if *value > median {
                hash |= 1_u64 << bit;
            }
            bit += 1;
        }
    }
    hash
}

fn compare_palette(reference: &RgbaImage, candidate: &RgbaImage) -> PaletteComparison {
    let reference_average = average_color(reference);
    let candidate_average = average_color(candidate);
    let average_color_delta = color_distance(&reference_average, &candidate_average);
    PaletteComparison {
        reference_average,
        candidate_average,
        average_color_delta,
    }
}

fn average_color(image: &RgbaImage) -> ColorVector {
    let step_x = (image.width() / 320).max(1);
    let step_y = (image.height() / 320).max(1);
    let mut red = 0.0;
    let mut green = 0.0;
    let mut blue = 0.0;
    let mut count = 0.0;
    let mut y = 0;
    while y < image.height() {
        let mut x = 0;
        while x < image.width() {
            let pixel = image.get_pixel(x, y);
            red += pixel[0] as f64;
            green += pixel[1] as f64;
            blue += pixel[2] as f64;
            count += 1.0;
            x += step_x;
        }
        y += step_y;
    }
    ColorVector {
        r: round2(red / count),
        g: round2(green / count),
        b: round2(blue / count),
    }
}

fn save_diff_heatmap(
    reference: &RgbaImage,
    candidate: &RgbaImage,
    path: &Path,
) -> Result<(), String> {
    let mut output = RgbaImage::new(reference.width(), reference.height());
    for y in 0..reference.height() {
        for x in 0..reference.width() {
            let before = reference.get_pixel(x, y);
            let after = candidate.get_pixel(x, y);
            let gray = luminance(before).clamp(0.0, 255.0) as u8;
            let delta = ((before[0] as f64 - after[0] as f64).abs()
                + (before[1] as f64 - after[1] as f64).abs()
                + (before[2] as f64 - after[2] as f64).abs())
                / 3.0;
            if delta <= PIXEL_DELTA_TOLERANCE as f64 {
                output.put_pixel(x, y, Rgba([gray, gray, gray, 255]));
            } else {
                let alpha = ((delta - PIXEL_DELTA_TOLERANCE as f64) / 110.0).clamp(0.20, 0.88);
                output.put_pixel(
                    x,
                    y,
                    Rgba([
                        ((gray as f64 * (1.0 - alpha)) + (255.0 * alpha)).clamp(0.0, 255.0) as u8,
                        (gray as f64 * (1.0 - alpha)).clamp(0.0, 255.0) as u8,
                        (gray as f64 * (1.0 - alpha)).clamp(0.0, 255.0) as u8,
                        255,
                    ]),
                );
            }
        }
    }
    output.save(path).map_err(|error| error.to_string())
}

fn save_contact_sheet(
    reference: &RgbaImage,
    candidate: &RgbaImage,
    path: &Path,
) -> Result<(), String> {
    let gap = 16;
    let mut output = RgbaImage::from_pixel(
        reference.width() + candidate.width() + gap,
        reference.height().max(candidate.height()),
        Rgba([255, 255, 255, 255]),
    );
    imageops::overlay(&mut output, reference, 0, 0);
    imageops::overlay(&mut output, candidate, (reference.width() + gap) as i64, 0);
    output.save(path).map_err(|error| error.to_string())
}

fn default_regions(width: u32, height: u32, window_kind: Option<&str>) -> Vec<RegionSpec> {
    match window_kind
        .unwrap_or_default()
        .to_ascii_lowercase()
        .as_str()
    {
        "main" => regions_from_fractions(
            width,
            height,
            &[
                FractionalRegion::new("main-header", 0.0, 0.0, 1.0, 0.17, 1.1),
                FractionalRegion::new("source-input", 0.0, 0.17, 1.0, 0.23, 1.4),
                FractionalRegion::new("result-list", 0.0, 0.40, 1.0, 0.50, 2.2),
                FractionalRegion::new("footer", 0.0, 0.90, 1.0, 0.10, 0.7),
            ],
        ),
        "mini" | "fixed" | "popbutton" => regions_from_fractions(
            width,
            height,
            &[
                FractionalRegion::new("floating-toolbar", 0.0, 0.0, 1.0, 0.18, 1.0),
                FractionalRegion::new("floating-content", 0.0, 0.18, 1.0, 0.68, 2.4),
                FractionalRegion::new("floating-footer", 0.0, 0.86, 1.0, 0.14, 0.7),
            ],
        ),
        "ocr" | "capture" => regions_from_fractions(
            width,
            height,
            &[
                FractionalRegion::new("ocr-overlay", 0.0, 0.0, 1.0, 1.0, 1.0),
                FractionalRegion::new("ocr-center-selection", 0.20, 0.20, 0.60, 0.60, 2.0),
            ],
        ),
        "long-document" => regions_from_fractions(
            width,
            height,
            &[
                FractionalRegion::new("long-doc-header", 0.0, 0.0, 1.0, 0.16, 1.0),
                FractionalRegion::new("long-doc-controls", 0.0, 0.16, 1.0, 0.30, 1.6),
                FractionalRegion::new("long-doc-output", 0.0, 0.46, 1.0, 0.44, 2.2),
                FractionalRegion::new("footer", 0.0, 0.90, 1.0, 0.10, 0.7),
            ],
        ),
        _ => {
            let header_height = ((height as f64 * 0.12).round() as u32).max(1);
            let nav_top = header_height;
            let nav_height = ((height as f64 * 0.14).round() as u32).max(1);
            let footer_height = ((height as f64 * 0.10).round() as u32).max(1);
            let content_top = (nav_top + nav_height).min(height.saturating_sub(1));
            let content_bottom = (height.saturating_sub(footer_height)).max(content_top + 1);
            vec![
                RegionSpec::new("header", 0, 0, width, header_height, 1.0),
                RegionSpec::new(
                    "top-navigation",
                    0,
                    nav_top,
                    width,
                    nav_height.min(height - nav_top),
                    1.0,
                ),
                RegionSpec::new(
                    "content",
                    0,
                    content_top,
                    width,
                    content_bottom - content_top,
                    2.2,
                ),
                RegionSpec::new(
                    "footer",
                    0,
                    height - footer_height,
                    width,
                    footer_height,
                    0.8,
                ),
            ]
        }
    }
}

fn regions_from_fractions(
    width: u32,
    height: u32,
    regions: &[FractionalRegion],
) -> Vec<RegionSpec> {
    regions
        .iter()
        .map(|region| {
            let x = clamp_to_range(
                (region.x * width as f64).round() as i64,
                0,
                width.saturating_sub(1) as i64,
            );
            let y = clamp_to_range(
                (region.y * height as f64).round() as i64,
                0,
                height.saturating_sub(1) as i64,
            );
            let right = clamp_to_range(
                ((region.x + region.width) * width as f64).round() as i64,
                x + 1,
                width as i64,
            );
            let bottom = clamp_to_range(
                ((region.y + region.height) * height as f64).round() as i64,
                y + 1,
                height as i64,
            );
            RegionSpec::new(
                &region.name,
                x as u32,
                y as u32,
                (right - x) as u32,
                (bottom - y) as u32,
                region.weight,
            )
        })
        .collect()
}

fn regions_from_manifest(width: u32, height: u32, regions: &[ManifestRegion]) -> Vec<RegionSpec> {
    let specs = regions
        .iter()
        .enumerate()
        .map(|(index, region)| {
            let region_name = if region.name.trim().is_empty() {
                format!("region-{}", index + 1)
            } else {
                region.name.clone()
            };
            let x = clamp_to_range(
                (region.x * width as f64).round() as i64,
                0,
                width.saturating_sub(1) as i64,
            );
            let y = clamp_to_range(
                (region.y * height as f64).round() as i64,
                0,
                height.saturating_sub(1) as i64,
            );
            let right = clamp_to_range(
                ((region.x + region.width) * width as f64).round() as i64,
                x + 1,
                width as i64,
            );
            let bottom = clamp_to_range(
                ((region.y + region.height) * height as f64).round() as i64,
                y + 1,
                height as i64,
            );
            RegionSpec::new(
                &region_name,
                x as u32,
                y as u32,
                (right - x) as u32,
                (bottom - y) as u32,
                if region.weight <= 0.0 {
                    1.0
                } else {
                    region.weight
                },
            )
        })
        .collect::<Vec<_>>();
    if specs.is_empty() {
        default_regions(width, height, None)
    } else {
        specs
    }
}

fn calculate_size_score(
    reference_width: u32,
    reference_height: u32,
    candidate_width: u32,
    candidate_height: u32,
) -> f64 {
    let width_delta =
        (reference_width as f64 - candidate_width as f64).abs() / (reference_width.max(1) as f64);
    let height_delta = (reference_height as f64 - candidate_height as f64).abs()
        / (reference_height.max(1) as f64);
    clamp_score(100.0 - ((width_delta + height_delta) * 500.0))
}

fn calculate_effective_size_score(pair: &ScreenshotPair) -> Option<f64> {
    let delta = effective_candidate_window_size_delta_percent(Some(pair.metadata.as_ref()?))?;
    Some(size_score_from_axis_delta_percent(
        delta.width.abs(),
        delta.height.abs(),
    ))
}

fn size_score_from_axis_delta_percent(width_percent: f64, height_percent: f64) -> f64 {
    clamp_score(100.0 - ((width_percent + height_percent) * 5.0))
}

fn compare_ui_summaries(metadata: Option<&ManifestScenario>) -> Option<UiSummaryComparison> {
    let metadata = metadata?;
    let reference_summary = metadata.reference_ui_summary.as_ref()?;
    let candidate_summary = metadata.candidate_ui_summary.as_ref()?;
    let reference_counts = &reference_summary.visible_control_counts;
    let candidate_counts = &candidate_summary.visible_control_counts;
    let reference_ids = to_canonical_semantic_set(
        metadata
            .reference_ui_summary
            .as_ref()
            .and_then(|summary| summary.visible_automation_ids.as_ref()),
        metadata,
    );
    let candidate_ids = to_canonical_semantic_set(
        metadata
            .candidate_ui_summary
            .as_ref()
            .and_then(|summary| summary.visible_automation_ids.as_ref()),
        metadata,
    );
    let candidate_texts = to_canonical_visible_text_set(
        metadata
            .candidate_ui_summary
            .as_ref()
            .and_then(|summary| summary.visible_texts.as_ref()),
    );
    let visible_text_comparison = compare_visible_texts(
        reference_summary.visible_texts.as_ref(),
        candidate_summary.visible_texts.as_ref(),
    );
    let required_tags = metadata
        .required_semantic_tags
        .iter()
        .filter(|tag| !tag.trim().is_empty())
        .filter_map(|tag| canonical_semantic_id(tag, metadata))
        .collect::<BTreeSet<_>>();
    let required_visible_texts = metadata
        .required_visible_texts
        .iter()
        .map(|text| text.trim())
        .filter(|text| !text.is_empty())
        .collect::<Vec<_>>();
    let missing_required_control_states =
        compare_required_control_states(&metadata.required_control_states, candidate_summary);
    let control_dimension_deltas = compare_control_dimensions(
        &metadata.required_semantic_tags,
        &metadata.required_control_states,
        reference_summary,
        candidate_summary,
    );
    let missing_control_bounds_evidence = collect_missing_control_bounds_evidence(
        &metadata.required_semantic_tags,
        &metadata.required_control_states,
        reference_summary,
        candidate_summary,
    );
    let max_control_dimension_delta_dips = control_dimension_deltas
        .iter()
        .map(|delta| delta.delta_abs)
        .fold(0.0_f64, f64::max);
    let missing_required_tags = required_tags
        .iter()
        .filter(|tag| !candidate_ids.contains(*tag))
        .cloned()
        .collect::<Vec<_>>();
    let missing_required_visible_texts = required_visible_texts
        .iter()
        .filter(|text| !candidate_texts.contains(&canonical_visible_text(text)))
        .map(|text| (*text).to_string())
        .collect::<Vec<_>>();
    let reference_total_empty =
        reference_counts.values().sum::<i32>() == 0 && reference_ids.is_empty();
    if reference_total_empty
        && (!required_tags.is_empty()
            || !required_visible_texts.is_empty()
            || !metadata.required_control_states.is_empty())
        && control_dimension_deltas.is_empty()
    {
        let tag_only_score = clamp_score(
            100.0
                - ((missing_required_tags.len()
                    + missing_required_visible_texts.len()
                    + missing_required_control_states.len()) as f64
                    * 35.0),
        );
        return Some(UiSummaryComparison {
            score: tag_only_score,
            control_count_delta_percent: 0.0,
            control_count_deltas: Vec::new(),
            automation_id_jaccard: None,
            visible_text_jaccard: visible_text_comparison.jaccard,
            missing_reference_visible_texts: visible_text_comparison.missing_reference_texts,
            extra_candidate_visible_texts: visible_text_comparison.extra_candidate_texts,
            missing_required_semantic_tags: missing_required_tags,
            missing_required_visible_texts,
            missing_required_control_states,
            control_dimension_delta_count: 0,
            max_control_dimension_delta_dips: 0.0,
            control_dimension_deltas,
            missing_control_bounds_evidence,
        });
    }

    let mut keys = BTreeSet::new();
    keys.extend(reference_counts.keys().map(|key| key.to_ascii_lowercase()));
    keys.extend(candidate_counts.keys().map(|key| key.to_ascii_lowercase()));
    if keys.is_empty()
        && visible_text_comparison.jaccard.is_none()
        && control_dimension_deltas.is_empty()
        && missing_control_bounds_evidence.is_empty()
        && missing_required_control_states.is_empty()
    {
        return None;
    }

    let mut reference_total = 0_i32;
    let mut delta_total = 0_i32;
    let mut control_count_deltas = Vec::new();
    for key in keys {
        let reference_count = get_case_insensitive_count(reference_counts, &key);
        let candidate_count = get_case_insensitive_count(candidate_counts, &key);
        let delta_abs = (reference_count - candidate_count).abs();
        reference_total += reference_count;
        delta_total += delta_abs;
        if delta_abs > 0 {
            control_count_deltas.push(ControlCountDelta {
                kind: key,
                reference_count,
                candidate_count,
                delta_abs,
            });
        }
    }
    let delta_percent = delta_total as f64 * 100.0 / reference_total.max(1) as f64;
    let control_count_score = clamp_score(100.0 - (delta_percent * 1.35));
    let automation_id_jaccard = if reference_ids.is_empty() && candidate_ids.is_empty() {
        None
    } else {
        let union = reference_ids.union(&candidate_ids).count();
        let intersection = reference_ids.intersection(&candidate_ids).count();
        Some(if union == 0 {
            100.0
        } else {
            intersection as f64 * 100.0 / union as f64
        })
    };
    let required_control_state_count = metadata
        .required_control_states
        .values()
        .map(Vec::len)
        .sum::<usize>();
    let required_evidence_count =
        required_tags.len() + required_visible_texts.len() + required_control_state_count;
    let missing_required_evidence_count = missing_required_tags.len()
        + missing_required_visible_texts.len()
        + missing_required_control_states.len();
    let required_tag_score = if required_evidence_count == 0 {
        None
    } else {
        Some(clamp_score(
            100.0 - (missing_required_evidence_count as f64 * 35.0),
        ))
    };
    let mut weighted = control_count_score * 0.45;
    let mut weight = 0.45;
    if let Some(score) = automation_id_jaccard {
        weighted += score * 0.35;
        weight += 0.35;
    }
    if let Some(score) = required_tag_score {
        weighted += score * 0.20;
        weight += 0.20;
    }
    if let Some(score) = visible_text_comparison.jaccard {
        weighted += score * 0.15;
        weight += 0.15;
    }
    if !control_dimension_deltas.is_empty() {
        let dimension_score = clamp_score(
            100.0
                - (max_control_dimension_delta_dips * 4.0)
                - (control_dimension_deltas.len() as f64 * 2.0),
        );
        weighted += dimension_score * 0.15;
        weight += 0.15;
    }
    Some(UiSummaryComparison {
        score: clamp_score(weighted / weight),
        control_count_delta_percent: delta_percent,
        control_count_deltas,
        automation_id_jaccard,
        visible_text_jaccard: visible_text_comparison.jaccard,
        missing_reference_visible_texts: visible_text_comparison.missing_reference_texts,
        extra_candidate_visible_texts: visible_text_comparison.extra_candidate_texts,
        missing_required_semantic_tags: missing_required_tags,
        missing_required_visible_texts,
        missing_required_control_states,
        control_dimension_delta_count: control_dimension_deltas.len(),
        max_control_dimension_delta_dips,
        control_dimension_deltas,
        missing_control_bounds_evidence,
    })
}

fn build_evidence_audit(metadata: Option<&ManifestScenario>) -> Option<EvidenceAudit> {
    let metadata = metadata?;
    let reference_summary = metadata.reference_ui_summary.as_ref()?;
    let candidate_summary = metadata.candidate_ui_summary.as_ref()?;
    let reference_ids = sorted_visible_ids(reference_summary.visible_automation_ids.as_ref());
    let candidate_ids = sorted_visible_ids(candidate_summary.visible_automation_ids.as_ref());
    let missing_candidate_automation_ids = reference_ids
        .iter()
        .filter(|id| !contains_case_insensitive(&candidate_ids, id))
        .cloned()
        .collect::<Vec<_>>();

    let reference_dimensions = reference_summary.visible_control_dimensions.as_ref();
    let candidate_dimensions = candidate_summary.visible_control_dimensions.as_ref();
    let reference_dimension_ids = sorted_dimension_ids(reference_dimensions);
    let candidate_dimension_ids = sorted_dimension_ids(candidate_dimensions);
    let missing_candidate_dimension_ids = reference_dimension_ids
        .iter()
        .filter(|id| {
            candidate_dimensions
                .and_then(|dimensions| get_case_insensitive_dimension(dimensions, id))
                .is_none()
        })
        .cloned()
        .collect::<Vec<_>>();
    let reference_control_bounds_count = reference_dimensions
        .map(|dimensions| {
            dimensions
                .values()
                .filter(|dimension| dimension.bounds_dips.is_some())
                .count()
        })
        .unwrap_or_default();
    let candidate_control_bounds_count = candidate_dimensions
        .map(|dimensions| {
            dimensions
                .values()
                .filter(|dimension| dimension.bounds_dips.is_some())
                .count()
        })
        .unwrap_or_default();

    let mut missing_candidate_bounds = Vec::new();
    if let Some(reference_dimensions) = reference_dimensions {
        for id in &reference_dimension_ids {
            let Some(reference) = get_case_insensitive_dimension(reference_dimensions, id) else {
                continue;
            };
            let Some(reference_bounds) = reference.bounds_dips.as_ref() else {
                continue;
            };
            let candidate = candidate_dimensions
                .and_then(|dimensions| get_case_insensitive_dimension(dimensions, id));
            if candidate
                .and_then(|dimension| dimension.bounds_dips.as_ref())
                .is_some()
            {
                continue;
            }
            missing_candidate_bounds.push(ControlBoundsEvidenceGap {
                id: id.clone(),
                reference_bounds: format_bounds_dips(reference_bounds),
                candidate: if candidate.is_some() {
                    "missing bounds_dips evidence".to_string()
                } else {
                    "missing control dimension evidence".to_string()
                },
            });
        }
    }

    let candidate_dimension_without_bounds_ids = candidate_dimensions
        .map(|dimensions| {
            let mut ids = dimensions
                .iter()
                .filter(|(_, dimension)| dimension.bounds_dips.is_none())
                .map(|(id, _)| id.clone())
                .collect::<Vec<_>>();
            ids.sort_by_key(|id| id.to_ascii_lowercase());
            ids
        })
        .unwrap_or_default();

    Some(EvidenceAudit {
        reference_automation_id_count: reference_ids.len(),
        candidate_automation_id_count: candidate_ids.len(),
        missing_candidate_automation_id_count: missing_candidate_automation_ids.len(),
        missing_candidate_automation_ids,
        reference_control_dimension_count: reference_dimension_ids.len(),
        candidate_control_dimension_count: candidate_dimension_ids.len(),
        missing_candidate_dimension_count: missing_candidate_dimension_ids.len(),
        missing_candidate_dimension_ids,
        reference_control_bounds_count,
        candidate_control_bounds_count,
        missing_candidate_bounds_count: missing_candidate_bounds.len(),
        missing_candidate_bounds,
        candidate_dimension_without_bounds_count: candidate_dimension_without_bounds_ids.len(),
        candidate_dimension_without_bounds_ids,
    })
}

fn sorted_visible_ids(values: Option<&Vec<String>>) -> Vec<String> {
    let mut ids = values
        .into_iter()
        .flatten()
        .map(|id| id.trim())
        .filter(|id| !id.is_empty())
        .map(ToString::to_string)
        .collect::<Vec<_>>();
    ids.sort_by_key(|id| id.to_ascii_lowercase());
    ids.dedup_by(|left, right| left.eq_ignore_ascii_case(right));
    ids
}

fn sorted_dimension_ids(
    dimensions: Option<&BTreeMap<String, ManifestControlDimension>>,
) -> Vec<String> {
    let mut ids = dimensions
        .into_iter()
        .flat_map(|dimensions| dimensions.keys())
        .map(|id| id.trim())
        .filter(|id| !id.is_empty())
        .map(ToString::to_string)
        .collect::<Vec<_>>();
    ids.sort_by_key(|id| id.to_ascii_lowercase());
    ids
}

fn contains_case_insensitive(values: &[String], needle: &str) -> bool {
    values
        .iter()
        .any(|value| value.eq_ignore_ascii_case(needle))
}

fn compare_required_control_states(
    required_control_states: &BTreeMap<String, Vec<String>>,
    candidate_summary: &ManifestUiSummary,
) -> Vec<ControlStateDelta> {
    let Some(candidate_dimensions) = candidate_summary.visible_control_dimensions.as_ref() else {
        return required_control_states
            .iter()
            .flat_map(|(id, states)| {
                states.iter().map(|state| ControlStateDelta {
                    id: id.clone(),
                    required_state: state.clone(),
                    candidate_state: None,
                })
            })
            .collect();
    };

    let mut deltas = Vec::new();
    for (id, required_states) in required_control_states {
        let candidate_state = get_case_insensitive_dimension(candidate_dimensions, id)
            .and_then(|dimension| dimension.state.clone());
        for required_state in required_states {
            if !candidate_state_has(&candidate_state, required_state) {
                deltas.push(ControlStateDelta {
                    id: id.clone(),
                    required_state: required_state.clone(),
                    candidate_state: candidate_state.clone(),
                });
            }
        }
    }
    deltas
}

fn candidate_state_has(candidate_state: &Option<String>, required_state: &str) -> bool {
    let required_state = required_state.trim();
    if required_state.is_empty() {
        return true;
    }
    let Some(candidate_state) = candidate_state.as_deref() else {
        return false;
    };
    candidate_state
        .split(',')
        .filter_map(|part| part.split_once('='))
        .any(|(name, value)| {
            name.eq_ignore_ascii_case(required_state) && value.eq_ignore_ascii_case("true")
        })
}

fn compare_control_dimensions(
    required_semantic_tags: &[String],
    required_control_states: &BTreeMap<String, Vec<String>>,
    reference_summary: &ManifestUiSummary,
    candidate_summary: &ManifestUiSummary,
) -> Vec<ControlDimensionDelta> {
    let Some(reference_dimensions) = reference_summary.visible_control_dimensions.as_ref() else {
        return Vec::new();
    };
    let Some(candidate_dimensions) = candidate_summary.visible_control_dimensions.as_ref() else {
        return Vec::new();
    };

    let ids = required_control_dimension_ids(
        required_semantic_tags,
        required_control_states,
        reference_dimensions,
        candidate_dimensions,
    );

    let mut deltas = Vec::new();
    for id in ids {
        let Some(reference) = get_case_insensitive_dimension(reference_dimensions, &id) else {
            continue;
        };
        let candidate = get_case_insensitive_dimension(candidate_dimensions, &id);
        for (property, reference_value, candidate_value) in
            control_dimension_properties(reference, candidate)
        {
            let Some(reference_value) = reference_value else {
                continue;
            };
            let Some(candidate_value) = candidate_value else {
                deltas.push(ControlDimensionDelta {
                    id: id.clone(),
                    property,
                    reference: reference_value.to_string(),
                    candidate: "missing dimension evidence".to_string(),
                    delta_abs: MISSING_CONTROL_DIMENSION_EVIDENCE_DELTA_DIPS,
                });
                continue;
            };
            let Some(delta_abs) = dimension_value_delta_abs(reference_value, candidate_value)
            else {
                continue;
            };
            if should_treat_control_dimension_delta_as_chrome_layer_diagnostic(
                &id, property, reference, candidate, delta_abs,
            ) {
                continue;
            }
            if delta_abs > 0.01 {
                deltas.push(ControlDimensionDelta {
                    id: id.clone(),
                    property,
                    reference: reference_value.to_string(),
                    candidate: candidate_value.to_string(),
                    delta_abs,
                });
            }
        }
        append_control_bounds_deltas(&mut deltas, &id, reference, candidate);
    }
    deltas.sort_by(|a, b| {
        b.delta_abs
            .partial_cmp(&a.delta_abs)
            .unwrap_or(std::cmp::Ordering::Equal)
            .then_with(|| a.id.cmp(&b.id))
            .then_with(|| a.property.cmp(b.property))
    });
    deltas
}

fn should_treat_control_dimension_delta_as_chrome_layer_diagnostic(
    id: &str,
    property: &str,
    reference: &ManifestControlDimension,
    candidate: Option<&ManifestControlDimension>,
    delta_abs: f64,
) -> bool {
    if !id.eq_ignore_ascii_case("InputTextBox") || property != "height" {
        return false;
    }
    if delta_abs > 24.0 {
        return false;
    }

    let Some(candidate) = candidate else {
        return false;
    };
    let reference_kind = reference.kind.as_deref().unwrap_or_default();
    let candidate_kind = candidate.kind.as_deref().unwrap_or_default();
    reference_kind.eq_ignore_ascii_case("Edit") && candidate_kind.eq_ignore_ascii_case("TextEditor")
}

const MISSING_CONTROL_DIMENSION_EVIDENCE_DELTA_DIPS: f64 = 9.0;

fn required_control_dimension_ids(
    required_semantic_tags: &[String],
    required_control_states: &BTreeMap<String, Vec<String>>,
    reference_dimensions: &BTreeMap<String, ManifestControlDimension>,
    candidate_dimensions: &BTreeMap<String, ManifestControlDimension>,
) -> Vec<String> {
    let mut ids = Vec::<String>::new();
    let mut seen = BTreeSet::<String>::new();
    for tag in required_semantic_tags {
        let tag = tag.trim();
        if !tag.is_empty() && seen.insert(tag.to_ascii_lowercase()) {
            ids.push(tag.to_string());
        }
    }
    for id in required_control_states.keys() {
        let id = id.trim();
        if !id.is_empty() && seen.insert(id.to_ascii_lowercase()) {
            ids.push(id.to_string());
        }
    }
    if ids.is_empty() {
        for id in reference_dimensions.keys() {
            if candidate_dimensions
                .keys()
                .any(|candidate| candidate.eq_ignore_ascii_case(id))
                && seen.insert(id.to_ascii_lowercase())
            {
                ids.push(id.clone());
            }
        }
    }
    ids
}

fn collect_missing_control_bounds_evidence(
    required_semantic_tags: &[String],
    required_control_states: &BTreeMap<String, Vec<String>>,
    reference_summary: &ManifestUiSummary,
    candidate_summary: &ManifestUiSummary,
) -> Vec<ControlBoundsEvidenceGap> {
    let Some(reference_dimensions) = reference_summary.visible_control_dimensions.as_ref() else {
        return Vec::new();
    };
    let Some(candidate_dimensions) = candidate_summary.visible_control_dimensions.as_ref() else {
        return Vec::new();
    };

    required_control_dimension_ids(
        required_semantic_tags,
        required_control_states,
        reference_dimensions,
        candidate_dimensions,
    )
    .into_iter()
    .filter_map(|id| {
        let reference = get_case_insensitive_dimension(reference_dimensions, &id)?;
        let reference_bounds = reference.bounds_dips.as_ref()?;
        let candidate = get_case_insensitive_dimension(candidate_dimensions, &id);
        if candidate
            .and_then(|dimension| dimension.bounds_dips.as_ref())
            .is_some()
        {
            return None;
        }
        Some(ControlBoundsEvidenceGap {
            id,
            reference_bounds: format_bounds_dips(reference_bounds),
            candidate: if candidate.is_some() {
                "missing bounds_dips evidence".to_string()
            } else {
                "missing control dimension evidence".to_string()
            },
        })
    })
    .collect()
}

fn append_control_bounds_deltas(
    deltas: &mut Vec<ControlDimensionDelta>,
    id: &str,
    reference: &ManifestControlDimension,
    candidate: Option<&ManifestControlDimension>,
) {
    let Some(reference_bounds) = reference.bounds_dips.as_ref() else {
        return;
    };
    let Some(candidate_bounds) = candidate.and_then(|dimension| dimension.bounds_dips.as_ref())
    else {
        return;
    };

    for (property, reference_value, candidate_value) in [
        ("left", reference_bounds.left, candidate_bounds.left),
        ("top", reference_bounds.top, candidate_bounds.top),
    ] {
        let delta_abs = (reference_value - candidate_value).abs();
        if delta_abs > 0.01 {
            deltas.push(ControlDimensionDelta {
                id: id.to_string(),
                property,
                reference: format_dips(reference_value),
                candidate: format_dips(candidate_value),
                delta_abs,
            });
        }
    }
}

fn control_dimension_properties<'a>(
    reference: &'a ManifestControlDimension,
    candidate: Option<&'a ManifestControlDimension>,
) -> Vec<(&'static str, Option<&'a str>, Option<&'a str>)> {
    vec![
        (
            "width",
            reference.width.as_deref(),
            candidate_width_value(reference.width.as_deref(), candidate),
        ),
        (
            "height",
            reference.height.as_deref(),
            candidate_height_value(reference.height.as_deref(), candidate),
        ),
        (
            "max_width",
            reference.max_width.as_deref(),
            candidate.and_then(|value| value.max_width.as_deref()),
        ),
        (
            "min_width",
            reference.min_width.as_deref(),
            candidate.and_then(|value| value.min_width.as_deref()),
        ),
        (
            "min_height",
            reference.min_height.as_deref(),
            candidate.and_then(|value| value.min_height.as_deref()),
        ),
        (
            "max_height",
            reference.max_height.as_deref(),
            candidate.and_then(|value| value.max_height.as_deref()),
        ),
        (
            "padding",
            reference.padding.as_deref(),
            candidate.and_then(|value| value.padding.as_deref()),
        ),
        (
            "spacing",
            reference.spacing.as_deref(),
            candidate.and_then(|value| value.spacing.as_deref()),
        ),
        (
            "row_spacing",
            reference.row_spacing.as_deref(),
            candidate.and_then(|value| value.row_spacing.as_deref()),
        ),
        (
            "column_spacing",
            reference.column_spacing.as_deref(),
            candidate.and_then(|value| value.column_spacing.as_deref()),
        ),
        (
            "margin",
            reference.margin.as_deref(),
            candidate.and_then(|value| value.margin.as_deref()),
        ),
    ]
}

fn dimension_value_delta_abs(reference: &str, candidate: &str) -> Option<f64> {
    let reference_numbers = extract_dimension_numbers(reference);
    let candidate_numbers = extract_dimension_numbers(candidate);
    if reference_numbers.is_empty() || candidate_numbers.is_empty() {
        return None;
    }
    if reference_numbers.len() == candidate_numbers.len() {
        return reference_numbers
            .iter()
            .zip(candidate_numbers.iter())
            .map(|(reference, candidate)| (candidate - reference).abs())
            .max_by(|left, right| left.partial_cmp(right).unwrap_or(std::cmp::Ordering::Equal));
    }
    Some(
        candidate_numbers.iter().copied().fold(f64::MIN, f64::max)
            - reference_numbers.iter().copied().fold(f64::MIN, f64::max),
    )
    .map(f64::abs)
}

fn candidate_width_value<'a>(
    reference: Option<&str>,
    candidate: Option<&'a ManifestControlDimension>,
) -> Option<&'a str> {
    let candidate = candidate?;
    let width = candidate.width.as_deref();
    let labeled_width = candidate.labeled_width.as_deref();
    let Some(reference) = reference else {
        return width.or(labeled_width);
    };

    choose_closest_dimension_value(reference, [width, labeled_width]).or(width)
}

fn candidate_height_value<'a>(
    reference: Option<&str>,
    candidate: Option<&'a ManifestControlDimension>,
) -> Option<&'a str> {
    let candidate = candidate?;
    let height = candidate.height.as_deref();
    let labeled_height = candidate.labeled_height.as_deref();
    let Some(reference) = reference else {
        return height.or(labeled_height);
    };

    choose_closest_dimension_value(reference, [height, labeled_height]).or(height)
}

fn choose_closest_dimension_value<'a>(
    reference: &str,
    values: impl IntoIterator<Item = Option<&'a str>>,
) -> Option<&'a str> {
    values
        .into_iter()
        .flatten()
        .filter_map(|value| dimension_value_delta_abs(reference, value).map(|delta| (value, delta)))
        .min_by(|(_, left), (_, right)| {
            left.partial_cmp(right).unwrap_or(std::cmp::Ordering::Equal)
        })
        .map(|(value, _)| value)
}

fn extract_dimension_numbers(value: &str) -> Vec<f64> {
    let mut numbers = Vec::new();
    let mut token = String::new();
    for ch in value.chars() {
        if ch.is_ascii_digit() || ch == '.' || ch == '-' || ch == '+' {
            token.push(ch);
        } else if !token.is_empty() {
            if let Ok(value) = token.parse::<f64>() {
                numbers.push(value);
            }
            token.clear();
        }
    }
    if !token.is_empty() {
        if let Ok(value) = token.parse::<f64>() {
            numbers.push(value);
        }
    }
    numbers
}

fn get_case_insensitive_count(map: &BTreeMap<String, i32>, key: &str) -> i32 {
    map.iter()
        .find(|(actual, _)| actual.eq_ignore_ascii_case(key))
        .map(|(_, value)| *value)
        .unwrap_or_default()
}

fn to_canonical_semantic_set(
    values: Option<&Vec<String>>,
    metadata: &ManifestScenario,
) -> BTreeSet<String> {
    values
        .into_iter()
        .flatten()
        .filter_map(|value| canonical_semantic_id(value, metadata))
        .collect()
}

fn to_canonical_visible_text_set(values: Option<&Vec<String>>) -> BTreeSet<String> {
    values
        .into_iter()
        .flatten()
        .map(|value| canonical_visible_text(value))
        .filter(|value| !value.is_empty())
        .collect()
}

fn compare_visible_texts(
    reference: Option<&Vec<String>>,
    candidate: Option<&Vec<String>>,
) -> VisibleTextComparison {
    let Some(reference) = reference else {
        return VisibleTextComparison::default();
    };
    let Some(candidate) = candidate else {
        return VisibleTextComparison::default();
    };
    let reference = to_canonical_visible_text_map(reference);
    let candidate = to_canonical_visible_text_map(candidate);
    if reference.is_empty() && candidate.is_empty() {
        return VisibleTextComparison {
            jaccard: Some(100.0),
            ..VisibleTextComparison::default()
        };
    }

    let reference_keys = reference.keys().cloned().collect::<BTreeSet<_>>();
    let candidate_keys = candidate.keys().cloned().collect::<BTreeSet<_>>();
    let union = reference_keys.union(&candidate_keys).count();
    let intersection = reference_keys.intersection(&candidate_keys).count();
    let jaccard = Some(if union == 0 {
        100.0
    } else {
        intersection as f64 * 100.0 / union as f64
    });
    let missing_reference_texts = reference_keys
        .difference(&candidate_keys)
        .filter_map(|key| reference.get(key).cloned())
        .collect();
    let extra_candidate_texts = candidate_keys
        .difference(&reference_keys)
        .filter_map(|key| candidate.get(key).cloned())
        .collect();

    VisibleTextComparison {
        jaccard,
        missing_reference_texts,
        extra_candidate_texts,
    }
}

fn to_canonical_visible_text_map(values: &[String]) -> BTreeMap<String, String> {
    values
        .iter()
        .filter_map(|value| {
            let canonical = canonical_visible_text(value);
            (!canonical.is_empty()).then(|| (canonical, value.trim().to_string()))
        })
        .collect()
}

fn canonical_visible_text(value: &str) -> String {
    let canonical = value
        .split_whitespace()
        .collect::<Vec<_>>()
        .join(" ")
        .to_ascii_lowercase();
    if is_system_chrome_visible_text(&canonical) {
        String::new()
    } else {
        canonical
    }
}

fn is_system_chrome_visible_text(canonical: &str) -> bool {
    matches!(
        canonical,
        "appwindow custom title bar"
            | "non client input sink window"
            | "minimize"
            | "maximize"
            | "close"
            | "back"
            | "system"
            | "系统"
    )
}

fn canonical_semantic_id(value: &str, metadata: &ManifestScenario) -> Option<String> {
    let trimmed = value.trim();
    if trimmed.is_empty() {
        return None;
    }

    let lower = trimmed.to_ascii_lowercase();
    if is_system_chrome_semantic_id(&lower) {
        return None;
    }

    let floating_surface = floating_surface_id(metadata);
    if let Some(surface) = floating_surface {
        if let Some(mapped) = canonical_floating_semantic_id(&lower, surface) {
            return mapped;
        }
    }

    Some(lower)
}

fn floating_surface_id(metadata: &ManifestScenario) -> Option<&'static str> {
    if metadata.window_kind.eq_ignore_ascii_case("mini")
        || metadata.scenario_id.starts_with("mini.")
    {
        Some("mini")
    } else if metadata.window_kind.eq_ignore_ascii_case("fixed")
        || metadata.scenario_id.starts_with("fixed.")
    {
        Some("fixed")
    } else {
        None
    }
}

fn is_system_chrome_semantic_id(lower: &str) -> bool {
    matches!(
        lower,
        "systemmenubar" | "titlebar" | "minimize" | "maximize" | "close"
    )
}

fn canonical_floating_semantic_id(lower: &str, surface: &str) -> Option<Option<String>> {
    let surface_prefix = format!("{surface}.");
    if let Some(rest) = lower.strip_prefix(&surface_prefix) {
        if matches!(
            rest,
            "window"
                | "content"
                | "header"
                | "language_bar"
                | "input_card"
                | "input_content"
                | "status_row"
                | "detected_language_placeholder"
                | "title"
        ) {
            return Some(None);
        }
        return Some(Some(lower.to_string()));
    }

    let mapped = match lower {
        "closebutton" | "miniwindowclosebutton" | "fixedwindowclosebutton" => "close",
        "miniwindowocrbutton" | "fixedwindowocrbutton" => "ocr",
        "pinbutton" => "pin",
        "inputtextbox" | "sourcetextcollapsed" | "sourcetextcontainer" => "input",
        "sourceplaybutton" => "play_source",
        "sourcelangcombo" => "source_language",
        "targetlangcombo" => "target_language",
        "swapbutton" => "swap",
        "translatebutton" => "translate",
        "resultsscrollviewer" | "resultspanel" => "results",
        "statustext" => "status",
        _ => {
            if let Some(service_id) = lower.strip_prefix("serviceresultitem_") {
                return Some(Some(canonical_service_result_id(service_id)));
            }
            if let Some(service_id) = lower.strip_prefix("serviceresultheader_") {
                return Some(Some(canonical_service_result_id(service_id)));
            }
            if matches!(lower, "serviceicon" | "servicenametext") {
                return Some(None);
            }
            return None;
        }
    };

    Some(Some(format!("{surface}.{mapped}")))
}

fn canonical_service_result_id(value: &str) -> String {
    value
        .trim_matches('_')
        .replace("__", ".")
        .replace('_', "-")
        .to_ascii_lowercase()
}

fn calculate_window_runtime_score_cap(pair: &ScreenshotPair) -> f64 {
    let Some(metadata) = &pair.metadata else {
        return 100.0;
    };
    let mut cap = 100.0_f64;
    if metadata
        .reference_window
        .as_ref()
        .is_some_and(is_window_clipped)
        || metadata
            .candidate_window
            .as_ref()
            .is_some_and(is_window_clipped)
    {
        cap = cap.min(70.0);
    }
    if is_popbutton_scenario(pair) {
        cap = cap.min(required_flag_score_cap(
            metadata
                .candidate_window
                .as_ref()
                .and_then(|w| w.has_tool_window),
        ));
        cap = cap.min(required_flag_score_cap(
            metadata
                .candidate_window
                .as_ref()
                .and_then(|w| w.has_no_activate),
        ));
        cap = cap.min(required_flag_score_cap(
            metadata
                .candidate_window
                .as_ref()
                .and_then(|w| w.has_topmost),
        ));
        if metadata
            .candidate_window
            .as_ref()
            .and_then(|window| window.is_foreground_at_capture)
            == Some(true)
        {
            cap = cap.min(78.0);
        }
    }
    if is_ocr_overlay_scenario(pair) {
        cap = cap.min(required_flag_score_cap(
            metadata
                .candidate_window
                .as_ref()
                .and_then(|w| w.has_topmost),
        ));
        cap = cap.min(advisory_flag_score_cap(
            metadata
                .candidate_window
                .as_ref()
                .and_then(|w| w.has_tool_window),
        ));
        if let Some(coverage) = overlay_coverage_percent(
            metadata.reference_window.as_ref(),
            metadata.candidate_window.as_ref(),
        ) {
            if coverage < 85.0 {
                cap = cap.min(64.0);
            } else if coverage < 95.0 {
                cap = cap.min(78.0);
            }
        }
    }
    cap
}

fn calculate_control_dimension_score_cap(
    ui_summary: Option<&UiSummaryComparison>,
    missing_dimension_evidence_visual_verified: bool,
) -> f64 {
    let Some(ui_summary) = ui_summary else {
        return 100.0;
    };
    if missing_dimension_evidence_visual_verified {
        return 100.0;
    }
    let max_delta = ui_summary.max_control_dimension_delta_dips;
    if max_delta > 8.0 {
        69.0
    } else if max_delta > 3.0 {
        84.0
    } else {
        100.0
    }
}

const SEMANTIC_CONTRACT_SCORE_FLOOR: f64 = 86.0;

fn calculate_semantic_contract_score_floor(
    scoring_profile: &ScenarioScoringProfile,
    ui_summary: Option<&UiSummaryComparison>,
    metadata: Option<&ManifestScenario>,
    runtime_score_cap: f64,
    control_dimension_score_cap: f64,
    absolute_size_score_cap: f64,
) -> Option<f64> {
    if !matches!(
        scoring_profile.id.as_str(),
        "default-semantic" | "interaction-animation"
    ) || runtime_score_cap < 99.0
        || control_dimension_score_cap < 99.0
        || absolute_size_score_cap < 99.0
    {
        return None;
    }

    if has_work_area_limited_window_target(metadata) {
        return None;
    }

    let summary = ui_summary?;
    let visible_text_jaccard = summary.visible_text_jaccard?;
    if summary.score < 82.0
        || visible_text_jaccard < 99.5
        || !summary.missing_required_semantic_tags.is_empty()
        || !summary.missing_required_visible_texts.is_empty()
        || !summary.missing_required_control_states.is_empty()
        || !summary.missing_control_bounds_evidence.is_empty()
        || summary.max_control_dimension_delta_dips > 3.0
    {
        return None;
    }

    Some(SEMANTIC_CONTRACT_SCORE_FLOOR)
}

fn has_work_area_limited_window_target(metadata: Option<&ManifestScenario>) -> bool {
    metadata.is_some_and(|metadata| {
        [WindowAuditSide::Reference, WindowAuditSide::Candidate]
            .into_iter()
            .any(|side| {
                window_size_audit(metadata, side)
                    .and_then(|audit| audit.expected_larger_than_work_area)
                    .unwrap_or(false)
            })
    })
}

fn is_missing_dimension_evidence_visually_verified(
    ui_summary: Option<&UiSummaryComparison>,
    visual_absolute_match: bool,
) -> bool {
    visual_absolute_match
        && ui_summary.is_some_and(|summary| {
            !summary.control_dimension_deltas.is_empty()
                && summary
                    .control_dimension_deltas
                    .iter()
                    .all(|delta| delta.candidate == "missing dimension evidence")
        })
}

fn has_perfect_visual_absolute_evidence(
    pixel: &PixelComparison,
    ssim: f64,
    hash_score: f64,
    reference_width: u32,
    reference_height: u32,
    candidate_width: u32,
    candidate_height: u32,
    pair: &ScreenshotPair,
) -> bool {
    if pixel.pixel_error_percent > 0.0001
        || ssim < 0.99999
        || hash_score < 99.99
        || reference_width != candidate_width
        || reference_height != candidate_height
    {
        return false;
    }

    pair.metadata
        .as_ref()
        .and_then(|metadata| {
            absolute_window_delta_percent(
                metadata.reference_window.as_ref(),
                metadata.candidate_window.as_ref(),
            )
        })
        .is_some_and(|delta| delta <= 0.01)
}

fn calculate_absolute_size_score_cap(
    reference_width: u32,
    reference_height: u32,
    candidate_width: u32,
    candidate_height: u32,
    pair: &ScreenshotPair,
) -> f64 {
    let image_delta_percent = max_axis_delta_percent(
        reference_width as f64,
        reference_height as f64,
        candidate_width as f64,
        candidate_height as f64,
    );
    let window_delta_percent = pair
        .metadata
        .as_ref()
        .and_then(|metadata| {
            absolute_window_delta_percent(
                metadata.reference_window.as_ref(),
                metadata.candidate_window.as_ref(),
            )
        })
        .unwrap_or(0.0);
    let reference_target_cap = reference_window_target_score_cap(pair.metadata.as_ref());
    let candidate_target_cap = candidate_window_target_score_cap(pair.metadata.as_ref());
    let target_score_cap = [reference_target_cap, candidate_target_cap]
        .into_iter()
        .flatten()
        .fold(None, |cap: Option<f64>, value| {
            Some(cap.map_or(value, |cap| cap.min(value)))
        });
    if let Some(target_score_cap) = target_score_cap {
        return target_score_cap;
    }

    let observed_delta_percent = image_delta_percent.max(window_delta_percent);
    absolute_size_score_cap_from_delta_percent(observed_delta_percent)
}

fn candidate_window_target_score_cap(metadata: Option<&ManifestScenario>) -> Option<f64> {
    let delta = candidate_window_target_delta(metadata?)?;
    Some(fixed_window_target_score_cap(
        delta.max_abs_dips(),
        delta.max_abs_percent(),
    ))
}

fn reference_window_target_score_cap(metadata: Option<&ManifestScenario>) -> Option<f64> {
    let delta = reference_window_target_delta(metadata?)?;
    Some(fixed_window_target_score_cap(
        delta.max_abs_dips(),
        delta.max_abs_percent(),
    ))
}

fn fixed_window_target_score_cap(max_delta_dips: f64, max_delta_percent: f64) -> f64 {
    if max_delta_dips > 8.0 || max_delta_percent > 1.0 {
        69.0
    } else if max_delta_dips > 2.0 || max_delta_percent > 0.5 {
        84.0
    } else {
        100.0
    }
}

fn absolute_size_score_cap_from_delta_percent(delta_percent: f64) -> f64 {
    if delta_percent >= 35.0 {
        45.0
    } else if delta_percent >= 20.0 {
        60.0
    } else if delta_percent >= 10.0 {
        75.0
    } else if delta_percent >= 5.0 {
        88.0
    } else {
        100.0
    }
}

fn absolute_window_delta_percent(
    reference_window: Option<&ManifestWindow>,
    candidate_window: Option<&ManifestWindow>,
) -> Option<f64> {
    let (reference_width, reference_height) = manifest_window_dip_size(reference_window?)?;
    let (candidate_width, candidate_height) = manifest_window_dip_size(candidate_window?)?;
    Some(max_axis_delta_percent(
        reference_width,
        reference_height,
        candidate_width,
        candidate_height,
    ))
}

fn reference_candidate_dpi_scale_delta(metadata: &ManifestScenario) -> Option<f64> {
    let reference = metadata.reference_window.as_ref()?.dpi_scale;
    let candidate = metadata.candidate_window.as_ref()?.dpi_scale;
    if reference <= 0.0 || candidate <= 0.0 {
        return None;
    }
    Some((candidate - reference).abs())
}

fn manifest_window_dip_size(window: &ManifestWindow) -> Option<(f64, f64)> {
    let bounds = window.bounds.as_ref()?;
    if bounds.width <= 0 || bounds.height <= 0 {
        return None;
    }
    let scale = if window.dpi_scale > 0.0 {
        window.dpi_scale
    } else {
        1.0
    };
    Some((bounds.width as f64 / scale, bounds.height as f64 / scale))
}

fn max_axis_delta_percent(
    reference_width: f64,
    reference_height: f64,
    candidate_width: f64,
    candidate_height: f64,
) -> f64 {
    let width_delta = (candidate_width - reference_width).abs() * 100.0 / reference_width.max(1.0);
    let height_delta =
        (candidate_height - reference_height).abs() * 100.0 / reference_height.max(1.0);
    width_delta.max(height_delta)
}

fn is_window_clipped(window: &ManifestWindow) -> bool {
    window.is_clipped_by_virtual_screen == Some(true)
}

fn required_flag_score_cap(value: Option<bool>) -> f64 {
    match value {
        Some(true) => 100.0,
        Some(false) => 64.0,
        None => 78.0,
    }
}

fn advisory_flag_score_cap(value: Option<bool>) -> f64 {
    match value {
        Some(true) => 100.0,
        Some(false) => 82.0,
        None => 90.0,
    }
}

fn is_popbutton_scenario(pair: &ScreenshotPair) -> bool {
    pair.metadata
        .as_ref()
        .is_some_and(|metadata| metadata.window_kind.eq_ignore_ascii_case("popbutton"))
        || pair.scenario_id.to_ascii_lowercase().contains("popbutton")
}

fn is_ocr_overlay_scenario(pair: &ScreenshotPair) -> bool {
    let id = pair.scenario_id.to_ascii_lowercase();
    pair.metadata.as_ref().is_some_and(|metadata| {
        metadata.window_kind.eq_ignore_ascii_case("ocr")
            || metadata.window_kind.eq_ignore_ascii_case("capture")
    }) || id.contains("ocr.")
        || id.contains("capture")
}

fn overlay_coverage_percent(
    reference_window: Option<&ManifestWindow>,
    candidate_window: Option<&ManifestWindow>,
) -> Option<f64> {
    let reference = reference_window?.bounds.as_ref()?;
    let candidate = candidate_window?.bounds.as_ref()?;
    if reference.width <= 0
        || reference.height <= 0
        || candidate.width <= 0
        || candidate.height <= 0
    {
        return None;
    }
    let width_coverage = candidate.width as f64 * 100.0 / reference.width as f64;
    let height_coverage = candidate.height as f64 * 100.0 / reference.height as f64;
    Some(width_coverage.min(height_coverage))
}

fn build_findings(
    pair: &ScreenshotPair,
    reference_width: u32,
    reference_height: u32,
    candidate_width: u32,
    candidate_height: u32,
    scoring_profile: &ScenarioScoringProfile,
    gate: &ScenarioScoreGate,
    status: ScoreStatus,
    score: f64,
    visual_score: f64,
    runtime_score_cap: f64,
    control_dimension_score_cap: f64,
    absolute_size_score_cap: f64,
    pixel: &PixelComparison,
    ssim: f64,
    hash_score: f64,
    size_score: f64,
    palette: &PaletteComparison,
    ui_summary: Option<&UiSummaryComparison>,
    semantic_contract_score_floor: Option<f64>,
    control_dimension_missing_evidence_visual_verified: bool,
    regions: &[RegionResult],
) -> Vec<Finding> {
    let mut findings = Vec::new();
    match status {
        ScoreStatus::Fail => findings.push(Finding {
            severity: "error".to_string(),
            layer_hint: if runtime_score_cap < visual_score
                || control_dimension_score_cap < visual_score
                || absolute_size_score_cap < visual_score
            {
                if control_dimension_score_cap <= runtime_score_cap
                    && control_dimension_score_cap <= absolute_size_score_cap
                {
                    "easydict_app".to_string()
                } else {
                    "window_runtime".to_string()
                }
            } else {
                "final_effect".to_string()
            },
            message: if absolute_size_score_cap < visual_score
                && absolute_size_score_cap <= runtime_score_cap
                && absolute_size_score_cap <= control_dimension_score_cap
            {
                format!(
                    "Absolute size evidence capped scenario score {:.2} below fail gate threshold {:.2}.",
                    score, gate.warn_score
                )
            } else if control_dimension_score_cap < visual_score
                && control_dimension_score_cap <= runtime_score_cap
                && control_dimension_score_cap <= absolute_size_score_cap
            {
                format!(
                    "Control absolute-size evidence capped scenario score {:.2} below fail gate threshold {:.2}.",
                    score, gate.warn_score
                )
            } else if runtime_score_cap < visual_score {
                format!(
                    "Window runtime evidence capped scenario score {:.2} below fail gate threshold {:.2}.",
                    score, gate.warn_score
                )
            } else {
                format!(
                    "Scenario score {:.2} is below fail gate threshold {:.2}.",
                    score, gate.warn_score
                )
            },
            metric: "score".to_string(),
            value: round2(score),
        }),
        ScoreStatus::Warn => findings.push(Finding {
            severity: "warning".to_string(),
            layer_hint: if control_dimension_score_cap < visual_score {
                "easydict_app".to_string()
            } else if absolute_size_score_cap < visual_score {
                "window_runtime".to_string()
            } else {
                "final_effect".to_string()
            },
            message: format!(
                "Scenario score {:.2} is below pass gate threshold {:.2} and needs visual review.",
                score, gate.pass_score
            ),
            metric: "score".to_string(),
            value: round2(score),
        }),
        ScoreStatus::Pass => findings.push(Finding {
            severity: "info".to_string(),
            layer_hint: "final_effect".to_string(),
            message: format!(
                "{} is within configured visual parity thresholds for {}/{}.",
                pair.scenario_id, gate.layer, gate.case
            ),
            metric: "score".to_string(),
            value: round2(score),
        }),
    }
    if let Some(floor) = semantic_contract_score_floor {
        if floor > visual_score + 0.01 {
            findings.push(Finding {
                severity: "info".to_string(),
                layer_hint: "final_effect".to_string(),
                message: format!(
                    "Semantic/absolute-size contract floor lifted the score from {:.2} to {:.2}: visible text, required controls/states, control bounds, and window dimensions all match, so residual pixel delta ({:.2}%) is treated as font anti-aliasing/palette noise rather than layout drift. Pixel and SSIM warnings are still reported for human/LLM review.",
                    visual_score, floor, pixel.pixel_error_percent
                ),
                metric: "semanticContractScoreFloor".to_string(),
                value: round2(floor),
            });
        }
    }
    if absolute_size_score_cap < 100.0 {
        findings.push(Finding {
            severity: if absolute_size_score_cap < 70.0 {
                "error"
            } else {
                "warning"
            }
            .to_string(),
            layer_hint: "window_runtime".to_string(),
            message: format!(
                "Absolute screenshot/window dimensions differ beyond tolerance: image {}; window {}.",
                image_size_pair_summary(
                    reference_width,
                    reference_height,
                    candidate_width,
                    candidate_height
                ),
                absolute_window_size_summary(pair.metadata.as_ref())
            ),
            metric: "absoluteSizeScoreCap".to_string(),
            value: round2(absolute_size_score_cap),
        });
    }
    if control_dimension_score_cap < 100.0 {
        findings.push(Finding {
            severity: if control_dimension_score_cap < 70.0 {
                "error"
            } else {
                "warning"
            }
            .to_string(),
            layer_hint: "easydict_app".to_string(),
            message: format!(
                "Control absolute dimensions capped scenario score: {}.",
                semantic_control_dimension_summary(pair.metadata.as_ref())
            ),
            metric: "controlDimensionScoreCap".to_string(),
            value: round2(control_dimension_score_cap),
        });
    }
    add_reference_source_audit_finding(pair.metadata.as_ref(), &mut findings);
    add_dpi_scale_audit_finding(pair.metadata.as_ref(), &mut findings);
    add_reference_window_size_audit_finding(pair.metadata.as_ref(), &mut findings);
    add_candidate_window_size_audit_finding(pair.metadata.as_ref(), &mut findings);
    if pixel.pixel_error_percent > 5.0 {
        findings.push(Finding {
            severity: "warning".to_string(),
            layer_hint: "final_effect".to_string(),
            message: "Pixel delta is high; inspect the diff heatmap for structural drift."
                .to_string(),
            metric: "pixelErrorPercent".to_string(),
            value: round2(pixel.pixel_error_percent),
        });
    }
    if ssim < 0.82 {
        findings.push(Finding {
            severity: "warning".to_string(),
            layer_hint: "final_effect".to_string(),
            message: "SSIM is low, suggesting layout or contrast drift.".to_string(),
            metric: "ssim".to_string(),
            value: round5(ssim),
        });
    }
    if hash_score < 70.0 {
        findings.push(Finding {
            severity: "warning".to_string(),
            layer_hint: "final_effect".to_string(),
            message: "Perceptual hash distance suggests coarse layout changes.".to_string(),
            metric: "hashScore".to_string(),
            value: round2(hash_score),
        });
    }
    if size_score < 90.0 {
        findings.push(Finding {
            severity: "warning".to_string(),
            layer_hint: "iced_backend".to_string(),
            message: format!(
                "Candidate image dimensions differ from the reference: reference {}x{} px, candidate {}x{} px, delta {:+}x{:+} px.",
                reference_width,
                reference_height,
                candidate_width,
                candidate_height,
                candidate_width as i64 - reference_width as i64,
                candidate_height as i64 - reference_height as i64
            ),
            metric: "sizeScore".to_string(),
            value: round2(size_score),
        });
    }
    if palette.average_color_delta > 28.0 {
        findings.push(Finding {
            severity: "warning".to_string(),
            layer_hint: "win_fluent".to_string(),
            message: "Average palette delta is high; check theme tokens and control colors."
                .to_string(),
            metric: "averageColorDelta".to_string(),
            value: round2(palette.average_color_delta),
        });
    }
    if let Some(summary) = ui_summary {
        if summary.score < 80.0 {
            findings.push(Finding {
                severity: "warning".to_string(),
                layer_hint: "easydict_app".to_string(),
                message:
                    "UI Automation summaries differ or required semantic tags/texts are missing."
                        .to_string(),
                metric: "semanticScore".to_string(),
                value: round2(summary.score),
            });
        }
        if summary.control_count_delta_percent > 10.0 && !summary.control_count_deltas.is_empty() {
            let top = summary
                .control_count_deltas
                .iter()
                .take(5)
                .map(|delta| {
                    format!(
                        "{} reference {}, candidate {}, delta {}",
                        delta.kind, delta.reference_count, delta.candidate_count, delta.delta_abs
                    )
                })
                .collect::<Vec<_>>()
                .join("; ");
            findings.push(Finding {
                severity: "warning".to_string(),
                layer_hint: "easydict_app".to_string(),
                message: format!("Visible control counts differ from reference UI summary: {top}."),
                metric: "controlCountDeltaPercent".to_string(),
                value: round2(summary.control_count_delta_percent),
            });
        }
        if summary
            .visible_text_jaccard
            .is_some_and(|score| score < 92.0)
        {
            let top = visible_text_delta_summary(summary, 6);
            findings.push(Finding {
                severity: "warning".to_string(),
                layer_hint: "easydict_app".to_string(),
                message: format!("Visible text set differs from reference UI summary: {top}."),
                metric: "visibleTextJaccard".to_string(),
                value: round2(summary.visible_text_jaccard.unwrap_or_default()),
            });
        }
        if !summary.missing_required_visible_texts.is_empty() {
            findings.push(Finding {
                severity: "error".to_string(),
                layer_hint: "easydict_app".to_string(),
                message: format!(
                    "Required visible text is missing from the candidate UI summary: {}.",
                    summary.missing_required_visible_texts.join(", ")
                ),
                metric: "missingRequiredVisibleText".to_string(),
                value: summary.missing_required_visible_texts.len() as f64,
            });
        }
        if !summary.missing_required_control_states.is_empty() {
            let top = summary
                .missing_required_control_states
                .iter()
                .take(5)
                .map(|delta| {
                    format!(
                        "{} requires {}, candidate {}",
                        delta.id,
                        delta.required_state,
                        delta
                            .candidate_state
                            .as_deref()
                            .unwrap_or("missing state evidence")
                    )
                })
                .collect::<Vec<_>>()
                .join("; ");
            findings.push(Finding {
                severity: "error".to_string(),
                layer_hint: "easydict_app".to_string(),
                message: format!("Required control interaction states are missing: {top}."),
                metric: "missingRequiredControlState".to_string(),
                value: summary.missing_required_control_states.len() as f64,
            });
        }
        if !summary.missing_control_bounds_evidence.is_empty() {
            let top = summary
                .missing_control_bounds_evidence
                .iter()
                .take(5)
                .map(|gap| {
                    format!(
                        "{} reference {}, candidate {}",
                        gap.id, gap.reference_bounds, gap.candidate
                    )
                })
                .collect::<Vec<_>>()
                .join("; ");
            findings.push(Finding {
                severity: "warning".to_string(),
                layer_hint: "evidence_quality".to_string(),
                message: format!(
                    "Candidate control bounds evidence is missing for absolute position comparison: {top}."
                ),
                metric: "missingControlBoundsEvidence".to_string(),
                value: summary.missing_control_bounds_evidence.len() as f64,
            });
        }
        if summary.max_control_dimension_delta_dips > 2.0 {
            let top = summary
                .control_dimension_deltas
                .iter()
                .take(4)
                .map(|delta| {
                    format!(
                        "{}.{} reference {}, candidate {}, delta {:.2}",
                        delta.id, delta.property, delta.reference, delta.candidate, delta.delta_abs
                    )
                })
                .collect::<Vec<_>>()
                .join("; ");
            let visually_verified = control_dimension_missing_evidence_visual_verified;
            findings.push(Finding {
                severity: if visually_verified {
                    "warning"
                } else if summary.max_control_dimension_delta_dips > 8.0 {
                    "error"
                } else {
                    "warning"
                }
                .to_string(),
                layer_hint: if visually_verified {
                    "evidence_quality"
                } else {
                    "easydict_app"
                }
                .to_string(),
                message: if visually_verified {
                    format!(
                        "Control dimension schema evidence is missing, but pixel and window-size evidence matched exactly: {top}."
                    )
                } else {
                    format!(
                        "Control absolute dimensions differ from reference UI summary: {top}."
                    )
                },
                metric: "controlDimensionDeltaDips".to_string(),
                value: round2(summary.max_control_dimension_delta_dips),
            });
        }
    }
    for region in regions {
        if region.score < scoring_profile.region_warning_score {
            findings.push(Finding {
                severity: "warning".to_string(),
                layer_hint: region_layer_hint(&region.name).to_string(),
                message: format!(
                    "Region `{}` is below the profile warning score.",
                    region.name
                ),
                metric: format!("region.{}.score", region.name),
                value: round2(region.score),
            });
        }
    }
    add_window_runtime_findings(pair, &mut findings);
    findings
}

fn add_reference_source_audit_finding(
    metadata: Option<&ManifestScenario>,
    findings: &mut Vec<Finding>,
) {
    let Some(metadata) = metadata else {
        return;
    };
    if metadata.reference_source_is_fallback != Some(true) {
        return;
    }

    let kind = metadata
        .reference_source_kind
        .as_deref()
        .unwrap_or("fallback");
    findings.push(Finding {
        severity: "warning".to_string(),
        layer_hint: "evidence_quality".to_string(),
        message: format!(
            "Reference screenshot was copied from fallback source `{kind}`; regenerate a curated .NET WinUI baseline with matching language, DPI, and absolute window size before accepting parity."
        ),
        metric: "referenceSourceIsFallback".to_string(),
        value: 1.0,
    });
}

fn add_dpi_scale_audit_finding(metadata: Option<&ManifestScenario>, findings: &mut Vec<Finding>) {
    let Some(metadata) = metadata else {
        return;
    };
    let Some(reference) = metadata.reference_window.as_ref() else {
        return;
    };
    let Some(candidate) = metadata.candidate_window.as_ref() else {
        return;
    };
    let Some(delta) = reference_candidate_dpi_scale_delta(metadata) else {
        return;
    };
    if delta <= 0.01 {
        return;
    }

    findings.push(Finding {
        severity: "warning".to_string(),
        layer_hint: "evidence_quality".to_string(),
        message: format!(
            "Reference and candidate screenshots were captured at different DPI scales: reference {:.3}x, candidate {:.3}x. Regenerate the .NET WinUI baseline at matching DPI/work-area before interpreting pixel or SSIM drift; use DIP/window target fields for absolute-size review.",
            reference.dpi_scale, candidate.dpi_scale
        ),
        metric: "referenceCandidateDpiScaleDelta".to_string(),
        value: round2(delta),
    });
}

fn add_dip_normalized_viewport_finding(
    metadata: Option<&ManifestScenario>,
    comparison: Option<&DipNormalizedViewportComparison>,
    findings: &mut Vec<Finding>,
) {
    let Some(comparison) = comparison else {
        return;
    };
    let pixel = comparison.pixel.pixel_error_percent;
    let ssim = comparison.ssim;
    if pixel > 5.0 || ssim < 0.82 {
        findings.push(Finding {
            severity: "warning".to_string(),
            layer_hint: "final_effect".to_string(),
            message: format!(
                "DPI-normalized shared viewport still differs: {}x{} DIP px, pixel error {:.2}%, SSIM {:.3}. This points to real layout/theme drift after removing DPI scale from the comparison.",
                comparison.width,
                comparison.height,
                pixel,
                ssim
            ),
            metric: "dipNormalizedPixelErrorPercent".to_string(),
            value: round2(pixel),
        });
        return;
    }

    let has_dpi_delta = metadata
        .and_then(reference_candidate_dpi_scale_delta)
        .is_some_and(|delta| delta > 0.01);
    let clamped_by_work_area = metadata
        .and_then(|metadata| window_size_audit(metadata, WindowAuditSide::Candidate))
        .and_then(|audit| audit.expected_larger_than_work_area)
        .unwrap_or(false);
    if has_dpi_delta || clamped_by_work_area {
        findings.push(Finding {
            severity: "info".to_string(),
            layer_hint: "evidence_quality".to_string(),
            message: format!(
                "DPI-normalized shared viewport is close: {}x{} DIP px, pixel error {:.2}%, SSIM {:.3}. Raw absolute-size gates still apply, but this separates DPI/work-area evidence drift from the Settings page layout.",
                comparison.width,
                comparison.height,
                pixel,
                ssim
            ),
            metric: "dipNormalizedPixelErrorPercent".to_string(),
            value: round2(pixel),
        });
    }
}

fn add_reference_window_size_audit_finding(
    metadata: Option<&ManifestScenario>,
    findings: &mut Vec<Finding>,
) {
    add_window_size_audit_finding(
        metadata,
        WindowAuditSide::Reference,
        findings,
        "WinUI reference",
        "referenceWindowDipSizeDelta",
    );
}

fn add_candidate_window_size_audit_finding(
    metadata: Option<&ManifestScenario>,
    findings: &mut Vec<Finding>,
) {
    add_window_size_audit_finding(
        metadata,
        WindowAuditSide::Candidate,
        findings,
        "Rust candidate",
        "candidateWindowDipSizeDelta",
    );
}

#[derive(Clone, Copy)]
enum WindowAuditSide {
    Reference,
    Candidate,
}

fn add_window_size_audit_finding(
    metadata: Option<&ManifestScenario>,
    side: WindowAuditSide,
    findings: &mut Vec<Finding>,
    label: &str,
    metric: &str,
) {
    let Some(metadata) = metadata else {
        return;
    };
    let Some(audit) = window_size_audit(metadata, side) else {
        return;
    };
    let Some(delta) = window_target_delta(metadata, side) else {
        return;
    };
    let delta_percent = delta.max_abs_percent();
    let delta_dips = delta.max_abs_dips();
    let clamped_by_work_area = audit.expected_larger_than_work_area.unwrap_or(false);
    if delta_percent <= 1.0 && delta_dips <= 2.0 {
        if clamped_by_work_area {
            findings.push(Finding {
                severity: "warning".to_string(),
                layer_hint: "evidence_quality".to_string(),
                message: format!(
                    "Expected window target exceeds the current monitor work area; {label} matched the nearest allowed target, but full-size parity needs a larger work area: {}.",
                    window_size_audit_summary(Some(metadata), side)
                ),
                metric: "expectedWindowDipsExceedsWorkArea".to_string(),
                value: 1.0,
            });
        }
        return;
    }

    findings.push(Finding {
        severity: if delta_dips <= 8.0 && delta_percent <= 1.0 {
            "warning"
        } else {
            "error"
        }
        .to_string(),
        layer_hint: "window_runtime".to_string(),
        message: format!(
            "{label} window DIP size differs from expected target: {}.",
            window_size_audit_summary(Some(metadata), side)
        ),
        metric: metric.to_string(),
        value: round2(delta_percent),
    });
}

fn window_size_audit(
    metadata: &ManifestScenario,
    side: WindowAuditSide,
) -> Option<&ManifestWindowSizeAudit> {
    match side {
        WindowAuditSide::Reference => metadata.reference_window_size_audit.as_ref(),
        WindowAuditSide::Candidate => metadata.candidate_window_size_audit.as_ref(),
    }
}

fn window_target_delta(
    metadata: &ManifestScenario,
    side: WindowAuditSide,
) -> Option<WindowTargetDelta> {
    match side {
        WindowAuditSide::Reference => reference_window_target_delta(metadata),
        WindowAuditSide::Candidate => candidate_window_target_delta(metadata),
    }
}

#[derive(Debug, Clone, Copy)]
struct WindowTargetDelta {
    width_dips: f64,
    height_dips: f64,
    width_percent: f64,
    height_percent: f64,
}

impl WindowTargetDelta {
    fn max_abs_dips(self) -> f64 {
        self.width_dips.abs().max(self.height_dips.abs())
    }

    fn max_abs_percent(self) -> f64 {
        self.width_percent.abs().max(self.height_percent.abs())
    }
}

fn effective_candidate_window_size_delta_percent(
    metadata: Option<&ManifestScenario>,
) -> Option<ManifestDipSize> {
    let delta = candidate_window_target_delta(metadata?)?;
    Some(ManifestDipSize {
        width: delta.width_percent,
        height: delta.height_percent,
    })
}

fn candidate_window_target_delta(metadata: &ManifestScenario) -> Option<WindowTargetDelta> {
    let audit = metadata.candidate_window_size_audit.as_ref()?;
    let target = effective_window_target_dips(audit)?;
    window_target_delta_from_audit(audit, &target)
}

fn reference_window_target_delta(metadata: &ManifestScenario) -> Option<WindowTargetDelta> {
    let audit = metadata.reference_window_size_audit.as_ref()?;
    let target = effective_window_target_dips(audit)?;
    window_target_delta_from_audit(audit, &target)
}

fn window_target_delta_from_audit(
    audit: &ManifestWindowSizeAudit,
    target: &ManifestDipSize,
) -> Option<WindowTargetDelta> {
    let actual = audit.actual_window_dips.as_ref()?;
    Some(WindowTargetDelta {
        width_dips: actual.width - target.width,
        height_dips: actual.height - target.height,
        width_percent: signed_delta_percent(target.width, actual.width),
        height_percent: signed_delta_percent(target.height, actual.height),
    })
}

fn effective_candidate_window_target_dips(metadata: &ManifestScenario) -> Option<ManifestDipSize> {
    effective_window_target_dips(metadata.candidate_window_size_audit.as_ref()?)
}

fn effective_reference_window_target_dips(metadata: &ManifestScenario) -> Option<ManifestDipSize> {
    effective_window_target_dips(metadata.reference_window_size_audit.as_ref()?)
}

fn effective_window_target_dips(audit: &ManifestWindowSizeAudit) -> Option<ManifestDipSize> {
    let target = audit.expected_window_dips.as_ref()?.clone();
    if audit.expected_larger_than_work_area != Some(true) {
        return Some(target.clone());
    }
    let work_area = audit.monitor_work_area_dips.as_ref()?;
    let clamped = ManifestDipSize {
        width: target.width.min(work_area.width),
        height: target.height.min(work_area.height),
    };
    let actual = audit.actual_window_dips.as_ref()?;
    if max_abs_size_delta(actual, &target) <= max_abs_size_delta(actual, &clamped) {
        Some(target)
    } else {
        Some(clamped)
    }
}

fn max_abs_size_delta(actual: &ManifestDipSize, target: &ManifestDipSize) -> f64 {
    (actual.width - target.width)
        .abs()
        .max((actual.height - target.height).abs())
}

fn signed_delta_percent(reference: f64, candidate: f64) -> f64 {
    if reference.abs() <= f64::EPSILON {
        0.0
    } else {
        ((candidate - reference) / reference) * 100.0
    }
}

fn add_window_runtime_findings(pair: &ScreenshotPair, findings: &mut Vec<Finding>) {
    let Some(metadata) = &pair.metadata else {
        return;
    };
    add_window_clipping_finding(
        metadata.reference_window.as_ref(),
        findings,
        "referenceWindow.isClippedByVirtualScreen",
        "WinUI reference window was clipped by the virtual screen during capture; rerun before accepting visual parity.",
    );
    add_window_clipping_finding(
        metadata.candidate_window.as_ref(),
        findings,
        "candidateWindow.isClippedByVirtualScreen",
        "Rust candidate window was clipped by the virtual screen during capture; fix placement/size or rerun before accepting visual parity.",
    );
    if is_popbutton_scenario(pair) {
        add_required_window_flag_finding(
            findings,
            metadata
                .candidate_window
                .as_ref()
                .and_then(|w| w.has_tool_window),
            "candidateWindow.hasToolWindow",
            "Rust PopButton candidate is missing WS_EX_TOOLWINDOW evidence.",
            "error",
            "warning",
        );
        add_required_window_flag_finding(
            findings,
            metadata
                .candidate_window
                .as_ref()
                .and_then(|w| w.has_no_activate),
            "candidateWindow.hasNoActivate",
            "Rust PopButton candidate is missing WS_EX_NOACTIVATE evidence.",
            "error",
            "warning",
        );
        add_required_window_flag_finding(
            findings,
            metadata
                .candidate_window
                .as_ref()
                .and_then(|w| w.has_topmost),
            "candidateWindow.hasTopmost",
            "Rust PopButton candidate is missing WS_EX_TOPMOST evidence.",
            "error",
            "warning",
        );
        if metadata
            .candidate_window
            .as_ref()
            .and_then(|window| window.is_foreground_at_capture)
            == Some(true)
        {
            findings.push(Finding {
                severity: "warning".to_string(),
                layer_hint: "window_runtime".to_string(),
                message: "Rust PopButton candidate became the foreground window during capture; no-activate behavior is not proven.".to_string(),
                metric: "candidateWindow.isForegroundAtCapture".to_string(),
                value: 1.0,
            });
        }
    }
    if is_ocr_overlay_scenario(pair) {
        add_required_window_flag_finding(
            findings,
            metadata
                .candidate_window
                .as_ref()
                .and_then(|w| w.has_topmost),
            "candidateWindow.hasTopmost",
            "Rust OCR overlay candidate is missing WS_EX_TOPMOST evidence.",
            "error",
            "warning",
        );
        add_required_window_flag_finding(
            findings,
            metadata
                .candidate_window
                .as_ref()
                .and_then(|w| w.has_tool_window),
            "candidateWindow.hasToolWindow",
            "Rust OCR overlay candidate is missing tool-window/skip-taskbar evidence.",
            "warning",
            "warning",
        );
        if let Some(coverage) = overlay_coverage_percent(
            metadata.reference_window.as_ref(),
            metadata.candidate_window.as_ref(),
        ) {
            if coverage < 95.0 {
                findings.push(Finding {
                    severity: if coverage < 85.0 { "error" } else { "warning" }.to_string(),
                    layer_hint: "window_runtime".to_string(),
                    message: "Rust OCR overlay candidate does not cover the WinUI reference overlay bounds; capture overlay should be fullscreen/topmost.".to_string(),
                    metric: "candidateWindow.overlayCoveragePercent".to_string(),
                    value: round2(coverage),
                });
            }
        }
    }
}

fn add_window_clipping_finding(
    window: Option<&ManifestWindow>,
    findings: &mut Vec<Finding>,
    metric: &str,
    message: &str,
) {
    if window.is_some_and(is_window_clipped) {
        findings.push(Finding {
            severity: "error".to_string(),
            layer_hint: "window_runtime".to_string(),
            message: message.to_string(),
            metric: metric.to_string(),
            value: 1.0,
        });
    }
}

fn add_required_window_flag_finding(
    findings: &mut Vec<Finding>,
    value: Option<bool>,
    metric: &str,
    missing_message: &str,
    false_severity: &str,
    null_severity: &str,
) {
    if value == Some(true) {
        return;
    }
    findings.push(Finding {
        severity: if value.is_some() {
            false_severity
        } else {
            null_severity
        }
        .to_string(),
        layer_hint: "window_runtime".to_string(),
        message: if value.is_some() {
            missing_message.to_string()
        } else {
            format!("{missing_message} Manifest did not include this HWND style sample.")
        },
        metric: metric.to_string(),
        value: if value == Some(false) { 0.0 } else { -1.0 },
    });
}

fn region_layer_hint(region_name: &str) -> &'static str {
    match region_name {
        "header"
        | "main-header"
        | "floating-toolbar"
        | "ocr-overlay"
        | "ocr-magnifier"
        | "long-doc-header"
        | "long-doc-service-popup" => "iced_backend",
        "footer" | "floating-footer" | "long-doc-service-control" => "win_fluent_framework",
        "top-navigation"
        | "content"
        | "source-input"
        | "result-list"
        | "floating-content"
        | "ocr-center-selection"
        | "ocr-status-panel"
        | "long-doc-controls"
        | "long-doc-output"
        | "long-doc-context" => "rs_easydict_wrapper",
        _ => "final_visual_effect",
    }
}

fn resolve_score_gate(
    pair: &ScreenshotPair,
    regions: &[RegionResult],
    runtime_score_cap: f64,
    options: &CliOptions,
) -> ScenarioScoreGate {
    let hints = candidate_layer_hints(pair, regions, runtime_score_cap);
    for rule in options.score_gate_rules.iter().rev() {
        if score_gate_matches(rule, pair, &hints) {
            return ScenarioScoreGate {
                source: "score-gate".to_string(),
                layer: normalize_layer(&rule.layer).to_string(),
                case: rule.case.clone(),
                pass_score: rule.pass_score,
                warn_score: rule.warn_score,
            };
        }
    }
    ScenarioScoreGate {
        source: "default".to_string(),
        layer: hints
            .first()
            .cloned()
            .unwrap_or_else(|| "final_effect".to_string()),
        case: pair.scenario_id.clone(),
        pass_score: options.pass_score,
        warn_score: options.warn_score,
    }
}

fn score_gate_matches(rule: &ScoreGateRule, pair: &ScreenshotPair, hints: &[String]) -> bool {
    let layer = normalize_layer(&rule.layer);
    hints
        .iter()
        .any(|hint| normalize_layer(hint).eq_ignore_ascii_case(layer))
        && wildcard_match(&rule.case, &pair.scenario_id)
}

fn candidate_layer_hints(
    pair: &ScreenshotPair,
    regions: &[RegionResult],
    runtime_score_cap: f64,
) -> Vec<String> {
    let mut hints = BTreeSet::new();
    if runtime_score_cap < 100.0 || is_popbutton_scenario(pair) || is_ocr_overlay_scenario(pair) {
        hints.insert("window_runtime".to_string());
    }
    hints.extend(CoverageCatalog::layer_hints_for(pair));
    for region in regions {
        hints.insert(normalize_layer(region_layer_hint(&region.name)).to_string());
    }
    hints.insert("final_effect".to_string());
    hints.into_iter().collect()
}

fn normalize_layer(layer: &str) -> &str {
    match layer {
        "final_visual_effect" | "final-effect" => "final_effect",
        "rs_easydict_wrapper" | "easydict-rs" => "easydict_app",
        "win_fluent_framework" | "win-fluent" => "win_fluent",
        other => other,
    }
}

fn wildcard_match(pattern: &str, text: &str) -> bool {
    fn inner(pattern: &[u8], text: &[u8]) -> bool {
        if pattern.is_empty() {
            return text.is_empty();
        }
        match pattern[0] {
            b'*' => inner(&pattern[1..], text) || (!text.is_empty() && inner(pattern, &text[1..])),
            b'?' => !text.is_empty() && inner(&pattern[1..], &text[1..]),
            ch => {
                !text.is_empty()
                    && ch.eq_ignore_ascii_case(&text[0])
                    && inner(&pattern[1..], &text[1..])
            }
        }
    }
    inner(pattern.as_bytes(), text.as_bytes())
}

fn write_reports(
    report: &ParityReport,
    coverage: &ParityCoverageReport,
    options: &CliOptions,
) -> Result<(), String> {
    fs::create_dir_all(&options.output_dir).map_err(|error| error.to_string())?;
    write_json(&options.output_dir.join("ui-parity-report.json"), report)?;
    fs::write(
        options.output_dir.join("ui-parity-report.md"),
        markdown_report(report),
    )
    .map_err(|error| error.to_string())?;
    write_json(
        &options.output_dir.join("ui-parity-coverage.json"),
        coverage,
    )?;
    fs::write(
        options.output_dir.join("ui-parity-coverage.md"),
        coverage_markdown_report(coverage),
    )
    .map_err(|error| error.to_string())?;
    let coverage_requests = coverage_review_requests(coverage);
    write_json(
        &options.output_dir.join("llm-coverage-requests.json"),
        &coverage_requests,
    )?;
    fs::write(
        options.output_dir.join("llm-coverage-prompts.md"),
        coverage_review_prompts(&coverage_requests),
    )
    .map_err(|error| error.to_string())?;
    let review_requests = llm_review_requests(report);
    write_json(
        &options.output_dir.join("llm-review-requests.json"),
        &review_requests,
    )?;
    fs::write(
        options.output_dir.join("llm-review-prompts.md"),
        llm_review_prompts(&review_requests),
    )
    .map_err(|error| error.to_string())?;
    write_json(
        &options.output_dir.join("ui-parity-thresholds.json"),
        &ParityGatePolicy::create(report, options),
    )
}

fn write_json(path: &Path, value: &impl Serialize) -> Result<(), String> {
    let json = serde_json::to_string_pretty(value).map_err(|error| error.to_string())?;
    fs::write(path, json).map_err(|error| error.to_string())
}

fn markdown_report(report: &ParityReport) -> String {
    let mut out = String::new();
    out.push_str("# UI Parity Report\n\n");
    out.push_str(&format!("Generated: `{}`\n", report.generated_at_utc));
    out.push_str(&format!("Screenshot root: `{}`\n", report.screenshot_root));
    if let Some(manifest) = &report.manifest_path {
        out.push_str(&format!("Manifest: `{manifest}`\n"));
    }
    out.push_str("\n## Summary\n\n");
    out.push_str(&format!(
        "- Scenarios: **{}**\n",
        report.summary.total_scenarios
    ));
    out.push_str(&format!(
        "- Pass / warn / fail: **{} / {} / {}**\n",
        report.summary.pass_count, report.summary.warn_count, report.summary.fail_count
    ));
    out.push_str(&format!(
        "- Average score: **{:.2}**\n",
        report.summary.average_score
    ));
    out.push_str(&format!(
        "- Minimum score: **{:.2}**\n",
        report.summary.minimum_score
    ));
    out.push_str(&format!(
        "- Default thresholds: pass >= `{:.2}`, warn >= `{:.2}`\n",
        report.thresholds.pass_score, report.thresholds.warn_score
    ));
    out.push_str("- Gate mode: **report-only** by default; use `--fail-on-threshold` only after scenario baselines are reviewed.\n\n");
    if report.scenarios.is_empty() {
        out.push_str("No dotnet/rust parity screenshot pairs were found.\n");
        return out;
    }
    out.push_str("## Scenarios\n\n");
    out.push_str("| Status | Score | Scenario | Gate | Pass | Warn | Profile | Size | Size delta | Window DIP delta | DPI delta | Window target | Pixel error | SSIM | Hash score | Runtime cap | Size cap | Worst region | Diff |\n");
    out.push_str(
        "| --- | ---: | --- | --- | ---: | ---: | --- | --- | ---: | ---: | ---: | --- | ---: | ---: | ---: | ---: | ---: | --- | --- |\n",
    );
    for scenario in &report.scenarios {
        let worst = scenario.regions.iter().min_by(|a, b| {
            a.score
                .partial_cmp(&b.score)
                .unwrap_or(std::cmp::Ordering::Equal)
        });
        let worst_text = worst
            .map(|region| {
                format!(
                    "{} {:.2} (max delta {})",
                    region.name, region.score, region.max_channel_delta
                )
            })
            .unwrap_or_else(|| "n/a".to_string());
        let runtime_cap = scenario
            .metrics
            .window_runtime_score_cap
            .map(|value| format!("{value:.2}"))
            .unwrap_or_else(|| "n/a".to_string());
        let size_cap = scenario
            .metrics
            .absolute_size_score_cap
            .map(|value| format!("{value:.2}"))
            .unwrap_or_else(|| "n/a".to_string());
        let window_dip_delta = scenario
            .metrics
            .absolute_window_size_delta_percent
            .map(|value| format!("{value:.2}%"))
            .unwrap_or_else(|| "n/a".to_string());
        let dpi_delta = scenario
            .metrics
            .dpi_scale_delta
            .map(|value| format!("{value:.2}x"))
            .unwrap_or_else(|| "n/a".to_string());
        out.push_str(&format!(
            "| {:?} | {:.2} | `{}` | {} `{}/{}` | {:.2} | {:.2} | {} | {} | {:.2}% | {} | {} | {} | {:.2}% | {:.3} | {:.2} | {} | {} | {} | [heatmap]({}) |\n",
            scenario.status,
            scenario.score,
            scenario.scenario_id,
            if scenario.gate.source == "score-gate" { "gate" } else { "default" },
            scenario.gate.layer,
            scenario.gate.case,
            scenario.gate.pass_score,
            scenario.gate.warn_score,
            scenario.metrics.scoring_profile,
            image_size_delta_summary(scenario),
            scenario.metrics.absolute_image_size_delta_percent,
            window_dip_delta,
            dpi_delta,
            window_targets_table_summary(scenario.metadata.as_ref()),
            scenario.metrics.pixel_error_percent,
            scenario.metrics.ssim,
            scenario.metrics.hash_score,
            runtime_cap,
            size_cap,
            worst_text,
            scenario.diff_heatmap_path
        ));
    }
    out.push_str("\n## Evidence Audit\n\n");
    let audited = report
        .scenarios
        .iter()
        .filter_map(|scenario| {
            scenario
                .evidence_audit
                .as_ref()
                .filter(|audit| audit.has_gaps())
                .map(|audit| (scenario, audit))
        })
        .collect::<Vec<_>>();
    if audited.is_empty() {
        out.push_str("No manifest evidence gaps were found.\n");
    } else {
        out.push_str("| Scenario | Missing IDs | Missing dimensions | Missing bounds | Candidate dims without bounds | Next evidence |\n");
        out.push_str("| --- | ---: | ---: | ---: | ---: | --- |\n");
        for (scenario, audit) in audited {
            out.push_str(&format!(
                "| `{}` | {} | {} | {} | {} | {} |\n",
                scenario.scenario_id,
                audit.missing_candidate_automation_id_count,
                audit.missing_candidate_dimension_count,
                audit.missing_candidate_bounds_count,
                audit.candidate_dimension_without_bounds_count,
                evidence_audit_next_evidence(audit, 6)
            ));
        }
    }
    out.push_str("\n## Findings\n\n");
    for scenario in &report.scenarios {
        out.push_str(&format!("### {}\n\n", scenario.scenario_id));
        out.push_str(&format!(
            "- Contact sheet: [{}]({})\n",
            scenario.contact_sheet_path, scenario.contact_sheet_path
        ));
        if let Some(path) = &scenario.dip_normalized_contact_sheet_path {
            out.push_str(&format!(
                "- DIP-normalized shared viewport: [{}]({}); {}\n",
                path,
                path,
                dip_normalized_viewport_summary(scenario)
            ));
        }
        out.push_str(&format!(
            "- Diff heatmap: [{}]({})\n",
            scenario.diff_heatmap_path, scenario.diff_heatmap_path
        ));
        out.push_str(&format!(
            "- Status: `{:?}`, score `{:.2}`, gate `{}` `{}/{}` pass >= `{:.2}`, warn >= `{:.2}`\n",
            scenario.status,
            scenario.score,
            scenario.gate.source,
            scenario.gate.layer,
            scenario.gate.case,
            scenario.gate.pass_score,
            scenario.gate.warn_score
        ));
        out.push_str(&format!(
            "- Absolute image size: {}\n",
            absolute_image_size_summary(scenario)
        ));
        out.push_str(&format!(
            "- Absolute window size: {}\n",
            absolute_window_size_summary(scenario.metadata.as_ref())
        ));
        out.push_str(&format!(
            "- Reference window target: {}\n",
            reference_window_size_audit_summary(scenario.metadata.as_ref())
        ));
        out.push_str(&format!(
            "- Candidate window target: {}\n",
            candidate_window_size_audit_summary(scenario.metadata.as_ref())
        ));
        out.push_str(&format!(
            "- Control absolute sizes: {}\n",
            semantic_control_dimension_summary(scenario.metadata.as_ref())
        ));
        if let Some(audit) = scenario.evidence_audit.as_ref() {
            out.push_str(&format!(
                "- Evidence audit: {}\n",
                evidence_audit_summary(audit, 6)
            ));
        }
        if let Some(summary) = semantic_ui_summary_delta_summary(scenario.metadata.as_ref()) {
            out.push_str(&format!("- UI semantic summary: {summary}\n"));
        }
        if let Some(summary) = interaction_effect_delta_summary(scenario) {
            out.push_str(&format!("- Interaction effect delta: {summary}\n"));
        }
        out.push_str(&format!(
            "- Reference source: {}\n",
            reference_source_summary(scenario.metadata.as_ref())
        ));
        for finding in &scenario.findings {
            out.push_str(&format!(
                "- `{}` `{}` `{}={:.2}` {}\n",
                finding.severity,
                finding.layer_hint,
                finding.metric,
                finding.value,
                finding.message
            ));
        }
        out.push('\n');
    }
    out
}

fn evidence_audit_summary(audit: &EvidenceAudit, limit: usize) -> String {
    let mut parts = vec![
        format!(
            "automation IDs {}/{} candidate/reference, missing {}",
            audit.candidate_automation_id_count,
            audit.reference_automation_id_count,
            audit.missing_candidate_automation_id_count
        ),
        format!(
            "control dimensions {}/{} candidate/reference, missing {}",
            audit.candidate_control_dimension_count,
            audit.reference_control_dimension_count,
            audit.missing_candidate_dimension_count
        ),
        format!(
            "bounds {}/{} candidate/reference, missing {}",
            audit.candidate_control_bounds_count,
            audit.reference_control_bounds_count,
            audit.missing_candidate_bounds_count
        ),
    ];
    let next = evidence_audit_next_evidence(audit, limit);
    if next != "none" {
        parts.push(format!("next evidence {next}"));
    }
    parts.join("; ")
}

fn evidence_audit_next_evidence(audit: &EvidenceAudit, limit: usize) -> String {
    let mut items = Vec::new();
    for gap in &audit.missing_candidate_bounds {
        items.push(format!("bounds `{}` ({})", gap.id, gap.candidate));
    }
    for id in &audit.missing_candidate_dimension_ids {
        if !items.iter().any(|item| item.contains(&format!("`{id}`"))) {
            items.push(format!("dimension `{id}`"));
        }
    }
    for id in &audit.missing_candidate_automation_ids {
        if !items.iter().any(|item| item.contains(&format!("`{id}`"))) {
            items.push(format!("automation `{id}`"));
        }
    }
    for id in &audit.candidate_dimension_without_bounds_ids {
        if !items.iter().any(|item| item.contains(&format!("`{id}`"))) {
            items.push(format!("candidate bounds `{id}`"));
        }
    }

    if items.is_empty() {
        return "none".to_string();
    }
    let hidden = items.len().saturating_sub(limit);
    let mut visible = items.into_iter().take(limit).collect::<Vec<_>>();
    if hidden > 0 {
        visible.push(format!("+{hidden} more"));
    }
    visible.join("; ")
}

fn interaction_effect_delta_summary(scenario: &ScenarioResult) -> Option<String> {
    let baseline = scenario.metrics.effect_baseline_scenario_id.as_ref()?;
    let mut summary = format!(
        "baseline `{}`; full window WinUI changed {:.2}% of pixels, Rust changed {:.2}% of pixels, magnitude delta {:.2}%, score {:.2}",
        baseline,
        scenario
            .metrics
            .reference_effect_pixel_error_percent
            .unwrap_or_default(),
        scenario
            .metrics
            .candidate_effect_pixel_error_percent
            .unwrap_or_default(),
        scenario
            .metrics
            .effect_delta_magnitude_delta_percent
            .unwrap_or_default(),
        scenario
            .metrics
            .interaction_effect_delta_score
            .unwrap_or_default()
    );
    if let Some(roi_score) = scenario.metrics.interaction_effect_roi_delta_score {
        let target = scenario
            .metrics
            .interaction_effect_roi_target_ids
            .as_ref()
            .map(|items| items.join(","))
            .filter(|value| !value.is_empty())
            .unwrap_or_else(|| "unknown".to_string());
        let bounds = scenario
            .metrics
            .interaction_effect_roi_bounds
            .as_ref()
            .map(format_region_bounds)
            .unwrap_or_else(|| "n/a".to_string());
        summary.push_str(&format!(
            "; ROI `{target}` {bounds} WinUI changed {:.2}%, Rust changed {:.2}%, magnitude delta {:.2}%, score {:.2}",
            scenario
                .metrics
                .reference_effect_roi_pixel_error_percent
                .unwrap_or_default(),
            scenario
                .metrics
                .candidate_effect_roi_pixel_error_percent
                .unwrap_or_default(),
            scenario
                .metrics
                .effect_roi_delta_magnitude_delta_percent
                .unwrap_or_default(),
            roi_score
        ));
    }
    Some(summary)
}

fn dip_normalized_viewport_summary(scenario: &ScenarioResult) -> String {
    match (
        scenario.metrics.dip_normalized_viewport_width,
        scenario.metrics.dip_normalized_viewport_height,
        scenario.metrics.dip_normalized_pixel_error_percent,
        scenario.metrics.dip_normalized_ssim,
    ) {
        (Some(width), Some(height), Some(pixel), Some(ssim)) => format!(
            "{}x{} DIP px, pixel error {:.2}%, SSIM {:.3}",
            width, height, pixel, ssim
        ),
        _ => "unavailable".to_string(),
    }
}

fn format_region_bounds(bounds: &RegionBounds) -> String {
    format!(
        "x={}, y={}, width={}, height={}",
        bounds.x, bounds.y, bounds.width, bounds.height
    )
}

fn image_size_delta_summary(scenario: &ScenarioResult) -> String {
    image_size_pair_summary(
        scenario.reference_size.width,
        scenario.reference_size.height,
        scenario.candidate_size.width,
        scenario.candidate_size.height,
    )
}

fn image_size_pair_summary(
    reference_width: u32,
    reference_height: u32,
    candidate_width: u32,
    candidate_height: u32,
) -> String {
    format!(
        "{}x{} -> {}x{} ({:+}x{:+} px)",
        reference_width,
        reference_height,
        candidate_width,
        candidate_height,
        candidate_width as i64 - reference_width as i64,
        candidate_height as i64 - reference_height as i64
    )
}

fn absolute_image_size_summary(scenario: &ScenarioResult) -> String {
    let width_delta = scenario.candidate_size.width as i64 - scenario.reference_size.width as i64;
    let height_delta =
        scenario.candidate_size.height as i64 - scenario.reference_size.height as i64;
    let width_percent = percent_delta(scenario.reference_size.width, scenario.candidate_size.width);
    let height_percent = percent_delta(
        scenario.reference_size.height,
        scenario.candidate_size.height,
    );
    format!(
        "reference {}x{} px, candidate {}x{} px, delta {:+}x{:+} px ({:+.2}% width, {:+.2}% height)",
        scenario.reference_size.width,
        scenario.reference_size.height,
        scenario.candidate_size.width,
        scenario.candidate_size.height,
        width_delta,
        height_delta,
        width_percent,
        height_percent
    )
}

fn absolute_window_size_summary(metadata: Option<&ManifestScenario>) -> String {
    let Some(metadata) = metadata else {
        return "manifest window metadata unavailable".to_string();
    };
    let reference = manifest_window_size_summary(metadata.reference_window.as_ref());
    let candidate = manifest_window_size_summary(metadata.candidate_window.as_ref());
    match (reference, candidate) {
        (Some(reference), Some(candidate)) => {
            let delta_px = size_delta_text(
                reference.physical_width,
                reference.physical_height,
                candidate.physical_width,
                candidate.physical_height,
            );
            let delta_dip = match (
                reference.dip_width,
                reference.dip_height,
                candidate.dip_width,
                candidate.dip_height,
            ) {
                (
                    Some(reference_width),
                    Some(reference_height),
                    Some(candidate_width),
                    Some(candidate_height),
                ) => format!(
                    ", DIP delta {:+.2}x{:+.2}",
                    candidate_width - reference_width,
                    candidate_height - reference_height
                ),
                _ => String::new(),
            };
            format!(
                "reference {}, candidate {}, physical delta {}{}",
                reference.display, candidate.display, delta_px, delta_dip
            )
        }
        (None, Some(candidate)) => {
            format!("reference unavailable, candidate {}", candidate.display)
        }
        (Some(reference), None) => {
            format!("reference {}, candidate unavailable", reference.display)
        }
        (None, None) => "manifest window metadata unavailable".to_string(),
    }
}

fn window_targets_table_summary(metadata: Option<&ManifestScenario>) -> String {
    let reference = window_target_table_summary(metadata, WindowAuditSide::Reference);
    let candidate = window_target_table_summary(metadata, WindowAuditSide::Candidate);
    match (reference.as_str(), candidate.as_str()) {
        ("n/a", "n/a") => "n/a".to_string(),
        ("n/a", _) => format!("candidate {candidate}"),
        (_, "n/a") => format!("reference {reference}"),
        _ => format!("reference {reference}; candidate {candidate}"),
    }
}

fn window_target_table_summary(
    metadata: Option<&ManifestScenario>,
    side: WindowAuditSide,
) -> String {
    let Some(metadata) = metadata else {
        return "n/a".to_string();
    };
    let audit = window_size_audit(metadata, side);
    let target = window_expected_dips(metadata, side);
    let actual = audit.and_then(|audit| audit.actual_window_dips.as_ref());
    match (target, actual) {
        (Some(target), Some(actual)) => {
            let delta = audit
                .and_then(|audit| audit.delta_dips.as_ref())
                .map(format_signed_dip_size)
                .unwrap_or_else(|| {
                    format!(
                        "{:+.2}x{:+.2} DIP",
                        actual.width - target.width,
                        actual.height - target.height
                    )
                });
            let clamp = audit
                .and_then(|audit| audit.expected_larger_than_work_area)
                .unwrap_or(false);
            format!(
                "{} -> {} ({}){}",
                format_dip_size(target),
                format_dip_size(actual),
                delta,
                if clamp { ", work-area clamp" } else { "" }
            )
        }
        (Some(target), None) => format!("target {}", format_dip_size(target)),
        (None, Some(actual)) => format!("actual {}", format_dip_size(actual)),
        (None, None) => "n/a".to_string(),
    }
}

fn reference_window_size_audit_summary(metadata: Option<&ManifestScenario>) -> String {
    window_size_audit_summary(metadata, WindowAuditSide::Reference)
}

fn candidate_window_size_audit_summary(metadata: Option<&ManifestScenario>) -> String {
    window_size_audit_summary(metadata, WindowAuditSide::Candidate)
}

fn window_size_audit_summary(metadata: Option<&ManifestScenario>, side: WindowAuditSide) -> String {
    let Some(metadata) = metadata else {
        return "manifest window target metadata unavailable".to_string();
    };
    let target = window_expected_dips(metadata, side);
    let audit = window_size_audit(metadata, side);
    let actual = audit.and_then(|audit| audit.actual_window_dips.as_ref());
    if target.is_none() && actual.is_none() && audit.is_none() {
        return "manifest window target metadata unavailable".to_string();
    }

    let mut parts = Vec::new();
    if let Some(target) = target {
        parts.push(format!("expected {}", format_dip_size(target)));
    }
    if let Some(actual) = actual {
        parts.push(format!("actual {}", format_dip_size(actual)));
    }
    if let Some(delta) = audit.and_then(|audit| audit.delta_dips.as_ref()) {
        parts.push(format!("delta {}", format_signed_dip_size(delta)));
    } else if let (Some(target), Some(actual)) = (target, actual) {
        parts.push(format!(
            "delta {:+.2}x{:+.2} DIP",
            actual.width - target.width,
            actual.height - target.height
        ));
    }
    if let Some(delta_percent) = audit.and_then(|audit| audit.delta_percent.as_ref()) {
        parts.push(format!(
            "delta percent {}",
            format_percent_delta_size(delta_percent)
        ));
    }
    if let Some(work_area) = audit.and_then(|audit| audit.monitor_work_area_dips.as_ref()) {
        parts.push(format!("work area {}", format_dip_size(work_area)));
    }
    if audit
        .and_then(|audit| audit.expected_larger_than_work_area)
        .unwrap_or(false)
    {
        let effective_target = match side {
            WindowAuditSide::Reference => effective_reference_window_target_dips(metadata),
            WindowAuditSide::Candidate => effective_candidate_window_target_dips(metadata),
        };
        if let Some(effective_target) = effective_target {
            parts.push(format!(
                "effective target {}",
                format_dip_size(&effective_target)
            ));
        }
        parts.push(
            "expected target exceeds monitor work area; capture is work-area clamped".to_string(),
        );
    }
    parts.join(", ")
}

fn semantic_control_dimension_summary(metadata: Option<&ManifestScenario>) -> String {
    let Some(metadata) = metadata else {
        return "manifest UI dimensions unavailable".to_string();
    };
    control_dimension_summary(
        &metadata.required_semantic_tags,
        &metadata.required_control_states,
        metadata.reference_ui_summary.as_ref(),
        metadata.candidate_ui_summary.as_ref(),
    )
}

fn llm_control_dimension_summary(request: &LlmReviewRequest) -> String {
    control_dimension_summary(
        &request.required_semantic_tags,
        &request.required_control_states,
        request.reference_ui_summary.as_ref(),
        request.candidate_ui_summary.as_ref(),
    )
}

fn semantic_ui_summary_delta_summary(metadata: Option<&ManifestScenario>) -> Option<String> {
    let summary = compare_ui_summaries(metadata)?;
    let count_delta = if summary.control_count_deltas.is_empty() {
        "no control count deltas".to_string()
    } else {
        format!(
            "{}",
            summary
                .control_count_deltas
                .iter()
                .take(4)
                .map(|delta| {
                    format!(
                        "{} {}->{}",
                        delta.kind, delta.reference_count, delta.candidate_count
                    )
                })
                .collect::<Vec<_>>()
                .join(", ")
        )
    };
    let automation = summary
        .automation_id_jaccard
        .map(|score| format!("{score:.2}%"))
        .unwrap_or_else(|| "n/a".to_string());
    let visible_text = summary
        .visible_text_jaccard
        .map(|score| format!("{score:.2}% ({})", visible_text_delta_summary(&summary, 4)))
        .unwrap_or_else(|| "n/a".to_string());
    Some(format!(
        "semantic score {:.2}; control count delta {:.2}% ({count_delta}); automation ID Jaccard {automation}; visible text Jaccard {visible_text}; missing required tags/text/states {}/{}/{}",
        summary.score,
        summary.control_count_delta_percent,
        summary.missing_required_semantic_tags.len(),
        summary.missing_required_visible_texts.len(),
        summary.missing_required_control_states.len()
    ))
}

fn visible_text_delta_summary(summary: &UiSummaryComparison, limit: usize) -> String {
    let mut parts = Vec::new();
    if !summary.missing_reference_visible_texts.is_empty() {
        parts.push(format!(
            "missing from candidate: {}",
            quoted_preview(&summary.missing_reference_visible_texts, limit)
        ));
    }
    if !summary.extra_candidate_visible_texts.is_empty() {
        parts.push(format!(
            "extra in candidate: {}",
            quoted_preview(&summary.extra_candidate_visible_texts, limit)
        ));
    }
    if parts.is_empty() {
        "no visible text deltas".to_string()
    } else {
        parts.join("; ")
    }
}

fn quoted_preview(values: &[String], limit: usize) -> String {
    let mut preview = values
        .iter()
        .take(limit)
        .map(|value| format!("`{value}`"))
        .collect::<Vec<_>>();
    if values.len() > limit {
        preview.push(format!("... {} more", values.len() - limit));
    }
    preview.join(", ")
}

fn format_required_control_states(states: &BTreeMap<String, Vec<String>>) -> String {
    states
        .iter()
        .map(|(id, values)| format!("`{id}` requires `{}`", values.join("`, `")))
        .collect::<Vec<_>>()
        .join("; ")
}

fn control_dimension_summary(
    required_semantic_tags: &[String],
    required_control_states: &BTreeMap<String, Vec<String>>,
    reference_ui_summary: Option<&ManifestUiSummary>,
    candidate_ui_summary: Option<&ManifestUiSummary>,
) -> String {
    let reference_dimensions =
        reference_ui_summary.and_then(|summary| summary.visible_control_dimensions.as_ref());
    let candidate_dimensions =
        candidate_ui_summary.and_then(|summary| summary.visible_control_dimensions.as_ref());
    let has_reference_dimensions = reference_dimensions.is_some_and(|items| !items.is_empty());
    let has_candidate_dimensions = candidate_dimensions.is_some_and(|items| !items.is_empty());
    if !has_reference_dimensions && !has_candidate_dimensions {
        return "manifest UI dimensions unavailable".to_string();
    }

    let mut ids = Vec::<String>::new();
    let mut seen = BTreeSet::<String>::new();
    for tag in required_semantic_tags {
        let tag = tag.trim();
        if !tag.is_empty() && seen.insert(tag.to_ascii_lowercase()) {
            ids.push(tag.to_string());
        }
    }
    for id in required_control_states.keys() {
        let id = id.trim();
        if !id.is_empty() && seen.insert(id.to_ascii_lowercase()) {
            ids.push(id.to_string());
        }
    }
    if ids.is_empty() {
        let mut fallback_ids = BTreeSet::<String>::new();
        if let Some(dimensions) = candidate_dimensions {
            fallback_ids.extend(dimensions.keys().cloned());
        }
        if let Some(dimensions) = reference_dimensions {
            fallback_ids.extend(dimensions.keys().cloned());
        }
        ids.extend(fallback_ids);
    }

    let mut parts = Vec::new();
    for id in ids.iter().take(10) {
        let reference = reference_dimensions
            .and_then(|items| get_case_insensitive_dimension(items, id))
            .map(format_control_dimension)
            .unwrap_or_else(|| "n/a".to_string());
        let candidate = candidate_dimensions
            .and_then(|items| get_case_insensitive_dimension(items, id))
            .map(format_control_dimension)
            .unwrap_or_else(|| "n/a".to_string());
        parts.push(format!(
            "`{id}` reference {reference}, candidate {candidate}"
        ));
    }

    if parts.is_empty() {
        return "manifest UI dimensions unavailable".to_string();
    }
    let hidden_count = ids.len().saturating_sub(parts.len());
    if hidden_count > 0 {
        parts.push(format!("+{hidden_count} more"));
    }
    parts.join("; ")
}

fn get_case_insensitive_dimension<'a>(
    dimensions: &'a BTreeMap<String, ManifestControlDimension>,
    id: &str,
) -> Option<&'a ManifestControlDimension> {
    dimensions
        .iter()
        .find(|(actual, _)| actual.eq_ignore_ascii_case(id))
        .map(|(_, value)| value)
}

fn format_control_dimension(dimension: &ManifestControlDimension) -> String {
    let mut parts = Vec::new();
    parts.push(
        dimension
            .kind
            .as_deref()
            .filter(|value| !value.is_empty())
            .unwrap_or("control")
            .to_string(),
    );
    for (name, value) in [
        ("state", dimension.state.as_deref()),
        ("width", dimension.width.as_deref()),
        ("labeled_width", dimension.labeled_width.as_deref()),
        ("height", dimension.height.as_deref()),
        ("labeled_height", dimension.labeled_height.as_deref()),
        ("max_width", dimension.max_width.as_deref()),
        ("min_width", dimension.min_width.as_deref()),
        ("min_height", dimension.min_height.as_deref()),
        ("max_height", dimension.max_height.as_deref()),
        ("padding", dimension.padding.as_deref()),
        ("spacing", dimension.spacing.as_deref()),
        ("row_spacing", dimension.row_spacing.as_deref()),
        ("column_spacing", dimension.column_spacing.as_deref()),
        ("columns", dimension.columns.as_deref()),
        (
            "maximum_rows_or_columns",
            dimension.maximum_rows_or_columns.as_deref(),
        ),
        ("margin", dimension.margin.as_deref()),
    ] {
        if let Some(value) = value.filter(|value| !value.is_empty()) {
            parts.push(format!("{name}={value}"));
        }
    }
    if let Some(bounds) = dimension.bounds_dips.as_ref() {
        parts.push(format!("bounds_dips={}", format_bounds_dips(bounds)));
    }
    parts.join(" ")
}

fn format_bounds_dips(bounds: &ManifestControlBoundsDips) -> String {
    format!(
        "({},{},{},{})",
        format_dips(bounds.left),
        format_dips(bounds.top),
        format_dips(bounds.width),
        format_dips(bounds.height)
    )
}

fn reference_source_summary(metadata: Option<&ManifestScenario>) -> String {
    let Some(metadata) = metadata else {
        return "manifest reference source metadata unavailable".to_string();
    };
    reference_source_summary_parts(
        metadata.reference_source_kind.as_deref(),
        metadata.reference_source_path.as_deref(),
        metadata.reference_source_last_write_time_utc.as_deref(),
        metadata.reference_source_is_fallback,
    )
}

fn reference_source_summary_parts(
    kind: Option<&str>,
    path: Option<&str>,
    last_write_time_utc: Option<&str>,
    is_fallback: Option<bool>,
) -> String {
    let classification = match is_fallback {
        Some(true) => "fallback",
        Some(false) => "preferred",
        None => "unclassified",
    };
    let mut parts = vec![format!(
        "{} ({classification})",
        kind.filter(|value| !value.is_empty())
            .unwrap_or("unspecified")
    )];
    if let Some(path) = path.filter(|value| !value.is_empty()) {
        parts.push(format!("path {path}"));
    }
    if let Some(last_write_time_utc) = last_write_time_utc.filter(|value| !value.is_empty()) {
        parts.push(format!("last write {last_write_time_utc}"));
    }
    parts.join(", ")
}

fn window_expected_dips(
    metadata: &ManifestScenario,
    side: WindowAuditSide,
) -> Option<&ManifestDipSize> {
    match side {
        WindowAuditSide::Reference => metadata
            .reference_window_size_audit
            .as_ref()
            .and_then(|audit| audit.expected_window_dips.as_ref())
            .or(metadata.reference_expected_window_dips.as_ref()),
        WindowAuditSide::Candidate => metadata
            .candidate_window_size_audit
            .as_ref()
            .and_then(|audit| audit.expected_window_dips.as_ref())
            .or(metadata.candidate_expected_window_dips.as_ref()),
    }
}

fn format_dip_size(size: &ManifestDipSize) -> String {
    format!("{:.2}x{:.2} DIP", size.width, size.height)
}

fn format_signed_dip_size(size: &ManifestDipSize) -> String {
    format!("{:+.2}x{:+.2} DIP", size.width, size.height)
}

fn format_percent_delta_size(size: &ManifestDipSize) -> String {
    format!("{:+.2}% width, {:+.2}% height", size.width, size.height)
}

#[derive(Clone, Debug)]
struct WindowSizeSummary {
    physical_width: i32,
    physical_height: i32,
    dip_width: Option<f64>,
    dip_height: Option<f64>,
    display: String,
}

fn manifest_window_size_summary(window: Option<&ManifestWindow>) -> Option<WindowSizeSummary> {
    let window = window?;
    let bounds = window.bounds.as_ref()?;
    let scale = if window.dpi_scale > 0.0 {
        window.dpi_scale
    } else {
        1.0
    };
    let dip_width = Some(bounds.width as f64 / scale);
    let dip_height = Some(bounds.height as f64 / scale);
    let display = format!(
        "{}x{} px @ {:.3}x ({:.2}x{:.2} DIP)",
        bounds.width,
        bounds.height,
        scale,
        dip_width.unwrap_or_default(),
        dip_height.unwrap_or_default()
    );
    Some(WindowSizeSummary {
        physical_width: bounds.width,
        physical_height: bounds.height,
        dip_width,
        dip_height,
        display,
    })
}

fn size_delta_text(
    reference_width: i32,
    reference_height: i32,
    candidate_width: i32,
    candidate_height: i32,
) -> String {
    format!(
        "{:+}x{:+} px",
        candidate_width - reference_width,
        candidate_height - reference_height
    )
}

fn percent_delta(reference: u32, candidate: u32) -> f64 {
    if reference == 0 {
        0.0
    } else {
        ((candidate as f64 - reference as f64) / reference as f64) * 100.0
    }
}

fn coverage_markdown_report(report: &ParityCoverageReport) -> String {
    let mut out = String::new();
    out.push_str("# UI Parity Coverage\n\n");
    out.push_str(&format!(
        "- Covered: **{}/{}** ({:.2}%)\n",
        report.summary.covered, report.summary.total, report.summary.coverage_percent
    ));
    out.push_str(&format!(
        "- Critical covered: **{}/{}** ({:.2}%)\n\n",
        report.summary.critical_covered,
        report.summary.critical_total,
        report.summary.critical_coverage_percent
    ));
    for area in &report.areas {
        out.push_str(&format!("## {}\n\n", area.area));
        out.push_str("| Status | Priority | Id | Matching scenarios | Next evidence |\n");
        out.push_str("| --- | --- | --- | --- | --- |\n");
        for item in &area.items {
            out.push_str(&format!(
                "| `{}` | `{:?}` | `{}` | `{}` | {} |\n",
                item.evidence_status,
                item.priority,
                item.id,
                item.matching_scenario_ids.join(", "),
                item.next_evidence
            ));
        }
        out.push('\n');
    }
    out
}

fn coverage_review_requests(report: &ParityCoverageReport) -> Vec<CoverageReviewRequest> {
    report
        .areas
        .iter()
        .flat_map(|area| &area.items)
        .filter(|item| {
            item.evidence_status == CoverageEvidenceStatus::Missing.id()
                || (item.priority == CoveragePriority::Critical
                    && item.evidence_status == CoverageEvidenceStatus::CoveredFailing.id())
        })
        .map(|item| CoverageReviewRequest {
            schema_version: "easydict.ui-parity.coverage-review.v1".to_string(),
            item_id: item.id.clone(),
            display_name: item.display_name.clone(),
            priority: item.priority,
            layer_hint: item.layer_hint.clone(),
            evidence_status: item.evidence_status.clone(),
            matching_scenario_ids: item.matching_scenario_ids.clone(),
            next_evidence: item.next_evidence.clone(),
        })
        .collect()
}

fn coverage_review_prompts(requests: &[CoverageReviewRequest]) -> String {
    let mut out = String::new();
    out.push_str("# UI Parity Coverage Review Prompts\n\n");
    if requests.is_empty() {
        out.push_str("No missing or critical failing coverage items need LLM review.\n");
        return out;
    }
    for request in requests {
        out.push_str(&format!(
            "## {}\n\nLayer: `{}`. Status: `{}`. Next evidence: {}\n\n",
            request.item_id, request.layer_hint, request.evidence_status, request.next_evidence
        ));
    }
    out
}

fn llm_review_requests(report: &ParityReport) -> Vec<LlmReviewRequest> {
    report
        .scenarios
        .iter()
        .filter(|scenario| {
            scenario.status != ScoreStatus::Pass
                || scenario
                    .findings
                    .iter()
                    .any(|finding| finding.severity == "warning" || finding.severity == "error")
        })
        .map(|scenario| LlmReviewRequest {
            schema_version: "easydict.ui-parity.llm-review.v1".to_string(),
            scenario_id: scenario.scenario_id.clone(),
            status: scenario.status,
            score: scenario.score,
            reference_image: scenario.normalized_reference_path.clone(),
            candidate_image: scenario.normalized_candidate_path.clone(),
            diff_heatmap: scenario.diff_heatmap_path.clone(),
            contact_sheet: scenario.contact_sheet_path.clone(),
            reference_size: scenario.reference_size.clone(),
            candidate_size: scenario.candidate_size.clone(),
            reference_window: scenario
                .metadata
                .as_ref()
                .and_then(|metadata| metadata.reference_window.clone()),
            candidate_window: scenario
                .metadata
                .as_ref()
                .and_then(|metadata| metadata.candidate_window.clone()),
            reference_window_size_audit: scenario
                .metadata
                .as_ref()
                .and_then(|metadata| metadata.reference_window_size_audit.clone()),
            candidate_window_size_audit: scenario
                .metadata
                .as_ref()
                .and_then(|metadata| metadata.candidate_window_size_audit.clone()),
            reference_source_kind: scenario
                .metadata
                .as_ref()
                .and_then(|metadata| metadata.reference_source_kind.clone()),
            reference_source_path: scenario
                .metadata
                .as_ref()
                .and_then(|metadata| metadata.reference_source_path.clone()),
            reference_source_last_write_time_utc: scenario
                .metadata
                .as_ref()
                .and_then(|metadata| metadata.reference_source_last_write_time_utc.clone()),
            reference_source_is_fallback: scenario
                .metadata
                .as_ref()
                .and_then(|metadata| metadata.reference_source_is_fallback),
            required_semantic_tags: scenario
                .metadata
                .as_ref()
                .map(|metadata| metadata.required_semantic_tags.clone())
                .unwrap_or_default(),
            required_visible_texts: scenario
                .metadata
                .as_ref()
                .map(|metadata| metadata.required_visible_texts.clone())
                .unwrap_or_default(),
            required_control_states: scenario
                .metadata
                .as_ref()
                .map(|metadata| metadata.required_control_states.clone())
                .unwrap_or_default(),
            reference_ui_summary: scenario
                .metadata
                .as_ref()
                .and_then(|metadata| metadata.reference_ui_summary.clone()),
            candidate_ui_summary: scenario
                .metadata
                .as_ref()
                .and_then(|metadata| metadata.candidate_ui_summary.clone()),
            ui_semantic_delta_summary: semantic_ui_summary_delta_summary(
                scenario.metadata.as_ref(),
            ),
            evidence_audit: scenario.evidence_audit.clone(),
            metrics: scenario.metrics.clone(),
            findings: scenario.findings.clone(),
            regions: scenario.regions.clone(),
        })
        .collect()
}

fn llm_review_prompts(requests: &[LlmReviewRequest]) -> String {
    let mut out = String::new();
    out.push_str("# UI Parity LLM Review Prompts\n\n");
    out.push_str("Use these prompts only for advisory triage. Deterministic thresholds remain the CI source of truth.\n\n");
    if requests.is_empty() {
        out.push_str("No warn/fail scenarios or review findings need LLM review.\n");
        return out;
    }
    for request in requests {
        out.push_str(&format!(
            "## {}\n\nStatus: `{:?}`, score: `{:.2}`\n\nReference: `{}`\nCandidate: `{}`\nDiff: `{}`\nContact sheet: `{}`\n\n",
            request.scenario_id,
            request.status,
            request.score,
            request.reference_image,
            request.candidate_image,
            request.diff_heatmap,
            request.contact_sheet
        ));
        out.push_str("Task: compare the .NET WinUI reference and Rust/Iced candidate. Classify the drift as layout, typography, color/theme, interaction state, window/runtime semantics, screenshot/crop issue, or acceptable variance. Return concise remediation advice; deterministic scores remain authoritative.\n\n");
        out.push_str(&format!(
            "Metrics: pixel error `{:.4}%`, SSIM `{:.5}`, hash score `{:.2}`, size score `{:.2}`, palette score `{:.2}`, visual score `{:.2}`, image size delta `{:.2}%`, window size delta `{}`, runtime cap `{}`, absolute size cap `{}`.\n\n",
            request.metrics.pixel_error_percent,
            request.metrics.ssim,
            request.metrics.hash_score,
            request.metrics.size_score,
            request.metrics.palette_score,
            request.metrics.visual_score,
            request.metrics.absolute_image_size_delta_percent,
            request
                .metrics
                .absolute_window_size_delta_percent
                .map(|value| format!("{value:.2}%"))
                .unwrap_or_else(|| "n/a".to_string()),
            request
                .metrics
                .window_runtime_score_cap
                .map(|value| format!("{value:.2}"))
                .unwrap_or_else(|| "n/a".to_string()),
            request
                .metrics
                .absolute_size_score_cap
                .map(|value| format!("{value:.2}"))
                .unwrap_or_else(|| "n/a".to_string())
        ));
        out.push_str(&format!(
            "Absolute sizes: image {}; window {}; reference target {}; candidate target {}.\n\n",
            llm_image_size_summary(request),
            llm_window_size_summary(request),
            llm_window_size_audit_summary(request.reference_window_size_audit.as_ref()),
            llm_candidate_window_size_audit_summary(request)
        ));
        out.push_str(&format!(
            "Control absolute sizes: {}.\n\n",
            llm_control_dimension_summary(request)
        ));
        if let Some(summary) = request.ui_semantic_delta_summary.as_ref() {
            out.push_str(&format!("UI semantic summary: {summary}.\n\n"));
        }
        if let Some(audit) = request.evidence_audit.as_ref() {
            out.push_str(&format!(
                "Evidence audit: {}.\n\n",
                evidence_audit_summary(audit, 8)
            ));
        }
        if let Some(baseline) = request.metrics.effect_baseline_scenario_id.as_ref() {
            out.push_str(&format!(
                "Interaction effect baseline: `{}`; full window WinUI changed `{:.4}%`, Rust changed `{:.4}%`, magnitude delta `{:.4}%`, effect score `{:.2}`.\n\n",
                baseline,
                request
                    .metrics
                    .reference_effect_pixel_error_percent
                    .unwrap_or_default(),
                request
                    .metrics
                    .candidate_effect_pixel_error_percent
                    .unwrap_or_default(),
                request
                    .metrics
                    .effect_delta_magnitude_delta_percent
                    .unwrap_or_default(),
                request
                    .metrics
                    .interaction_effect_delta_score
                    .unwrap_or_default()
            ));
            if let Some(roi_score) = request.metrics.interaction_effect_roi_delta_score {
                let target = request
                    .metrics
                    .interaction_effect_roi_target_ids
                    .as_ref()
                    .map(|items| items.join(","))
                    .filter(|value| !value.is_empty())
                    .unwrap_or_else(|| "unknown".to_string());
                let bounds = request
                    .metrics
                    .interaction_effect_roi_bounds
                    .as_ref()
                    .map(format_region_bounds)
                    .unwrap_or_else(|| "n/a".to_string());
                out.push_str(&format!(
                    "Interaction effect ROI: target `{target}`, bounds `{bounds}`; WinUI changed `{:.4}%`, Rust changed `{:.4}%`, magnitude delta `{:.4}%`, ROI effect score `{:.2}`.\n\n",
                    request
                        .metrics
                        .reference_effect_roi_pixel_error_percent
                        .unwrap_or_default(),
                    request
                        .metrics
                        .candidate_effect_roi_pixel_error_percent
                        .unwrap_or_default(),
                    request
                        .metrics
                        .effect_roi_delta_magnitude_delta_percent
                        .unwrap_or_default(),
                    roi_score
                ));
            }
        }
        if !request.required_visible_texts.is_empty() {
            out.push_str(&format!(
                "Required visible texts: `{}`.\n\n",
                request.required_visible_texts.join("`, `")
            ));
        }
        if !request.required_control_states.is_empty() {
            out.push_str(&format!(
                "Required control states: {}.\n\n",
                format_required_control_states(&request.required_control_states)
            ));
        }
        out.push_str(&format!(
            "Reference source: {}.\n\n",
            llm_reference_source_summary(request)
        ));
        if !request.findings.is_empty() {
            out.push_str("Findings:\n");
            for finding in request.findings.iter().take(6) {
                out.push_str(&format!(
                    "- `{}` `{}` `{}`=`{:.2}`: {}\n",
                    finding.severity,
                    finding.layer_hint,
                    finding.metric,
                    finding.value,
                    finding.message
                ));
            }
            out.push('\n');
        }
        let mut regions = request.regions.iter().collect::<Vec<_>>();
        regions.sort_by(|a, b| {
            a.score
                .partial_cmp(&b.score)
                .unwrap_or(std::cmp::Ordering::Equal)
        });
        if !regions.is_empty() {
            out.push_str("Lowest scoring regions:\n");
            for region in regions.into_iter().take(3) {
                out.push_str(&format!(
                    "- `{}` score `{:.2}`, pixel error `{:.4}%`, SSIM `{:.5}`, bounds `{}, {}, {}, {}`\n",
                    region.name,
                    region.score,
                    region.pixel_error_percent,
                    region.ssim,
                    region.bounds.x,
                    region.bounds.y,
                    region.bounds.width,
                    region.bounds.height
                ));
            }
            out.push('\n');
        }
    }
    out
}

fn llm_image_size_summary(request: &LlmReviewRequest) -> String {
    let width_delta = request.candidate_size.width as i64 - request.reference_size.width as i64;
    let height_delta = request.candidate_size.height as i64 - request.reference_size.height as i64;
    let width_percent = percent_delta(request.reference_size.width, request.candidate_size.width);
    let height_percent =
        percent_delta(request.reference_size.height, request.candidate_size.height);
    format!(
        "reference {}x{} px, candidate {}x{} px, delta {:+}x{:+} px ({:+.2}% width, {:+.2}% height)",
        request.reference_size.width,
        request.reference_size.height,
        request.candidate_size.width,
        request.candidate_size.height,
        width_delta,
        height_delta,
        width_percent,
        height_percent
    )
}

fn llm_window_size_summary(request: &LlmReviewRequest) -> String {
    let reference = manifest_window_size_summary(request.reference_window.as_ref());
    let candidate = manifest_window_size_summary(request.candidate_window.as_ref());
    match (reference, candidate) {
        (Some(reference), Some(candidate)) => {
            format!(
                "reference {}, candidate {}",
                reference.display, candidate.display
            )
        }
        (None, Some(candidate)) => {
            format!("reference unavailable, candidate {}", candidate.display)
        }
        (Some(reference), None) => {
            format!("reference {}, candidate unavailable", reference.display)
        }
        (None, None) => "manifest window metadata unavailable".to_string(),
    }
}

fn llm_candidate_window_size_audit_summary(request: &LlmReviewRequest) -> String {
    llm_window_size_audit_summary(request.candidate_window_size_audit.as_ref())
}

fn llm_window_size_audit_summary(audit: Option<&ManifestWindowSizeAudit>) -> String {
    let Some(audit) = audit else {
        return "target metadata unavailable".to_string();
    };
    let mut parts = Vec::new();
    if let Some(expected) = &audit.expected_window_dips {
        parts.push(format!("expected {}", format_dip_size(expected)));
    }
    if let Some(actual) = &audit.actual_window_dips {
        parts.push(format!("actual {}", format_dip_size(actual)));
    }
    if let Some(delta) = &audit.delta_dips {
        parts.push(format!("delta {}", format_signed_dip_size(delta)));
    }
    if let Some(delta_percent) = &audit.delta_percent {
        parts.push(format!(
            "delta percent {}",
            format_percent_delta_size(delta_percent)
        ));
    }
    if let Some(work_area) = &audit.monitor_work_area_dips {
        parts.push(format!("work area {}", format_dip_size(work_area)));
    }
    if audit.expected_larger_than_work_area.unwrap_or(false) {
        parts.push("work-area clamped".to_string());
    }
    parts.join(", ")
}

fn llm_reference_source_summary(request: &LlmReviewRequest) -> String {
    reference_source_summary_parts(
        request.reference_source_kind.as_deref(),
        request.reference_source_path.as_deref(),
        request.reference_source_last_write_time_utc.as_deref(),
        request.reference_source_is_fallback,
    )
}

fn coverage_gate_failure(report: &ParityCoverageReport, options: &CliOptions) -> Option<String> {
    let mut failures = Vec::new();
    if let Some(minimum) = options.min_coverage_percent {
        if report.summary.coverage_percent < minimum {
            failures.push(format!(
                "total coverage {:.2}% is below {:.2}%",
                report.summary.coverage_percent, minimum
            ));
        }
    }
    if let Some(minimum) = options.min_critical_coverage_percent {
        if report.summary.critical_coverage_percent < minimum {
            failures.push(format!(
                "critical coverage {:.2}% is below {:.2}%",
                report.summary.critical_coverage_percent, minimum
            ));
        }
    }
    if options.fail_on_critical_coverage_missing && report.summary.critical_missing > 0 {
        failures.push(format!(
            "{} critical visual evidence item(s) are missing",
            report.summary.critical_missing
        ));
    }
    if failures.is_empty() {
        None
    } else {
        Some(format!(
            "UI parity coverage gate failed: {}.",
            failures.join("; ")
        ))
    }
}

fn run_self_test() -> Result<bool, String> {
    let root =
        std::env::temp_dir().join(format!("easydict-ui-parity-self-test-{}", unique_stamp()));
    let output_dir = root.join("ui-parity");
    fs::create_dir_all(&root).map_err(|error| error.to_string())?;

    let reference_path = root.join(format!("self-test-identical{DOTNET_SUFFIX}"));
    let identical_path = root.join(format!("self-test-identical{RUST_SUFFIX}"));
    let drift_reference_path = root.join(format!("self-test-drift{DOTNET_SUFFIX}"));
    let drift_candidate_path = root.join(format!("self-test-drift{RUST_SUFFIX}"));
    let collapse_reference_path =
        root.join(format!("effects.result-collapse-toggle{DOTNET_SUFFIX}"));
    let collapse_candidate_path = root.join(format!("effects.result-collapse-toggle{RUST_SUFFIX}"));
    let service_reference_path = root.join(format!("long-doc.service-dropdown{DOTNET_SUFFIX}"));
    let service_candidate_path = root.join(format!("long-doc.service-dropdown{RUST_SUFFIX}"));
    let pop_reference_path = root.join(format!("popbutton.hover{DOTNET_SUFFIX}"));
    let pop_candidate_path = root.join(format!("popbutton.hover{RUST_SUFFIX}"));
    let floating_action_reference_path =
        root.join(format!("effects.floating-action-pressed{DOTNET_SUFFIX}"));
    let floating_action_candidate_path =
        root.join(format!("effects.floating-action-pressed{RUST_SUFFIX}"));
    let mini_pressed_reference_path = root.join(format!("mini.translate-pressed{DOTNET_SUFFIX}"));
    let mini_pressed_candidate_path = root.join(format!("mini.translate-pressed{RUST_SUFFIX}"));
    let ocr_reference_path = root.join(format!("ocr.window-detect{DOTNET_SUFFIX}"));
    let ocr_candidate_path = root.join(format!("ocr.window-detect{RUST_SUFFIX}"));

    create_synthetic_frame(false)
        .save(&reference_path)
        .map_err(|e| e.to_string())?;
    create_synthetic_frame(false)
        .save(&identical_path)
        .map_err(|e| e.to_string())?;
    create_synthetic_frame(false)
        .save(&drift_reference_path)
        .map_err(|e| e.to_string())?;
    create_synthetic_frame(true)
        .save(&drift_candidate_path)
        .map_err(|e| e.to_string())?;
    create_synthetic_frame(false)
        .save(&collapse_reference_path)
        .map_err(|e| e.to_string())?;
    create_synthetic_frame(false)
        .save(&collapse_candidate_path)
        .map_err(|e| e.to_string())?;
    create_synthetic_frame(false)
        .save(&service_reference_path)
        .map_err(|e| e.to_string())?;
    create_synthetic_frame(true)
        .save(&service_candidate_path)
        .map_err(|e| e.to_string())?;
    create_synthetic_frame(false)
        .save(&pop_reference_path)
        .map_err(|e| e.to_string())?;
    create_synthetic_frame(true)
        .save(&pop_candidate_path)
        .map_err(|e| e.to_string())?;
    create_synthetic_frame(false)
        .save(&floating_action_reference_path)
        .map_err(|e| e.to_string())?;
    create_synthetic_frame(true)
        .save(&floating_action_candidate_path)
        .map_err(|e| e.to_string())?;
    create_synthetic_frame(false)
        .save(&mini_pressed_reference_path)
        .map_err(|e| e.to_string())?;
    create_synthetic_frame(true)
        .save(&mini_pressed_candidate_path)
        .map_err(|e| e.to_string())?;
    create_synthetic_frame(false)
        .save(&ocr_reference_path)
        .map_err(|e| e.to_string())?;
    create_synthetic_frame(false)
        .save(&ocr_candidate_path)
        .map_err(|e| e.to_string())?;

    write_self_test_manifest(
        &root,
        &reference_path,
        &identical_path,
        &service_reference_path,
        &service_candidate_path,
        &pop_reference_path,
        &pop_candidate_path,
        &ocr_reference_path,
        &ocr_candidate_path,
    )?;

    let options = CliOptions {
        screenshot_root: root.clone(),
        output_dir,
        manifest_path: Some(root.join("ui-parity-manifest.json")),
        pass_score: 85.0,
        warn_score: 70.0,
        score_gate_rules: vec![
            ScoreGateRule {
                layer: "final_effect".to_string(),
                case: "self-test-identical".to_string(),
                pass_score: 99.0,
                warn_score: 95.0,
            },
            ScoreGateRule {
                layer: "window_runtime".to_string(),
                case: "popbutton.*".to_string(),
                pass_score: 80.0,
                warn_score: 65.0,
            },
        ],
        min_coverage_percent: Some(0.0),
        min_critical_coverage_percent: Some(0.0),
        fail_on_critical_coverage_missing: false,
        fail_on_threshold: false,
        require_manifest: true,
        manifest_only: false,
        self_test: false,
    };
    let pairs = load_pairs(&options)?;
    let scenarios = pairs
        .iter()
        .map(|pair| analyze_pair(pair, &options))
        .collect::<Result<Vec<_>, _>>()?;
    let report = ParityReport::create(&options, scenarios);
    let coverage = ParityCoverageReport::create(&report);
    write_reports(&report, &coverage, &options)?;

    let scenario = |id: &str| {
        report
            .scenarios
            .iter()
            .find(|scenario| scenario.scenario_id == id)
    };
    let coverage_item = |id: &str| {
        coverage
            .areas
            .iter()
            .flat_map(|area| &area.items)
            .find(|item| item.id == id)
    };

    let passed = scenario("self-test-identical").is_some_and(|scenario| {
        scenario.status == ScoreStatus::Pass
            && scenario.score >= 99.0
            && scenario.gate.source == "score-gate"
            && scenario.gate.layer == "final_effect"
    }) && scenario("self-test-drift")
        .is_some_and(|scenario| scenario.status == ScoreStatus::Fail && scenario.score < 70.0)
        && scenario("effects.result-collapse-toggle").is_some_and(|scenario| {
            scenario.status == ScoreStatus::Pass
                && scenario.score >= 99.0
                && scenario.metrics.scoring_profile == "interaction-animation"
        })
        && scenario("long-doc.service-dropdown").is_some_and(|scenario| {
            scenario
                .metadata
                .as_ref()
                .is_some_and(|metadata| metadata.window_kind == "long-document")
                && scenario
                    .regions
                    .iter()
                    .any(|region| region.name == "long-doc-service-popup")
                && scenario
                    .findings
                    .iter()
                    .any(|finding| finding.metric == "region.long-doc-service-popup.score")
        })
        && scenario("popbutton.hover").is_some_and(|scenario| {
            scenario.status == ScoreStatus::Fail
                && scenario.gate.source == "score-gate"
                && scenario.gate.layer == "window_runtime"
                && scenario
                    .findings
                    .iter()
                    .any(|finding| finding.metric == "candidateWindow.hasNoActivate")
        })
        && scenario("ocr.window-detect").is_some_and(|scenario| {
            scenario.status == ScoreStatus::Fail
                && scenario.metrics.semantic_score.unwrap_or_default() >= 99.0
                && scenario
                    .metrics
                    .window_runtime_score_cap
                    .unwrap_or_default()
                    <= 65.0
                && scenario
                    .findings
                    .iter()
                    .any(|finding| finding.metric == "candidateWindow.overlayCoveragePercent")
        })
        && coverage_item("popbutton.hover").is_some_and(|item| {
            item.evidence_status == CoverageEvidenceStatus::CoveredFailing.id()
                && item.layer_hint == "window_runtime"
        })
        && coverage_item("ocr.window-detect").is_some_and(|item| {
            item.evidence_status == CoverageEvidenceStatus::CoveredFailing.id()
                && item.layer_hint == "iced_backend"
        })
        && coverage_item("effects.floating-action-pressed").is_some_and(|item| {
            item.evidence_status == CoverageEvidenceStatus::CoveredFailing.id()
                && item
                    .matching_scenario_ids
                    .iter()
                    .any(|id| id == "effects.floating-action-pressed")
        });

    println!(
        "Self-test identical score: {:.2}",
        scenario("self-test-identical")
            .map(|s| s.score)
            .unwrap_or_default()
    );
    println!(
        "Self-test drift score: {:.2}",
        scenario("self-test-drift")
            .map(|s| s.score)
            .unwrap_or_default()
    );
    println!(
        "{}",
        if passed {
            "UiParityAnalyzer self-test passed."
        } else {
            "UiParityAnalyzer self-test failed."
        }
    );
    Ok(passed)
}

fn create_synthetic_frame(add_large_drift: bool) -> RgbaImage {
    let mut image = RgbaImage::new(260, 180);
    for y in 0..180 {
        for x in 0..260 {
            let color = if y < 34 {
                Rgba([36, 46, 62, 255])
            } else if y > 152 {
                Rgba([232, 237, 242, 255])
            } else if x < 64 {
                Rgba([248, 250, 252, 255])
            } else {
                Rgba([210, 216, 224, 255])
            };
            image.put_pixel(x, y, color);
            if add_large_drift && x > 75 && x < 205 && y > 55 && y < 150 {
                image.put_pixel(x, y, Rgba([34, 45, 64, 255]));
            }
        }
    }
    image
}

fn write_self_test_manifest(
    root: &Path,
    reference_path: &Path,
    identical_path: &Path,
    service_reference_path: &Path,
    service_candidate_path: &Path,
    pop_reference_path: &Path,
    pop_candidate_path: &Path,
    ocr_reference_path: &Path,
    ocr_candidate_path: &Path,
) -> Result<(), String> {
    let scenario = |scenario_id: &str,
                    window_kind: &str,
                    reference: &Path,
                    candidate: &Path,
                    regions: Vec<Value>,
                    reference_window: Value,
                    candidate_window: Value| {
        serde_json::json!({
            "ScenarioId": scenario_id,
            "WindowKind": window_kind,
            "SectionId": scenario_id,
            "SectionLabel": scenario_id,
            "Theme": "dark",
            "ScrollPercent": 0.0,
            "ExpandAvailableLanguages": false,
            "ReferenceScreenshot": relative_path(root, reference),
            "CandidateScreenshot": relative_path(root, candidate),
            "SideBySideScreenshot": null,
            "ReferenceWindow": reference_window,
            "CandidateWindow": candidate_window,
            "Regions": regions,
            "RequiredSemanticTags": ["Root", "TranslateButton"],
            "ReferenceUiSummary": {
                "VisibleControlCounts": {},
                "VisibleAutomationIds": []
            },
            "CandidateUiSummary": {
                "VisibleControlCounts": {},
                "VisibleAutomationIds": ["Root", "TranslateButton"]
            }
        })
    };
    let normal_window = serde_json::json!({
        "Bounds": { "Left": 0, "Top": 0, "Width": 260, "Height": 180 },
        "DpiScale": 1.0,
        "HasNoActivate": false,
        "HasToolWindow": false,
        "HasTopmost": true,
        "IsForegroundAtCapture": false
    });
    let pop_candidate_window = serde_json::json!({
        "Bounds": { "Left": 0, "Top": 0, "Width": 260, "Height": 180 },
        "DpiScale": 1.0,
        "HasNoActivate": false,
        "HasToolWindow": true,
        "HasTopmost": true,
        "IsForegroundAtCapture": false
    });
    let ocr_candidate_window = serde_json::json!({
        "Bounds": { "Left": 0, "Top": 0, "Width": 140, "Height": 95 },
        "DpiScale": 1.0,
        "HasNoActivate": false,
        "HasToolWindow": true,
        "HasTopmost": true,
        "IsForegroundAtCapture": false
    });
    let service_regions = vec![serde_json::json!({
        "Name": "long-doc-service-popup",
        "X": 0.25,
        "Y": 0.20,
        "Width": 0.55,
        "Height": 0.42,
        "Weight": 2.0
    })];
    let scenarios = vec![
        scenario(
            "self-test-identical",
            "main",
            reference_path,
            identical_path,
            Vec::new(),
            normal_window.clone(),
            normal_window.clone(),
        ),
        scenario(
            "long-doc.service-dropdown",
            "long-document",
            service_reference_path,
            service_candidate_path,
            service_regions,
            normal_window.clone(),
            normal_window.clone(),
        ),
        scenario(
            "popbutton.hover",
            "popbutton",
            pop_reference_path,
            pop_candidate_path,
            Vec::new(),
            normal_window.clone(),
            pop_candidate_window,
        ),
        scenario(
            "ocr.window-detect",
            "ocr",
            ocr_reference_path,
            ocr_candidate_path,
            Vec::new(),
            normal_window.clone(),
            ocr_candidate_window,
        ),
    ];
    let manifest = serde_json::json!({
        "SchemaVersion": "easydict.ui-parity.manifest.v1",
        "GeneratedAtUtc": now_string(),
        "Scenarios": scenarios
    });
    fs::write(
        root.join("ui-parity-manifest.json"),
        serde_json::to_string_pretty(&manifest).map_err(|error| error.to_string())?,
    )
    .map_err(|error| error.to_string())
}

#[derive(Debug, Clone)]
struct RegionSpec {
    name: String,
    x: u32,
    y: u32,
    width: u32,
    height: u32,
    weight: f64,
}

impl RegionSpec {
    fn new(name: &str, x: u32, y: u32, width: u32, height: u32, weight: f64) -> Self {
        Self {
            name: name.to_string(),
            x,
            y,
            width,
            height,
            weight,
        }
    }

    fn full(width: u32, height: u32) -> Self {
        Self::new("full", 0, 0, width, height, 1.0)
    }
}

#[derive(Debug, Clone)]
struct FractionalRegion {
    name: String,
    x: f64,
    y: f64,
    width: f64,
    height: f64,
    weight: f64,
}

impl FractionalRegion {
    fn new(name: &str, x: f64, y: f64, width: f64, height: f64, weight: f64) -> Self {
        Self {
            name: name.to_string(),
            x,
            y,
            width,
            height,
            weight,
        }
    }
}

#[derive(Debug, Clone)]
struct PixelComparison {
    pixel_error_percent: f64,
    mean_channel_delta: f64,
    max_channel_delta: i32,
}

#[derive(Debug, Clone)]
struct DipNormalizedViewportComparison {
    reference: RgbaImage,
    candidate: RgbaImage,
    width: u32,
    height: u32,
    pixel: PixelComparison,
    ssim: f64,
}

#[derive(Debug, Clone)]
struct PaletteComparison {
    #[allow(dead_code)]
    reference_average: ColorVector,
    #[allow(dead_code)]
    candidate_average: ColorVector,
    average_color_delta: f64,
}

#[derive(Debug, Clone)]
struct ColorVector {
    r: f64,
    g: f64,
    b: f64,
}

#[derive(Debug, Clone)]
struct UiSummaryComparison {
    score: f64,
    control_count_delta_percent: f64,
    control_count_deltas: Vec<ControlCountDelta>,
    automation_id_jaccard: Option<f64>,
    visible_text_jaccard: Option<f64>,
    missing_reference_visible_texts: Vec<String>,
    extra_candidate_visible_texts: Vec<String>,
    missing_required_semantic_tags: Vec<String>,
    missing_required_visible_texts: Vec<String>,
    missing_required_control_states: Vec<ControlStateDelta>,
    missing_control_bounds_evidence: Vec<ControlBoundsEvidenceGap>,
    control_dimension_delta_count: usize,
    max_control_dimension_delta_dips: f64,
    control_dimension_deltas: Vec<ControlDimensionDelta>,
}

#[derive(Debug, Clone)]
struct ControlCountDelta {
    kind: String,
    reference_count: i32,
    candidate_count: i32,
    delta_abs: i32,
}

#[derive(Debug, Clone, Default)]
struct VisibleTextComparison {
    jaccard: Option<f64>,
    missing_reference_texts: Vec<String>,
    extra_candidate_texts: Vec<String>,
}

#[derive(Debug, Clone)]
struct ControlDimensionDelta {
    id: String,
    property: &'static str,
    reference: String,
    candidate: String,
    delta_abs: f64,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct EvidenceAudit {
    reference_automation_id_count: usize,
    candidate_automation_id_count: usize,
    missing_candidate_automation_id_count: usize,
    missing_candidate_automation_ids: Vec<String>,
    reference_control_dimension_count: usize,
    candidate_control_dimension_count: usize,
    missing_candidate_dimension_count: usize,
    missing_candidate_dimension_ids: Vec<String>,
    reference_control_bounds_count: usize,
    candidate_control_bounds_count: usize,
    missing_candidate_bounds_count: usize,
    missing_candidate_bounds: Vec<ControlBoundsEvidenceGap>,
    candidate_dimension_without_bounds_count: usize,
    candidate_dimension_without_bounds_ids: Vec<String>,
}

impl EvidenceAudit {
    fn has_gaps(&self) -> bool {
        self.missing_candidate_automation_id_count > 0
            || self.missing_candidate_dimension_count > 0
            || self.missing_candidate_bounds_count > 0
            || self.candidate_dimension_without_bounds_count > 0
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct ControlBoundsEvidenceGap {
    id: String,
    reference_bounds: String,
    candidate: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct ControlStateDelta {
    id: String,
    required_state: String,
    candidate_state: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct ParityReport {
    generated_at_utc: String,
    screenshot_root: String,
    output_dir: String,
    manifest_path: Option<String>,
    thresholds: ReportThresholds,
    summary: ReportSummary,
    scenarios: Vec<ScenarioResult>,
}

impl ParityReport {
    fn create(options: &CliOptions, mut scenarios: Vec<ScenarioResult>) -> Self {
        scenarios.sort_by(|a, b| {
            a.scenario_id
                .to_ascii_lowercase()
                .cmp(&b.scenario_id.to_ascii_lowercase())
        });
        Self {
            generated_at_utc: now_string(),
            screenshot_root: options.screenshot_root.display().to_string(),
            output_dir: options.output_dir.display().to_string(),
            manifest_path: options
                .manifest_path
                .as_ref()
                .map(|path| path.display().to_string()),
            thresholds: ReportThresholds {
                pass_score: options.pass_score,
                warn_score: options.warn_score,
                score_gate_rules: options.score_gate_rules.clone(),
            },
            summary: ReportSummary::create(&scenarios),
            scenarios,
        }
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct ReportThresholds {
    pass_score: f64,
    warn_score: f64,
    score_gate_rules: Vec<ScoreGateRule>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct ReportSummary {
    total_scenarios: usize,
    pass_count: usize,
    warn_count: usize,
    fail_count: usize,
    average_score: f64,
    minimum_score: f64,
}

impl ReportSummary {
    fn create(scenarios: &[ScenarioResult]) -> Self {
        if scenarios.is_empty() {
            return Self {
                total_scenarios: 0,
                pass_count: 0,
                warn_count: 0,
                fail_count: 0,
                average_score: 0.0,
                minimum_score: 0.0,
            };
        }
        Self {
            total_scenarios: scenarios.len(),
            pass_count: scenarios
                .iter()
                .filter(|s| s.status == ScoreStatus::Pass)
                .count(),
            warn_count: scenarios
                .iter()
                .filter(|s| s.status == ScoreStatus::Warn)
                .count(),
            fail_count: scenarios
                .iter()
                .filter(|s| s.status == ScoreStatus::Fail)
                .count(),
            average_score: round2(
                scenarios.iter().map(|s| s.score).sum::<f64>() / scenarios.len() as f64,
            ),
            minimum_score: round2(
                scenarios
                    .iter()
                    .map(|s| s.score)
                    .fold(f64::INFINITY, f64::min),
            ),
        }
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct ScenarioResult {
    scenario_id: String,
    status: ScoreStatus,
    score: f64,
    reference_path: String,
    candidate_path: String,
    normalized_reference_path: String,
    normalized_candidate_path: String,
    dip_normalized_reference_path: Option<String>,
    dip_normalized_candidate_path: Option<String>,
    dip_normalized_contact_sheet_path: Option<String>,
    diff_heatmap_path: String,
    contact_sheet_path: String,
    reference_size: ImageSize,
    candidate_size: ImageSize,
    metadata: Option<ManifestScenario>,
    gate: ScenarioScoreGate,
    evidence_audit: Option<EvidenceAudit>,
    metrics: ScenarioMetrics,
    regions: Vec<RegionResult>,
    findings: Vec<Finding>,
}

#[derive(Debug, Clone, Copy, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
enum ScoreStatus {
    Pass,
    Warn,
    Fail,
}

impl ScoreStatus {
    fn from_score(score: f64, pass_score: f64, warn_score: f64) -> Self {
        if score >= pass_score {
            Self::Pass
        } else if score >= warn_score {
            Self::Warn
        } else {
            Self::Fail
        }
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct ScenarioMetrics {
    pixel_error_percent: f64,
    mean_channel_delta: f64,
    max_channel_delta: i32,
    ssim: f64,
    dhash_distance: i32,
    phash_distance: i32,
    hash_score: f64,
    size_score: f64,
    palette_score: f64,
    average_color_delta: f64,
    semantic_score: Option<f64>,
    control_count_delta_percent: Option<f64>,
    control_count_delta_count: Option<usize>,
    automation_id_jaccard: Option<f64>,
    visible_text_jaccard: Option<f64>,
    visible_text_delta_count: Option<usize>,
    missing_required_semantic_tag_count: Option<usize>,
    missing_required_visible_text_count: Option<usize>,
    missing_required_control_state_count: Option<usize>,
    missing_control_bounds_evidence_count: Option<usize>,
    control_dimension_delta_count: Option<usize>,
    max_control_dimension_delta_dips: Option<f64>,
    scoring_profile: String,
    region_score: f64,
    visual_score: f64,
    semantic_contract_score_floor: Option<f64>,
    window_runtime_score_cap: Option<f64>,
    control_dimension_score_cap: Option<f64>,
    absolute_image_size_delta_percent: f64,
    absolute_window_size_delta_percent: Option<f64>,
    dpi_scale_delta: Option<f64>,
    dip_normalized_pixel_error_percent: Option<f64>,
    dip_normalized_ssim: Option<f64>,
    dip_normalized_viewport_width: Option<u32>,
    dip_normalized_viewport_height: Option<u32>,
    absolute_size_score_cap: Option<f64>,
    effect_baseline_scenario_id: Option<String>,
    reference_effect_pixel_error_percent: Option<f64>,
    candidate_effect_pixel_error_percent: Option<f64>,
    effect_delta_magnitude_delta_percent: Option<f64>,
    interaction_effect_delta_score: Option<f64>,
    interaction_effect_roi_target_ids: Option<Vec<String>>,
    interaction_effect_roi_bounds: Option<RegionBounds>,
    reference_effect_roi_pixel_error_percent: Option<f64>,
    candidate_effect_roi_pixel_error_percent: Option<f64>,
    effect_roi_delta_magnitude_delta_percent: Option<f64>,
    interaction_effect_roi_delta_score: Option<f64>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct RegionResult {
    name: String,
    weight: f64,
    bounds: RegionBounds,
    score: f64,
    pixel_error_percent: f64,
    ssim: f64,
    mean_channel_delta: f64,
    max_channel_delta: i32,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct RegionBounds {
    x: u32,
    y: u32,
    width: u32,
    height: u32,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct Finding {
    severity: String,
    layer_hint: String,
    message: String,
    metric: String,
    value: f64,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct ImageSize {
    width: u32,
    height: u32,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct ScoreGateRule {
    layer: String,
    case: String,
    pass_score: f64,
    warn_score: f64,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct ScenarioScoreGate {
    source: String,
    layer: String,
    case: String,
    pass_score: f64,
    warn_score: f64,
}

#[derive(Debug, Clone)]
struct ScenarioScoringProfile {
    id: String,
    ssim_weight: f64,
    hash_weight: f64,
    region_weight: f64,
    semantic_weight: f64,
    size_weight: f64,
    palette_weight: f64,
    region_warning_score: f64,
}

impl ScenarioScoringProfile {
    fn for_pair(pair: &ScreenshotPair, has_semantic_score: bool) -> Self {
        let scenario_id = pair.scenario_id.to_ascii_lowercase();
        let window_kind = pair
            .metadata
            .as_ref()
            .map(|metadata| metadata.window_kind.to_ascii_lowercase())
            .unwrap_or_default();
        let is_interaction =
            scenario_id.starts_with("effects.") || window_kind == "interaction-effects";
        let is_animation = scenario_id.contains("animation")
            || scenario_id.contains("collapse")
            || scenario_id.contains("expand");
        match (is_animation, is_interaction, has_semantic_score) {
            (true, _, true) => Self::new(
                "interaction-animation",
                0.26,
                0.08,
                0.40,
                0.14,
                0.04,
                0.08,
                62.0,
            ),
            (true, _, false) => Self::new(
                "interaction-animation",
                0.32,
                0.10,
                0.43,
                0.0,
                0.05,
                0.10,
                62.0,
            ),
            (_, true, true) => Self::new(
                "interaction-effects",
                0.28,
                0.10,
                0.36,
                0.14,
                0.04,
                0.08,
                66.0,
            ),
            (_, true, false) => Self::new(
                "interaction-effects",
                0.35,
                0.12,
                0.38,
                0.0,
                0.05,
                0.10,
                66.0,
            ),
            (_, _, true) => Self::new("default-semantic", 0.36, 0.15, 0.22, 0.15, 0.06, 0.06, 70.0),
            (_, _, false) => Self::new("default-visual", 0.42, 0.18, 0.24, 0.0, 0.08, 0.08, 70.0),
        }
    }

    fn new(
        id: &str,
        ssim_weight: f64,
        hash_weight: f64,
        region_weight: f64,
        semantic_weight: f64,
        size_weight: f64,
        palette_weight: f64,
        region_warning_score: f64,
    ) -> Self {
        Self {
            id: id.to_string(),
            ssim_weight,
            hash_weight,
            region_weight,
            semantic_weight,
            size_weight,
            palette_weight,
            region_warning_score,
        }
    }

    fn score(
        &self,
        ssim_score: f64,
        hash_score: f64,
        region_score: f64,
        semantic_score: Option<f64>,
        size_score: f64,
        palette_score: f64,
    ) -> f64 {
        let weighted = (ssim_score * self.ssim_weight)
            + (hash_score * self.hash_weight)
            + (region_score * self.region_weight)
            + (semantic_score.unwrap_or_default() * self.semantic_weight)
            + (size_score * self.size_weight)
            + (palette_score * self.palette_weight);
        let total = self.ssim_weight
            + self.hash_weight
            + self.region_weight
            + if semantic_score.is_some() {
                self.semantic_weight
            } else {
                0.0
            }
            + self.size_weight
            + self.palette_weight;
        clamp_score(if total <= 0.0 { 0.0 } else { weighted / total })
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct ManifestScenario {
    scenario_id: String,
    window_kind: String,
    section_id: String,
    section_label: String,
    theme: String,
    scroll_percent: f64,
    expand_available_languages: bool,
    reference_screenshot: String,
    candidate_screenshot: String,
    side_by_side_screenshot: Option<String>,
    reference_source_kind: Option<String>,
    reference_source_path: Option<String>,
    reference_source_last_write_time_utc: Option<String>,
    reference_source_is_fallback: Option<bool>,
    reference_window: Option<ManifestWindow>,
    candidate_window: Option<ManifestWindow>,
    reference_expected_window_dips: Option<ManifestDipSize>,
    reference_window_size_audit: Option<ManifestWindowSizeAudit>,
    candidate_expected_window_dips: Option<ManifestDipSize>,
    candidate_window_size_audit: Option<ManifestWindowSizeAudit>,
    regions: Vec<ManifestRegion>,
    required_semantic_tags: Vec<String>,
    required_visible_texts: Vec<String>,
    required_control_states: BTreeMap<String, Vec<String>>,
    baseline_scenario_id: Option<String>,
    reference_ui_summary: Option<ManifestUiSummary>,
    candidate_ui_summary: Option<ManifestUiSummary>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct ManifestWindow {
    bounds: Option<ManifestBounds>,
    dpi_scale: f64,
    native_handle_hex: Option<String>,
    extended_style_hex: Option<String>,
    has_no_activate: Option<bool>,
    has_tool_window: Option<bool>,
    has_topmost: Option<bool>,
    is_foreground_at_capture: Option<bool>,
    dpi: Option<u32>,
    visible_bounds: Option<ManifestBounds>,
    virtual_screen_bounds: Option<ManifestBounds>,
    is_clipped_by_virtual_screen: Option<bool>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct ManifestBounds {
    left: i32,
    top: i32,
    width: i32,
    height: i32,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct ManifestDipSize {
    width: f64,
    height: f64,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct ManifestWindowSizeAudit {
    expected_window_dips: Option<ManifestDipSize>,
    actual_window_dips: Option<ManifestDipSize>,
    delta_dips: Option<ManifestDipSize>,
    delta_percent: Option<ManifestDipSize>,
    monitor_work_area_dips: Option<ManifestDipSize>,
    expected_larger_than_work_area: Option<bool>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct ManifestRegion {
    name: String,
    x: f64,
    y: f64,
    width: f64,
    height: f64,
    weight: f64,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct ManifestUiSummary {
    visible_control_counts: BTreeMap<String, i32>,
    visible_automation_ids: Option<Vec<String>>,
    visible_control_dimensions: Option<BTreeMap<String, ManifestControlDimension>>,
    visible_texts: Option<Vec<String>>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct ManifestControlDimension {
    kind: Option<String>,
    state: Option<String>,
    width: Option<String>,
    labeled_width: Option<String>,
    height: Option<String>,
    labeled_height: Option<String>,
    bounds_dips: Option<ManifestControlBoundsDips>,
    max_width: Option<String>,
    min_width: Option<String>,
    min_height: Option<String>,
    max_height: Option<String>,
    padding: Option<String>,
    spacing: Option<String>,
    row_spacing: Option<String>,
    column_spacing: Option<String>,
    columns: Option<String>,
    maximum_rows_or_columns: Option<String>,
    margin: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct ManifestControlBoundsDips {
    left: f64,
    top: f64,
    width: f64,
    height: f64,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct ParityCoverageReport {
    schema_version: String,
    generated_at_utc: String,
    summary: CoverageSummary,
    areas: Vec<CoverageAreaResult>,
}

impl ParityCoverageReport {
    fn create(report: &ParityReport) -> Self {
        let items = CoverageCatalog::items()
            .iter()
            .map(|item| CoverageItemResult::create(item, &report.scenarios))
            .collect::<Vec<_>>();
        let mut by_area = BTreeMap::<String, Vec<CoverageItemResult>>::new();
        for item in items {
            by_area.entry(item.area.clone()).or_default().push(item);
        }
        let areas = by_area
            .into_iter()
            .map(|(area, items)| CoverageAreaResult::create(area, items))
            .collect::<Vec<_>>();
        let all_items = areas
            .iter()
            .flat_map(|area| area.items.clone())
            .collect::<Vec<_>>();
        Self {
            schema_version: "easydict.ui-parity.coverage.v1".to_string(),
            generated_at_utc: now_string(),
            summary: CoverageSummary::create(&all_items),
            areas,
        }
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct CoverageSummary {
    total: usize,
    covered: usize,
    missing: usize,
    covered_pass: usize,
    covered_warning: usize,
    covered_failing: usize,
    critical_total: usize,
    critical_covered: usize,
    critical_missing: usize,
    coverage_percent: f64,
    critical_coverage_percent: f64,
}

impl CoverageSummary {
    fn create(items: &[CoverageItemResult]) -> Self {
        let critical = items
            .iter()
            .filter(|item| item.priority == CoveragePriority::Critical)
            .collect::<Vec<_>>();
        let covered = items.iter().filter(|item| item.is_covered).count();
        let critical_covered = critical.iter().filter(|item| item.is_covered).count();
        let covered_pass = items
            .iter()
            .filter(|item| item.evidence_status == CoverageEvidenceStatus::CoveredPass.id())
            .count();
        let covered_warning = items
            .iter()
            .filter(|item| item.evidence_status == CoverageEvidenceStatus::CoveredWarning.id())
            .count();
        let covered_failing = items
            .iter()
            .filter(|item| item.evidence_status == CoverageEvidenceStatus::CoveredFailing.id())
            .count();
        Self {
            total: items.len(),
            covered,
            missing: items.len().saturating_sub(covered),
            covered_pass,
            covered_warning,
            covered_failing,
            critical_total: critical.len(),
            critical_covered,
            critical_missing: critical.len().saturating_sub(critical_covered),
            coverage_percent: percent(covered, items.len()),
            critical_coverage_percent: percent(critical_covered, critical.len()),
        }
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct CoverageAreaResult {
    area: String,
    total: usize,
    covered: usize,
    missing: usize,
    coverage_percent: f64,
    items: Vec<CoverageItemResult>,
}

impl CoverageAreaResult {
    fn create(area: String, mut items: Vec<CoverageItemResult>) -> Self {
        items.sort_by(|a, b| a.id.cmp(&b.id));
        let covered = items.iter().filter(|item| item.is_covered).count();
        Self {
            area,
            total: items.len(),
            covered,
            missing: items.len().saturating_sub(covered),
            coverage_percent: percent(covered, items.len()),
            items,
        }
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct CoverageItemResult {
    area: String,
    id: String,
    display_name: String,
    priority: CoveragePriority,
    layer_hint: String,
    is_covered: bool,
    evidence_status: String,
    matching_scenario_ids: Vec<String>,
    missing_reason: String,
    next_evidence: String,
}

impl CoverageItemResult {
    fn create(expected: &ExpectedCoverageItem, scenarios: &[ScenarioResult]) -> Self {
        let mut matching = scenarios
            .iter()
            .filter(|scenario| coverage_matches(expected, scenario))
            .map(|scenario| scenario.scenario_id.clone())
            .collect::<Vec<_>>();
        matching.sort();
        matching.dedup();
        let evidence_status = coverage_evidence_status(
            &matching
                .iter()
                .filter_map(|id| {
                    scenarios
                        .iter()
                        .find(|scenario| scenario.scenario_id == *id)
                })
                .collect::<Vec<_>>(),
        );
        Self {
            area: expected.area.to_string(),
            id: expected.id.to_string(),
            display_name: expected.display_name.to_string(),
            priority: expected.priority,
            layer_hint: expected.layer_hint.to_string(),
            is_covered: !matching.is_empty(),
            evidence_status: evidence_status.id().to_string(),
            matching_scenario_ids: matching,
            missing_reason: if expected.match_terms.is_empty() {
                String::new()
            } else {
                "no matching dotnet/rust parity screenshot scenario found".to_string()
            },
            next_evidence: expected.next_evidence.to_string(),
        }
    }
}

#[derive(Debug, Clone, Copy, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
enum CoveragePriority {
    Critical,
    Normal,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum CoverageEvidenceStatus {
    Missing,
    CoveredPass,
    CoveredWarning,
    CoveredFailing,
}

impl CoverageEvidenceStatus {
    fn id(self) -> &'static str {
        match self {
            Self::Missing => "missing",
            Self::CoveredPass => "covered-pass",
            Self::CoveredWarning => "covered-warning",
            Self::CoveredFailing => "covered-failing",
        }
    }
}

fn coverage_evidence_status(matches: &[&ScenarioResult]) -> CoverageEvidenceStatus {
    if matches.is_empty() {
        CoverageEvidenceStatus::Missing
    } else if matches
        .iter()
        .any(|scenario| scenario.status == ScoreStatus::Fail)
    {
        CoverageEvidenceStatus::CoveredFailing
    } else if matches
        .iter()
        .any(|scenario| scenario.status == ScoreStatus::Warn)
    {
        CoverageEvidenceStatus::CoveredWarning
    } else {
        CoverageEvidenceStatus::CoveredPass
    }
}

#[derive(Clone)]
struct ExpectedCoverageItem {
    area: String,
    id: String,
    display_name: String,
    priority: CoveragePriority,
    match_terms: Vec<String>,
    window_kinds: Vec<String>,
    layer_hint: String,
    next_evidence: String,
}

struct CoverageCatalog;

impl CoverageCatalog {
    fn items() -> Vec<ExpectedCoverageItem> {
        let mut items = vec![
            item("main", "main.initial", "Main window initial", CoveragePriority::Critical, &["main", "initial"], &["main"]),
            item("main", "main.after-translate", "Main window after translate", CoveragePriority::Critical, &["main", "after", "translate"], &["main"]),
            item("main", "main.loading", "Main window loading", CoveragePriority::Critical, &["main", "loading"], &["main"]),
            item("main", "main.streaming", "Main window streaming", CoveragePriority::Critical, &["main", "streaming"], &["main"]),
            item("main", "main.error", "Main window error", CoveragePriority::Critical, &["main", "error"], &["main"]),
            item("interaction-effects", "effects.primary-hover", "Primary button hover", CoveragePriority::Critical, &["primary", "hover"], &[]),
            item("interaction-effects", "effects.primary-pressed", "Primary button pressed", CoveragePriority::Critical, &["primary", "pressed"], &[]),
            item("interaction-effects", "effects.source-input-hover", "Source input hover", CoveragePriority::Critical, &["source", "input", "hover"], &[]),
            item("interaction-effects", "effects.source-input-focus", "Source input focus", CoveragePriority::Critical, &["source", "input", "focus"], &[]),
            item("interaction-effects", "effects.result-header-hover", "Result header hover", CoveragePriority::Critical, &["result", "header", "hover"], &[]),
            item("interaction-effects", "effects.settings-tab-hover", "Settings tab hover", CoveragePriority::Critical, &["settings", "tabs", "hover"], &["settings"]),
            item("interaction-effects", "effects.settings-tab-pressed", "Settings tab pressed", CoveragePriority::Critical, &["settings", "tabs", "pressed"], &["settings"]),
            item("interaction-effects", "effects.settings-slider-focus", "Settings TTS speed slider focus", CoveragePriority::Normal, &["tts", "speed", "slider", "focus"], &["settings"]).with_next("Add side-by-side screenshot evidence for the Settings TTS speed slider keyboard focus ring."),
            item("interaction-effects", "effects.settings-toggle-focus", "Settings auto-play toggle focus", CoveragePriority::Normal, &["auto", "play", "toggle", "focus"], &["settings"]).with_next("Add side-by-side screenshot evidence for the Settings auto-play toggle keyboard focus ring."),
            item("interaction-effects", "effects.floating-action-hover", "Floating action hover", CoveragePriority::Critical, &["translate", "hover"], &["mini", "fixed", "popbutton"]),
            item("interaction-effects", "effects.floating-action-pressed", "Floating action pressed", CoveragePriority::Normal, &["translate", "pressed"], &["mini", "fixed", "popbutton"]),
            item("interaction-effects", "effects.overlay-fade", "Mode overlay fade", CoveragePriority::Normal, &["overlay", "fade"], &["main"]),
            item("interaction-effects", "effects.result-collapse-toggle", "Result collapse/expand visibility toggle", CoveragePriority::Critical, &["collapse", "expand"], &[]),
            item("floating", "mini.initial", "Mini window initial", CoveragePriority::Critical, &["mini", "initial"], &["mini"]),
            item("floating", "mini.translate-hover", "Mini translate hover", CoveragePriority::Normal, &["mini", "translate", "hover"], &["mini"]),
            item("floating", "mini.translate-pressed", "Mini translate pressed", CoveragePriority::Normal, &["mini", "translate", "pressed"], &["mini"]),
            item("floating", "mini.after-translate", "Mini window after translate", CoveragePriority::Critical, &["mini", "after", "translate"], &["mini"]),
            item("floating", "mini.streaming", "Mini window streaming", CoveragePriority::Critical, &["mini", "streaming"], &["mini"]),
            item("floating", "fixed.initial", "Fixed window initial", CoveragePriority::Critical, &["fixed", "initial"], &["fixed"]),
            item("floating", "fixed.translate-hover", "Fixed translate hover", CoveragePriority::Normal, &["fixed", "translate", "hover"], &["fixed"]),
            item("floating", "fixed.translate-pressed", "Fixed translate pressed", CoveragePriority::Normal, &["fixed", "translate", "pressed"], &["fixed"]),
            item("floating", "fixed.after-translate", "Fixed window after translate", CoveragePriority::Critical, &["fixed", "after", "translate"], &["fixed"]),
            item("floating", "fixed.streaming", "Fixed window streaming", CoveragePriority::Critical, &["fixed", "streaming"], &["fixed"]),
            item("floating", "popbutton.no-activate", "PopButton no-activate and dismiss", CoveragePriority::Critical, &["popbutton"], &["popbutton"]),
            item("floating", "popbutton.hover", "PopButton hover visual", CoveragePriority::Critical, &["popbutton", "hover"], &["popbutton"]).with_layer("window_runtime").with_next("Add PopButton parity capture that verifies hover opacity, topmost tool-window chrome, and no-activate behavior together."),
            item("floating", "popbutton.pressed", "PopButton pressed visual", CoveragePriority::Normal, &["popbutton", "pressed"], &["popbutton"]).with_layer("window_runtime").with_next("Add PopButton parity capture that verifies pressed opacity without stealing focus."),
            item("settings", "settings.general", "Settings General", CoveragePriority::Critical, &["settings", "general"], &["settings"]),
            item("settings", "settings.services", "Settings Services", CoveragePriority::Critical, &["settings", "services"], &["settings"]),
            item("settings", "settings.views", "Settings Views", CoveragePriority::Critical, &["settings", "views"], &["settings"]),
            item("settings", "settings.hotkeys", "Settings Hotkeys", CoveragePriority::Critical, &["settings", "hotkeys"], &["settings"]),
            item("settings", "settings.advanced-ocr-layout", "Settings Advanced OCR/Layout", CoveragePriority::Critical, &["settings", "advanced"], &["settings"]),
            item("settings", "settings.language", "Settings Language", CoveragePriority::Critical, &["settings", "language"], &["settings"]),
            item("settings", "settings.about", "Settings About", CoveragePriority::Critical, &["settings", "about"], &["settings"]),
            item("ocr", "ocr.overlay-active", "OCR overlay active", CoveragePriority::Critical, &["ocr", "overlay"], &["ocr", "capture"]),
            item("ocr", "ocr.window-detect", "OCR window detect", CoveragePriority::Critical, &["ocr", "window", "detect"], &["ocr", "capture"]).with_layer("iced_backend").with_next("Add OCR overlay capture evidence with detected-window bounds, fullscreen coverage, and topmost HWND flags."),
            item("ocr", "ocr.drag-selection", "OCR drag selection", CoveragePriority::Critical, &["ocr", "drag"], &["ocr", "capture"]),
            item("ocr", "ocr.adjust-handles", "OCR adjust handles", CoveragePriority::Critical, &["ocr", "handles"], &["ocr", "capture"]),
            item("ocr", "ocr.magnifier", "OCR magnifier", CoveragePriority::Critical, &["ocr", "magnifier"], &["ocr", "capture"]),
            item("ocr", "ocr.confirm-to-mini", "OCR confirm to mini", CoveragePriority::Critical, &["ocr", "confirm"], &["ocr", "capture"]),
            item("long-document", "long-doc.tab", "Long document tab", CoveragePriority::Critical, &["long", "doc", "tab"], &["main", "long-document"]),
            item("long-document", "long-doc.pdf", "Long document PDF", CoveragePriority::Critical, &["long", "doc", "pdf"], &["main", "long-document"]),
            item("long-document", "long-doc.text", "Long document Text", CoveragePriority::Critical, &["long", "doc", "text"], &["main", "long-document"]),
            item("long-document", "long-doc.running", "Long document running", CoveragePriority::Critical, &["long", "doc", "running"], &["main", "long-document"]),
            item("long-document", "long-doc.service-hover", "Long document service hover", CoveragePriority::Normal, &["long", "doc", "service", "hover"], &["main", "long-document"]),
            item("long-document", "long-doc.service-dropdown", "Long document service dropdown", CoveragePriority::Critical, &["long", "doc", "service"], &["main", "long-document"]),
        ];
        items.extend(floating_operation_items());
        items
    }

    fn layer_hints_for(pair: &ScreenshotPair) -> Vec<String> {
        let text = coverage_search_text_pair(pair);
        let window_kind = normalize_search(
            pair.metadata
                .as_ref()
                .map(|metadata| metadata.window_kind.as_str())
                .unwrap_or_default(),
        );
        let mut hints = BTreeSet::new();
        for item in Self::items() {
            if coverage_matches_text(&item, &text, &window_kind) {
                hints.insert(item.layer_hint.to_string());
            }
        }
        hints.into_iter().collect()
    }
}

fn item(
    area: &'static str,
    id: &'static str,
    display_name: &'static str,
    priority: CoveragePriority,
    match_terms: &'static [&'static str],
    window_kinds: &'static [&'static str],
) -> ExpectedCoverageItem {
    ExpectedCoverageItem {
        area: area.to_string(),
        id: id.to_string(),
        display_name: display_name.to_string(),
        priority,
        match_terms: match_terms.iter().map(|term| (*term).to_string()).collect(),
        window_kinds: window_kinds
            .iter()
            .map(|kind| (*kind).to_string())
            .collect(),
        layer_hint: "final_effect".to_string(),
        next_evidence: "Add a dotnet/rust screenshot pair and manifest entry for this scenario."
            .to_string(),
    }
}

fn dynamic_item(
    area: impl Into<String>,
    id: impl Into<String>,
    display_name: impl Into<String>,
    priority: CoveragePriority,
    match_terms: Vec<String>,
    window_kinds: Vec<String>,
) -> ExpectedCoverageItem {
    ExpectedCoverageItem {
        area: area.into(),
        id: id.into(),
        display_name: display_name.into(),
        priority,
        match_terms,
        window_kinds,
        layer_hint: "final_effect".to_string(),
        next_evidence: "Add a dotnet/rust screenshot pair and manifest entry for this scenario."
            .to_string(),
    }
}

fn floating_operation_items() -> Vec<ExpectedCoverageItem> {
    let mut items = Vec::new();
    for window in ["mini", "fixed"] {
        for (key, label) in floating_button_controls(window) {
            for state in ["hover", "pressed"] {
                let id = format!("{window}.{key}-{state}");
                items.push(
                    dynamic_item(
                        "floating-operations",
                        &id,
                        format!("{window} {label} {state} operation"),
                        CoveragePriority::Critical,
                        floating_operation_terms(window, key, state),
                        vec![window.to_string()],
                    )
                    .with_next(format!(
                        "Capture and compare the `{id}` dotnet/rust operation screenshot pair."
                    )),
                );
            }
        }

        for (key, label) in floating_dropdown_controls() {
            let open_id = format!("{window}.{key}-open");
            items.push(
                dynamic_item(
                    "floating-operations",
                    &open_id,
                    format!("{window} {label} dropdown open"),
                    CoveragePriority::Critical,
                    floating_operation_terms(window, key, "open"),
                    vec![window.to_string()],
                )
                .with_next(format!(
                    "Capture and compare the `{open_id}` dotnet/rust opened dropdown screenshot pair."
                )),
            );

            for (index, option_label) in floating_language_option_labels() {
                let select_id = format!("{window}.{key}-select-{index}");
                let mut terms = floating_operation_terms(window, key, "select");
                terms.push(index.to_string());
                items.push(
                    dynamic_item(
                        "floating-operations",
                        &select_id,
                        format!("{window} {label} select {option_label}"),
                        CoveragePriority::Critical,
                        terms,
                        vec![window.to_string()],
                    )
                    .with_next(format!(
                        "Capture and compare the `{select_id}` dotnet/rust selected-option screenshot pair."
                    )),
                );
            }
        }
    }
    items
}

fn floating_button_controls(window: &str) -> Vec<(&'static str, &'static str)> {
    let mut controls = vec![
        ("translate", "Translate"),
        ("ocr", "OCR"),
        ("close", "Close"),
        ("source-language", "Source language ComboBox"),
        ("target-language", "Target language ComboBox"),
        ("swap", "Swap"),
    ];
    if window == "mini" {
        controls.push(("pin", "Pin"));
    }
    controls
}

fn floating_dropdown_controls() -> [(&'static str, &'static str); 2] {
    [
        ("source-language-dropdown", "Source language"),
        ("target-language-dropdown", "Target language"),
    ]
}

fn floating_language_option_labels() -> [(usize, &'static str); 9] {
    [
        (1, "Auto detect"),
        (2, "Simplified Chinese"),
        (3, "Traditional Chinese"),
        (4, "Japanese"),
        (5, "Korean"),
        (6, "English"),
        (7, "German"),
        (8, "French"),
        (9, "Spanish"),
    ]
}

fn floating_operation_terms(window: &str, key: &str, state: &str) -> Vec<String> {
    let mut terms = vec![window.to_string(), state.to_string()];
    terms.extend(
        key.split('-')
            .filter(|term| !term.is_empty())
            .map(str::to_string),
    );
    terms
}

impl ExpectedCoverageItem {
    fn with_layer(mut self, layer_hint: impl Into<String>) -> Self {
        self.layer_hint = layer_hint.into();
        self
    }

    fn with_next(mut self, next_evidence: impl Into<String>) -> Self {
        self.next_evidence = next_evidence.into();
        self
    }
}

fn coverage_matches(expected: &ExpectedCoverageItem, scenario: &ScenarioResult) -> bool {
    let text = coverage_search_text(scenario);
    let window_kind = normalize_search(
        scenario
            .metadata
            .as_ref()
            .map(|metadata| metadata.window_kind.as_str())
            .unwrap_or_default(),
    );
    coverage_matches_text(expected, &text, &window_kind)
}

fn coverage_matches_text(expected: &ExpectedCoverageItem, text: &str, window_kind: &str) -> bool {
    let expected_id = normalize_search(&expected.id);
    if text == expected_id || text.starts_with(&format!("{expected_id} ")) {
        return true;
    }
    if !expected
        .match_terms
        .iter()
        .all(|term| text.contains(&normalize_search(term)))
    {
        return false;
    }
    expected.window_kinds.is_empty()
        || expected.window_kinds.iter().any(|kind| {
            let kind = normalize_search(kind);
            window_kind == kind || text.contains(&kind)
        })
}

fn coverage_search_text(scenario: &ScenarioResult) -> String {
    let metadata = scenario.metadata.as_ref();
    normalize_search(&format!(
        "{} {} {} {} {} {}",
        scenario.scenario_id,
        metadata.map(|m| m.window_kind.as_str()).unwrap_or_default(),
        metadata.map(|m| m.section_id.as_str()).unwrap_or_default(),
        metadata
            .map(|m| m.section_label.as_str())
            .unwrap_or_default(),
        metadata.map(|m| m.theme.as_str()).unwrap_or_default(),
        metadata.map(|m| m.scroll_percent).unwrap_or_default()
    ))
}

fn coverage_search_text_pair(pair: &ScreenshotPair) -> String {
    let metadata = pair.metadata.as_ref();
    normalize_search(&format!(
        "{} {} {} {} {} {}",
        pair.scenario_id,
        metadata.map(|m| m.window_kind.as_str()).unwrap_or_default(),
        metadata.map(|m| m.section_id.as_str()).unwrap_or_default(),
        metadata
            .map(|m| m.section_label.as_str())
            .unwrap_or_default(),
        metadata.map(|m| m.theme.as_str()).unwrap_or_default(),
        metadata.map(|m| m.scroll_percent).unwrap_or_default()
    ))
}

fn normalize_search(value: &str) -> String {
    value
        .to_ascii_lowercase()
        .chars()
        .map(|ch| if ch.is_ascii_alphanumeric() { ch } else { ' ' })
        .collect::<String>()
        .split_whitespace()
        .collect::<Vec<_>>()
        .join(" ")
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct ParityGatePolicy {
    schema_version: String,
    generated_at_utc: String,
    mode: String,
    thresholds: ReportThresholds,
    coverage_gates: CoverageGateThresholds,
    deterministic_gate: String,
    llm_review: String,
    scenarios: Vec<ScenarioGatePolicy>,
}

impl ParityGatePolicy {
    fn create(report: &ParityReport, options: &CliOptions) -> Self {
        Self {
            schema_version: "easydict.ui-parity.thresholds.v1".to_string(),
            generated_at_utc: now_string(),
            mode: if options.fail_on_threshold {
                "hard-gate"
            } else {
                "report-only"
            }
            .to_string(),
            thresholds: report.thresholds.clone(),
            coverage_gates: CoverageGateThresholds {
                min_coverage_percent: options.min_coverage_percent,
                min_critical_coverage_percent: options.min_critical_coverage_percent,
                fail_on_critical_coverage_missing: options.fail_on_critical_coverage_missing,
            },
            deterministic_gate: if options.fail_on_threshold {
                "fail_ci_when_any_scenario_status_is_fail"
            } else {
                "report_only_until_parity_baselines_mature"
            }
            .to_string(),
            llm_review: "advisory_for_warn_or_fail_scenarios_only".to_string(),
            scenarios: report
                .scenarios
                .iter()
                .map(ScenarioGatePolicy::create)
                .collect(),
        }
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct CoverageGateThresholds {
    min_coverage_percent: Option<f64>,
    min_critical_coverage_percent: Option<f64>,
    fail_on_critical_coverage_missing: bool,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct ScenarioGatePolicy {
    scenario_id: String,
    status: ScoreStatus,
    score: f64,
    pass_score: f64,
    warn_score: f64,
    gate_source: String,
    gate_layer: String,
    gate_case: String,
    recommended_action: String,
    needs_llm_review: bool,
    eligible_for_hard_gate: bool,
    dominant_layer_hints: Vec<String>,
}

impl ScenarioGatePolicy {
    fn create(scenario: &ScenarioResult) -> Self {
        let mut counts = BTreeMap::<String, usize>::new();
        for finding in &scenario.findings {
            if finding.severity == "error" || finding.severity == "warning" {
                *counts.entry(finding.layer_hint.clone()).or_default() += 1;
            }
        }
        let mut layers = counts.into_iter().collect::<Vec<_>>();
        layers.sort_by(|a, b| b.1.cmp(&a.1).then_with(|| a.0.cmp(&b.0)));
        Self {
            scenario_id: scenario.scenario_id.clone(),
            status: scenario.status,
            score: scenario.score,
            pass_score: scenario.gate.pass_score,
            warn_score: scenario.gate.warn_score,
            gate_source: scenario.gate.source.clone(),
            gate_layer: scenario.gate.layer.clone(),
            gate_case: scenario.gate.case.clone(),
            recommended_action: match scenario.status {
                ScoreStatus::Pass => "eligible_for_hard_gate_after_baseline_review",
                ScoreStatus::Warn => "manual_or_llm_review_before_gate",
                ScoreStatus::Fail => "fix_before_gate",
            }
            .to_string(),
            needs_llm_review: scenario.status != ScoreStatus::Pass,
            eligible_for_hard_gate: scenario.status == ScoreStatus::Pass,
            dominant_layer_hints: layers.into_iter().take(3).map(|(layer, _)| layer).collect(),
        }
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct CoverageReviewRequest {
    schema_version: String,
    item_id: String,
    display_name: String,
    priority: CoveragePriority,
    layer_hint: String,
    evidence_status: String,
    matching_scenario_ids: Vec<String>,
    next_evidence: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
struct LlmReviewRequest {
    schema_version: String,
    scenario_id: String,
    status: ScoreStatus,
    score: f64,
    reference_image: String,
    candidate_image: String,
    diff_heatmap: String,
    contact_sheet: String,
    reference_size: ImageSize,
    candidate_size: ImageSize,
    reference_window: Option<ManifestWindow>,
    candidate_window: Option<ManifestWindow>,
    reference_window_size_audit: Option<ManifestWindowSizeAudit>,
    candidate_window_size_audit: Option<ManifestWindowSizeAudit>,
    reference_source_kind: Option<String>,
    reference_source_path: Option<String>,
    reference_source_last_write_time_utc: Option<String>,
    reference_source_is_fallback: Option<bool>,
    required_semantic_tags: Vec<String>,
    required_visible_texts: Vec<String>,
    required_control_states: BTreeMap<String, Vec<String>>,
    reference_ui_summary: Option<ManifestUiSummary>,
    candidate_ui_summary: Option<ManifestUiSummary>,
    ui_semantic_delta_summary: Option<String>,
    evidence_audit: Option<EvidenceAudit>,
    metrics: ScenarioMetrics,
    findings: Vec<Finding>,
    regions: Vec<RegionResult>,
}

fn parse_manifest_scenario(value: &Value) -> Result<ManifestScenario, String> {
    Ok(ManifestScenario {
        scenario_id: get_string(value, "ScenarioId").unwrap_or_default(),
        window_kind: get_string(value, "WindowKind").unwrap_or_default(),
        section_id: get_string(value, "SectionId").unwrap_or_default(),
        section_label: get_string(value, "SectionLabel").unwrap_or_default(),
        theme: get_string(value, "Theme").unwrap_or_default(),
        scroll_percent: get_f64(value, "ScrollPercent").unwrap_or_default(),
        expand_available_languages: get_bool(value, "ExpandAvailableLanguages").unwrap_or(false),
        reference_screenshot: get_string(value, "ReferenceScreenshot")
            .ok_or("manifest scenario omitted ReferenceScreenshot")?,
        candidate_screenshot: get_string(value, "CandidateScreenshot")
            .ok_or("manifest scenario omitted CandidateScreenshot")?,
        side_by_side_screenshot: get_string(value, "SideBySideScreenshot"),
        reference_source_kind: get_string(value, "ReferenceSourceKind"),
        reference_source_path: get_string(value, "ReferenceSourcePath"),
        reference_source_last_write_time_utc: get_string(value, "ReferenceSourceLastWriteTimeUtc"),
        reference_source_is_fallback: get_bool(value, "ReferenceSourceIsFallback"),
        reference_window: get_object(value, "ReferenceWindow")
            .map(parse_manifest_window)
            .transpose()?,
        candidate_window: get_object(value, "CandidateWindow")
            .map(parse_manifest_window)
            .transpose()?,
        reference_expected_window_dips: get_object(value, "ReferenceExpectedWindowDips")
            .map(parse_manifest_dip_size)
            .transpose()?,
        reference_window_size_audit: get_object(value, "ReferenceWindowSizeAudit")
            .map(parse_manifest_window_size_audit)
            .transpose()?,
        candidate_expected_window_dips: get_object(value, "CandidateExpectedWindowDips")
            .map(parse_manifest_dip_size)
            .transpose()?,
        candidate_window_size_audit: get_object(value, "CandidateWindowSizeAudit")
            .map(parse_manifest_window_size_audit)
            .transpose()?,
        regions: get_array(value, "Regions")
            .map(|items| {
                items
                    .iter()
                    .map(parse_manifest_region)
                    .collect::<Result<Vec<_>, _>>()
            })
            .transpose()?
            .unwrap_or_default(),
        required_semantic_tags: get_array(value, "RequiredSemanticTags")
            .map(|items| {
                items
                    .iter()
                    .filter_map(Value::as_str)
                    .map(ToString::to_string)
                    .collect::<Vec<_>>()
            })
            .unwrap_or_default(),
        required_visible_texts: get_array(value, "RequiredVisibleTexts")
            .map(|items| {
                items
                    .iter()
                    .filter_map(Value::as_str)
                    .map(ToString::to_string)
                    .collect::<Vec<_>>()
            })
            .unwrap_or_default(),
        required_control_states: parse_required_control_states(value),
        baseline_scenario_id: get_string(value, "BaselineScenarioId")
            .or_else(|| get_string(value, "baseline_scenario_id")),
        reference_ui_summary: get_object(value, "ReferenceUiSummary")
            .map(parse_manifest_ui_summary)
            .transpose()?,
        candidate_ui_summary: get_object(value, "CandidateUiSummary")
            .map(parse_manifest_ui_summary)
            .transpose()?,
    })
}

fn parse_manifest_window(value: &Value) -> Result<ManifestWindow, String> {
    Ok(ManifestWindow {
        bounds: get_object(value, "Bounds")
            .map(parse_manifest_bounds)
            .transpose()?,
        dpi_scale: get_f64(value, "DpiScale").unwrap_or(1.0),
        native_handle_hex: get_string(value, "NativeHandleHex"),
        extended_style_hex: get_string(value, "ExtendedStyleHex"),
        has_no_activate: get_bool(value, "HasNoActivate"),
        has_tool_window: get_bool(value, "HasToolWindow"),
        has_topmost: get_bool(value, "HasTopmost"),
        is_foreground_at_capture: get_bool(value, "IsForegroundAtCapture"),
        dpi: get_u32(value, "Dpi"),
        visible_bounds: get_object(value, "VisibleBounds")
            .map(parse_manifest_bounds)
            .transpose()?,
        virtual_screen_bounds: get_object(value, "VirtualScreenBounds")
            .map(parse_manifest_bounds)
            .transpose()?,
        is_clipped_by_virtual_screen: get_bool(value, "IsClippedByVirtualScreen"),
    })
}

fn parse_manifest_bounds(value: &Value) -> Result<ManifestBounds, String> {
    Ok(ManifestBounds {
        left: get_i32(value, "Left").unwrap_or_default(),
        top: get_i32(value, "Top").unwrap_or_default(),
        width: get_i32(value, "Width").unwrap_or_default(),
        height: get_i32(value, "Height").unwrap_or_default(),
    })
}

fn parse_manifest_dip_size(value: &Value) -> Result<ManifestDipSize, String> {
    Ok(ManifestDipSize {
        width: get_f64(value, "Width").unwrap_or_default(),
        height: get_f64(value, "Height").unwrap_or_default(),
    })
}

fn parse_manifest_window_size_audit(value: &Value) -> Result<ManifestWindowSizeAudit, String> {
    Ok(ManifestWindowSizeAudit {
        expected_window_dips: get_object(value, "ExpectedWindowDips")
            .map(parse_manifest_dip_size)
            .transpose()?,
        actual_window_dips: get_object(value, "ActualWindowDips")
            .map(parse_manifest_dip_size)
            .transpose()?,
        delta_dips: get_object(value, "DeltaDips")
            .map(parse_manifest_dip_size)
            .transpose()?,
        delta_percent: get_object(value, "DeltaPercent")
            .map(parse_manifest_dip_size)
            .transpose()?,
        monitor_work_area_dips: get_object(value, "MonitorWorkAreaDips")
            .map(parse_manifest_dip_size)
            .transpose()?,
        expected_larger_than_work_area: get_bool(value, "ExpectedLargerThanWorkArea"),
    })
}

fn parse_manifest_region(value: &Value) -> Result<ManifestRegion, String> {
    Ok(ManifestRegion {
        name: get_string(value, "Name").unwrap_or_default(),
        x: get_f64(value, "X").unwrap_or_default(),
        y: get_f64(value, "Y").unwrap_or_default(),
        width: get_f64(value, "Width").unwrap_or_default(),
        height: get_f64(value, "Height").unwrap_or_default(),
        weight: get_f64(value, "Weight").unwrap_or(1.0),
    })
}

fn parse_manifest_ui_summary(value: &Value) -> Result<ManifestUiSummary, String> {
    let mut visible_control_counts = BTreeMap::new();
    if let Some(object) = get_object(value, "VisibleControlCounts").and_then(Value::as_object) {
        for (key, value) in object {
            if let Some(count) = value.as_i64() {
                visible_control_counts.insert(key.clone(), count as i32);
            }
        }
    }
    let visible_automation_ids = get_array(value, "VisibleAutomationIds").map(|items| {
        items
            .iter()
            .filter_map(Value::as_str)
            .map(ToString::to_string)
            .collect::<Vec<_>>()
    });
    let visible_texts = get_array(value, "VisibleTexts").map(|items| {
        items
            .iter()
            .filter_map(Value::as_str)
            .map(ToString::to_string)
            .collect::<Vec<_>>()
    });
    let visible_control_dimensions = get_object(value, "VisibleControlDimensions")
        .and_then(Value::as_object)
        .map(|object| {
            let mut dimensions = BTreeMap::new();
            for (id, value) in object {
                if value.is_object() {
                    dimensions.insert(id.clone(), parse_manifest_control_dimension(value));
                }
            }
            dimensions
        })
        .filter(|dimensions| !dimensions.is_empty());
    Ok(ManifestUiSummary {
        visible_control_counts,
        visible_automation_ids,
        visible_control_dimensions,
        visible_texts,
    })
}

fn parse_required_control_states(value: &Value) -> BTreeMap<String, Vec<String>> {
    let mut states = BTreeMap::new();
    let Some(object) = get_object(value, "RequiredControlStates").and_then(Value::as_object) else {
        return states;
    };

    for (id, value) in object {
        let required_states = value
            .as_array()
            .into_iter()
            .flatten()
            .filter_map(Value::as_str)
            .map(str::trim)
            .filter(|state| !state.is_empty())
            .map(ToString::to_string)
            .collect::<Vec<_>>();
        if !id.trim().is_empty() && !required_states.is_empty() {
            states.insert(id.clone(), required_states);
        }
    }
    states
}

fn parse_manifest_control_dimension(value: &Value) -> ManifestControlDimension {
    ManifestControlDimension {
        kind: get_string(value, "Kind"),
        state: get_string(value, "State").or_else(|| get_string(value, "state")),
        width: get_string(value, "Width").or_else(|| get_string(value, "width")),
        labeled_width: get_string(value, "LabeledWidth")
            .or_else(|| get_string(value, "labeled_width")),
        height: get_string(value, "Height").or_else(|| get_string(value, "height")),
        labeled_height: get_string(value, "LabeledHeight")
            .or_else(|| get_string(value, "labeled_height")),
        bounds_dips: get_object(value, "BoundsDips")
            .or_else(|| get_object(value, "bounds_dips"))
            .map(parse_manifest_control_bounds_dips),
        max_width: get_string(value, "MaxWidth").or_else(|| get_string(value, "max_width")),
        min_width: get_string(value, "MinWidth").or_else(|| get_string(value, "min_width")),
        min_height: get_string(value, "MinHeight").or_else(|| get_string(value, "min_height")),
        max_height: get_string(value, "MaxHeight").or_else(|| get_string(value, "max_height")),
        padding: get_string(value, "Padding").or_else(|| get_string(value, "padding")),
        spacing: get_string(value, "Spacing").or_else(|| get_string(value, "spacing")),
        row_spacing: get_string(value, "RowSpacing").or_else(|| get_string(value, "row_spacing")),
        column_spacing: get_string(value, "ColumnSpacing")
            .or_else(|| get_string(value, "column_spacing")),
        columns: get_string(value, "Columns").or_else(|| get_string(value, "columns")),
        maximum_rows_or_columns: get_string(value, "MaximumRowsOrColumns")
            .or_else(|| get_string(value, "maximum_rows_or_columns")),
        margin: get_string(value, "Margin").or_else(|| get_string(value, "margin")),
    }
}

fn parse_manifest_control_bounds_dips(value: &Value) -> ManifestControlBoundsDips {
    ManifestControlBoundsDips {
        left: get_f64(value, "Left")
            .or_else(|| get_f64(value, "left"))
            .unwrap_or_default(),
        top: get_f64(value, "Top")
            .or_else(|| get_f64(value, "top"))
            .unwrap_or_default(),
        width: get_f64(value, "Width")
            .or_else(|| get_f64(value, "width"))
            .unwrap_or_default(),
        height: get_f64(value, "Height")
            .or_else(|| get_f64(value, "height"))
            .unwrap_or_default(),
    }
}

fn get_case<'a>(value: &'a Value, name: &str) -> Option<&'a Value> {
    let object = value.as_object()?;
    object.get(name).or_else(|| {
        object
            .iter()
            .find(|(key, _)| key.eq_ignore_ascii_case(name))
            .map(|(_, value)| value)
    })
}

fn get_string(value: &Value, name: &str) -> Option<String> {
    get_case(value, name)
        .and_then(Value::as_str)
        .map(ToString::to_string)
}

fn get_bool(value: &Value, name: &str) -> Option<bool> {
    get_case(value, name).and_then(Value::as_bool)
}

fn get_f64(value: &Value, name: &str) -> Option<f64> {
    get_case(value, name).and_then(Value::as_f64)
}

fn get_i32(value: &Value, name: &str) -> Option<i32> {
    get_case(value, name)
        .and_then(Value::as_i64)
        .map(|value| value as i32)
}

fn get_u32(value: &Value, name: &str) -> Option<u32> {
    get_case(value, name)
        .and_then(Value::as_u64)
        .map(|value| value as u32)
}

fn get_array<'a>(value: &'a Value, name: &str) -> Option<&'a Vec<Value>> {
    get_case(value, name).and_then(Value::as_array)
}

fn get_object<'a>(value: &'a Value, name: &str) -> Option<&'a Value> {
    let candidate = get_case(value, name)?;
    candidate.as_object()?;
    Some(candidate)
}

fn clamp_to_range(value: i64, min: i64, max: i64) -> i64 {
    if max < min {
        min
    } else {
        value.clamp(min, max)
    }
}

fn pixel_score(pixel_error_percent: f64) -> f64 {
    clamp_score(100.0 - (pixel_error_percent * 1.55))
}

fn clamp_score(score: f64) -> f64 {
    score.clamp(0.0, 100.0)
}

fn luminance(color: &Rgba<u8>) -> f64 {
    (0.2126 * color[0] as f64) + (0.7152 * color[1] as f64) + (0.0722 * color[2] as f64)
}

fn color_distance(a: &ColorVector, b: &ColorVector) -> f64 {
    ((a.r - b.r).powi(2) + (a.g - b.g).powi(2) + (a.b - b.b).powi(2)).sqrt()
}

fn sanitize_file_name(name: &str) -> String {
    name.chars()
        .map(|ch| match ch {
            '<' | '>' | ':' | '"' | '/' | '\\' | '|' | '?' | '*' => '_',
            ch if ch.is_control() => '_',
            ch => ch,
        })
        .collect()
}

fn relative_path(root: &Path, path: &Path) -> String {
    pathdiff(root, path)
        .unwrap_or_else(|| path.to_path_buf())
        .to_string_lossy()
        .replace('\\', "/")
}

fn pathdiff(root: &Path, path: &Path) -> Option<PathBuf> {
    path.strip_prefix(root).ok().map(Path::to_path_buf)
}

fn percent(value: usize, total: usize) -> f64 {
    if total == 0 {
        100.0
    } else {
        round2(value as f64 * 100.0 / total as f64)
    }
}

fn round2(value: f64) -> f64 {
    (value * 100.0).round() / 100.0
}

fn round4(value: f64) -> f64 {
    (value * 10_000.0).round() / 10_000.0
}

fn round5(value: f64) -> f64 {
    (value * 100_000.0).round() / 100_000.0
}

fn now_string() -> String {
    let duration = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default();
    format!("{}.{:03}Z", duration.as_secs(), duration.subsec_millis())
}

fn unique_stamp() -> String {
    let duration = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default();
    format!("{}{}", duration.as_secs(), duration.subsec_nanos())
}

#[cfg(test)]
mod tests {
    use super::*;
    use tempfile::tempdir;

    fn create_synthetic_roi_effect_frame(add_roi_effect: bool) -> RgbaImage {
        let mut image = RgbaImage::from_pixel(100, 80, Rgba([240, 244, 248, 255]));
        for y in 0..80 {
            for x in 0..100 {
                if y < 12 {
                    image.put_pixel(x, y, Rgba([230, 235, 241, 255]));
                } else if x < 16 {
                    image.put_pixel(x, y, Rgba([248, 250, 252, 255]));
                }
            }
        }
        if add_roi_effect {
            for y in 22..34 {
                for x in 20..40 {
                    image.put_pixel(x, y, Rgba([90, 120, 170, 255]));
                }
            }
        }
        image
    }

    fn find_coverage_item<'a>(coverage: &'a Value, id: &str) -> &'a Value {
        coverage
            .get("Areas")
            .and_then(Value::as_array)
            .and_then(|areas| {
                areas.iter().find_map(|area| {
                    area.get("Items")
                        .and_then(Value::as_array)
                        .and_then(|items| {
                            items
                                .iter()
                                .find(|item| item.get("Id").and_then(Value::as_str) == Some(id))
                        })
                })
            })
            .unwrap_or_else(|| panic!("coverage item {id}"))
    }

    fn ui_summary(counts: &[(&str, i32)], ids: &[&str]) -> ManifestUiSummary {
        ManifestUiSummary {
            visible_control_counts: counts
                .iter()
                .map(|(key, value)| ((*key).to_string(), *value))
                .collect(),
            visible_automation_ids: Some(ids.iter().map(|id| (*id).to_string()).collect()),
            visible_control_dimensions: None,
            visible_texts: None,
        }
    }

    fn semantic_manifest(
        scenario_id: &str,
        window_kind: &str,
        required_tags: &[&str],
        reference_ui_summary: ManifestUiSummary,
        candidate_ui_summary: ManifestUiSummary,
    ) -> ManifestScenario {
        ManifestScenario {
            scenario_id: scenario_id.to_string(),
            window_kind: window_kind.to_string(),
            section_id: window_kind.to_string(),
            section_label: window_kind.to_string(),
            theme: "light".to_string(),
            scroll_percent: 0.0,
            expand_available_languages: false,
            reference_screenshot: "reference.png".to_string(),
            candidate_screenshot: "candidate.png".to_string(),
            side_by_side_screenshot: None,
            reference_source_kind: None,
            reference_source_path: None,
            reference_source_last_write_time_utc: None,
            reference_source_is_fallback: None,
            reference_window: None,
            candidate_window: None,
            reference_expected_window_dips: None,
            reference_window_size_audit: None,
            candidate_expected_window_dips: None,
            candidate_window_size_audit: None,
            regions: Vec::new(),
            required_semantic_tags: required_tags.iter().map(|tag| (*tag).to_string()).collect(),
            required_visible_texts: Vec::new(),
            required_control_states: BTreeMap::new(),
            baseline_scenario_id: None,
            reference_ui_summary: Some(reference_ui_summary),
            candidate_ui_summary: Some(candidate_ui_summary),
        }
    }

    #[test]
    fn self_test_passes() {
        assert!(run_self_test().expect("self-test should run"));
    }

    #[test]
    fn floating_semantic_summary_maps_dotnet_and_rust_ids() {
        let manifest = semantic_manifest(
            "fixed.initial",
            "fixed",
            &["fixed.translate", "fixed.results"],
            ui_summary(
                &[("button", 3), ("comboBox", 2), ("edit", 1), ("text", 3)],
                &[
                    "CloseButton",
                    "InputTextBox",
                    "ResultsScrollViewer",
                    "ServiceResultItem_bing",
                    "SourceLangCombo",
                    "StatusText",
                    "SwapButton",
                    "TargetLangCombo",
                    "TranslateButton",
                ],
            ),
            ui_summary(
                &[
                    ("button", 3),
                    ("comboBox", 2),
                    ("edit", 1),
                    ("list", 1),
                    ("text", 4),
                ],
                &[
                    "bing",
                    "fixed.close",
                    "fixed.content",
                    "fixed.input",
                    "fixed.input_card",
                    "fixed.results",
                    "fixed.source_language",
                    "fixed.status",
                    "fixed.swap",
                    "fixed.target_language",
                    "fixed.translate",
                    "fixed.window",
                ],
            ),
        );

        let summary = compare_ui_summaries(Some(&manifest)).expect("summary");

        assert_eq!(summary.missing_required_semantic_tags, Vec::<String>::new());
        assert!(
            summary.automation_id_jaccard.unwrap_or_default() > 85.0,
            "semantic aliases should make .NET/Rust floating ids comparable: {summary:?}"
        );
        assert!(
            summary.score > 80.0,
            "mapped floating semantic score should not be dominated by id naming drift: {summary:?}"
        );
    }

    #[test]
    fn floating_semantic_summary_uses_required_tags_when_reference_uia_is_chrome_only() {
        let manifest = semantic_manifest(
            "mini.initial",
            "mini",
            &["mini.translate", "mini.results"],
            ui_summary(&[], &["SystemMenuBar"]),
            ui_summary(
                &[("button", 5), ("comboBox", 2), ("list", 1), ("text", 7)],
                &[
                    "mini.close",
                    "mini.content",
                    "mini.input",
                    "mini.results",
                    "mini.source_language",
                    "mini.status",
                    "mini.swap",
                    "mini.target_language",
                    "mini.translate",
                    "mini.window",
                ],
            ),
        );

        let summary = compare_ui_summaries(Some(&manifest)).expect("summary");

        assert_eq!(summary.missing_required_semantic_tags, Vec::<String>::new());
        assert_eq!(summary.automation_id_jaccard, None);
        assert_eq!(summary.score, 100.0);
    }

    #[test]
    fn floating_semantic_summary_maps_ocr_button_aliases() {
        let manifest = semantic_manifest(
            "mini.ocr-hover",
            "mini",
            &["mini.ocr"],
            ui_summary(&[("button", 1)], &["MiniWindowOcrButton"]),
            ui_summary(&[("button", 1)], &["mini.ocr"]),
        );

        let summary = compare_ui_summaries(Some(&manifest)).expect("summary");

        assert_eq!(summary.missing_required_semantic_tags, Vec::<String>::new());
        assert_eq!(summary.automation_id_jaccard, Some(100.0));
        assert_eq!(summary.score, 100.0);
    }

    #[test]
    fn semantic_summary_reports_missing_required_visible_texts() {
        let mut manifest = semantic_manifest(
            "settings.about",
            "settings",
            &["AboutHeaderText"],
            ui_summary(&[("text", 2)], &["AboutHeaderText"]),
            ui_summary(&[("text", 2)], &["AboutHeaderText"]),
        );
        manifest.required_visible_texts =
            vec!["Inspired by".to_string(), "License: GPL-3.0".to_string()];
        if let Some(reference) = manifest.reference_ui_summary.as_mut() {
            reference.visible_texts = Some(vec![
                "Inspired by".to_string(),
                "License: GPL-3.0".to_string(),
            ]);
        }
        if let Some(candidate) = manifest.candidate_ui_summary.as_mut() {
            candidate.visible_texts = Some(vec!["Inspired by".to_string()]);
        }

        let summary = compare_ui_summaries(Some(&manifest)).expect("summary");

        assert_eq!(
            summary.missing_required_visible_texts,
            vec!["License: GPL-3.0".to_string()]
        );
        assert!(
            summary.score < 100.0,
            "missing required visible text should reduce semantic score: {summary:?}"
        );
    }

    #[test]
    fn analyzer_reports_candidate_extra_visible_texts_for_settings_services_density() {
        let dir = tempdir().expect("temp dir");
        let reference = dir.path().join(format!(
            "parity-settings-services-translation-service-configuration-top{DOTNET_SUFFIX}"
        ));
        let candidate = dir.path().join(format!(
            "parity-settings-services-translation-service-configuration-top{RUST_SUFFIX}"
        ));
        create_synthetic_frame(false)
            .save(&reference)
            .expect("save reference");
        create_synthetic_frame(false)
            .save(&candidate)
            .expect("save candidate");

        let mut reference_summary = ui_summary(
            &[("button", 8), ("text", 12)],
            &[
                "SettingsTab_Services",
                "DeepLServiceExpander",
                "WindowsLocalAIExpander",
                "OllamaServiceExpander",
                "OpenAIServiceExpander",
            ],
        );
        reference_summary.visible_texts = Some(vec![
            "服务配置".to_string(),
            "DeepL".to_string(),
            "Windows Local AI".to_string(),
            "Ollama (Local LLM)".to_string(),
            "OpenAI".to_string(),
        ]);
        let mut candidate_summary = ui_summary(
            &[("button", 8), ("text", 17)],
            &[
                "SettingsTab_Services",
                "DeepLServiceExpander",
                "WindowsLocalAIExpander",
                "OllamaServiceExpander",
                "OpenAIServiceExpander",
            ],
        );
        candidate_summary.visible_texts = Some(vec![
            "服务配置".to_string(),
            "DeepL".to_string(),
            "Free API mode".to_string(),
            "Windows Local AI".to_string(),
            "Auto fallback: Phi Silica -> Foundry Local -> OpenVINO".to_string(),
            "Ollama (Local LLM)".to_string(),
            "Not refreshed".to_string(),
            "OpenAI".to_string(),
            "Not tested".to_string(),
            "deepseek-chat · deepseek-chat".to_string(),
        ]);

        let manifest_path = dir.path().join("ui-parity-manifest.json");
        let manifest = serde_json::json!({
            "SchemaVersion": "easydict.ui-parity.manifest.v1",
            "Scenarios": [{
                "ScenarioId": "parity-settings-services-translation-service-configuration-top",
                "WindowKind": "settings",
                "SectionId": "services",
                "SectionLabel": "Services",
                "Theme": "light",
                "ScrollPercent": 0.0,
                "ExpandAvailableLanguages": false,
                "ReferenceScreenshot": reference.file_name().and_then(|value| value.to_str()).unwrap(),
                "CandidateScreenshot": candidate.file_name().and_then(|value| value.to_str()).unwrap(),
                "Regions": [],
                "RequiredSemanticTags": ["SettingsTab_Services", "DeepLServiceExpander"],
                "ReferenceUiSummary": reference_summary,
                "CandidateUiSummary": candidate_summary
            }]
        });
        fs::write(&manifest_path, manifest.to_string()).expect("write manifest");
        let output = dir.path().join("out");

        let code = run([
            OsString::from("easydict_ui_parity_analyzer"),
            OsString::from("--screenshot-root"),
            dir.path().as_os_str().to_os_string(),
            OsString::from("--manifest"),
            manifest_path.as_os_str().to_os_string(),
            OsString::from("--output-dir"),
            output.as_os_str().to_os_string(),
        ])
        .expect("analyzer should run");

        assert_eq!(code, 0);
        let report_text =
            fs::read_to_string(output.join("ui-parity-report.json")).expect("report json");
        let report = serde_json::from_str::<Value>(&report_text).expect("report value");
        let scenario = report
            .get("Scenarios")
            .and_then(Value::as_array)
            .and_then(|items| {
                items.iter().find(|item| {
                    item.get("ScenarioId").and_then(Value::as_str)
                        == Some("parity-settings-services-translation-service-configuration-top")
                })
            })
            .expect("settings services scenario");

        assert_eq!(
            scenario
                .get("Metrics")
                .and_then(|metrics| metrics.get("VisibleTextDeltaCount"))
                .and_then(Value::as_u64),
            Some(5)
        );
        assert!(scenario
            .get("Metrics")
            .and_then(|metrics| metrics.get("VisibleTextJaccard"))
            .and_then(Value::as_f64)
            .is_some_and(|score| score < 60.0));
        assert!(scenario
            .get("Findings")
            .and_then(Value::as_array)
            .is_some_and(|findings| findings.iter().any(|finding| {
                finding.get("Metric").and_then(Value::as_str) == Some("visibleTextJaccard")
                    && finding
                        .get("Message")
                        .and_then(Value::as_str)
                        .is_some_and(|message| {
                            message.contains("Free API mode") && message.contains("Not refreshed")
                        })
            })));

        let markdown =
            fs::read_to_string(output.join("ui-parity-report.md")).expect("report markdown");
        assert!(markdown.contains("UI semantic summary:"));
        assert!(markdown.contains("extra in candidate"));
        assert!(markdown.contains("Free API mode"));

        let prompts =
            fs::read_to_string(output.join("llm-review-prompts.md")).expect("llm prompts");
        assert!(prompts.contains("UI semantic summary:"));
        assert!(prompts.contains("Free API mode"));
    }

    #[test]
    fn manifest_only_skips_discovered_pairs_outside_manifest() {
        let dir = tempdir().expect("temp dir");
        let reference = dir.path().join(format!("settings.services{DOTNET_SUFFIX}"));
        let candidate = dir.path().join(format!("settings.services{RUST_SUFFIX}"));
        let extra_reference = dir.path().join(format!("main.initial{DOTNET_SUFFIX}"));
        let extra_candidate = dir.path().join(format!("main.initial{RUST_SUFFIX}"));
        create_synthetic_frame(false)
            .save(&reference)
            .expect("save reference");
        create_synthetic_frame(false)
            .save(&candidate)
            .expect("save candidate");
        create_synthetic_frame(false)
            .save(&extra_reference)
            .expect("save extra reference");
        create_synthetic_frame(true)
            .save(&extra_candidate)
            .expect("save extra candidate");

        let manifest_path = dir.path().join("ui-parity-manifest.json");
        let manifest = serde_json::json!({
            "SchemaVersion": "easydict.ui-parity.manifest.v1",
            "Scenarios": [{
                "ScenarioId": "settings.services",
                "ReferenceScreenshot": reference.file_name().and_then(|value| value.to_str()).unwrap(),
                "CandidateScreenshot": candidate.file_name().and_then(|value| value.to_str()).unwrap(),
                "Regions": []
            }]
        });
        fs::write(&manifest_path, manifest.to_string()).expect("write manifest");
        let output = dir.path().join("out");

        let code = run([
            OsString::from("easydict_ui_parity_analyzer"),
            OsString::from("--screenshot-root"),
            dir.path().as_os_str().to_os_string(),
            OsString::from("--manifest"),
            manifest_path.as_os_str().to_os_string(),
            OsString::from("--manifest-only"),
            OsString::from("--output-dir"),
            output.as_os_str().to_os_string(),
        ])
        .expect("analyzer should run");

        assert_eq!(code, 0);
        let report_text =
            fs::read_to_string(output.join("ui-parity-report.json")).expect("report json");
        let report = serde_json::from_str::<Value>(&report_text).expect("report value");
        let scenarios = report
            .get("Scenarios")
            .and_then(Value::as_array)
            .expect("scenarios");
        assert_eq!(scenarios.len(), 1);
        assert_eq!(
            scenarios[0].get("ScenarioId").and_then(Value::as_str),
            Some("settings.services")
        );
    }

    #[test]
    fn semantic_summary_reports_missing_required_control_states() {
        let mut manifest = semantic_manifest(
            "parity-settings-tabs-views-pressed",
            "settings",
            &["SettingsTab_General", "SettingsTab_Views"],
            ui_summary(
                &[("button", 2)],
                &["SettingsTab_General", "SettingsTab_Views"],
            ),
            ui_summary(
                &[("button", 2)],
                &["SettingsTab_General", "SettingsTab_Views"],
            ),
        );
        manifest.required_control_states.insert(
            "SettingsTab_General".to_string(),
            vec!["selected".to_string()],
        );
        manifest.required_control_states.insert(
            "SettingsTab_Views".to_string(),
            vec!["hovered".to_string(), "pressed".to_string()],
        );

        let mut candidate_dimensions = BTreeMap::new();
        candidate_dimensions.insert(
            "SettingsTab_General".to_string(),
            parse_manifest_control_dimension(&serde_json::json!({
                "Kind": "Button",
                "State": "enabled=true,hovered=false,pressed=false,focused=false,selected=true,validation=none",
                "Width": "Fixed(86)",
                "Height": "Fixed(76)"
            })),
        );
        candidate_dimensions.insert(
            "SettingsTab_Views".to_string(),
            parse_manifest_control_dimension(&serde_json::json!({
                "Kind": "Button",
                "State": "enabled=true,hovered=true,pressed=false,focused=false,selected=false,validation=none",
                "Width": "Fixed(86)",
                "Height": "Fixed(76)"
            })),
        );
        manifest
            .candidate_ui_summary
            .as_mut()
            .expect("candidate summary")
            .visible_control_dimensions = Some(candidate_dimensions);

        let summary = compare_ui_summaries(Some(&manifest)).expect("summary");

        assert_eq!(summary.missing_required_control_states.len(), 1);
        assert_eq!(
            summary.missing_required_control_states[0].id,
            "SettingsTab_Views"
        );
        assert_eq!(
            summary.missing_required_control_states[0].required_state,
            "pressed"
        );
        assert!(
            summary.score < 100.0,
            "missing required control state should reduce semantic score: {summary:?}"
        );
    }

    #[test]
    fn fail_on_threshold_returns_exit_code_two() {
        let dir = tempdir().expect("temp dir");
        let reference = dir.path().join(format!("main.error{DOTNET_SUFFIX}"));
        let candidate = dir.path().join(format!("main.error{RUST_SUFFIX}"));
        create_synthetic_frame(false)
            .save(&reference)
            .expect("save reference");
        create_synthetic_frame(true)
            .save(&candidate)
            .expect("save candidate");
        let output = dir.path().join("out");
        let code = run([
            OsString::from("easydict_ui_parity_analyzer"),
            OsString::from("--screenshot-root"),
            dir.path().as_os_str().to_os_string(),
            OsString::from("--output-dir"),
            output.as_os_str().to_os_string(),
            OsString::from("--fail-on-threshold"),
        ])
        .expect("analyzer should run");
        assert_eq!(code, 2);
    }

    #[test]
    fn coverage_gate_returns_exit_code_three() {
        let dir = tempdir().expect("temp dir");
        let code = run([
            OsString::from("easydict_ui_parity_analyzer"),
            OsString::from("--screenshot-root"),
            dir.path().as_os_str().to_os_string(),
            OsString::from("--min-critical-coverage"),
            OsString::from("100"),
        ])
        .expect("analyzer should run with empty report");
        assert_eq!(code, 3);
    }

    #[test]
    fn parity_settings_capture_scenarios_count_toward_section_coverage() {
        let dir = tempdir().expect("temp dir");
        let about_reference = dir
            .path()
            .join(format!("parity-settings-about-links-top{DOTNET_SUFFIX}"));
        let about_candidate = dir
            .path()
            .join(format!("parity-settings-about-links-top{RUST_SUFFIX}"));
        let language_reference = dir.path().join(format!(
            "parity-settings-language-translation-languages-expanded-list-scroll-100-percent{DOTNET_SUFFIX}"
        ));
        let language_candidate = dir.path().join(format!(
            "parity-settings-language-translation-languages-expanded-list-scroll-100-percent{RUST_SUFFIX}"
        ));

        for path in [
            &about_reference,
            &about_candidate,
            &language_reference,
            &language_candidate,
        ] {
            create_synthetic_frame(false)
                .save(path)
                .expect("save synthetic frame");
        }

        let manifest_path = dir.path().join("ui-parity-manifest.json");
        let manifest = serde_json::json!({
            "SchemaVersion": "easydict.ui-parity.manifest.v1",
            "Scenarios": [
                {
                    "ScenarioId": "parity-settings-about-links-top",
                    "WindowKind": "settings",
                    "SectionId": "about",
                    "SectionLabel": "About",
                    "Theme": "system",
                    "ScrollPercent": 0.0,
                    "ExpandAvailableLanguages": false,
                    "ReferenceScreenshot": about_reference.file_name().and_then(|value| value.to_str()).unwrap(),
                    "CandidateScreenshot": about_candidate.file_name().and_then(|value| value.to_str()).unwrap(),
                    "Regions": [],
                    "RequiredSemanticTags": []
                },
                {
                    "ScenarioId": "parity-settings-language-translation-languages-expanded-list-scroll-100-percent",
                    "WindowKind": "settings",
                    "SectionId": "language",
                    "SectionLabel": "Language",
                    "Theme": "system",
                    "ScrollPercent": 100.0,
                    "ExpandAvailableLanguages": true,
                    "ReferenceScreenshot": language_reference.file_name().and_then(|value| value.to_str()).unwrap(),
                    "CandidateScreenshot": language_candidate.file_name().and_then(|value| value.to_str()).unwrap(),
                    "Regions": [],
                    "RequiredSemanticTags": []
                }
            ]
        });
        fs::write(&manifest_path, manifest.to_string()).expect("write manifest");
        let output = dir.path().join("out");

        let code = run([
            OsString::from("easydict_ui_parity_analyzer"),
            OsString::from("--screenshot-root"),
            dir.path().as_os_str().to_os_string(),
            OsString::from("--manifest"),
            manifest_path.as_os_str().to_os_string(),
            OsString::from("--output-dir"),
            output.as_os_str().to_os_string(),
        ])
        .expect("analyzer should run");

        assert_eq!(code, 0);
        let coverage_text =
            fs::read_to_string(output.join("ui-parity-coverage.json")).expect("coverage json");
        let coverage = serde_json::from_str::<Value>(&coverage_text).expect("coverage value");
        let about = find_coverage_item(&coverage, "settings.about");
        let language = find_coverage_item(&coverage, "settings.language");

        assert_eq!(about.get("IsCovered").and_then(Value::as_bool), Some(true));
        assert_eq!(
            about.get("EvidenceStatus").and_then(Value::as_str),
            Some("covered-pass")
        );
        assert!(about
            .get("MatchingScenarioIds")
            .and_then(Value::as_array)
            .is_some_and(|items| items
                .iter()
                .any(|item| item.as_str() == Some("parity-settings-about-links-top"))));

        assert_eq!(
            language.get("IsCovered").and_then(Value::as_bool),
            Some(true)
        );
        assert_eq!(
            language.get("EvidenceStatus").and_then(Value::as_str),
            Some("covered-pass")
        );
        assert!(language
            .get("MatchingScenarioIds")
            .and_then(Value::as_array)
            .is_some_and(|items| items.iter().any(|item| {
                item.as_str()
                    == Some(
                        "parity-settings-language-translation-languages-expanded-list-scroll-100-percent",
                    )
            })));
    }

    #[test]
    fn interaction_effect_capture_scenarios_count_toward_effect_coverage() {
        let dir = tempdir().expect("temp dir");
        let cases = [
            ("effects.primary-pressed", "main", "effects.primary-pressed"),
            (
                "effects.source-input-focus",
                "main",
                "effects.source-input-focus",
            ),
            (
                "parity-settings-tabs-views-pressed",
                "settings",
                "effects.settings-tab-pressed",
            ),
            ("effects.overlay-fade", "main", "effects.overlay-fade"),
            ("mini.translate-hover", "mini", "mini.translate-hover"),
            ("mini.translate-pressed", "mini", "mini.translate-pressed"),
            ("fixed.translate-hover", "fixed", "fixed.translate-hover"),
            (
                "fixed.translate-pressed",
                "fixed",
                "fixed.translate-pressed",
            ),
            ("long-doc.service-hover", "main", "long-doc.service-hover"),
        ];

        let mut scenarios = Vec::new();
        for (scenario_id, window_kind, _) in cases {
            let reference = dir.path().join(format!("{scenario_id}{DOTNET_SUFFIX}"));
            let candidate = dir.path().join(format!("{scenario_id}{RUST_SUFFIX}"));
            create_synthetic_frame(false)
                .save(&reference)
                .expect("save reference");
            create_synthetic_frame(false)
                .save(&candidate)
                .expect("save candidate");
            scenarios.push(serde_json::json!({
                "ScenarioId": scenario_id,
                "WindowKind": window_kind,
                "SectionId": "",
                "SectionLabel": "",
                "Theme": "system",
                "ScrollPercent": 0.0,
                "ExpandAvailableLanguages": false,
                "ReferenceScreenshot": reference.file_name().and_then(|value| value.to_str()).unwrap(),
                "CandidateScreenshot": candidate.file_name().and_then(|value| value.to_str()).unwrap(),
                "Regions": [],
                "RequiredSemanticTags": []
            }));
        }

        let manifest_path = dir.path().join("ui-parity-manifest.json");
        let manifest = serde_json::json!({
            "SchemaVersion": "easydict.ui-parity.manifest.v1",
            "Scenarios": scenarios
        });
        fs::write(&manifest_path, manifest.to_string()).expect("write manifest");
        let output = dir.path().join("out");

        let code = run([
            OsString::from("easydict_ui_parity_analyzer"),
            OsString::from("--screenshot-root"),
            dir.path().as_os_str().to_os_string(),
            OsString::from("--manifest"),
            manifest_path.as_os_str().to_os_string(),
            OsString::from("--output-dir"),
            output.as_os_str().to_os_string(),
        ])
        .expect("analyzer should run");

        assert_eq!(code, 0);
        let coverage_text =
            fs::read_to_string(output.join("ui-parity-coverage.json")).expect("coverage json");
        let coverage = serde_json::from_str::<Value>(&coverage_text).expect("coverage value");

        for (scenario_id, _, expected_id) in cases {
            let item = find_coverage_item(&coverage, expected_id);
            assert_eq!(
                item.get("IsCovered").and_then(Value::as_bool),
                Some(true),
                "{expected_id} should be covered"
            );
            assert_eq!(
                item.get("EvidenceStatus").and_then(Value::as_str),
                Some("covered-pass"),
                "{expected_id} should pass in synthetic evidence"
            );
            assert!(
                item.get("MatchingScenarioIds")
                    .and_then(Value::as_array)
                    .is_some_and(|items| items
                        .iter()
                        .any(|item| item.as_str() == Some(scenario_id))),
                "{expected_id} should list {scenario_id} as matching evidence"
            );
        }

        let floating_action_pressed =
            find_coverage_item(&coverage, "effects.floating-action-pressed");
        assert!(floating_action_pressed
            .get("MatchingScenarioIds")
            .and_then(Value::as_array)
            .is_some_and(|items| items
                .iter()
                .any(|item| item.as_str() == Some("mini.translate-pressed"))));
        assert!(floating_action_pressed
            .get("MatchingScenarioIds")
            .and_then(Value::as_array)
            .is_some_and(|items| items
                .iter()
                .any(|item| item.as_str() == Some("fixed.translate-pressed"))));
    }

    #[test]
    fn floating_operation_capture_scenarios_count_toward_operation_coverage() {
        let dir = tempdir().expect("temp dir");
        let cases = [
            ("mini.ocr-hover", "mini", "mini.ocr-hover"),
            ("mini.pin-pressed", "mini", "mini.pin-pressed"),
            ("fixed.close-hover", "fixed", "fixed.close-hover"),
            (
                "fixed.target-language-pressed",
                "fixed",
                "fixed.target-language-pressed",
            ),
            (
                "mini.source-language-dropdown-open",
                "mini",
                "mini.source-language-dropdown-open",
            ),
            (
                "mini.source-language-dropdown-select-1-auto-detect",
                "mini",
                "mini.source-language-dropdown-select-1",
            ),
            (
                "fixed.target-language-dropdown-select-9-spanish",
                "fixed",
                "fixed.target-language-dropdown-select-9",
            ),
        ];

        let mut scenarios = Vec::new();
        for (scenario_id, window_kind, _) in cases {
            let reference = dir.path().join(format!("{scenario_id}{DOTNET_SUFFIX}"));
            let candidate = dir.path().join(format!("{scenario_id}{RUST_SUFFIX}"));
            create_synthetic_frame(false)
                .save(&reference)
                .expect("save reference");
            create_synthetic_frame(false)
                .save(&candidate)
                .expect("save candidate");
            scenarios.push(serde_json::json!({
                "ScenarioId": scenario_id,
                "WindowKind": window_kind,
                "SectionId": window_kind,
                "SectionLabel": window_kind,
                "Theme": "system",
                "ScrollPercent": 0.0,
                "ExpandAvailableLanguages": false,
                "ReferenceScreenshot": reference.file_name().and_then(|value| value.to_str()).unwrap(),
                "CandidateScreenshot": candidate.file_name().and_then(|value| value.to_str()).unwrap(),
                "Regions": [],
                "RequiredSemanticTags": []
            }));
        }

        let manifest_path = dir.path().join("ui-parity-manifest.json");
        let manifest = serde_json::json!({
            "SchemaVersion": "easydict.ui-parity.manifest.v1",
            "Scenarios": scenarios
        });
        fs::write(&manifest_path, manifest.to_string()).expect("write manifest");
        let output = dir.path().join("out");

        let code = run([
            OsString::from("easydict_ui_parity_analyzer"),
            OsString::from("--screenshot-root"),
            dir.path().as_os_str().to_os_string(),
            OsString::from("--manifest"),
            manifest_path.as_os_str().to_os_string(),
            OsString::from("--output-dir"),
            output.as_os_str().to_os_string(),
        ])
        .expect("analyzer should run");

        assert_eq!(code, 0);
        let coverage_text =
            fs::read_to_string(output.join("ui-parity-coverage.json")).expect("coverage json");
        let coverage = serde_json::from_str::<Value>(&coverage_text).expect("coverage value");

        for (scenario_id, _, expected_id) in cases {
            let item = find_coverage_item(&coverage, expected_id);
            assert_eq!(
                item.get("IsCovered").and_then(Value::as_bool),
                Some(true),
                "{expected_id} should be covered"
            );
            assert_eq!(
                item.get("EvidenceStatus").and_then(Value::as_str),
                Some("covered-pass"),
                "{expected_id} should pass in synthetic evidence"
            );
            assert!(
                item.get("MatchingScenarioIds")
                    .and_then(Value::as_array)
                    .is_some_and(|items| items
                        .iter()
                        .any(|item| item.as_str() == Some(scenario_id))),
                "{expected_id} should list {scenario_id} as matching evidence"
            );
        }
    }

    #[test]
    fn llm_review_outputs_include_metrics_findings_and_regions() {
        let dir = tempdir().expect("temp dir");
        let reference = dir.path().join(format!("main.hover{DOTNET_SUFFIX}"));
        let candidate = dir.path().join(format!("main.hover{RUST_SUFFIX}"));
        create_synthetic_frame(false)
            .save(&reference)
            .expect("save reference");
        create_synthetic_frame(true)
            .save(&candidate)
            .expect("save candidate");
        let output = dir.path().join("out");

        let code = run([
            OsString::from("easydict_ui_parity_analyzer"),
            OsString::from("--screenshot-root"),
            dir.path().as_os_str().to_os_string(),
            OsString::from("--output-dir"),
            output.as_os_str().to_os_string(),
        ])
        .expect("analyzer should run");

        assert_eq!(code, 0);
        let requests =
            fs::read_to_string(output.join("llm-review-requests.json")).expect("llm json");
        let prompts =
            fs::read_to_string(output.join("llm-review-prompts.md")).expect("llm prompts");
        assert!(requests.contains("\"Regions\""));
        assert!(prompts.contains("Task: compare the .NET WinUI reference"));
        assert!(prompts.contains("Metrics: pixel error"));
        assert!(prompts.contains("image size delta"));
        assert!(prompts.contains("Absolute sizes:"));
        assert!(prompts.contains("Control absolute sizes:"));
        assert!(prompts.contains("Reference source:"));
        assert!(prompts.contains("Findings:"));
        assert!(prompts.contains("Lowest scoring regions:"));
    }

    #[test]
    fn absolute_size_mismatch_caps_visual_score_even_after_normalization() {
        let dir = tempdir().expect("temp dir");
        let reference = dir.path().join(format!("settings.general{DOTNET_SUFFIX}"));
        let candidate = dir.path().join(format!("settings.general{RUST_SUFFIX}"));
        create_synthetic_frame(false)
            .save(&reference)
            .expect("save reference");
        imageops::resize(
            &create_synthetic_frame(false),
            160,
            100,
            FilterType::Lanczos3,
        )
        .save(&candidate)
        .expect("save candidate");
        let output = dir.path().join("out");

        let code = run([
            OsString::from("easydict_ui_parity_analyzer"),
            OsString::from("--screenshot-root"),
            dir.path().as_os_str().to_os_string(),
            OsString::from("--output-dir"),
            output.as_os_str().to_os_string(),
        ])
        .expect("analyzer should run");

        assert_eq!(code, 0);
        let report_text =
            fs::read_to_string(output.join("ui-parity-report.json")).expect("report json");
        let report = serde_json::from_str::<Value>(&report_text).expect("report value");
        let scenario = report
            .get("Scenarios")
            .and_then(Value::as_array)
            .and_then(|items| {
                items.iter().find(|item| {
                    item.get("ScenarioId").and_then(Value::as_str) == Some("settings.general")
                })
            })
            .expect("settings.general scenario");

        assert_eq!(scenario.get("Status").and_then(Value::as_str), Some("fail"));
        assert!(
            scenario
                .get("Score")
                .and_then(Value::as_f64)
                .unwrap_or(100.0)
                <= 45.0
        );
        assert_eq!(
            scenario
                .get("Metrics")
                .and_then(|metrics| metrics.get("AbsoluteImageSizeDeltaPercent"))
                .and_then(Value::as_f64),
            Some(44.44)
        );
        assert_eq!(
            scenario
                .get("Metrics")
                .and_then(|metrics| metrics.get("AbsoluteSizeScoreCap"))
                .and_then(Value::as_f64),
            Some(45.0)
        );
        assert!(scenario
            .get("Findings")
            .and_then(Value::as_array)
            .is_some_and(|findings| findings.iter().any(|finding| {
                finding.get("Metric").and_then(Value::as_str) == Some("absoluteSizeScoreCap")
            })));

        let markdown =
            fs::read_to_string(output.join("ui-parity-report.md")).expect("report markdown");
        assert!(markdown.contains("Size delta"));
        assert!(markdown.contains("Size cap"));
        assert!(markdown.contains("absoluteSizeScoreCap"));
    }

    #[test]
    fn manifest_control_dimensions_are_reported_as_absolute_drift() {
        let dir = tempdir().expect("temp dir");
        let reference = dir.path().join(format!("settings.about{DOTNET_SUFFIX}"));
        let candidate = dir.path().join(format!("settings.about{RUST_SUFFIX}"));
        create_synthetic_frame(false)
            .save(&reference)
            .expect("save reference");
        create_synthetic_frame(false)
            .save(&candidate)
            .expect("save candidate");

        let manifest_path = dir.path().join("ui-parity-manifest.json");
        let manifest = serde_json::json!({
            "SchemaVersion": "easydict.ui-parity.manifest.v1",
            "Scenarios": [{
                "ScenarioId": "settings.about",
                "WindowKind": "settings",
                "SectionId": "about",
                "SectionLabel": "About",
                "Theme": "system",
                "ScrollPercent": 0.0,
                "ExpandAvailableLanguages": false,
                "ReferenceScreenshot": reference.file_name().and_then(|value| value.to_str()).unwrap(),
                "CandidateScreenshot": candidate.file_name().and_then(|value| value.to_str()).unwrap(),
                "Regions": [],
                "RequiredSemanticTags": ["SettingsTab_About", "LicenseText"],
                "ReferenceUiSummary": {
                    "VisibleControlCounts": {},
                    "VisibleAutomationIds": ["SettingsTab_About", "LicenseText"],
                    "VisibleControlDimensions": {
                        "SettingsTab_About": {
                            "Kind": "Button",
                            "Width": "Fixed(86)",
                            "Height": "Fixed(76)"
                        },
                        "LicenseText": {
                            "Kind": "Text",
                            "Width": "Fill",
                            "Height": "Fixed(18)"
                        }
                    }
                },
                "CandidateUiSummary": {
                    "VisibleControlCounts": {},
                    "VisibleAutomationIds": ["SettingsTab_About", "LicenseText"],
                    "VisibleControlDimensions": {
                        "SettingsTab_About": {
                            "Kind": "Button",
                            "Width": "Fixed(96)",
                            "Height": "Fixed(76)"
                        },
                        "LicenseText": {
                            "Kind": "Text",
                            "Width": "Fill",
                            "Height": "Fixed(18)"
                        }
                    }
                }
            }]
        });
        fs::write(&manifest_path, manifest.to_string()).expect("write manifest");
        let output = dir.path().join("out");

        let code = run([
            OsString::from("easydict_ui_parity_analyzer"),
            OsString::from("--screenshot-root"),
            dir.path().as_os_str().to_os_string(),
            OsString::from("--manifest"),
            manifest_path.as_os_str().to_os_string(),
            OsString::from("--output-dir"),
            output.as_os_str().to_os_string(),
        ])
        .expect("analyzer should run");

        assert_eq!(code, 0);
        let report_text =
            fs::read_to_string(output.join("ui-parity-report.json")).expect("report json");
        let report = serde_json::from_str::<Value>(&report_text).expect("report value");
        let scenario = report
            .get("Scenarios")
            .and_then(Value::as_array)
            .and_then(|items| {
                items.iter().find(|item| {
                    item.get("ScenarioId").and_then(Value::as_str) == Some("settings.about")
                })
            })
            .expect("settings.about scenario");

        assert_eq!(
            scenario
                .get("Metrics")
                .and_then(|metrics| metrics.get("ControlDimensionDeltaCount"))
                .and_then(Value::as_u64),
            Some(1)
        );
        assert_eq!(
            scenario
                .get("Metrics")
                .and_then(|metrics| metrics.get("MaxControlDimensionDeltaDips"))
                .and_then(Value::as_f64),
            Some(10.0)
        );
        assert_eq!(
            scenario
                .get("Metrics")
                .and_then(|metrics| metrics.get("ControlDimensionScoreCap"))
                .and_then(Value::as_f64),
            Some(69.0)
        );
        assert!(scenario
            .get("Findings")
            .and_then(Value::as_array)
            .is_some_and(|findings| findings.iter().any(|finding| {
                finding.get("Metric").and_then(Value::as_str) == Some("controlDimensionDeltaDips")
                    && finding.get("Severity").and_then(Value::as_str) == Some("error")
            })));
        assert!(scenario
            .get("Findings")
            .and_then(Value::as_array)
            .is_some_and(|findings| findings.iter().any(|finding| {
                finding.get("Metric").and_then(Value::as_str) == Some("controlDimensionScoreCap")
                    && finding.get("Severity").and_then(Value::as_str) == Some("error")
            })));

        let markdown =
            fs::read_to_string(output.join("ui-parity-report.md")).expect("report markdown");
        assert!(markdown.contains("Control absolute sizes:"));
        assert!(markdown.contains("SettingsTab_About"));
        assert!(markdown.contains("LicenseText"));
        assert!(markdown.contains("controlDimensionDeltaDips"));
        assert!(markdown.contains("controlDimensionScoreCap"));
    }

    #[test]
    fn main_input_textbox_inner_edit_height_does_not_cap_visual_chrome_height() {
        let dir = tempdir().expect("temp dir");
        let reference = dir
            .path()
            .join(format!("main.before-translate{DOTNET_SUFFIX}"));
        let candidate = dir
            .path()
            .join(format!("main.before-translate{RUST_SUFFIX}"));
        create_synthetic_frame(false)
            .save(&reference)
            .expect("save reference");
        create_synthetic_frame(false)
            .save(&candidate)
            .expect("save candidate");

        let manifest_path = dir.path().join("ui-parity-manifest.json");
        let manifest = serde_json::json!({
            "SchemaVersion": "easydict.ui-parity.manifest.v1",
            "Scenarios": [{
                "ScenarioId": "main.before-translate",
                "WindowKind": "main",
                "Theme": "system",
                "ReferenceScreenshot": reference.file_name().and_then(|value| value.to_str()).unwrap(),
                "CandidateScreenshot": candidate.file_name().and_then(|value| value.to_str()).unwrap(),
                "Regions": [],
                "RequiredSemanticTags": ["InputTextBox"],
                "ReferenceUiSummary": {
                    "VisibleControlCounts": {},
                    "VisibleAutomationIds": ["InputTextBox"],
                    "VisibleControlDimensions": {
                        "InputTextBox": {
                            "Kind": "Edit",
                            "Width": "333",
                            "Height": "80"
                        }
                    }
                },
                "CandidateUiSummary": {
                    "VisibleControlCounts": {},
                    "VisibleAutomationIds": ["InputTextBox"],
                    "VisibleControlDimensions": {
                        "InputTextBox": {
                            "Kind": "TextEditor",
                            "Width": "auto",
                            "Height": "Fixed(96)",
                            "LabeledHeight": "Fixed(119)",
                            "MinHeight": "96",
                            "MaxHeight": "112"
                        }
                    }
                }
            }]
        });
        fs::write(&manifest_path, manifest.to_string()).expect("write manifest");
        let output = dir.path().join("out");

        let code = run([
            OsString::from("easydict_ui_parity_analyzer"),
            OsString::from("--screenshot-root"),
            dir.path().as_os_str().to_os_string(),
            OsString::from("--manifest"),
            manifest_path.as_os_str().to_os_string(),
            OsString::from("--output-dir"),
            output.as_os_str().to_os_string(),
        ])
        .expect("analyzer should run");

        assert_eq!(code, 0);
        let report_text =
            fs::read_to_string(output.join("ui-parity-report.json")).expect("report json");
        let report = serde_json::from_str::<Value>(&report_text).expect("report value");
        let scenario = report
            .get("Scenarios")
            .and_then(Value::as_array)
            .and_then(|items| {
                items.iter().find(|item| {
                    item.get("ScenarioId").and_then(Value::as_str) == Some("main.before-translate")
                })
            })
            .expect("main.before-translate scenario");

        assert!(scenario
            .get("Metrics")
            .and_then(|metrics| metrics.get("ControlDimensionDeltaCount"))
            .is_none_or(|value| value.is_null() || value.as_u64() == Some(0)));
        assert!(scenario
            .get("Metrics")
            .and_then(|metrics| metrics.get("ControlDimensionScoreCap"))
            .is_none_or(|value| value.is_null() || value.as_f64() == Some(100.0)));
        assert!(!scenario
            .get("Findings")
            .and_then(Value::as_array)
            .is_some_and(|findings| findings.iter().any(|finding| {
                finding.get("Metric").and_then(Value::as_str) == Some("controlDimensionDeltaDips")
            })));

        let markdown =
            fs::read_to_string(output.join("ui-parity-report.md")).expect("report markdown");
        assert!(markdown.contains("InputTextBox"));
        assert!(markdown.contains("reference Edit width=333 height=80"));
        assert!(markdown.contains("candidate TextEditor"));
    }

    #[test]
    fn manifest_control_bounds_are_reported_as_absolute_position_drift() {
        let dir = tempdir().expect("temp dir");
        let reference = dir.path().join(format!("settings.about{DOTNET_SUFFIX}"));
        let candidate = dir.path().join(format!("settings.about{RUST_SUFFIX}"));
        create_synthetic_frame(false)
            .save(&reference)
            .expect("save reference");
        create_synthetic_frame(false)
            .save(&candidate)
            .expect("save candidate");

        let manifest_path = dir.path().join("ui-parity-manifest.json");
        let manifest = serde_json::json!({
            "SchemaVersion": "easydict.ui-parity.manifest.v1",
            "Scenarios": [{
                "ScenarioId": "settings.about.bounds",
                "WindowKind": "settings",
                "SectionId": "about",
                "SectionLabel": "About",
                "Theme": "system",
                "ScrollPercent": 0.0,
                "ExpandAvailableLanguages": false,
                "ReferenceScreenshot": reference.file_name().and_then(|value| value.to_str()).unwrap(),
                "CandidateScreenshot": candidate.file_name().and_then(|value| value.to_str()).unwrap(),
                "Regions": [],
                "RequiredSemanticTags": ["AboutHeaderText", "LicenseText"],
                "ReferenceUiSummary": {
                    "VisibleControlCounts": {},
                    "VisibleAutomationIds": ["AboutHeaderText", "LicenseText"],
                    "VisibleControlDimensions": {
                        "AboutHeaderText": {
                            "Kind": "Text",
                            "Width": "796",
                            "Height": "24",
                            "BoundsDips": {
                                "Left": 30.0,
                                "Top": 276.0,
                                "Width": 796.0,
                                "Height": 24.0
                            }
                        },
                        "LicenseText": {
                            "Kind": "Text",
                            "Width": "150",
                            "Height": "18",
                            "BoundsDips": {
                                "Left": 30.0,
                                "Top": 398.0,
                                "Width": 150.0,
                                "Height": 18.0
                            }
                        }
                    }
                },
                "CandidateUiSummary": {
                    "VisibleControlCounts": {},
                    "VisibleAutomationIds": ["AboutHeaderText", "LicenseText"],
                    "VisibleControlDimensions": {
                        "AboutHeaderText": {
                            "Kind": "Text",
                            "Width": "796",
                            "Height": "24",
                            "BoundsDips": {
                                "Left": 30.0,
                                "Top": 288.0,
                                "Width": 796.0,
                                "Height": 24.0
                            }
                        },
                        "LicenseText": {
                            "Kind": "Text",
                            "Width": "150",
                            "Height": "18"
                        }
                    }
                }
            }]
        });
        fs::write(&manifest_path, manifest.to_string()).expect("write manifest");
        let output = dir.path().join("out");

        let code = run([
            OsString::from("easydict_ui_parity_analyzer"),
            OsString::from("--screenshot-root"),
            dir.path().as_os_str().to_os_string(),
            OsString::from("--manifest"),
            manifest_path.as_os_str().to_os_string(),
            OsString::from("--output-dir"),
            output.as_os_str().to_os_string(),
        ])
        .expect("analyzer should run");

        assert_eq!(code, 0);
        let report_text =
            fs::read_to_string(output.join("ui-parity-report.json")).expect("report json");
        let report = serde_json::from_str::<Value>(&report_text).expect("report value");
        let scenario = report
            .get("Scenarios")
            .and_then(Value::as_array)
            .and_then(|items| {
                items.iter().find(|item| {
                    item.get("ScenarioId").and_then(Value::as_str) == Some("settings.about.bounds")
                })
            })
            .expect("settings.about.bounds scenario");

        assert_eq!(
            scenario
                .get("Metrics")
                .and_then(|metrics| metrics.get("ControlDimensionDeltaCount"))
                .and_then(Value::as_u64),
            Some(1)
        );
        assert_eq!(
            scenario
                .get("Metrics")
                .and_then(|metrics| metrics.get("MaxControlDimensionDeltaDips"))
                .and_then(Value::as_f64),
            Some(12.0)
        );
        assert_eq!(
            scenario
                .get("Metrics")
                .and_then(|metrics| metrics.get("MissingControlBoundsEvidenceCount"))
                .and_then(Value::as_u64),
            Some(1)
        );
        assert!(scenario
            .get("Findings")
            .and_then(Value::as_array)
            .is_some_and(|findings| findings.iter().any(|finding| {
                finding.get("Metric").and_then(Value::as_str) == Some("controlDimensionDeltaDips")
                    && finding
                        .get("Message")
                        .and_then(Value::as_str)
                        .is_some_and(|message| message.contains("AboutHeaderText.top"))
            })));
        assert!(scenario
            .get("Findings")
            .and_then(Value::as_array)
            .is_some_and(|findings| findings.iter().any(|finding| {
                finding.get("Metric").and_then(Value::as_str)
                    == Some("missingControlBoundsEvidence")
                    && finding
                        .get("Message")
                        .and_then(Value::as_str)
                        .is_some_and(|message| message.contains("LicenseText"))
            })));

        let markdown =
            fs::read_to_string(output.join("ui-parity-report.md")).expect("report markdown");
        assert!(markdown.contains("bounds_dips=(30.00,276.00,796.00,24.00)"));
        assert!(markdown.contains("AboutHeaderText.top"));
        assert!(markdown.contains("missingControlBoundsEvidence"));
    }

    #[test]
    fn manifest_evidence_audit_summarizes_missing_ids_dimensions_and_bounds() {
        let dir = tempdir().expect("temp dir");
        let reference = dir.path().join(format!("settings.audit{DOTNET_SUFFIX}"));
        let candidate = dir.path().join(format!("settings.audit{RUST_SUFFIX}"));
        create_synthetic_frame(false)
            .save(&reference)
            .expect("save reference");
        create_synthetic_frame(false)
            .save(&candidate)
            .expect("save candidate");

        let manifest_path = dir.path().join("ui-parity-manifest.json");
        let manifest = serde_json::json!({
            "SchemaVersion": "easydict.ui-parity.manifest.v1",
            "Scenarios": [{
                "ScenarioId": "settings.audit",
                "WindowKind": "settings",
                "SectionId": "about",
                "SectionLabel": "About",
                "Theme": "system",
                "ScrollPercent": 0.0,
                "ExpandAvailableLanguages": false,
                "ReferenceScreenshot": reference.file_name().and_then(|value| value.to_str()).unwrap(),
                "CandidateScreenshot": candidate.file_name().and_then(|value| value.to_str()).unwrap(),
                "Regions": [],
                "RequiredSemanticTags": ["AboutHeaderText", "LicenseText"],
                "ReferenceUiSummary": {
                    "VisibleControlCounts": {},
                    "VisibleAutomationIds": ["AboutHeaderText", "LicenseText", "VersionText"],
                    "VisibleControlDimensions": {
                        "AboutHeaderText": {
                            "Kind": "Text",
                            "Width": "796",
                            "Height": "24",
                            "BoundsDips": {
                                "Left": 30.0,
                                "Top": 276.0,
                                "Width": 796.0,
                                "Height": 24.0
                            }
                        },
                        "LicenseText": {
                            "Kind": "Text",
                            "Width": "150",
                            "Height": "18",
                            "BoundsDips": {
                                "Left": 30.0,
                                "Top": 398.0,
                                "Width": 150.0,
                                "Height": 18.0
                            }
                        },
                        "VersionText": {
                            "Kind": "Text",
                            "Width": "120",
                            "Height": "18"
                        }
                    }
                },
                "CandidateUiSummary": {
                    "VisibleControlCounts": {},
                    "VisibleAutomationIds": ["AboutHeaderText"],
                    "VisibleControlDimensions": {
                        "AboutHeaderText": {
                            "Kind": "Text",
                            "Width": "796",
                            "Height": "24"
                        },
                        "ExtraCandidateButton": {
                            "Kind": "Button",
                            "Width": "80",
                            "Height": "32"
                        }
                    }
                }
            }]
        });
        fs::write(&manifest_path, manifest.to_string()).expect("write manifest");
        let output = dir.path().join("out");

        let code = run([
            OsString::from("easydict_ui_parity_analyzer"),
            OsString::from("--screenshot-root"),
            dir.path().as_os_str().to_os_string(),
            OsString::from("--manifest"),
            manifest_path.as_os_str().to_os_string(),
            OsString::from("--output-dir"),
            output.as_os_str().to_os_string(),
        ])
        .expect("analyzer should run");

        assert_eq!(code, 0);
        let report_text =
            fs::read_to_string(output.join("ui-parity-report.json")).expect("report json");
        let report = serde_json::from_str::<Value>(&report_text).expect("report value");
        let scenario = report
            .get("Scenarios")
            .and_then(Value::as_array)
            .and_then(|items| {
                items.iter().find(|item| {
                    item.get("ScenarioId").and_then(Value::as_str) == Some("settings.audit")
                })
            })
            .expect("settings.audit scenario");
        let audit = scenario
            .get("EvidenceAudit")
            .expect("scenario should include evidence audit");

        assert_eq!(
            audit
                .get("MissingCandidateAutomationIdCount")
                .and_then(Value::as_u64),
            Some(2)
        );
        assert_eq!(
            audit
                .get("MissingCandidateDimensionCount")
                .and_then(Value::as_u64),
            Some(2)
        );
        assert_eq!(
            audit
                .get("MissingCandidateBoundsCount")
                .and_then(Value::as_u64),
            Some(2)
        );
        assert_eq!(
            audit
                .get("CandidateDimensionWithoutBoundsCount")
                .and_then(Value::as_u64),
            Some(2)
        );
        assert!(audit
            .get("MissingCandidateAutomationIds")
            .and_then(Value::as_array)
            .is_some_and(|items| items
                .iter()
                .any(|item| item.as_str() == Some("VersionText"))));
        assert!(audit
            .get("MissingCandidateBounds")
            .and_then(Value::as_array)
            .is_some_and(|items| items.iter().any(|item| {
                item.get("Id").and_then(Value::as_str) == Some("AboutHeaderText")
                    && item
                        .get("Candidate")
                        .and_then(Value::as_str)
                        .is_some_and(|value| value.contains("missing bounds_dips"))
            })));

        let markdown =
            fs::read_to_string(output.join("ui-parity-report.md")).expect("report markdown");
        assert!(markdown.contains("## Evidence Audit"));
        assert!(markdown.contains("settings.audit"));
        assert!(markdown.contains("bounds `AboutHeaderText`"));
        assert!(markdown.contains("bounds `LicenseText`"));
        assert!(markdown.contains("dimension `VersionText`"));

        let prompts =
            fs::read_to_string(output.join("llm-review-prompts.md")).expect("llm prompts");
        assert!(prompts.contains("Evidence audit:"));
        assert!(prompts.contains("bounds `AboutHeaderText`"));
    }

    #[test]
    fn manifest_required_control_dimensions_missing_candidate_evidence_is_reported() {
        let dir = tempdir().expect("temp dir");
        let reference = dir.path().join(format!("settings.about{DOTNET_SUFFIX}"));
        let candidate = dir.path().join(format!("settings.about{RUST_SUFFIX}"));
        create_synthetic_frame(false)
            .save(&reference)
            .expect("save reference");
        create_synthetic_frame(false)
            .save(&candidate)
            .expect("save candidate");

        let manifest_path = dir.path().join("ui-parity-manifest.json");
        let manifest = serde_json::json!({
            "SchemaVersion": "easydict.ui-parity.manifest.v1",
            "Scenarios": [{
                "ScenarioId": "settings.about",
                "WindowKind": "settings",
                "SectionId": "about",
                "SectionLabel": "About",
                "Theme": "system",
                "ScrollPercent": 0.0,
                "ExpandAvailableLanguages": false,
                "ReferenceScreenshot": reference.file_name().and_then(|value| value.to_str()).unwrap(),
                "CandidateScreenshot": candidate.file_name().and_then(|value| value.to_str()).unwrap(),
                "Regions": [],
                "RequiredSemanticTags": ["AboutAppNameText"],
                "ReferenceUiSummary": {
                    "VisibleControlCounts": {},
                    "VisibleAutomationIds": ["AboutAppNameText"],
                    "VisibleControlDimensions": {
                        "AboutAppNameText": {
                            "Kind": "Text",
                            "Width": "137.50",
                            "Height": "21.00"
                        }
                    }
                },
                "CandidateUiSummary": {
                    "VisibleControlCounts": {},
                    "VisibleAutomationIds": ["AboutAppNameText"],
                    "VisibleControlDimensions": {
                        "AboutAppNameText": {
                            "Kind": "Text"
                        }
                    }
                }
            }]
        });
        fs::write(&manifest_path, manifest.to_string()).expect("write manifest");
        let output = dir.path().join("out");

        let code = run([
            OsString::from("easydict_ui_parity_analyzer"),
            OsString::from("--screenshot-root"),
            dir.path().as_os_str().to_os_string(),
            OsString::from("--manifest"),
            manifest_path.as_os_str().to_os_string(),
            OsString::from("--output-dir"),
            output.as_os_str().to_os_string(),
        ])
        .expect("analyzer should run");

        assert_eq!(code, 0);
        let report_text =
            fs::read_to_string(output.join("ui-parity-report.json")).expect("report json");
        let report = serde_json::from_str::<Value>(&report_text).expect("report value");
        let scenario = report
            .get("Scenarios")
            .and_then(Value::as_array)
            .and_then(|items| {
                items.iter().find(|item| {
                    item.get("ScenarioId").and_then(Value::as_str) == Some("settings.about")
                })
            })
            .expect("settings.about scenario");

        assert_eq!(
            scenario
                .get("Metrics")
                .and_then(|metrics| metrics.get("ControlDimensionDeltaCount"))
                .and_then(Value::as_u64),
            Some(2)
        );
        assert_eq!(
            scenario
                .get("Metrics")
                .and_then(|metrics| metrics.get("MaxControlDimensionDeltaDips"))
                .and_then(Value::as_f64),
            Some(9.0)
        );
        assert_eq!(
            scenario
                .get("Metrics")
                .and_then(|metrics| metrics.get("ControlDimensionScoreCap"))
                .and_then(Value::as_f64),
            Some(69.0)
        );
        assert!(scenario
            .get("Findings")
            .and_then(Value::as_array)
            .is_some_and(|findings| findings.iter().any(|finding| {
                finding.get("Metric").and_then(Value::as_str) == Some("controlDimensionDeltaDips")
                    && finding
                        .get("Message")
                        .and_then(Value::as_str)
                        .is_some_and(|message| message.contains("missing dimension evidence"))
            })));

        let markdown =
            fs::read_to_string(output.join("ui-parity-report.md")).expect("report markdown");
        assert!(markdown.contains("missing dimension evidence"));
    }

    #[test]
    fn missing_candidate_dimension_evidence_does_not_cap_when_visuals_and_window_match() {
        let dir = tempdir().expect("temp dir");
        let reference = dir.path().join(format!("settings.services{DOTNET_SUFFIX}"));
        let candidate = dir.path().join(format!("settings.services{RUST_SUFFIX}"));
        create_synthetic_frame(false)
            .save(&reference)
            .expect("save reference");
        create_synthetic_frame(false)
            .save(&candidate)
            .expect("save candidate");

        let window = serde_json::json!({
            "Bounds": {
                "Left": 24,
                "Top": 0,
                "Width": 1692,
                "Height": 1800
            },
            "DpiScale": 2.0
        });
        let manifest_path = dir.path().join("ui-parity-manifest.json");
        let manifest = serde_json::json!({
            "SchemaVersion": "easydict.ui-parity.manifest.v1",
            "Scenarios": [{
                "ScenarioId": "settings.services",
                "WindowKind": "settings",
                "SectionId": "services",
                "SectionLabel": "Services",
                "Theme": "system",
                "ScrollPercent": 0.0,
                "ExpandAvailableLanguages": false,
                "ReferenceScreenshot": reference.file_name().and_then(|value| value.to_str()).unwrap(),
                "CandidateScreenshot": candidate.file_name().and_then(|value| value.to_str()).unwrap(),
                "ReferenceWindow": window.clone(),
                "CandidateWindow": window,
                "Regions": [],
                "RequiredSemanticTags": ["DeepLServiceExpander"],
                "ReferenceUiSummary": {
                    "VisibleControlCounts": {},
                    "VisibleAutomationIds": ["DeepLServiceExpander"],
                    "VisibleControlDimensions": {
                        "DeepLServiceExpander": {
                            "Kind": "Button",
                            "Width": "796",
                            "Height": "48"
                        }
                    }
                },
                "CandidateUiSummary": {
                    "VisibleControlCounts": {},
                    "VisibleAutomationIds": ["DeepLServiceExpander"],
                    "VisibleControlDimensions": {
                        "DeepLServiceExpander": {
                            "Kind": "Expander"
                        }
                    }
                }
            }]
        });
        fs::write(&manifest_path, manifest.to_string()).expect("write manifest");
        let output = dir.path().join("out");

        let code = run([
            OsString::from("easydict_ui_parity_analyzer"),
            OsString::from("--screenshot-root"),
            dir.path().as_os_str().to_os_string(),
            OsString::from("--manifest"),
            manifest_path.as_os_str().to_os_string(),
            OsString::from("--output-dir"),
            output.as_os_str().to_os_string(),
        ])
        .expect("analyzer should run");

        assert_eq!(code, 0);
        let report_text =
            fs::read_to_string(output.join("ui-parity-report.json")).expect("report json");
        let report = serde_json::from_str::<Value>(&report_text).expect("report value");
        let scenario = report
            .get("Scenarios")
            .and_then(Value::as_array)
            .and_then(|items| {
                items.iter().find(|item| {
                    item.get("ScenarioId").and_then(Value::as_str) == Some("settings.services")
                })
            })
            .expect("settings.services scenario");

        assert!(scenario
            .get("Metrics")
            .and_then(|metrics| metrics.get("ControlDimensionScoreCap"))
            .is_none_or(Value::is_null));
        assert!(scenario
            .get("Findings")
            .and_then(Value::as_array)
            .is_some_and(|findings| findings.iter().any(|finding| {
                finding.get("Metric").and_then(Value::as_str) == Some("controlDimensionDeltaDips")
                    && finding.get("Severity").and_then(Value::as_str) == Some("warning")
                    && finding.get("LayerHint").and_then(Value::as_str) == Some("evidence_quality")
            })));
    }

    fn fully_aligned_ui_summary() -> UiSummaryComparison {
        UiSummaryComparison {
            score: 99.0,
            control_count_delta_percent: 0.0,
            control_count_deltas: Vec::new(),
            automation_id_jaccard: Some(100.0),
            visible_text_jaccard: Some(100.0),
            missing_reference_visible_texts: Vec::new(),
            extra_candidate_visible_texts: Vec::new(),
            missing_required_semantic_tags: Vec::new(),
            missing_required_visible_texts: Vec::new(),
            missing_required_control_states: Vec::new(),
            missing_control_bounds_evidence: Vec::new(),
            control_dimension_delta_count: 0,
            max_control_dimension_delta_dips: 0.0,
            control_dimension_deltas: Vec::new(),
        }
    }

    #[test]
    fn semantic_contract_floor_flips_settings_scene_to_pass_under_default_gate() {
        // End-to-end guard: a settings scene whose visible text, required tags,
        // control bounds, and window dimensions all match the reference, but whose
        // pixels drift (font anti-aliasing / palette noise), must PASS under the
        // production settings gate (final_effect/settings.*=78,62) because the
        // semantic/absolute-size contract floor lifts the score above 78.
        let dir = tempdir().expect("temp dir");
        let reference = dir.path().join(format!("settings.services{DOTNET_SUFFIX}"));
        let candidate = dir.path().join(format!("settings.services{RUST_SUFFIX}"));
        create_synthetic_frame(false)
            .save(&reference)
            .expect("save reference");
        // Drifted candidate so the raw visual score lands below the pass gate.
        create_synthetic_frame(true)
            .save(&candidate)
            .expect("save candidate");

        let window = serde_json::json!({
            "Bounds": { "Left": 24, "Top": 0, "Width": 1692, "Height": 1826 },
            "DpiScale": 2.0
        });
        let dimensions = serde_json::json!({
            "DeepLServiceExpander": { "Kind": "Expander", "Width": "796", "Height": "48" }
        });
        let manifest_path = dir.path().join("ui-parity-manifest.json");
        let manifest = serde_json::json!({
            "SchemaVersion": "easydict.ui-parity.manifest.v1",
            "Scenarios": [{
                "ScenarioId": "settings.services",
                "WindowKind": "settings",
                "SectionId": "services",
                "SectionLabel": "Services",
                "Theme": "system",
                "ScrollPercent": 0.0,
                "ExpandAvailableLanguages": false,
                "ReferenceScreenshot": reference.file_name().and_then(|value| value.to_str()).unwrap(),
                "CandidateScreenshot": candidate.file_name().and_then(|value| value.to_str()).unwrap(),
                "ReferenceWindow": window.clone(),
                "CandidateWindow": window,
                "Regions": [],
                "RequiredSemanticTags": ["DeepLServiceExpander"],
                "RequiredVisibleTexts": ["DeepL"],
                "ReferenceUiSummary": {
                    "VisibleControlCounts": {},
                    "VisibleAutomationIds": ["DeepLServiceExpander"],
                    "VisibleTexts": ["DeepL"],
                    "VisibleControlDimensions": dimensions.clone()
                },
                "CandidateUiSummary": {
                    "VisibleControlCounts": {},
                    "VisibleAutomationIds": ["DeepLServiceExpander"],
                    "VisibleTexts": ["DeepL"],
                    "VisibleControlDimensions": dimensions
                }
            }]
        });
        fs::write(&manifest_path, manifest.to_string()).expect("write manifest");
        let output = dir.path().join("out");

        let code = run([
            OsString::from("easydict_ui_parity_analyzer"),
            OsString::from("--screenshot-root"),
            dir.path().as_os_str().to_os_string(),
            OsString::from("--manifest"),
            manifest_path.as_os_str().to_os_string(),
            OsString::from("--output-dir"),
            output.as_os_str().to_os_string(),
            OsString::from("--score-gate"),
            OsString::from("final_effect/settings.*=78,62"),
        ])
        .expect("analyzer should run");

        assert_eq!(code, 0);
        let report_text =
            fs::read_to_string(output.join("ui-parity-report.json")).expect("report json");
        let report = serde_json::from_str::<Value>(&report_text).expect("report value");
        let scenario = report
            .get("Scenarios")
            .and_then(Value::as_array)
            .and_then(|items| {
                items.iter().find(|item| {
                    item.get("ScenarioId").and_then(Value::as_str) == Some("settings.services")
                })
            })
            .expect("settings.services scenario");

        // The scene passes only because the contract floor lifted it.
        assert_eq!(
            scenario.get("Status").and_then(Value::as_str),
            Some("pass"),
            "settings scene should pass via the semantic contract floor"
        );
        let visual_score = scenario
            .get("Metrics")
            .and_then(|metrics| metrics.get("VisualScore"))
            .and_then(Value::as_f64)
            .expect("visual score");
        assert!(
            visual_score < 78.0,
            "raw visual score {visual_score} should be below the settings pass gate so the floor is what causes the pass"
        );
        assert_eq!(
            scenario
                .get("Metrics")
                .and_then(|metrics| metrics.get("SemanticContractScoreFloor"))
                .and_then(Value::as_f64),
            Some(SEMANTIC_CONTRACT_SCORE_FLOOR)
        );
        assert!(scenario
            .get("Findings")
            .and_then(Value::as_array)
            .is_some_and(|findings| findings.iter().any(|finding| {
                finding.get("Metric").and_then(Value::as_str) == Some("semanticContractScoreFloor")
                    && finding.get("Severity").and_then(Value::as_str) == Some("info")
            })));
    }

    #[test]
    fn semantic_contract_floor_lifts_when_semantic_and_absolute_size_fully_match() {
        let profile = ScenarioScoringProfile::new(
            "default-semantic",
            0.36,
            0.15,
            0.22,
            0.15,
            0.06,
            0.06,
            70.0,
        );
        let summary = fully_aligned_ui_summary();

        let floor = calculate_semantic_contract_score_floor(
            &profile,
            Some(&summary),
            None,
            100.0,
            100.0,
            100.0,
        );

        assert_eq!(floor, Some(SEMANTIC_CONTRACT_SCORE_FLOOR));

        let mut dpi_rounding = fully_aligned_ui_summary();
        dpi_rounding.score = 83.1;
        dpi_rounding.control_dimension_delta_count = 12;
        dpi_rounding.max_control_dimension_delta_dips = 2.5;
        assert_eq!(
            calculate_semantic_contract_score_floor(
                &profile,
                Some(&dpi_rounding),
                None,
                100.0,
                100.0,
                100.0,
            ),
            Some(SEMANTIC_CONTRACT_SCORE_FLOOR)
        );

        let animation_profile = ScenarioScoringProfile::new(
            "interaction-animation",
            0.26,
            0.08,
            0.40,
            0.14,
            0.04,
            0.08,
            62.0,
        );
        assert_eq!(
            calculate_semantic_contract_score_floor(
                &animation_profile,
                Some(&fully_aligned_ui_summary()),
                None,
                100.0,
                100.0,
                100.0,
            ),
            Some(SEMANTIC_CONTRACT_SCORE_FLOOR)
        );
    }

    #[test]
    fn semantic_contract_floor_is_withheld_when_window_target_is_work_area_limited() {
        let profile = ScenarioScoringProfile::new(
            "default-semantic",
            0.36,
            0.15,
            0.22,
            0.15,
            0.06,
            0.06,
            70.0,
        );
        let summary = fully_aligned_ui_summary();
        let mut manifest = semantic_manifest(
            "settings.services",
            "settings",
            &["DeepLServiceExpander"],
            ui_summary(&[("button", 1)], &["DeepLServiceExpander"]),
            ui_summary(&[("button", 1)], &["DeepLServiceExpander"]),
        );
        manifest.candidate_window_size_audit = Some(ManifestWindowSizeAudit {
            expected_window_dips: Some(ManifestDipSize {
                width: 846.0,
                height: 900.0,
            }),
            actual_window_dips: Some(ManifestDipSize {
                width: 846.0,
                height: 852.0,
            }),
            delta_dips: Some(ManifestDipSize {
                width: 0.0,
                height: -48.0,
            }),
            delta_percent: Some(ManifestDipSize {
                width: 0.0,
                height: -5.33,
            }),
            monitor_work_area_dips: Some(ManifestDipSize {
                width: 1440.0,
                height: 852.0,
            }),
            expected_larger_than_work_area: Some(true),
        });

        assert_eq!(
            calculate_semantic_contract_score_floor(
                &profile,
                Some(&summary),
                Some(&manifest),
                100.0,
                100.0,
                100.0,
            ),
            None
        );
    }

    #[test]
    fn semantic_contract_floor_is_withheld_when_evidence_is_incomplete() {
        let profile = ScenarioScoringProfile::new(
            "default-semantic",
            0.36,
            0.15,
            0.22,
            0.15,
            0.06,
            0.06,
            70.0,
        );

        // Visual-only profile never qualifies for the semantic floor.
        let visual_profile =
            ScenarioScoringProfile::new("default-visual", 0.42, 0.18, 0.24, 0.0, 0.08, 0.08, 70.0);
        assert_eq!(
            calculate_semantic_contract_score_floor(
                &visual_profile,
                Some(&fully_aligned_ui_summary()),
                None,
                100.0,
                100.0,
                100.0,
            ),
            None
        );

        let interaction_effects_profile = ScenarioScoringProfile::new(
            "interaction-effects",
            0.28,
            0.10,
            0.36,
            0.14,
            0.04,
            0.08,
            66.0,
        );
        assert_eq!(
            calculate_semantic_contract_score_floor(
                &interaction_effects_profile,
                Some(&fully_aligned_ui_summary()),
                None,
                100.0,
                100.0,
                100.0,
            ),
            None
        );

        // A capped runtime/control/size dimension blocks the floor.
        assert_eq!(
            calculate_semantic_contract_score_floor(
                &profile,
                Some(&fully_aligned_ui_summary()),
                None,
                98.0,
                100.0,
                100.0,
            ),
            None
        );

        // Missing visible text breaks the contract.
        let mut low_text = fully_aligned_ui_summary();
        low_text.visible_text_jaccard = Some(99.0);
        assert_eq!(
            calculate_semantic_contract_score_floor(
                &profile,
                Some(&low_text),
                None,
                100.0,
                100.0,
                100.0,
            ),
            None
        );

        // A required semantic tag gap breaks the contract.
        let mut missing_tag = fully_aligned_ui_summary();
        missing_tag
            .missing_required_semantic_tags
            .push("DeepLServiceExpander".to_string());
        assert_eq!(
            calculate_semantic_contract_score_floor(
                &profile,
                Some(&missing_tag),
                None,
                100.0,
                100.0,
                100.0,
            ),
            None
        );

        // A control bounds delta beyond the DPI rounding tolerance breaks the contract.
        let mut dimension_drift = fully_aligned_ui_summary();
        dimension_drift.control_dimension_delta_count = 1;
        dimension_drift.max_control_dimension_delta_dips = 3.5;
        assert_eq!(
            calculate_semantic_contract_score_floor(
                &profile,
                Some(&dimension_drift),
                None,
                100.0,
                100.0,
                100.0,
            ),
            None
        );
    }

    #[test]
    fn manifest_fallback_reference_source_is_reported_as_evidence_quality() {
        let dir = tempdir().expect("temp dir");
        let reference = dir.path().join(format!("settings.tabs{DOTNET_SUFFIX}"));
        let candidate = dir.path().join(format!("settings.tabs{RUST_SUFFIX}"));
        create_synthetic_frame(false)
            .save(&reference)
            .expect("save reference");
        create_synthetic_frame(false)
            .save(&candidate)
            .expect("save candidate");

        let manifest_path = dir.path().join("ui-parity-manifest.json");
        let manifest = serde_json::json!({
            "SchemaVersion": "easydict.ui-parity.manifest.v1",
            "Scenarios": [{
                "ScenarioId": "settings.tabs",
                "WindowKind": "settings",
                "SectionId": "settings",
                "SectionLabel": "settings.tabs",
                "Theme": "system",
                "ScrollPercent": 0.0,
                "ExpandAvailableLanguages": false,
                "ReferenceScreenshot": reference.file_name().and_then(|value| value.to_str()).unwrap(),
                "CandidateScreenshot": candidate.file_name().and_then(|value| value.to_str()).unwrap(),
                "ReferenceSourceKind": "fallback-settings-general-schema",
                "ReferenceSourcePath": "artifacts/ui-screenshots/settings-general-schema-20260603-200127/settings.tabs-dotnet-winui-reference.png",
                "ReferenceSourceLastWriteTimeUtc": "2026-06-03T20:01:27Z",
                "ReferenceSourceIsFallback": true
            }]
        });
        fs::write(&manifest_path, manifest.to_string()).expect("write manifest");
        let output = dir.path().join("out");

        let code = run([
            OsString::from("easydict_ui_parity_analyzer"),
            OsString::from("--screenshot-root"),
            dir.path().as_os_str().to_os_string(),
            OsString::from("--manifest"),
            manifest_path.as_os_str().to_os_string(),
            OsString::from("--output-dir"),
            output.as_os_str().to_os_string(),
        ])
        .expect("analyzer should run");

        assert_eq!(code, 0);
        let report_text =
            fs::read_to_string(output.join("ui-parity-report.json")).expect("report json");
        let report = serde_json::from_str::<Value>(&report_text).expect("report value");
        let scenario = report
            .get("Scenarios")
            .and_then(Value::as_array)
            .and_then(|items| {
                items.iter().find(|item| {
                    item.get("ScenarioId").and_then(Value::as_str) == Some("settings.tabs")
                })
            })
            .expect("settings.tabs scenario");
        assert!(scenario
            .get("Findings")
            .and_then(Value::as_array)
            .is_some_and(|findings| findings.iter().any(|finding| {
                finding.get("Metric").and_then(Value::as_str) == Some("referenceSourceIsFallback")
                    && finding.get("LayerHint").and_then(Value::as_str) == Some("evidence_quality")
            })));

        let markdown =
            fs::read_to_string(output.join("ui-parity-report.md")).expect("report markdown");
        assert!(markdown.contains("Reference source: fallback-settings-general-schema (fallback)"));
        assert!(markdown.contains("regenerate a curated .NET WinUI baseline"));
    }

    #[test]
    fn manifest_window_size_audit_reports_expected_actual_dip_and_work_area_clamp() {
        let dir = tempdir().expect("temp dir");
        let reference = dir.path().join(format!("settings.views{DOTNET_SUFFIX}"));
        let candidate = dir.path().join(format!("settings.views{RUST_SUFFIX}"));
        create_synthetic_frame(false)
            .save(&reference)
            .expect("save reference");
        imageops::resize(
            &create_synthetic_frame(false),
            520,
            360,
            FilterType::Lanczos3,
        )
        .save(&candidate)
        .expect("save candidate");

        let manifest_path = dir.path().join("ui-parity-manifest.json");
        let manifest = serde_json::json!({
            "SchemaVersion": "easydict.ui-parity.manifest.v1",
            "Scenarios": [{
                "ScenarioId": "settings.views",
                "WindowKind": "settings",
                "SectionId": "settings",
                "SectionLabel": "settings.views",
                "Theme": "system",
                "ScrollPercent": 0.0,
                "ExpandAvailableLanguages": false,
                "ReferenceScreenshot": reference.file_name().and_then(|value| value.to_str()).unwrap(),
                "CandidateScreenshot": candidate.file_name().and_then(|value| value.to_str()).unwrap(),
                "ReferenceWindow": {
                    "Bounds": { "Left": 31, "Top": 30, "Width": 846, "Height": 913 },
                    "DpiScale": 1.0,
                    "Dpi": 96
                },
                "CandidateWindow": {
                    "Bounds": { "Left": 594, "Top": 0, "Width": 1692, "Height": 1704 },
                    "DpiScale": 2.0,
                    "Dpi": 192
                },
                "CandidateExpectedWindowDips": {
                    "Width": 846,
                    "Height": 913
                },
                "CandidateWindowSizeAudit": {
                    "ExpectedWindowDips": { "Width": 846, "Height": 913 },
                    "ActualWindowDips": { "Width": 846, "Height": 852 },
                    "DeltaDips": { "Width": 0, "Height": -61 },
                    "DeltaPercent": { "Width": 0, "Height": -6.68 },
                    "MonitorWorkAreaDips": { "Width": 1440, "Height": 852 },
                    "ExpectedLargerThanWorkArea": true
                },
                "Regions": [],
                "RequiredSemanticTags": []
            }]
        });
        fs::write(&manifest_path, manifest.to_string()).expect("write manifest");
        let output = dir.path().join("out");

        let code = run([
            OsString::from("easydict_ui_parity_analyzer"),
            OsString::from("--screenshot-root"),
            dir.path().as_os_str().to_os_string(),
            OsString::from("--manifest"),
            manifest_path.as_os_str().to_os_string(),
            OsString::from("--output-dir"),
            output.as_os_str().to_os_string(),
        ])
        .expect("analyzer should run");

        assert_eq!(code, 0);
        let markdown =
            fs::read_to_string(output.join("ui-parity-report.md")).expect("report markdown");
        assert!(markdown.contains("Window DIP delta"));
        assert!(markdown.contains("DPI delta"));
        assert!(markdown.contains("Window target"));
        assert!(markdown.contains("expected 846.00x913.00 DIP"));
        assert!(markdown.contains("actual 846.00x852.00 DIP"));
        assert!(markdown.contains("work area 1440.00x852.00 DIP"));
        assert!(markdown.contains("effective target 846.00x852.00 DIP"));
        assert!(markdown.contains("expected target exceeds monitor work area"));
        assert!(markdown.contains("expectedWindowDipsExceedsWorkArea"));
        assert!(markdown.contains("referenceCandidateDpiScaleDelta"));
        assert!(markdown.contains("different DPI scales"));
        assert!(!markdown.contains("candidateWindowDipSizeDelta"));
        assert!(!markdown.contains("absoluteSizeScoreCap"));

        let report_text =
            fs::read_to_string(output.join("ui-parity-report.json")).expect("report json");
        assert!(report_text.contains("\"CandidateWindowSizeAudit\""));
        assert!(report_text.contains("\"expectedWindowDipsExceedsWorkArea\""));
        let report = serde_json::from_str::<Value>(&report_text).expect("report value");
        let scenario = report
            .get("Scenarios")
            .and_then(Value::as_array)
            .and_then(|items| {
                items.iter().find(|item| {
                    item.get("ScenarioId").and_then(Value::as_str) == Some("settings.views")
                })
            })
            .expect("settings.views scenario");
        assert_eq!(
            scenario
                .get("Metrics")
                .and_then(|metrics| metrics.get("DpiScaleDelta"))
                .and_then(Value::as_f64),
            Some(1.0)
        );
        assert_eq!(
            scenario
                .get("Metrics")
                .and_then(|metrics| metrics.get("AbsoluteImageSizeDeltaPercent"))
                .and_then(Value::as_f64),
            Some(100.0)
        );
        assert!(scenario
            .get("Metrics")
            .and_then(|metrics| metrics.get("AbsoluteSizeScoreCap"))
            .and_then(Value::as_f64)
            .is_none());
        assert!(scenario
            .get("Findings")
            .and_then(Value::as_array)
            .is_some_and(|findings| findings.iter().any(|finding| {
                finding.get("Metric").and_then(Value::as_str)
                    == Some("referenceCandidateDpiScaleDelta")
                    && finding.get("LayerHint").and_then(Value::as_str) == Some("evidence_quality")
            })));
        assert!(scenario
            .get("Findings")
            .and_then(Value::as_array)
            .is_some_and(|findings| !findings.iter().any(|finding| {
                finding.get("Metric").and_then(Value::as_str) == Some("absoluteSizeScoreCap")
            })));
        assert!(!report_text.contains("\"candidateWindowDipSizeDelta\""));
    }

    #[test]
    fn manifest_window_size_audit_caps_score_on_small_fixed_window_dip_delta() {
        let dir = tempdir().expect("temp dir");
        let reference = dir.path().join(format!("settings.about{DOTNET_SUFFIX}"));
        let candidate = dir.path().join(format!("settings.about{RUST_SUFFIX}"));
        create_synthetic_frame(false)
            .save(&reference)
            .expect("save reference");
        create_synthetic_frame(false)
            .save(&candidate)
            .expect("save candidate");

        let manifest_path = dir.path().join("ui-parity-manifest.json");
        let manifest = serde_json::json!({
            "SchemaVersion": "easydict.ui-parity.manifest.v1",
            "Scenarios": [{
                "ScenarioId": "settings.about",
                "WindowKind": "settings",
                "SectionId": "settings",
                "SectionLabel": "settings.about",
                "Theme": "system",
                "ScrollPercent": 0.0,
                "ExpandAvailableLanguages": false,
                "ReferenceScreenshot": reference.file_name().and_then(|value| value.to_str()).unwrap(),
                "CandidateScreenshot": candidate.file_name().and_then(|value| value.to_str()).unwrap(),
                "CandidateWindow": {
                    "Bounds": { "Left": 40, "Top": 40, "Width": 846, "Height": 904 },
                    "DpiScale": 1.0,
                    "Dpi": 96
                },
                "CandidateExpectedWindowDips": {
                    "Width": 846,
                    "Height": 913
                },
                "CandidateWindowSizeAudit": {
                    "ExpectedWindowDips": { "Width": 846, "Height": 913 },
                    "ActualWindowDips": { "Width": 846, "Height": 904 },
                    "DeltaDips": { "Width": 0, "Height": -9 },
                    "DeltaPercent": { "Width": 0, "Height": -0.99 },
                    "MonitorWorkAreaDips": { "Width": 1920, "Height": 1032 },
                    "ExpectedLargerThanWorkArea": false
                },
                "Regions": [],
                "RequiredSemanticTags": []
            }]
        });
        fs::write(&manifest_path, manifest.to_string()).expect("write manifest");
        let output = dir.path().join("out");

        let code = run([
            OsString::from("easydict_ui_parity_analyzer"),
            OsString::from("--screenshot-root"),
            dir.path().as_os_str().to_os_string(),
            OsString::from("--manifest"),
            manifest_path.as_os_str().to_os_string(),
            OsString::from("--output-dir"),
            output.as_os_str().to_os_string(),
        ])
        .expect("analyzer should run");

        assert_eq!(code, 0);
        let report_text =
            fs::read_to_string(output.join("ui-parity-report.json")).expect("report json");
        let report = serde_json::from_str::<Value>(&report_text).expect("report value");
        let scenario = report
            .get("Scenarios")
            .and_then(Value::as_array)
            .and_then(|items| {
                items.iter().find(|item| {
                    item.get("ScenarioId").and_then(Value::as_str) == Some("settings.about")
                })
            })
            .expect("settings.about scenario");

        assert_eq!(scenario.get("Status").and_then(Value::as_str), Some("fail"));
        assert_eq!(scenario.get("Score").and_then(Value::as_f64), Some(69.0));
        assert_eq!(
            scenario
                .get("Metrics")
                .and_then(|metrics| metrics.get("AbsoluteSizeScoreCap"))
                .and_then(Value::as_f64),
            Some(69.0)
        );
        assert!(scenario
            .get("Findings")
            .and_then(Value::as_array)
            .is_some_and(|findings| findings.iter().any(|finding| {
                finding.get("Metric").and_then(Value::as_str) == Some("candidateWindowDipSizeDelta")
                    && finding.get("Severity").and_then(Value::as_str) == Some("error")
            })));

        let markdown =
            fs::read_to_string(output.join("ui-parity-report.md")).expect("report markdown");
        assert!(markdown.contains("Size cap"));
        assert!(markdown.contains("candidateWindowDipSizeDelta"));
        assert!(markdown.contains("actual 846.00x904.00 DIP"));
    }

    #[test]
    fn manifest_reference_window_size_audit_caps_score_when_reference_size_is_wrong() {
        let dir = tempdir().expect("temp dir");
        let reference = dir.path().join(format!("settings.about{DOTNET_SUFFIX}"));
        let candidate = dir.path().join(format!("settings.about{RUST_SUFFIX}"));
        create_synthetic_frame(false)
            .save(&reference)
            .expect("save reference");
        create_synthetic_frame(false)
            .save(&candidate)
            .expect("save candidate");

        let manifest_path = dir.path().join("ui-parity-manifest.json");
        let manifest = serde_json::json!({
            "SchemaVersion": "easydict.ui-parity.manifest.v1",
            "Scenarios": [{
                "ScenarioId": "settings.about.reference-size",
                "WindowKind": "settings",
                "SectionId": "settings",
                "SectionLabel": "settings.about",
                "Theme": "system",
                "ScrollPercent": 0.0,
                "ExpandAvailableLanguages": false,
                "ReferenceScreenshot": reference.file_name().and_then(|value| value.to_str()).unwrap(),
                "CandidateScreenshot": candidate.file_name().and_then(|value| value.to_str()).unwrap(),
                "ReferenceExpectedWindowDips": {
                    "Width": 846,
                    "Height": 913
                },
                "ReferenceWindowSizeAudit": {
                    "ExpectedWindowDips": { "Width": 846, "Height": 913 },
                    "ActualWindowDips": { "Width": 1444, "Height": 909.5 },
                    "DeltaDips": { "Width": 598, "Height": -3.5 },
                    "DeltaPercent": { "Width": 70.69, "Height": -0.38 },
                    "MonitorWorkAreaDips": { "Width": 1440, "Height": 900 },
                    "ExpectedLargerThanWorkArea": true
                },
                "CandidateExpectedWindowDips": {
                    "Width": 846,
                    "Height": 913
                },
                "CandidateWindowSizeAudit": {
                    "ExpectedWindowDips": { "Width": 846, "Height": 913 },
                    "ActualWindowDips": { "Width": 846, "Height": 913 },
                    "DeltaDips": { "Width": 0, "Height": 0 },
                    "DeltaPercent": { "Width": 0, "Height": 0 },
                    "MonitorWorkAreaDips": { "Width": 1440, "Height": 900 },
                    "ExpectedLargerThanWorkArea": true
                },
                "Regions": [],
                "RequiredSemanticTags": []
            }]
        });
        fs::write(&manifest_path, manifest.to_string()).expect("write manifest");
        let output = dir.path().join("out");

        let code = run([
            OsString::from("easydict_ui_parity_analyzer"),
            OsString::from("--screenshot-root"),
            dir.path().as_os_str().to_os_string(),
            OsString::from("--manifest"),
            manifest_path.as_os_str().to_os_string(),
            OsString::from("--output-dir"),
            output.as_os_str().to_os_string(),
        ])
        .expect("analyzer should run");

        assert_eq!(code, 0);
        let report_text =
            fs::read_to_string(output.join("ui-parity-report.json")).expect("report json");
        let report = serde_json::from_str::<Value>(&report_text).expect("report value");
        let scenario = report
            .get("Scenarios")
            .and_then(Value::as_array)
            .and_then(|items| {
                items.iter().find(|item| {
                    item.get("ScenarioId").and_then(Value::as_str)
                        == Some("settings.about.reference-size")
                })
            })
            .expect("settings.about.reference-size scenario");

        assert_eq!(scenario.get("Status").and_then(Value::as_str), Some("fail"));
        assert_eq!(
            scenario
                .get("Metrics")
                .and_then(|metrics| metrics.get("AbsoluteSizeScoreCap"))
                .and_then(Value::as_f64),
            Some(69.0)
        );
        assert!(scenario
            .get("Findings")
            .and_then(Value::as_array)
            .is_some_and(|findings| findings.iter().any(|finding| {
                finding.get("Metric").and_then(Value::as_str) == Some("referenceWindowDipSizeDelta")
                    && finding.get("Severity").and_then(Value::as_str) == Some("error")
            })));

        let markdown =
            fs::read_to_string(output.join("ui-parity-report.md")).expect("report markdown");
        assert!(markdown.contains("Reference window target"));
        assert!(markdown.contains("actual 1444.00x909.50 DIP"));
        assert!(markdown.contains("referenceWindowDipSizeDelta"));
    }

    #[test]
    fn help_returns_zero_without_process_exit() {
        let code = run([
            OsString::from("easydict_ui_parity_analyzer"),
            OsString::from("--help"),
        ])
        .expect("help should be handled");
        assert_eq!(code, 0);
    }

    #[test]
    fn unknown_argument_returns_one_without_process_exit() {
        let code = run([
            OsString::from("easydict_ui_parity_analyzer"),
            OsString::from("--unknown-argument"),
        ])
        .expect("parse errors should be handled");
        assert_eq!(code, 1);
    }

    #[test]
    fn screenshot_summary_generates_gallery_and_priority_markdown() {
        let dir = tempdir().expect("temp dir");
        let root = dir.path().join("screenshots");
        let diffs = root.join("visual-diffs");
        let baselines = root.join("baseline-candidates");
        fs::create_dir_all(&diffs).expect("diff dir");
        fs::create_dir_all(&baselines).expect("baseline dir");
        create_synthetic_frame(false)
            .save(root.join("regular.png"))
            .expect("regular screenshot");
        create_synthetic_frame(true)
            .save(diffs.join("main_diff.png"))
            .expect("diff screenshot");
        RgbaImage::new(12, 12)
            .save(root.join("tiny.png"))
            .expect("tiny screenshot");
        create_synthetic_frame(false)
            .save(baselines.join("settings.png"))
            .expect("baseline screenshot");
        let summary_path = dir.path().join("summary.md");

        let code = run([
            OsString::from("easydict_ui_parity_analyzer"),
            OsString::from("screenshot-summary"),
            OsString::from("--screenshot-root"),
            root.as_os_str().to_os_string(),
            OsString::from("--artifact-name"),
            OsString::from("ui-screenshots-test"),
            OsString::from("--summary-path"),
            summary_path.as_os_str().to_os_string(),
        ])
        .expect("summary should run");

        assert_eq!(code, 0);
        let summary = fs::read_to_string(summary_path).expect("summary markdown");
        assert!(summary.contains("Generated **4** screenshot(s)."));
        assert!(summary.contains("data:image/jpeg;base64"));
        assert!(summary.contains("Review priority"));
        assert!(summary.contains("visual diff"));
        assert!(summary.contains("suspicious screenshot dimensions"));
        assert!(summary.contains("baseline candidate"));
        assert!(root.join("ui-screenshot-gallery.jpg").exists());
    }

    #[test]
    fn triage_summarizes_reports_and_skips_malformed_json() {
        let dir = tempdir().expect("temp dir");
        let artifact_root = dir.path().join("artifacts");
        let report_dir = artifact_root.join("main-effects").join("ui-parity");
        let bad_report_dir = artifact_root.join("bad-artifact").join("ui-parity");
        fs::create_dir_all(&report_dir).expect("report dir");
        fs::create_dir_all(&bad_report_dir).expect("bad report dir");

        let report = serde_json::json!({
            "GeneratedAtUtc": "1.000Z",
            "ScreenshotRoot": artifact_root.join("main-effects").display().to_string(),
            "Summary": {
                "TotalScenarios": 1,
                "PassCount": 0,
                "WarnCount": 0,
                "FailCount": 1,
                "AverageScore": 55.0,
                "MinimumScore": 55.0
            },
            "Scenarios": [{
                "ScenarioId": "main.initial",
                "Status": "fail",
                "Score": 55.0,
                "Gate": { "PassScore": 85.0, "WarnScore": 70.0 },
                "Metrics": {
                    "SemanticScore": 66.0,
                    "MaxControlDimensionDeltaDips": 4.0
                },
                "Regions": [{
                    "Name": "result-list",
                    "Score": 40.0
                }],
                "Findings": [{
                    "Severity": "warning",
                    "LayerHint": "rs_easydict_wrapper",
                    "Metric": "region.result-list.score",
                    "Value": 40.0,
                    "Message": "Region `result-list` is below the profile warning score."
                }]
            }]
        });
        fs::write(
            report_dir.join("ui-parity-report.json"),
            serde_json::to_string(&report).expect("serialize report"),
        )
        .expect("write report");
        fs::write(bad_report_dir.join("ui-parity-report.json"), "").expect("write bad report");
        let output = dir.path().join("triage");

        let code = run([
            OsString::from("easydict_ui_parity_analyzer"),
            OsString::from("triage"),
            OsString::from("--artifact-root"),
            artifact_root.as_os_str().to_os_string(),
            OsString::from("--output-dir"),
            output.as_os_str().to_os_string(),
            OsString::from("--top"),
            OsString::from("5"),
        ])
        .expect("triage should run");

        assert_eq!(code, 0);
        let triage_json =
            fs::read_to_string(output.join("ui-parity-triage.json")).expect("triage json");
        let triage = serde_json::from_str::<Value>(&triage_json).expect("triage value");
        assert_eq!(triage.get("ReportCount").and_then(Value::as_u64), Some(1));
        assert_eq!(
            triage
                .get("NextIterationQueue")
                .and_then(Value::as_array)
                .and_then(|items| items.first())
                .and_then(|item| item.get("ScenarioId"))
                .and_then(Value::as_str),
            Some("main.initial")
        );
        assert_eq!(
            triage
                .get("SkippedReports")
                .and_then(Value::as_array)
                .map(Vec::len),
            Some(1)
        );

        let markdown =
            fs::read_to_string(output.join("ui-parity-triage.md")).expect("triage markdown");
        assert!(markdown.contains("Next Iteration Queue"));
        assert!(markdown.contains("result-list"));
        assert!(markdown.contains("Skipped Reports"));
    }

    #[test]
    fn code_parity_reports_theme_and_layout_drift_without_minimal_color_overwrite() {
        let dir = tempdir().expect("temp dir");
        let repo = dir.path();
        let dotnet_views = repo.join("dotnet/src/Easydict.WinUI/Views");
        let dotnet_themes = repo.join("dotnet/src/Easydict.WinUI/Themes");
        let rust_src = repo.join("rs/crates/easydict_app/src");
        fs::create_dir_all(&dotnet_views).expect("dotnet views dir");
        fs::create_dir_all(&dotnet_themes).expect("dotnet themes dir");
        fs::create_dir_all(&rust_src).expect("rust src dir");

        fs::write(
            dotnet_views.join("MainPage.xaml"),
            r#"
<Page>
  <Button AutomationProperties.AutomationId="ActionButton" Width="100" Height="40" />
  <TextBox AutomationProperties.AutomationId="EndpointBox" Width="450" />
  <TextBlock AutomationProperties.AutomationId="AboutHeaderText" FontSize="18" />
</Page>
"#,
        )
        .expect("write main xaml");
        fs::write(
            dotnet_views.join("SettingsPage.xaml"),
            r#"<Page><StackPanel AutomationProperties.AutomationId="EnabledServicesSection" Spacing="12" /></Page>"#,
        )
        .expect("write settings xaml");
        for name in [
            "MiniWindow.xaml",
            "FixedWindow.xaml",
            "PopButtonWindow.xaml",
        ] {
            fs::write(dotnet_views.join(name), "<Page />").expect("write empty xaml");
        }

        fs::write(
            dotnet_themes.join("Colors.xaml"),
            r##"
<ResourceDictionary>
  <ResourceDictionary x:Key="Light">
    <Color x:Key="FloatingInputBackgroundColor">#F1F4F8</Color>
    <Color x:Key="SettingsTabBackgroundColor">#00FFFFFF</Color>
    <Color x:Key="QueryTextColor">#262626</Color>
    <Color x:Key="ButtonHoverColor">#EEF3F8</Color>
    <Color x:Key="ServiceResultHeaderForegroundColor">#1F2328</Color>
  </ResourceDictionary>
  <ResourceDictionary x:Key="Dark">
    <Color x:Key="FloatingInputBackgroundColor">#000000</Color>
  </ResourceDictionary>
</ResourceDictionary>
"##,
        )
        .expect("write colors");
        fs::write(
            dotnet_themes.join("SettingsPageResources.xaml"),
            r#"<ResourceDictionary />"#,
        )
        .expect("write settings resources");
        fs::write(
            dotnet_themes.join("MinimalResources.xaml"),
            r##"
<ResourceDictionary>
  <Color x:Key="FloatingInputBackgroundColor">#FFFFFF</Color>
</ResourceDictionary>
"##,
        )
        .expect("write minimal resources");

        fs::write(
            rust_src.join("theme.rs"),
            r#"
fn easydict_light() -> ThemeTokens {
    ThemeTokens {
        accent: AccentPalette {
            base: Color::rgb(0, 120, 212),
            light_1: Color::rgb(0, 0, 0),
            light_2: Color::rgb(0, 0, 0),
            dark_1: Color::rgb(0, 0, 0),
            dark_2: Color::rgb(0, 0, 0),
        },
        typography: TypographyTokens {},
        floating_input_surface: Color::rgb(241, 244, 248),
        tile_surface: Color::rgba(255, 255, 255, 0),
        button_hover: Color::rgb(241, 244, 248),
        result_header_foreground: Color::rgb(31, 35, 40),
        text_primary: Color::rgb(38, 38, 38),
    }
}
fn easydict_dark() -> ThemeTokens { easydict_light() }
"#,
        )
        .expect("write theme rs");
        fs::write(
            rust_src.join("ui.rs"),
            r#"
fn view() {
    let _ = button(text("Go")).width(Length::Fixed(120.0)).height(Length::Fixed(40.0)).id("ActionButton");
    let _ = fixed_width_field(
        "EndpointField",
        450,
        column((
            text_editor(endpoint.clone())
                .id("EndpointBox")
                .placeholder("https://example.test")
                .on_input(Message::EndpointChanged),
            combo_box(items)
                .width(Length::Fixed(200))
                .id("NeighborCombo"),
        )),
    );
    let _ = column![].spacing(12).id("EnabledServicesSection");
}
"#,
        )
        .expect("write ui rs");

        let output = repo.join("out");
        let code = run([
            OsString::from("easydict_ui_parity_analyzer"),
            OsString::from("code-parity"),
            OsString::from("--repo-root"),
            repo.as_os_str().to_os_string(),
            OsString::from("--output-dir"),
            output.as_os_str().to_os_string(),
            OsString::from("--top"),
            OsString::from("20"),
        ])
        .expect("code parity should run");

        assert_eq!(code, 0);
        let report_json =
            fs::read_to_string(output.join("ui-code-parity.json")).expect("code parity json");
        let report = serde_json::from_str::<Value>(&report_json).expect("code parity value");
        let summary = report.get("Summary").expect("summary");
        assert_eq!(
            summary.get("ThemeDriftCount").and_then(Value::as_u64),
            Some(1)
        );
        assert_eq!(
            summary.get("ThemeMissingCount").and_then(Value::as_u64),
            Some(0)
        );
        assert_eq!(
            summary.get("LayoutDriftCount").and_then(Value::as_u64),
            Some(1)
        );
        assert_eq!(
            summary.get("LayoutMissingCount").and_then(Value::as_u64),
            Some(1)
        );

        let theme_keys = report
            .get("ThemeComparisons")
            .and_then(Value::as_array)
            .expect("theme comparisons")
            .iter()
            .filter_map(|item| item.get("DotnetKey").and_then(Value::as_str))
            .collect::<Vec<_>>();
        assert_eq!(theme_keys, vec!["ButtonHoverColor"]);
        assert!(!theme_keys.contains(&"FloatingInputBackgroundColor"));
        assert!(!theme_keys.contains(&"SettingsTabBackgroundColor"));
        assert!(!theme_keys.contains(&"ServiceResultHeaderForegroundColor"));

        let layout = report
            .get("LayoutComparisons")
            .and_then(Value::as_array)
            .expect("layout comparisons");
        assert!(layout.iter().any(|item| {
            item.get("Id").and_then(Value::as_str) == Some("ActionButton")
                && item.get("Property").and_then(Value::as_str) == Some("Width")
                && item.get("Status").and_then(Value::as_str) == Some("drift")
        }));
        assert!(layout.iter().any(|item| {
            item.get("Id").and_then(Value::as_str) == Some("AboutHeaderText")
                && item.get("Property").and_then(Value::as_str) == Some("FontSize")
                && item.get("Status").and_then(Value::as_str) == Some("missing")
        }));
        assert!(!layout.iter().any(|item| {
            item.get("Id").and_then(Value::as_str) == Some("EndpointBox")
                && item.get("Property").and_then(Value::as_str) == Some("Width")
        }));
        let buckets = report
            .get("LayoutGapBuckets")
            .and_then(Value::as_array)
            .expect("layout gap buckets");
        assert!(buckets.iter().any(|item| {
            item.get("Area").and_then(Value::as_str) == Some("About")
                && item.get("Total").and_then(Value::as_u64) == Some(1)
        }));
        let components = report
            .get("LayoutGapComponents")
            .and_then(Value::as_array)
            .expect("layout gap components");
        assert!(components.iter().any(|item| {
            item.get("Id").and_then(Value::as_str) == Some("ActionButton")
                && item.get("Total").and_then(Value::as_u64) == Some(1)
        }));

        let markdown =
            fs::read_to_string(output.join("ui-code-parity.md")).expect("code parity markdown");
        assert!(markdown.contains("UI Code Parity Report"));
        assert!(markdown.contains("Layout Gap Buckets"));
        assert!(markdown.contains("Top Layout Gap Components"));
        assert!(markdown.contains("ButtonHoverColor"));
        assert!(markdown.contains("ActionButton"));
    }

    #[test]
    fn code_parity_reports_interaction_and_tray_contracts() {
        let dir = tempdir().expect("temp dir");
        let repo = dir.path();
        let ui_tests = repo.join("rs/crates/easydict_app/tests");
        let platform_src = repo.join("lib/winfluent-rs/crates/win_fluent/src");
        let dotnet_services = repo.join("dotnet/src/Easydict.WinUI/Services");
        fs::create_dir_all(&ui_tests).expect("ui tests dir");
        fs::create_dir_all(&platform_src).expect("platform src dir");
        fs::create_dir_all(&dotnet_services).expect("dotnet services dir");

        fs::write(
            ui_tests.join("ui_contract.rs"),
            r#"
fn interaction_contracts() {
    let _ = PreviewScenario::PrimaryHover;
    assert_control_contains(&primary_hover, "TranslateButton", "hovered=true");
    let _ = PreviewScenario::PrimaryPressed;
    assert_control_contains(&primary_pressed, "TranslateButton", "pressed=true");
}
"#,
        )
        .expect("write ui contract");
        fs::write(
            ui_tests.join("quick_translate_behavior.rs"),
            r#"
fn default_tray_menu_covers_migration_contract() {
    assert!(icon_path.ends_with("AppIcon.ico"));
    assert_eq!(menu.default_item_id.as_deref(), Some(TRAY_SHOW_MAIN));
    assert_eq!(ids, vec![
            TRAY_SHOW_MAIN,
            "browser-support",
            TRAY_EXIT,
    ]);
}
"#,
        )
        .expect("write quick behavior");
        fs::write(
            platform_src.join("platform.rs"),
            r#"
pub struct TrayMenu<Message> {
    pub tooltip: String,
    pub items: Vec<TrayMenuItem<Message>>,
}
impl<Message> TrayMenu<Message> {}
pub struct TrayMenuItem<Message> {
    pub id: String,
    pub label: String,
    pub children: Vec<TrayMenuItem<Message>>,
}
impl<Message> TrayMenuItem<Message> {}
"#,
        )
        .expect("write platform");
        fs::write(
            dotnet_services.join("TrayIconService.cs"),
            r#"
private static void SetTip(MenuFlyoutItem item) =>
    ToolTipService.SetToolTip(item, item.Text);
private MenuFlyout CreateContextMenu()
{
    var presenterStyle = new Style(typeof(MenuFlyoutPresenter));
    presenterStyle.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 300d));
    var showItem = new MenuFlyoutItem { Text = L("TrayShow") };
    SetTip(showItem);
    menu.Items.Add(CreateBrowserSupportSubmenu());
    var exitItem = new MenuFlyoutItem { Text = L("TrayExit") };
}
private MenuFlyoutSubItem CreateBrowserSupportSubmenu()
{
    var browserMenu = new MenuFlyoutSubItem { Text = L("TrayBrowserSupport") };
    SetTip(browserMenu);
    return browserMenu;
}
"#,
        )
        .expect("write tray service");

        let options = CodeParityOptions {
            repo_root: repo.to_path_buf(),
            output_dir: repo.join("out"),
            dotnet_xaml: Vec::new(),
            dotnet_resources: Vec::new(),
            rust_ui: repo.join("rs/crates/easydict_app/src/ui.rs"),
            rust_theme: repo.join("rs/crates/easydict_app/src/theme.rs"),
            context_lines: 18,
            top: 20,
            fail_on_drift: false,
        };

        let checks = code_interaction_contracts(&options).expect("interaction contracts");
        let primary = checks
            .iter()
            .find(|item| item.contract.contains("main primary action"))
            .expect("primary action contract");
        assert_eq!(primary.status, "pass");
        assert!(primary.rust_evidence.contains("ui_contract.rs"));

        let tooltip = checks
            .iter()
            .find(|item| item.contract.contains("hover tooltips"))
            .expect("tray tooltip contract");
        assert_eq!(tooltip.status, "missing");
        assert!(tooltip.rust_evidence.contains("no per-item tooltip field"));

        let min_width = checks
            .iter()
            .find(|item| item.contract.contains("minimum width"))
            .expect("tray minimum width contract");
        assert_eq!(min_width.status, "missing");
        assert!(min_width
            .rust_evidence
            .contains("no presenter/min_width field"));
    }

    #[test]
    fn code_parity_compares_xaml_thickness_with_rust_edges_by_side() {
        assert_eq!(
            compare_code_property_value(
                "Width",
                "{Binding ViewportWidth, ElementName=MainScrollViewer}",
                Some("Length::Fill"),
            ),
            "pass"
        );
        assert_eq!(
            compare_code_property_value(
                "Padding",
                "12,5,40,5",
                Some(
                    r#"Edges {
                        top: 5,
                        right: 40,
                        bottom: 5,
                        left: 12,
                    }"#,
                ),
            ),
            "pass"
        );
        assert_eq!(
            compare_code_property_value(
                "Padding",
                "12,5,40,5",
                Some(
                    r#"Edges {
                        top: 5,
                        right: 28,
                        bottom: 5,
                        left: 12,
                    }"#,
                ),
            ),
            "drift"
        );

        let theme = CodeThemeFacts {
            colors: BTreeMap::new(),
            metrics: BTreeMap::from([("FloatingInputPadding".to_string(), "12,10".to_string())]),
        };
        let resolved =
            resolve_code_layout_value("Padding", "{ThemeResource FloatingInputPadding}", &theme);
        assert_eq!(resolved, "12,10");
        assert_eq!(
            compare_code_property_value("Padding", &resolved, Some("12,10")),
            "pass"
        );

        let candidate_resolved =
            resolve_code_layout_value("Padding", "{ThemeResource FloatingInputPadding}", &theme);
        assert_eq!(
            compare_code_property_value("Padding", &resolved, Some(&candidate_resolved)),
            "pass"
        );
    }

    #[test]
    fn code_parity_synthesizes_llm_provider_descriptor_controls() {
        let source = r#"
fn llm_provider_descriptors() -> [LlmProviderDescriptor; 1] {
    [
        LlmProviderDescriptor {
            service_id: "custom-openai",
            title: "Custom OpenAI Compatible",
            expander_id: "CustomOpenAIServiceExpander",
            status_id: "CustomOpenAIStatusText",
            key_header_id: "CustomOpenAIKeyHeaderText",
            key_box_id: "CustomOpenAIKeyBox",
            key_reveal_id: "CustomOpenAIKeyRevealButton",
            key_label: "API Key (Optional)",
            key_placeholder: "Enter API key if required",
            endpoint_box_id: Some("CustomOpenAIEndpointBox"),
            endpoint_placeholder: "https://your-api.example.com/v1/chat/completions",
            model_box_id: "CustomOpenAIModelBox",
            test_button_id: "TestCustomOpenAIButton",
            description: "Configure any OpenAI-compatible API endpoint.",
            default_endpoint: "",
            default_model: "gpt-3.5-turbo",
            model_options: &["gpt-3.5-turbo"],
        },
    ]
}
"#;
        let mut facts = BTreeMap::new();

        add_rust_llm_provider_descriptor_facts(
            "rs/crates/easydict_app/src/ui.rs",
            source,
            &mut facts,
        );

        let key_box = facts.get("CustomOpenAIKeyBox").expect("key box fact");
        assert_eq!(key_box.kind, "text_editor");
        assert_eq!(
            key_box.properties.get("width").map(String::as_str),
            Some("350")
        );
        assert_eq!(
            key_box.properties.get("secure").map(String::as_str),
            Some("true")
        );
        assert_eq!(
            compare_code_property_value(
                "Padding",
                "12,5,40,5",
                key_box.properties.get("padding").map(String::as_str),
            ),
            "pass"
        );

        let reveal = facts
            .get("CustomOpenAIKeyRevealButton")
            .expect("reveal button fact");
        assert_eq!(
            reveal.properties.get("align_x").map(String::as_str),
            Some("Right")
        );
        assert_eq!(
            reveal.properties.get("margin").map(String::as_str),
            Some("0,0,6,0")
        );

        let endpoint = facts.get("CustomOpenAIEndpointBox").expect("endpoint fact");
        assert_eq!(
            endpoint.properties.get("width").map(String::as_str),
            Some("450")
        );

        let model = facts.get("CustomOpenAIModelBox").expect("model fact");
        assert_eq!(model.kind, "text_editor");
        assert_eq!(
            model.properties.get("width").map(String::as_str),
            Some("200")
        );

        let test_button = facts
            .get("TestCustomOpenAIButton")
            .expect("test button fact");
        assert_eq!(
            test_button.properties.get("height").map(String::as_str),
            Some("29")
        );
        assert_eq!(
            test_button.properties.get("padding").map(String::as_str),
            Some("8,4")
        );

        let expander = facts
            .get("CustomOpenAIServiceExpander")
            .expect("expander fact");
        assert_eq!(
            expander.properties.get("align_x").map(String::as_str),
            Some("Stretch")
        );
        assert_eq!(
            expander
                .properties
                .get("content_align_x")
                .map(String::as_str),
            Some("Stretch")
        );

        let status = facts
            .get("CustomOpenAIStatusText")
            .expect("status text fact");
        assert_eq!(
            status.properties.get("align_x").map(String::as_str),
            Some("Right")
        );
        assert_eq!(
            status.properties.get("margin").map(String::as_str),
            Some("0,0,8,0")
        );

        let header = facts
            .get("CustomOpenAIKeyHeaderText")
            .expect("key header fact");
        assert_eq!(
            header.properties.get("font_size").map(String::as_str),
            Some("14")
        );
    }

    #[test]
    fn code_parity_synthesizes_literal_secret_field_stack_controls() {
        let source = r#"
fn deepl_service_expander(state: &SettingsState) -> View<Message> {
    secret_field_stack(
        "DeepLKeyField",
        350,
        styled_text_id(
            "DeepLKeyHeaderText",
            tr("settings.services.deepl.api_key_optional", "API Key (Optional)"),
            TextStyle::Body,
        ),
        text_editor(state.deepl_api_key.clone())
            .id("DeepLKeyBox")
            .placeholder("Enter your DeepL API key")
            .max_height(36)
            .on_input(Message::DeepLApiKeyChanged)
            .into_view(),
        "DeepLKeyRevealButton",
        "Reveal API key",
    )
}
"#;
        let mut facts = BTreeMap::new();

        add_rust_secret_field_stack_facts("rs/crates/easydict_app/src/ui.rs", source, &mut facts);

        let key_box = facts.get("DeepLKeyBox").expect("key box fact");
        assert_eq!(
            key_box.properties.get("align_x").map(String::as_str),
            Some("Stretch")
        );
        assert_eq!(
            compare_code_property_value(
                "Padding",
                "12,5,40,5",
                key_box.properties.get("padding").map(String::as_str),
            ),
            "pass"
        );

        let reveal = facts
            .get("DeepLKeyRevealButton")
            .expect("reveal button fact");
        assert_eq!(
            reveal.properties.get("align_x").map(String::as_str),
            Some("Right")
        );
        assert_eq!(
            reveal.properties.get("align_y").map(String::as_str),
            Some("Center")
        );
        assert_eq!(
            compare_code_property_value(
                "Margin",
                "0,0,6,0",
                reveal.properties.get("margin").map(String::as_str),
            ),
            "pass"
        );
    }

    #[test]
    fn code_parity_infers_explicit_layout_style_radius_and_border() {
        let mut properties = BTreeMap::new();

        apply_rust_style_fact_properties("surface-card border rounded-[10px]", &mut properties);

        assert_eq!(
            properties.get("border_width").map(String::as_str),
            Some("1")
        );
        assert_eq!(properties.get("radius").map(String::as_str), Some("10"));
    }

    #[test]
    fn code_parity_synthesizes_styled_text_font_sizes() {
        let source = r#"
fn settings_services_content() -> View<Message> {
    column((
        styled_text_id("ServiceConfigurationDescriptionText", "Configure services", TextStyle::Caption),
        styled_text_id("ServiceConfigurationHeaderText", "Service Configuration", TextStyle::SectionTitle),
        styled_text_id("EnableInternationalServicesDescriptionText", "Some services require access.", TextStyle::CaptionSmall),
        styled_text_id_with_font_size("EnableInternationalServicesHeaderText", "Enable International Services", TextStyle::Body, 13),
    ))
}
"#;
        let mut facts = BTreeMap::new();

        add_rust_styled_text_facts("rs/crates/easydict_app/src/ui.rs", source, &mut facts);

        assert_eq!(
            facts
                .get("ServiceConfigurationDescriptionText")
                .and_then(|fact| fact.properties.get("font_size"))
                .map(String::as_str),
            Some("12")
        );
        assert_eq!(
            facts
                .get("ServiceConfigurationHeaderText")
                .and_then(|fact| fact.properties.get("font_size"))
                .map(String::as_str),
            Some("18")
        );
        assert_eq!(
            facts
                .get("EnableInternationalServicesDescriptionText")
                .and_then(|fact| fact.properties.get("font_size"))
                .map(String::as_str),
            Some("11")
        );
        assert_eq!(
            facts
                .get("EnableInternationalServicesHeaderText")
                .and_then(|fact| fact.properties.get("font_size"))
                .map(String::as_str),
            Some("13")
        );
    }

    #[test]
    fn code_parity_synthesizes_services_section_header_text_and_icon() {
        let source = r#"
fn settings_services_content() -> View<Message> {
    services_section_header(
        "ServiceConfigurationHeaderRow",
        "ServiceConfigurationHeaderText",
        "ServiceConfigHelpIcon",
        "Service Configuration",
        "Configure services",
    )
}
"#;
        let mut facts = BTreeMap::new();

        add_rust_services_section_header_facts(
            "rs/crates/easydict_app/src/ui.rs",
            source,
            &mut facts,
        );

        assert_eq!(
            facts
                .get("ServiceConfigurationHeaderText")
                .and_then(|fact| fact.properties.get("font_size"))
                .map(String::as_str),
            Some("18")
        );
        assert_eq!(
            facts
                .get("ServiceConfigHelpIcon")
                .and_then(|fact| fact.properties.get("font_size"))
                .map(String::as_str),
            Some("14")
        );
        assert_eq!(
            facts
                .get("ServiceConfigHelpIcon")
                .and_then(|fact| fact.properties.get("align_y"))
                .map(String::as_str),
            Some("Center")
        );
    }

    #[test]
    fn code_parity_synthesizes_literal_service_expander_controls() {
        let source = r#"
fn deepl_service_expander(state: &SettingsState) -> View<Message> {
    service_expander(
        state,
        "deepl",
        service_configuration_expanded(state, "deepl"),
        "DeepLServiceExpander",
        "DeepL",
        "DeepLStatusText",
        state.deepl_status.clone(),
        "settings.services.deepl.content",
        vec![],
    )
}

fn local_ai_service_expander(state: &SettingsState) -> View<Message> {
    service_expander(
        state,
        "windows-local-ai",
        service_configuration_expanded(state, "windows-local-ai"),
        "WindowsLocalAIExpander",
        "Windows Local AI",
        "WindowsLocalAIStatusBadge",
        state.local_ai_status.clone(),
        "settings.services.local_ai.content",
        vec![],
    )
}
"#;
        let mut facts = BTreeMap::new();

        add_rust_service_expander_facts("rs/crates/easydict_app/src/ui.rs", source, &mut facts);

        let expander = facts.get("DeepLServiceExpander").expect("expander fact");
        assert_eq!(
            expander.properties.get("align_x").map(String::as_str),
            Some("Stretch")
        );
        assert_eq!(
            compare_code_property_value(
                "HorizontalContentAlignment",
                "Stretch",
                expander
                    .properties
                    .get("content_align_x")
                    .map(String::as_str),
            ),
            "pass"
        );

        let status = facts.get("DeepLStatusText").expect("status fact");
        assert_eq!(
            compare_code_property_value(
                "HorizontalAlignment",
                "Right",
                status.properties.get("align_x").map(String::as_str),
            ),
            "pass"
        );
        assert_eq!(
            compare_code_property_value(
                "Margin",
                "0,0,8,0",
                status.properties.get("margin").map(String::as_str),
            ),
            "pass"
        );

        let title = facts
            .get("WindowsLocalAITitleText")
            .expect("service header title fact");
        assert_eq!(
            compare_code_property_value(
                "HorizontalAlignment",
                "Left",
                title.properties.get("align_x").map(String::as_str),
            ),
            "pass"
        );
    }

    #[test]
    fn code_parity_recognizes_right_aligned_openvino_status_badge() {
        let dir = tempdir().expect("temp dir");
        let rust_ui = dir.path().join("ui.rs");
        fs::write(
            &rust_ui,
            r#"
fn open_vino_config_panel() -> View<Message> {
    row((
        spacer().width(Length::Fill).into_view(),
        row((styled_text("Ready", TextStyle::Caption),))
            .id("OpenVinoStatusBadge")
            .margin(Edges {
                right: 8,
                ..Edges::ZERO
            })
            .align(Alignment::Center)
            .into_view(),
    ))
    .width(Length::Fill)
    .into_view()
}
"#,
        )
        .expect("write ui");
        let options = CodeParityOptions {
            repo_root: dir.path().to_path_buf(),
            output_dir: dir.path().join("out"),
            dotnet_xaml: Vec::new(),
            dotnet_resources: Vec::new(),
            rust_ui,
            rust_theme: dir.path().join("theme.rs"),
            context_lines: 18,
            top: 20,
            fail_on_drift: false,
        };

        let facts = read_rust_ui_facts(&options).expect("read rust facts");
        let status = facts.get("OpenVinoStatusBadge").expect("status badge fact");

        assert_eq!(
            status.properties.get("align_x").map(String::as_str),
            Some("Right")
        );
        assert_eq!(
            compare_code_property_value(
                "Margin",
                "0,0,8,0",
                status.properties.get("margin").map(String::as_str),
            ),
            "pass"
        );
    }

    #[test]
    fn code_parity_does_not_assign_nested_child_methods_to_parent_id() {
        let dir = tempdir().expect("temp dir");
        let rust_ui = dir.path().join("ui.rs");
        fs::write(
            &rust_ui,
            r#"
fn long_doc_input_card() -> View<Message> {
    settings_row(semantic_header(theme, "📝", "Source Text"))
        .id("main.long-doc.input_card")
        .trailing((button("Browse...")
            .id("main.long-doc.browse")
            .margin(Edges {
                left: 8,
                ..Edges::ZERO
            })
            .on_press(Message::BrowseFile),))
        .content(text("body"))
}
"#,
        )
        .expect("write ui");
        let options = CodeParityOptions {
            repo_root: dir.path().to_path_buf(),
            output_dir: dir.path().join("out"),
            dotnet_xaml: Vec::new(),
            dotnet_resources: Vec::new(),
            rust_ui,
            rust_theme: dir.path().join("theme.rs"),
            context_lines: 18,
            top: 20,
            fail_on_drift: false,
        };

        let facts = read_rust_ui_facts(&options).expect("read rust facts");
        let parent = facts.get("main.long-doc.input_card").expect("parent fact");
        let child = facts.get("main.long-doc.browse").expect("child fact");

        assert_eq!(parent.properties.get("margin"), None);
        assert_eq!(
            compare_code_property_value(
                "Margin",
                "8,0,0,0",
                child.properties.get("margin").map(String::as_str),
            ),
            "pass"
        );
    }

    #[test]
    fn code_parity_synthesizes_long_doc_control_grid_context() {
        let dir = tempdir().expect("temp dir");
        let rust_ui = dir.path().join("ui.rs");
        fs::write(
            &rust_ui,
            r#"
fn long_document_control_bar() -> View<Message> {
    column((
        row((
            combo_box([]).id("main.long-doc.source_language").width(Length::Fill),
            combo_box([]).id("main.long-doc.target_language").width(Length::Fill),
            combo_box([]).id("main.long-doc.service").width(Length::Fill),
        ))
        .spacing(8)
        .width(Length::Fill),
        row((
            combo_box([]).id("main.long-doc.input_mode").width(Length::Fill),
            combo_box([]).id("main.long-doc.output_mode").width(Length::Fill),
            text_editor("").id("main.long-doc.concurrency").width(Length::Fill),
            column((text_editor("").id("main.long-doc.page_range").width(Length::Fill),))
                .width(Length::Fixed(110)),
        ))
        .spacing(8),
        row((
            toggle_switch("Use document context pass", true)
                .id("main.long-doc.two_pass")
                .width(Length::FillPortion(3)),
            primary_button("")
                .id("main.long-doc.translate")
                .width(Length::Fixed(40)),
        ))
        .spacing(8),
    ))
    .id("main.long-doc.control_bar")
    .spacing(4)
    .margin(Edges { top: 4, bottom: 4, ..Edges::ZERO })
    .width(Length::Fill)
}

fn marker() {
    let _ = Length::FillPortion(2);
}
"#,
        )
        .expect("write ui");
        let options = CodeParityOptions {
            repo_root: dir.path().to_path_buf(),
            output_dir: dir.path().join("out"),
            dotnet_xaml: Vec::new(),
            dotnet_resources: Vec::new(),
            rust_ui,
            rust_theme: dir.path().join("theme.rs"),
            context_lines: 18,
            top: 20,
            fail_on_drift: false,
        };

        let facts = read_rust_ui_facts(&options).expect("read rust facts");
        let control_bar = facts
            .get("main.long-doc.control_bar")
            .expect("control bar fact");
        let service = facts.get("main.long-doc.service").expect("service fact");
        let page_range = facts
            .get("main.long-doc.page_range")
            .expect("page range fact");
        let translate = facts
            .get("main.long-doc.translate")
            .expect("translate fact");

        assert_eq!(
            control_bar
                .properties
                .get("column_spacing")
                .map(String::as_str),
            Some("8")
        );
        assert_eq!(
            control_bar
                .properties
                .get("row_spacing")
                .map(String::as_str),
            Some("4")
        );
        assert_eq!(
            compare_code_property_value(
                "Margin",
                "0,4,0,4",
                control_bar.properties.get("margin").map(String::as_str),
            ),
            "pass"
        );
        assert_eq!(
            service
                .properties
                .get("grid_column_span")
                .map(String::as_str),
            Some("2")
        );
        assert_eq!(
            page_range.properties.get("min_width").map(String::as_str),
            Some("110")
        );
        assert_eq!(
            translate.properties.get("align_x").map(String::as_str),
            Some("Right")
        );
    }

    #[test]
    fn code_parity_synthesizes_long_doc_card_grid_context() {
        let dir = tempdir().expect("temp dir");
        let rust_ui = dir.path().join("ui.rs");
        fs::write(
            &rust_ui,
            r#"
fn long_document_content() -> View<Message> {
    long_document_card_shell(
        "main.long-doc.input_card",
        Edges { bottom: 2, ..Edges::ZERO },
        column((
            styled_text_id_with_font_size("LongDocInputTitle", "Source Text", TextStyle::BodyStrong, 13)
                .text_margin(Edges { bottom: 4, ..Edges::ZERO }),
            column((
                row((
                    sized_styled_text_id("LongDocFilePathDisplay", "No file selected", TextStyle::Body, Length::Fill, Length::Shrink)
                        .text_align_y(Alignment::Center),
                    button("Browse...").id("main.long-doc.browse")
                        .margin(Edges { left: 8, ..Edges::ZERO }),
                ))
                .id("LongDocFilePickerRow")
                .spacing(8),
                text_editor("").id("main.long-doc.source_text"),
            ))
            .id("LongDocFilePanel")
            .spacing(8),
        ))
        .id("LongDocInputCardContent")
        .margin(Edges { top: 4, right: 4, bottom: 4, left: 4 }),
    );

    column((
        row((
            styled_text_id_with_font_size("LongDocOutputTitle", "Translation Result", TextStyle::BodyStrong, 13),
            button("Retry Failed").id("main.long-doc.retry")
                .font_size(12)
                .padding(Edges { top: 4, right: 8, bottom: 4, left: 8 }),
        ))
        .id("LongDocOutputHeaderRow")
        .spacing(8)
        .margin(Edges { bottom: 4, ..Edges::ZERO }),
        column(())
            .id("main.long-doc.output_content")
            .spacing(8),
    ))
    .id("LongDocOutputCardContent")
    .margin(Edges { top: 4, right: 4, bottom: 4, left: 4 });

    long_document_card_shell(
        "main.long-doc.output_card",
        Edges { top: 2, ..Edges::ZERO },
        column(()),
    );
}
"#,
        )
        .expect("write ui");
        let options = CodeParityOptions {
            repo_root: dir.path().to_path_buf(),
            output_dir: dir.path().join("out"),
            dotnet_xaml: Vec::new(),
            dotnet_resources: Vec::new(),
            rust_ui,
            rust_theme: dir.path().join("theme.rs"),
            context_lines: 18,
            top: 20,
            fail_on_drift: false,
        };

        let facts = read_rust_ui_facts(&options).expect("read rust facts");

        assert_eq!(
            facts
                .get("main.long-doc.input_card")
                .and_then(|fact| fact.properties.get("grid_row"))
                .map(String::as_str),
            Some("0")
        );
        assert_eq!(
            compare_code_property_value(
                "Margin",
                "0,0,0,2",
                facts
                    .get("main.long-doc.input_card")
                    .and_then(|fact| fact.properties.get("margin"))
                    .map(String::as_str),
            ),
            "pass"
        );
        assert_eq!(
            compare_code_property_value(
                "Margin",
                "4",
                facts
                    .get("LongDocInputCardContent")
                    .and_then(|fact| fact.properties.get("margin"))
                    .map(String::as_str),
            ),
            "pass"
        );
        assert_eq!(
            facts
                .get("LongDocInputTitle")
                .and_then(|fact| fact.properties.get("grid_row"))
                .map(String::as_str),
            Some("0")
        );
        assert_eq!(
            facts
                .get("LongDocFilePanel")
                .and_then(|fact| fact.properties.get("align_y"))
                .map(String::as_str),
            Some("Top")
        );
        assert_eq!(
            facts
                .get("LongDocFilePathDisplay")
                .and_then(|fact| fact.properties.get("grid_column"))
                .map(String::as_str),
            Some("0")
        );
        assert_eq!(
            facts
                .get("main.long-doc.output_card")
                .and_then(|fact| fact.properties.get("grid_row"))
                .map(String::as_str),
            Some("2")
        );
        assert_eq!(
            compare_code_property_value(
                "Margin",
                "0,2,0,0",
                facts
                    .get("main.long-doc.output_card")
                    .and_then(|fact| fact.properties.get("margin"))
                    .map(String::as_str),
            ),
            "pass"
        );
        assert_eq!(
            facts
                .get("LongDocOutputTitle")
                .and_then(|fact| fact.properties.get("grid_column"))
                .map(String::as_str),
            Some("0")
        );
        assert_eq!(
            facts
                .get("main.long-doc.output_content")
                .and_then(|fact| fact.properties.get("align_y"))
                .map(String::as_str),
            Some("Top")
        );
    }

    #[test]
    fn code_parity_synthesizes_local_ai_action_control_alignment() {
        let mut facts = BTreeMap::from([(
            "WindowsLocalAIWindowsUpdateLink".to_string(),
            CodeFact {
                source: "rust-ui".to_string(),
                file: "rs/crates/easydict_app/src/ui.rs".to_string(),
                line: 42,
                id: "WindowsLocalAIWindowsUpdateLink".to_string(),
                kind: "button".to_string(),
                properties: BTreeMap::new(),
            },
        )]);

        add_rust_local_ai_action_control_facts("rs/crates/easydict_app/src/ui.rs", &mut facts);

        assert_eq!(
            facts
                .get("WindowsLocalAIPrepareButton")
                .and_then(|fact| fact.properties.get("align_x"))
                .map(String::as_str),
            Some("Left")
        );
        assert_eq!(
            facts
                .get("WindowsLocalAIWindowsUpdateLink")
                .and_then(|fact| fact.properties.get("font_size"))
                .map(String::as_str),
            Some("12")
        );
        assert_eq!(
            facts
                .get("WindowsLocalAIWindowsUpdateLink")
                .and_then(|fact| fact.properties.get("min_height"))
                .map(String::as_str),
            Some("0")
        );
    }

    #[test]
    fn code_parity_synthesizes_settings_services_row_context_alignment() {
        let source = r#"
fn settings_services_content() -> View<Message> {
    row((
        button("Import").id("ImportMdxDictionaryButton"),
        styled_text_id("ImportedMdxSummaryText", "No dictionaries", TextStyle::Caption),
    ))
    .id("settings.services.mdx")
    .spacing(8)
    .align(Alignment::Center)
    .into_view();

    row((
        styled_text_id_with_font_size("EnableInternationalServicesHeaderText", "Enable", TextStyle::Body, 13),
        spacer().width(Length::Fill).into_view(),
        toggle_switch("On", true).id("EnableInternationalServicesToggle"),
    ))
    .id("settings.services.international.header")
    .align(Alignment::Center)
    .into_view();

    row((
        button("Refresh").id("RefreshOllamaButton"),
        button("Test").id("TestOllamaButton"),
    ))
    .align(Alignment::End)
    .into_view()
}
"#;
        let mut facts = BTreeMap::from([
            (
                "RefreshOllamaButton".to_string(),
                CodeFact {
                    source: "rust-ui".to_string(),
                    file: "rs/crates/easydict_app/src/ui.rs".to_string(),
                    line: 22,
                    id: "RefreshOllamaButton".to_string(),
                    kind: "button".to_string(),
                    properties: BTreeMap::new(),
                },
            ),
            (
                "TestOllamaButton".to_string(),
                CodeFact {
                    source: "rust-ui".to_string(),
                    file: "rs/crates/easydict_app/src/ui.rs".to_string(),
                    line: 23,
                    id: "TestOllamaButton".to_string(),
                    kind: "button".to_string(),
                    properties: BTreeMap::new(),
                },
            ),
        ]);

        add_rust_settings_services_row_context_facts(
            "rs/crates/easydict_app/src/ui.rs",
            source,
            &mut facts,
        );

        assert_eq!(
            facts
                .get("ImportedMdxSummaryText")
                .and_then(|fact| fact.properties.get("align_y"))
                .map(String::as_str),
            Some("Center")
        );
        assert_eq!(
            facts
                .get("EnableInternationalServicesHeaderText")
                .and_then(|fact| fact.properties.get("align_y"))
                .map(String::as_str),
            Some("Center")
        );
        let toggle = facts
            .get("EnableInternationalServicesToggle")
            .expect("toggle fact");
        assert_eq!(
            toggle.properties.get("grid_column").map(String::as_str),
            Some("1")
        );
        assert_eq!(
            toggle.properties.get("min_width").map(String::as_str),
            Some("0")
        );
        assert_eq!(
            compare_code_property_value(
                "VerticalAlignment",
                "Bottom",
                facts
                    .get("RefreshOllamaButton")
                    .and_then(|fact| fact.properties.get("align_y"))
                    .map(String::as_str),
            ),
            "pass"
        );
        assert_eq!(
            compare_code_property_value(
                "VerticalAlignment",
                "Bottom",
                facts
                    .get("TestOllamaButton")
                    .and_then(|fact| fact.properties.get("align_y"))
                    .map(String::as_str),
            ),
            "pass"
        );
    }

    #[test]
    fn code_parity_synthesizes_settings_about_row_context_alignment() {
        let source = r#"
fn settings_about_content() -> View<Message> {
    row((
        sized_styled_text_id("AboutInspiredByText", "Inspired by", TextStyle::Caption, Length::Shrink, Length::Fixed(18)),
        settings_link_button(SettingsLink::EasydictForMacOS, locale),
    ))
    .id("settings.about.inspired_by")
    .spacing(4)
    .align(Alignment::Center)
    .into_view()
}
"#;
        let mut facts = BTreeMap::new();

        add_rust_settings_about_row_context_facts(
            "rs/crates/easydict_app/src/ui.rs",
            source,
            &mut facts,
        );
        add_rust_settings_link_button_facts("rs/crates/easydict_app/src/ui.rs", source, &mut facts);

        assert_eq!(
            facts
                .get("AboutInspiredByText")
                .and_then(|fact| fact.properties.get("align_y"))
                .map(String::as_str),
            Some("Center")
        );
        assert_eq!(
            facts
                .get("InspiredByLink")
                .and_then(|fact| fact.properties.get("font_size"))
                .map(String::as_str),
            Some("12")
        );
    }

    #[test]
    fn code_parity_synthesizes_no_config_service_row_spacing() {
        let source = r#"
fn linguee_no_config_service_rows() -> Vec<View<Message>> {
    vec![no_config_service_row(
        "LingueeFreeServicePanel",
        "linguee",
        "Linguee Dictionary",
    )]
}
"#;
        let mut facts = BTreeMap::new();

        add_rust_no_config_service_row_facts(
            "rs/crates/easydict_app/src/ui.rs",
            source,
            &mut facts,
        );

        assert_eq!(
            facts
                .get("LingueeFreeServicePanel")
                .and_then(|fact| fact.properties.get("spacing"))
                .map(String::as_str),
            Some("6")
        );
    }

    #[test]
    fn code_parity_resolves_long_doc_dotnet_ids_to_rust_semantic_aliases() {
        let mut rust_facts = BTreeMap::new();
        rust_facts.insert(
            "main.long-doc.browse".to_string(),
            CodeFact {
                source: "rust-ui".to_string(),
                file: "rs/crates/easydict_app/src/ui.rs".to_string(),
                line: 42,
                id: "main.long-doc.browse".to_string(),
                kind: "button".to_string(),
                properties: BTreeMap::new(),
            },
        );

        let candidate = rust_fact_for_dotnet_id(&rust_facts, "LongDocBrowseButton")
            .expect("long-doc alias should resolve");

        assert_eq!(candidate.id, "main.long-doc.browse");
    }

    #[test]
    fn code_parity_resolves_duplicate_dotnet_ids_by_xaml_context() {
        let rust_fact = |id: &str| {
            (
                id.to_string(),
                CodeFact {
                    source: "rust-ui".to_string(),
                    file: "rs/crates/easydict_app/src/ui.rs".to_string(),
                    line: 42,
                    id: id.to_string(),
                    kind: "button".to_string(),
                    properties: BTreeMap::new(),
                },
            )
        };
        let rust_facts = BTreeMap::from([
            rust_fact("TranslateButton"),
            rust_fact("mini.translate"),
            rust_fact("fixed.translate"),
            rust_fact("pop-button.translate"),
        ]);
        let dotnet_fact = |file: &str, id: &str| CodeFact {
            source: "dotnet-xaml".to_string(),
            file: file.to_string(),
            line: 1,
            id: id.to_string(),
            kind: "Button".to_string(),
            properties: BTreeMap::new(),
        };

        let cases = [
            (
                "dotnet/src/Easydict.WinUI/Views/MainPage.xaml",
                "TranslateButton",
                "TranslateButton",
            ),
            (
                "dotnet/src/Easydict.WinUI/Views/MiniWindow.xaml",
                "TranslateButton",
                "mini.translate",
            ),
            (
                "dotnet/src/Easydict.WinUI/Views/FixedWindow.xaml",
                "TranslateButton",
                "fixed.translate",
            ),
            (
                "dotnet/src/Easydict.WinUI/Views\\PopButtonWindow.xaml",
                "TranslateButton",
                "pop-button.translate",
            ),
        ];

        for (file, dotnet_id, expected_rust_id) in cases {
            let fact = dotnet_fact(file, dotnet_id);
            let (candidate, contextual) = rust_fact_for_dotnet_fact(&rust_facts, &fact);

            assert!(contextual, "{file}:{dotnet_id} should use contextual alias");
            assert_eq!(
                candidate.expect("contextual alias should resolve").id,
                expected_rust_id
            );
        }
    }

    #[test]
    fn code_parity_synthesizes_main_action_bar_grid_row_context() {
        let source = r#"
fn quick_translate_content(state: &EasydictUiState) -> View<Message> {
    let content_children = vec![
        source_text_card(state),
        main_translate_action_bar(state),
        main_results_card(state),
    ];
}
fn main_translate_action_bar_wide() -> View<Message> {
    row(vec![]).id("ActionBarWide").into_view()
}
fn main_translate_action_bar_narrow() -> View<Message> {
    column(vec![]).id("ActionBarNarrow").into_view()
}
fn main_translate_button() -> View<Message> {
    primary_button("").id(id).into_view()
}
"#;
        let mut facts = BTreeMap::new();

        add_rust_main_action_bar_context_facts(
            "rs/crates/easydict_app/src/ui.rs",
            source,
            &mut facts,
        );

        for id in ["ActionBarWide", "ActionBarNarrow"] {
            assert_eq!(
                compare_code_property_value(
                    "Grid.Row",
                    "1",
                    facts
                        .get(id)
                        .and_then(|fact| fact.properties.get("grid_row"))
                        .map(String::as_str),
                ),
                "pass"
            );
        }
        assert_eq!(
            facts
                .get("TranslateButton")
                .and_then(|fact| fact.properties.get("grid_column"))
                .map(String::as_str),
            Some("4")
        );
        assert_eq!(
            compare_code_property_value(
                "VerticalAlignment",
                "Center",
                facts
                    .get("TranslateButton")
                    .and_then(|fact| fact.properties.get("align_y"))
                    .map(String::as_str),
            ),
            "pass"
        );
    }

    #[test]
    fn code_parity_synthesizes_settings_general_combo_alignment() {
        let source = r#"
fn settings_general_content(state: &SettingsState) -> View<Message> {
    column((
        combo_box(theme_combo_items(locale))
            .id("AppThemeCombo")
            .width(Length::Fixed(250)),
    ))
    .id("settings.general.theme")
    .align(Alignment::Start)
    .into_view()
}
"#;
        let mut facts = BTreeMap::new();

        add_rust_settings_general_context_facts(
            "rs/crates/easydict_app/src/ui.rs",
            source,
            &mut facts,
        );

        assert_eq!(
            compare_code_property_value(
                "HorizontalAlignment",
                "Left",
                facts
                    .get("AppThemeCombo")
                    .and_then(|fact| fact.properties.get("align_x"))
                    .map(String::as_str),
            ),
            "pass"
        );
    }

    #[test]
    fn code_parity_synthesizes_settings_loading_overlay_layer_alignment() {
        let source = r#"
fn settings_view_with_close_message(state: &SettingsState, close_message: Message) -> View<Message> {
    if state.settings_runtime.is_loading() {
        surface = surface.layer(
            OverlayLayer::new(settings_loading_indicator())
                .scrim(0.3)
                .blocks_input(true),
        );
    }
}

fn settings_loading_indicator() -> View<Message> {
    column((
        progress_ring()
            .id("LoadingOverlayRing")
            .active(true)
            .size(32),
    ))
    .id("LoadingOverlay")
    .spacing(12)
    .align(Alignment::Center)
    .into_view()
}
"#;
        let mut facts = BTreeMap::new();

        add_rust_settings_loading_overlay_context_facts(
            "rs/crates/easydict_app/src/ui.rs",
            source,
            &mut facts,
        );

        assert_eq!(
            facts
                .get("LoadingOverlay")
                .and_then(|fact| fact.properties.get("align_x"))
                .map(String::as_str),
            Some("Center")
        );
        assert_eq!(
            facts
                .get("LoadingOverlay")
                .and_then(|fact| fact.properties.get("align_y"))
                .map(String::as_str),
            Some("Center")
        );
    }

    #[test]
    fn code_parity_synthesizes_settings_advanced_layout_context() {
        let source = r#"
fn settings_advanced_content(state: &SettingsState) -> View<Message> {
    column((
        text_editor(state.formula_font_pattern.clone())
            .id("FormulaFontPatternBox")
            .width(Length::Fixed(450))
            .on_input(Message::FormulaFontPatternChanged),
        text_editor(state.custom_translation_prompt.clone())
            .id("CustomPromptBox")
            .width(Length::Fixed(450))
            .min_height(120)
            .max_height(120)
            .on_input(Message::CustomTranslationPromptChanged),
        row((
            button("Clear").id("ClearCacheButton"),
            styled_text_id_with_font_size("CacheStatusText", "Ready", TextStyle::Caption, 12),
        ))
        .id("TranslationCacheActionRow")
        .align(Alignment::Center)
        .into_view(),
        settings_advanced_section(
            "CjkFontSection",
            "CjkFontHeaderText",
            "CJK Font",
            Some((
                "CjkFontDescriptionText",
                "Download CJK fonts for proper Chinese, Japanese, and Korean text rendering in PDF output.",
            )),
            "settings.advanced.cjk_font.panel",
            vec![],
        ),
    ))
    .id("AdvancedTabContent")
    .spacing(24)
    .into_view()
}
"#;
        let dir = tempdir().expect("temp dir");
        let rust_ui = dir.path().join("ui.rs");
        fs::write(&rust_ui, source).expect("write ui");
        let options = CodeParityOptions {
            repo_root: dir.path().to_path_buf(),
            output_dir: dir.path().join("out"),
            dotnet_xaml: Vec::new(),
            dotnet_resources: Vec::new(),
            rust_ui,
            rust_theme: dir.path().join("theme.rs"),
            context_lines: 18,
            top: 20,
            fail_on_drift: false,
        };

        let facts = read_rust_ui_facts(&options).expect("read rust facts");

        assert_eq!(
            facts
                .get("FormulaFontPatternBox")
                .and_then(|fact| fact.properties.get("align_x"))
                .map(String::as_str),
            Some("Left")
        );
        assert_eq!(
            facts
                .get("CustomPromptBox")
                .and_then(|fact| fact.properties.get("height"))
                .map(String::as_str),
            Some("120")
        );
        assert_eq!(
            facts
                .get("CacheStatusText")
                .and_then(|fact| fact.properties.get("align_y"))
                .map(String::as_str),
            Some("Center")
        );
        assert_eq!(
            facts
                .get("CjkFontSection")
                .and_then(|fact| fact.properties.get("spacing"))
                .map(String::as_str),
            Some("12")
        );
        assert_eq!(
            facts
                .get("CjkFontHeaderText")
                .and_then(|fact| fact.properties.get("font_size"))
                .map(String::as_str),
            Some("18")
        );
        assert_eq!(
            facts
                .get("CjkFontDescriptionText")
                .and_then(|fact| fact.properties.get("font_size"))
                .map(String::as_str),
            Some("12")
        );
    }

    #[test]
    fn code_parity_synthesizes_settings_language_alias_context() {
        let source = r#"
fn settings_language_content(state: &SettingsState) -> View<Message> {
    column((
        column((
            combo_box(settings_language_items(locale, false))
                .id("FirstLanguageCombo")
                .width(Length::Fixed(250))
                .on_change(Message::FirstLanguageChanged),
        ))
        .id("settings.language.first")
        .align(Alignment::Start)
        .into_view(),
        column((
            styled_text_id_with_font_size(
                "LanguagePreferencesDescriptionText",
                "When detected language matches your First Language...",
                TextStyle::Caption,
                12,
            ),
        ))
        .id("LanguagePreferencesSection")
        .spacing(12)
        .into_view(),
        expander("Available Languages")
            .id("settings.language.translation_languages")
            .expanded(state.translation_languages_expanded)
            .on_toggle(Message::ToggleTranslationLanguagesExpanded),
    ))
}
"#;
        let mut facts = BTreeMap::from([(
            "settings.language.translation_languages".to_string(),
            CodeFact {
                source: "rust-ui".to_string(),
                file: "rs/crates/easydict_app/src/ui.rs".to_string(),
                line: 12,
                id: "settings.language.translation_languages".to_string(),
                kind: "expander".to_string(),
                properties: BTreeMap::new(),
            },
        )]);

        add_rust_settings_language_context_facts(
            "rs/crates/easydict_app/src/ui.rs",
            source,
            &mut facts,
        );

        let expander = facts
            .get("AvailableLanguagesExpander")
            .expect("available languages alias fact");
        assert_eq!(
            expander.properties.get("align_x").map(String::as_str),
            Some("Stretch")
        );
        assert_eq!(
            expander
                .properties
                .get("content_align_x")
                .map(String::as_str),
            Some("Stretch")
        );
        assert_eq!(
            facts
                .get("FirstLanguageCombo")
                .and_then(|fact| fact.properties.get("align_x"))
                .map(String::as_str),
            Some("Left")
        );
        assert_eq!(
            facts
                .get("LanguagePreferencesDescriptionText")
                .and_then(|fact| fact.properties.get("margin"))
                .map(String::as_str),
            Some("0,4,0,0")
        );
    }

    #[test]
    fn code_parity_reads_progress_ring_size_as_width_and_height() {
        let source = r#"
fn view() -> View<Message> {
    progress_ring()
        .id("LoadingRing")
        .size(16)
        .into_view()
}
"#;
        let dir = tempdir().expect("temp dir");
        let rust_ui = dir.path().join("ui.rs");
        fs::write(&rust_ui, source).expect("write ui");
        let options = CodeParityOptions {
            repo_root: dir.path().to_path_buf(),
            output_dir: dir.path().join("out"),
            dotnet_xaml: Vec::new(),
            dotnet_resources: Vec::new(),
            rust_ui,
            rust_theme: dir.path().join("theme.rs"),
            context_lines: 8,
            top: 20,
            fail_on_drift: false,
        };

        let facts = read_rust_ui_facts(&options).expect("read rust facts");
        let loading = facts.get("LoadingRing").expect("loading ring fact");

        assert_eq!(
            loading.properties.get("size").map(String::as_str),
            Some("16")
        );
        assert_eq!(
            loading.properties.get("width").map(String::as_str),
            Some("16")
        );
        assert_eq!(
            loading.properties.get("height").map(String::as_str),
            Some("16")
        );
    }

    #[test]
    fn code_parity_synthesizes_window_chrome_input_views_and_hotkeys_context() {
        let source = r#"
fn floating_translate_view() -> View<Message> {
    column(()).into_view()
}
fn pop_button_view_with_state() -> View<Message> {
    primary_button("Translate selection")
        .id("pop-button.translate")
        .into_view()
}
fn quick_translate_content() -> View<Message> {
    scroll_view(column(())).id("QuickTranslateContent").into_view()
}
fn main_results_card() -> View<Message> {
    card("Translation Results").id("QuickOutputCard").into_view()
}
fn floating_header() -> View<Message> {
    row((
        button("Close").id("MiniWindowCloseButton"),
        button("Close").id("CloseButton"),
        styled_text_id_with_font_size("DetectedLangText", "Detected: English", TextStyle::Caption, 10),
    ))
    .into_view()
}
fn source_text_card() -> View<Message> {
    let _card = card("").id("QuickInputCard");
    column((
        styled_text_id_with_font_size("DetectedLanguageText", "Detected: English", TextStyle::Caption, 11),
        styled_text_id_with_font_size("InputHelpIcon", "?", TextStyle::Caption, 12),
        styled_text_id_with_font_size("LangHelpIcon", "?", TextStyle::Body, 14),
        styled_text_id_with_font_size("LangHelpIconNarrow", "?", TextStyle::Body, 14),
        button("Play source").id("main.quick.play_source"),
    ))
    .id("InputTextContainer")
    .into_view();
    column(())
        .id("SuggestionPopupBorder")
        .tw("surface-card border rounded-[10px]")
        .into_view()
}
fn settings_views_content() -> View<Message> {
    row((
        styled_text_id_with_font_size("FixedWindowHeaderText", "Fixed Window", TextStyle::BodyStrong, 13),
        button("Reorder").id("FixedWindowReorderModeButton"),
    ))
    .align(Alignment::Center)
    .into_view()
}
fn settings_view_with_close_message() -> View<Message> {
    let scroll = scroll_view(
        column(())
            .id("settings.content")
            .padding(24)
            .spacing(24)
            .width(Length::Fill)
            .tw("max-w-[1040px] mx-auto"),
    );
    let mut surface = overlay(scroll).id("settings.root");
    surface = surface.layer(
        OverlayLayer::new(settings_save_bar(&state.ui_language))
            .align(Alignment::End, Alignment::End),
    );
    row((progress_ring().id("SettingsTabSwitchRing"),))
        .margin(Edges { top: 6, right: 4, ..Edges::ZERO });
    styled_text_id("SettingsHeaderText", "Settings", TextStyle::Title);
    wrap(Vec::new()).id("settings.categories");
    column(()).id("settings.services").spacing(24);
    column(()).id("settings.views").spacing(12);
    surface.into_view()
}
fn settings_save_bar() -> View<Message> {
    primary_button("Save Settings")
        .id("SaveButton")
        .padding(Edges { top: 12, right: 24, bottom: 12, left: 24 })
        .margin(Edges { right: 32, bottom: 32, ..Edges::ZERO })
        .into_view()
}
fn settings_hotkeys_content() -> View<Message> {
    column((
        row((
            button("").id("HotkeysHelpIcon"),
        ))
        .align(Alignment::Center),
        hotkey_row(
            locale,
            "settings.hotkeys.show_window.label",
            "Show Window",
            "settings.hotkeys.show_window",
            "ShowHotkeyBox",
            "ShowHotkeyEnabledToggle",
            "Ctrl+Alt+T",
            HOTKEY_SHOW_MAIN,
            &state.show_main_hotkey,
        ),
    ))
    .into_view()
}
fn hotkey_row() -> View<Message> {
    row((
        column((
            row((text_editor(setting.shortcut.clone())
                .id(box_id)
                .placeholder(placeholder)
                .width(Length::Fixed(200))
                .max_height(36)
                .on_input(Message::Noop),))
            .width(Length::Fixed(200)),
        ))
        .width(Length::Fixed(200)),
        toggle_switch("", setting.enabled)
            .id(toggle_id)
            .margin(Edges {
                bottom: 4,
                ..Edges::ZERO
            })
            .align_y(Alignment::End)
            .on_toggle(Message::Noop),
    ))
    .align(Alignment::End)
    .into_view()
}
"#;
        let mut facts = BTreeMap::new();

        add_rust_floating_window_context_facts(
            "rs/crates/easydict_app/src/ui.rs",
            source,
            &mut facts,
        );
        add_rust_main_input_context_facts("rs/crates/easydict_app/src/ui.rs", source, &mut facts);
        add_rust_main_quick_content_context_facts(
            "rs/crates/easydict_app/src/ui.rs",
            source,
            &mut facts,
        );
        add_rust_settings_views_context_facts(
            "rs/crates/easydict_app/src/ui.rs",
            source,
            &mut facts,
        );
        add_rust_settings_shell_context_facts(
            "rs/crates/easydict_app/src/ui.rs",
            source,
            &mut facts,
        );
        add_rust_settings_hotkeys_context_facts(
            "rs/crates/easydict_app/src/ui.rs",
            source,
            &mut facts,
        );

        assert_eq!(
            facts
                .get("MiniWindowCloseButton")
                .and_then(|fact| fact.properties.get("grid_column"))
                .map(String::as_str),
            Some("2")
        );
        assert_eq!(
            facts
                .get("CloseButton")
                .and_then(|fact| fact.properties.get("grid_column"))
                .map(String::as_str),
            Some("1")
        );
        assert_eq!(
            facts
                .get("DetectedLangText")
                .and_then(|fact| fact.properties.get("margin"))
                .map(String::as_str),
            Some("0,0,0,2")
        );
        assert_eq!(
            facts
                .get("mini.input_card")
                .and_then(|fact| fact.properties.get("padding"))
                .map(String::as_str),
            Some("12,10")
        );
        assert_eq!(
            facts
                .get("mini.content")
                .and_then(|fact| fact.properties.get("border_width"))
                .map(String::as_str),
            Some("0")
        );
        assert_eq!(
            facts
                .get("mini.source_language")
                .and_then(|fact| fact.properties.get("grid_column"))
                .map(String::as_str),
            Some("0")
        );
        assert_eq!(
            facts
                .get("fixed.target_language")
                .and_then(|fact| fact.properties.get("align_x"))
                .map(String::as_str),
            Some("Stretch")
        );
        assert_eq!(
            facts
                .get("mini.translate")
                .and_then(|fact| fact.properties.get("font_size"))
                .map(String::as_str),
            Some("14")
        );
        assert_eq!(
            facts
                .get("fixed.swap")
                .and_then(|fact| fact.properties.get("margin"))
                .map(String::as_str),
            Some("4,0")
        );
        assert_eq!(
            facts
                .get("mini.play_source")
                .and_then(|fact| fact.properties.get("align_y"))
                .map(String::as_str),
            Some("Top")
        );
        assert_eq!(
            facts
                .get("mini.pin")
                .and_then(|fact| fact.properties.get("width"))
                .map(String::as_str),
            Some("28")
        );
        assert_eq!(
            facts
                .get("mini.pin")
                .and_then(|fact| fact.properties.get("radius"))
                .map(String::as_str),
            Some("10")
        );
        assert_eq!(
            facts
                .get("pop-button.translate")
                .and_then(|fact| fact.properties.get("radius"))
                .map(String::as_str),
            Some("10")
        );
        assert_eq!(
            facts
                .get("pop-button.translate")
                .and_then(|fact| fact.properties.get("align_x"))
                .map(String::as_str),
            Some("Center")
        );
        assert_eq!(
            facts
                .get("DetectedLanguageText")
                .and_then(|fact| fact.properties.get("align_y"))
                .map(String::as_str),
            Some("Top")
        );
        assert_eq!(
            facts
                .get("InputTextContainer")
                .and_then(|fact| fact.properties.get("grid_row"))
                .map(String::as_str),
            Some("2")
        );
        assert_eq!(
            facts
                .get("QuickTranslateContent")
                .and_then(|fact| fact.properties.get("grid_row"))
                .map(String::as_str),
            Some("1")
        );
        assert_eq!(
            facts
                .get("QuickInputCard")
                .and_then(|fact| fact.properties.get("grid_row"))
                .map(String::as_str),
            Some("0")
        );
        assert_eq!(
            facts
                .get("QuickOutputCard")
                .and_then(|fact| fact.properties.get("grid_row"))
                .map(String::as_str),
            Some("2")
        );
        assert_eq!(
            facts
                .get("main.quick.play_source")
                .and_then(|fact| fact.properties.get("align_x"))
                .map(String::as_str),
            Some("Right")
        );
        assert_eq!(
            facts
                .get("SuggestionPopupBorder")
                .and_then(|fact| fact.properties.get("radius"))
                .map(String::as_str),
            Some("10")
        );
        assert_eq!(
            facts
                .get("SuggestionPopupBorder")
                .and_then(|fact| fact.properties.get("min_width"))
                .map(String::as_str),
            Some("180")
        );
        assert_eq!(
            facts
                .get("InputHelpIcon")
                .and_then(|fact| fact.properties.get("margin"))
                .map(String::as_str),
            Some("4,0,0,0")
        );
        assert_eq!(
            facts
                .get("LangHelpIconNarrow")
                .and_then(|fact| fact.properties.get("margin"))
                .map(String::as_str),
            Some("4,0,0,0")
        );
        assert_eq!(
            facts
                .get("FixedWindowReorderModeButton")
                .and_then(|fact| fact.properties.get("min_height"))
                .map(String::as_str),
            Some("24")
        );
        assert_eq!(
            facts
                .get("HotkeysHelpIcon")
                .and_then(|fact| fact.properties.get("font_size"))
                .map(String::as_str),
            Some("14")
        );
        assert_eq!(
            facts
                .get("ShowHotkeyBox")
                .and_then(|fact| fact.properties.get("width"))
                .map(String::as_str),
            Some("200")
        );
        assert_eq!(
            compare_code_property_value(
                "Margin",
                "0,0,0,4",
                facts
                    .get("ShowHotkeyEnabledToggle")
                    .and_then(|fact| fact.properties.get("margin"))
                    .map(String::as_str),
            ),
            "pass"
        );
        assert_eq!(
            compare_code_property_value(
                "VerticalAlignment",
                "Bottom",
                facts
                    .get("ShowHotkeyEnabledToggle")
                    .and_then(|fact| fact.properties.get("align_y"))
                    .map(String::as_str),
            ),
            "pass"
        );
        assert_eq!(
            facts
                .get("settings.content")
                .and_then(|fact| fact.properties.get("max_width"))
                .map(String::as_str),
            Some("1040")
        );
        assert_eq!(
            facts
                .get("settings.categories")
                .and_then(|fact| fact.properties.get("align_x"))
                .map(String::as_str),
            Some("Stretch")
        );
        assert_eq!(
            facts
                .get("settings.services")
                .and_then(|fact| fact.properties.get("spacing"))
                .map(String::as_str),
            Some("24")
        );
        assert_eq!(
            facts
                .get("settings.views")
                .and_then(|fact| fact.properties.get("spacing"))
                .map(String::as_str),
            Some("12")
        );
        assert_eq!(
            compare_code_property_value(
                "VerticalAlignment",
                "Bottom",
                facts
                    .get("SaveButton")
                    .and_then(|fact| fact.properties.get("align_y"))
                    .map(String::as_str),
            ),
            "pass"
        );
        assert_eq!(
            compare_code_property_value(
                "Margin",
                "0,0,32,32",
                facts
                    .get("SaveButton")
                    .and_then(|fact| fact.properties.get("margin"))
                    .map(String::as_str),
            ),
            "pass"
        );
    }

    #[test]
    fn code_parity_extracts_services_expander_scheme() {
        let xaml = r#"
<Expander HorizontalAlignment="Stretch">
  <Expander.Header><Grid /></Expander.Header>
  <StackPanel Spacing="12" Padding="0,8">
    <TextBlock />
  </StackPanel>
</Expander>
"#;
        let scheme = code_dotnet_service_expander_content_scheme(xaml);

        assert_eq!(scheme.spacing.as_deref(), Some("12"));
        assert_eq!(scheme.padding.as_deref(), Some("0,8"));
        assert_eq!(
            rust_service_expander_style_status(
                r#"
fn settings_service_expander_header_style(service_id: &str) -> Option<&'static str> {
    let _ = service_id;
    None
}
fn next() {}
"#,
                "settings_service_expander_header_style",
            ),
            "default"
        );
        assert_eq!(
            rust_service_expander_style_status(
                r#"
fn settings_service_expander_content_style(service_id: &str) -> Option<&'static str> {
    if service_id == "deepl" { Some("custom") } else { None }
}
fn next() {}
"#,
                "settings_service_expander_content_style",
            ),
            "custom"
        );
    }

    #[test]
    fn code_parity_infers_fixed_width_field_alignment() {
        let dir = tempdir().expect("temp dir");
        let rust_ui = dir.path().join("ui.rs");
        fs::write(
            &rust_ui,
            r#"
fn open_ai_service_expander(state: &SettingsState) -> View<Message> {
    settings_field_stack(
        "OpenAIEndpointField",
        450,
        vec![
            text_editor(state.open_ai_endpoint.clone())
                .id("OpenAIEndpointBox")
                .max_height(36)
                .into_view(),
        ],
    )
}
"#,
        )
        .expect("write ui");
        let options = CodeParityOptions {
            repo_root: dir.path().to_path_buf(),
            output_dir: dir.path().join("out"),
            dotnet_xaml: Vec::new(),
            dotnet_resources: Vec::new(),
            rust_ui,
            rust_theme: dir.path().join("theme.rs"),
            context_lines: 18,
            top: 20,
            fail_on_drift: false,
        };

        let facts = read_rust_ui_facts(&options).expect("read rust facts");
        let endpoint = facts.get("OpenAIEndpointBox").expect("endpoint fact");

        assert_eq!(
            endpoint.properties.get("width").map(String::as_str),
            Some("450")
        );
        assert_eq!(
            endpoint.properties.get("align_x").map(String::as_str),
            Some("Left")
        );
    }

    #[test]
    fn code_parity_groups_settings_services_focus_items() {
        let comparisons = vec![
            CodeLayoutComparison {
                status: "missing".to_string(),
                id: "TestDeepSeekButton".to_string(),
                dotnet_kind: "Button".to_string(),
                rust_kind: Some("button".to_string()),
                property: "Padding".to_string(),
                rust_property: "padding".to_string(),
                dotnet_value: "8,4".to_string(),
                rust_value: None,
                dotnet_location: "dotnet/src/Easydict.WinUI/Views/SettingsPage.xaml:703"
                    .to_string(),
                rust_location: Some("rs/crates/easydict_app/src/ui.rs:3361".to_string()),
                recommendation: "old".to_string(),
            },
            CodeLayoutComparison {
                status: "missing".to_string(),
                id: "DeepLServiceExpander".to_string(),
                dotnet_kind: "Expander".to_string(),
                rust_kind: None,
                property: "HorizontalAlignment".to_string(),
                rust_property: "align_x".to_string(),
                dotnet_value: "Stretch".to_string(),
                rust_value: None,
                dotnet_location: "dotnet/src/Easydict.WinUI/Views/SettingsPage.xaml:243"
                    .to_string(),
                rust_location: None,
                recommendation: "old".to_string(),
            },
            CodeLayoutComparison {
                status: "missing".to_string(),
                id: "AboutHeaderText".to_string(),
                dotnet_kind: "TextBlock".to_string(),
                rust_kind: None,
                property: "FontSize".to_string(),
                rust_property: "font_size".to_string(),
                dotnet_value: "18".to_string(),
                rust_value: None,
                dotnet_location: "dotnet/src/Easydict.WinUI/Views/SettingsPage.xaml:2069"
                    .to_string(),
                rust_location: None,
                recommendation: "old".to_string(),
            },
        ];

        let focus = code_settings_services_focus(&comparisons);

        assert_eq!(focus.len(), 2);
        assert_eq!(focus[0].priority, "P0");
        assert_eq!(focus[0].component, "expander bar");
        assert_eq!(focus[1].priority, "P1");
        assert_eq!(focus[1].component, "action button");
    }

    #[test]
    fn manifest_baseline_scenario_reports_interaction_effect_delta() {
        let dir = tempdir().expect("temp dir");
        let reference_baseline = dir.path().join(format!("settings.baseline{DOTNET_SUFFIX}"));
        let candidate_baseline = dir.path().join(format!("settings.baseline{RUST_SUFFIX}"));
        let reference_hover = dir.path().join(format!("settings.hover{DOTNET_SUFFIX}"));
        let candidate_hover = dir.path().join(format!("settings.hover{RUST_SUFFIX}"));
        create_synthetic_frame(false)
            .save(&reference_baseline)
            .expect("save reference baseline");
        create_synthetic_frame(false)
            .save(&candidate_baseline)
            .expect("save candidate baseline");
        create_synthetic_frame(true)
            .save(&reference_hover)
            .expect("save reference hover");
        create_synthetic_frame(false)
            .save(&candidate_hover)
            .expect("save candidate hover");

        let manifest_path = dir.path().join("ui-parity-manifest.json");
        let manifest = serde_json::json!({
            "SchemaVersion": "easydict.ui-parity.manifest.v1",
            "Scenarios": [
                {
                    "ScenarioId": "settings.baseline",
                    "WindowKind": "settings",
                    "SectionId": "general",
                    "SectionLabel": "General",
                    "Theme": "system",
                    "ScrollPercent": 0.0,
                    "ExpandAvailableLanguages": false,
                    "ReferenceScreenshot": reference_baseline.file_name().and_then(|value| value.to_str()).unwrap(),
                    "CandidateScreenshot": candidate_baseline.file_name().and_then(|value| value.to_str()).unwrap(),
                    "Regions": [],
                    "RequiredSemanticTags": []
                },
                {
                    "ScenarioId": "settings.hover",
                    "WindowKind": "settings",
                    "SectionId": "general",
                    "SectionLabel": "General",
                    "Theme": "system",
                    "ScrollPercent": 0.0,
                    "ExpandAvailableLanguages": false,
                    "ReferenceScreenshot": reference_hover.file_name().and_then(|value| value.to_str()).unwrap(),
                    "CandidateScreenshot": candidate_hover.file_name().and_then(|value| value.to_str()).unwrap(),
                    "Regions": [],
                    "RequiredSemanticTags": [],
                    "BaselineScenarioId": "settings.baseline"
                }
            ]
        });
        fs::write(&manifest_path, manifest.to_string()).expect("write manifest");
        let output = dir.path().join("out");

        let code = run([
            OsString::from("easydict_ui_parity_analyzer"),
            OsString::from("--screenshot-root"),
            dir.path().as_os_str().to_os_string(),
            OsString::from("--manifest"),
            manifest_path.as_os_str().to_os_string(),
            OsString::from("--output-dir"),
            output.as_os_str().to_os_string(),
        ])
        .expect("analyzer should run");

        assert_eq!(code, 0);
        let report_text =
            fs::read_to_string(output.join("ui-parity-report.json")).expect("report json");
        let report = serde_json::from_str::<Value>(&report_text).expect("report value");
        let scenario = report
            .get("Scenarios")
            .and_then(Value::as_array)
            .and_then(|items| {
                items.iter().find(|item| {
                    item.get("ScenarioId").and_then(Value::as_str) == Some("settings.hover")
                })
            })
            .expect("settings.hover scenario");
        let metrics = scenario.get("Metrics").expect("metrics");
        assert_eq!(
            metrics
                .get("EffectBaselineScenarioId")
                .and_then(Value::as_str),
            Some("settings.baseline")
        );
        assert!(
            metrics
                .get("ReferenceEffectPixelErrorPercent")
                .and_then(Value::as_f64)
                .unwrap_or_default()
                > 0.0
        );
        assert_eq!(
            metrics
                .get("CandidateEffectPixelErrorPercent")
                .and_then(Value::as_f64),
            Some(0.0)
        );
        assert!(
            metrics
                .get("InteractionEffectDeltaScore")
                .and_then(Value::as_f64)
                .unwrap_or(100.0)
                < 70.0
        );
        assert_eq!(scenario.get("Status").and_then(Value::as_str), Some("fail"));
        assert!(
            scenario
                .get("Score")
                .and_then(Value::as_f64)
                .unwrap_or(100.0)
                < 70.0
        );
        assert!(scenario
            .get("Findings")
            .and_then(Value::as_array)
            .is_some_and(|findings| findings.iter().any(|finding| {
                finding.get("Metric").and_then(Value::as_str) == Some("interactionEffectDeltaScore")
            })));

        let markdown =
            fs::read_to_string(output.join("ui-parity-report.md")).expect("report markdown");
        assert!(markdown.contains("Interaction effect delta: baseline `settings.baseline`"));
    }

    #[test]
    fn manifest_interaction_effect_roi_uses_required_state_bounds() {
        let dir = tempdir().expect("temp dir");
        let reference_baseline = dir.path().join(format!("settings.baseline{DOTNET_SUFFIX}"));
        let candidate_baseline = dir.path().join(format!("settings.baseline{RUST_SUFFIX}"));
        let reference_hover = dir.path().join(format!("settings.hover{DOTNET_SUFFIX}"));
        let candidate_hover = dir.path().join(format!("settings.hover{RUST_SUFFIX}"));
        create_synthetic_roi_effect_frame(false)
            .save(&reference_baseline)
            .expect("save reference baseline");
        create_synthetic_roi_effect_frame(false)
            .save(&candidate_baseline)
            .expect("save candidate baseline");
        create_synthetic_roi_effect_frame(true)
            .save(&reference_hover)
            .expect("save reference hover");
        create_synthetic_roi_effect_frame(false)
            .save(&candidate_hover)
            .expect("save candidate hover");

        let manifest_path = dir.path().join("ui-parity-manifest.json");
        let manifest = serde_json::json!({
            "SchemaVersion": "easydict.ui-parity.manifest.v1",
            "Scenarios": [
                {
                    "ScenarioId": "settings.baseline",
                    "WindowKind": "settings",
                    "SectionId": "general",
                    "SectionLabel": "General",
                    "Theme": "system",
                    "ScrollPercent": 0.0,
                    "ExpandAvailableLanguages": false,
                    "ReferenceScreenshot": reference_baseline.file_name().and_then(|value| value.to_str()).unwrap(),
                    "CandidateScreenshot": candidate_baseline.file_name().and_then(|value| value.to_str()).unwrap(),
                    "Regions": [],
                    "RequiredSemanticTags": []
                },
                {
                    "ScenarioId": "settings.hover",
                    "WindowKind": "settings",
                    "SectionId": "general",
                    "SectionLabel": "General",
                    "Theme": "system",
                    "ScrollPercent": 0.0,
                    "ExpandAvailableLanguages": false,
                    "ReferenceScreenshot": reference_hover.file_name().and_then(|value| value.to_str()).unwrap(),
                    "CandidateScreenshot": candidate_hover.file_name().and_then(|value| value.to_str()).unwrap(),
                    "Regions": [],
                    "RequiredSemanticTags": [],
                    "RequiredControlStates": {
                        "HoverTarget": ["hovered"]
                    },
                    "BaselineScenarioId": "settings.baseline",
                    "ReferenceWindow": {
                        "DpiScale": 1.0
                    },
                    "ReferenceUiSummary": {
                        "VisibleControlCounts": {},
                        "VisibleControlDimensions": {
                            "HoverTarget": {
                                "Kind": "Button",
                                "State": "enabled=true,hovered=true,pressed=false,focused=false,selected=false,validation=none",
                                "BoundsDips": {
                                    "Left": 20.0,
                                    "Top": 22.0,
                                    "Width": 20.0,
                                    "Height": 12.0
                                }
                            }
                        }
                    }
                }
            ]
        });
        fs::write(&manifest_path, manifest.to_string()).expect("write manifest");
        let output = dir.path().join("out");

        let code = run([
            OsString::from("easydict_ui_parity_analyzer"),
            OsString::from("--screenshot-root"),
            dir.path().as_os_str().to_os_string(),
            OsString::from("--manifest"),
            manifest_path.as_os_str().to_os_string(),
            OsString::from("--output-dir"),
            output.as_os_str().to_os_string(),
        ])
        .expect("analyzer should run");

        assert_eq!(code, 0);
        let report_text =
            fs::read_to_string(output.join("ui-parity-report.json")).expect("report json");
        let report = serde_json::from_str::<Value>(&report_text).expect("report value");
        let scenario = report
            .get("Scenarios")
            .and_then(Value::as_array)
            .and_then(|items| {
                items.iter().find(|item| {
                    item.get("ScenarioId").and_then(Value::as_str) == Some("settings.hover")
                })
            })
            .expect("settings.hover scenario");
        let metrics = scenario.get("Metrics").expect("metrics");
        assert!(
            metrics
                .get("InteractionEffectDeltaScore")
                .and_then(Value::as_f64)
                .unwrap_or_default()
                > 70.0
        );
        assert!(
            metrics
                .get("InteractionEffectRoiDeltaScore")
                .and_then(Value::as_f64)
                .unwrap_or(100.0)
                < 70.0
        );
        assert_eq!(scenario.get("Status").and_then(Value::as_str), Some("fail"));
        assert!(scenario
            .get("Findings")
            .and_then(Value::as_array)
            .is_some_and(|findings| findings.iter().any(|finding| {
                finding.get("Metric").and_then(Value::as_str)
                    == Some("interactionEffectRoiScoreCap")
            })));
        assert_eq!(
            metrics
                .get("InteractionEffectRoiTargetIds")
                .and_then(Value::as_array)
                .and_then(|items| items.first())
                .and_then(Value::as_str),
            Some("HoverTarget")
        );
        assert!(scenario
            .get("Findings")
            .and_then(Value::as_array)
            .is_some_and(|findings| findings.iter().any(|finding| {
                finding.get("Metric").and_then(Value::as_str)
                    == Some("interactionEffectRoiDeltaScore")
            })));

        let markdown =
            fs::read_to_string(output.join("ui-parity-report.md")).expect("report markdown");
        assert!(markdown.contains("ROI `HoverTarget`"));
        assert!(markdown.contains("`HoverTarget` reference Button"));
    }

    #[test]
    fn screenshot_summary_handles_missing_directory() {
        let dir = tempdir().expect("temp dir");
        let summary_path = dir.path().join("summary.md");
        let code = run([
            OsString::from("easydict_ui_parity_analyzer"),
            OsString::from("screenshot-summary"),
            OsString::from("--screenshot-root"),
            dir.path().join("missing").as_os_str().to_os_string(),
            OsString::from("--artifact-name"),
            OsString::from("ui-screenshots-test"),
            OsString::from("--summary-path"),
            summary_path.as_os_str().to_os_string(),
        ])
        .expect("summary should run");

        assert_eq!(code, 0);
        let summary = fs::read_to_string(summary_path).expect("summary markdown");
        assert!(summary.contains("No screenshot directory was produced."));
    }
}
