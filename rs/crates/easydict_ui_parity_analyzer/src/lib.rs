use base64::Engine;
use clap::{error::ErrorKind, Parser, Subcommand};
use image::codecs::jpeg::JpegEncoder;
use image::imageops::{self, FilterType};
use image::{ColorType, DynamicImage, Rgba, RgbaImage};
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

    let findings = build_findings(
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

    Ok(ScenarioResult {
        scenario_id: pair.scenario_id.clone(),
        status,
        score: round2(final_score),
        reference_path: relative_path(&options.output_dir, &pair.reference_path),
        candidate_path: relative_path(&options.output_dir, &pair.candidate_path),
        normalized_reference_path: relative_path(&options.output_dir, &normalized_reference_path),
        normalized_candidate_path: relative_path(&options.output_dir, &normalized_candidate_path),
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
    runtime_score_cap: f64,
    control_dimension_score_cap: f64,
    absolute_size_score_cap: f64,
) -> Option<f64> {
    if scoring_profile.id != "default-semantic"
        || runtime_score_cap < 99.0
        || control_dimension_score_cap < 99.0
        || absolute_size_score_cap < 99.0
    {
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
    let has_target_audit = reference_target_cap.is_some() || candidate_target_cap.is_some();
    let observed_delta_percent = if has_target_audit {
        image_delta_percent
    } else {
        image_delta_percent.max(window_delta_percent)
    };
    let observed_cap = absolute_size_score_cap_from_delta_percent(observed_delta_percent);
    [
        Some(observed_cap),
        reference_target_cap,
        candidate_target_cap,
    ]
    .into_iter()
    .flatten()
    .fold(100.0_f64, f64::min)
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
    out.push_str("\n## Findings\n\n");
    for scenario in &report.scenarios {
        out.push_str(&format!("### {}\n\n", scenario.scenario_id));
        out.push_str(&format!(
            "- Contact sheet: [{}]({})\n",
            scenario.contact_sheet_path, scenario.contact_sheet_path
        ));
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
    diff_heatmap_path: String,
    contact_sheet_path: String,
    reference_size: ImageSize,
    candidate_size: ImageSize,
    metadata: Option<ManifestScenario>,
    gate: ScenarioScoreGate,
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
    area: &'static str,
    id: &'static str,
    display_name: &'static str,
    priority: CoveragePriority,
    match_terms: &'static [&'static str],
    window_kinds: &'static [&'static str],
    layer_hint: &'static str,
    next_evidence: &'static str,
}

struct CoverageCatalog;

impl CoverageCatalog {
    fn items() -> Vec<ExpectedCoverageItem> {
        vec![
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
            item("interaction-effects", "effects.floating-action-pressed", "Floating action pressed", CoveragePriority::Normal, &["pressed"], &["mini", "fixed", "popbutton"]),
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
        ]
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
        area,
        id,
        display_name,
        priority,
        match_terms,
        window_kinds,
        layer_hint: "final_effect",
        next_evidence: "Add a dotnet/rust screenshot pair and manifest entry for this scenario.",
    }
}

impl ExpectedCoverageItem {
    fn with_layer(mut self, layer_hint: &'static str) -> Self {
        self.layer_hint = layer_hint;
        self
    }

    fn with_next(mut self, next_evidence: &'static str) -> Self {
        self.next_evidence = next_evidence;
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
    let expected_id = normalize_search(expected.id);
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
                100.0,
                100.0,
                100.0,
            ),
            Some(SEMANTIC_CONTRACT_SCORE_FLOOR)
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
        let visual_profile = ScenarioScoringProfile::new(
            "default-visual",
            0.42,
            0.18,
            0.24,
            0.0,
            0.08,
            0.08,
            70.0,
        );
        assert_eq!(
            calculate_semantic_contract_score_floor(
                &visual_profile,
                Some(&fully_aligned_ui_summary()),
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
            calculate_semantic_contract_score_floor(&profile, Some(&low_text), 100.0, 100.0, 100.0),
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
        create_synthetic_frame(false)
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
        assert!(scenario
            .get("Findings")
            .and_then(Value::as_array)
            .is_some_and(|findings| findings.iter().any(|finding| {
                finding.get("Metric").and_then(Value::as_str)
                    == Some("referenceCandidateDpiScaleDelta")
                    && finding.get("LayerHint").and_then(Value::as_str) == Some("evidence_quality")
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
