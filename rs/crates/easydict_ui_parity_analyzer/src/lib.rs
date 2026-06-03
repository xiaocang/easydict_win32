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

    for pair in discover_pairs(&options.screenshot_root)? {
        if seen.insert(pair.scenario_id.to_ascii_lowercase()) {
            pairs.push(pair);
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
    let size_score = calculate_size_score(
        reference.width(),
        reference.height(),
        candidate_original.width(),
        candidate_original.height(),
    );
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
    let final_score = visual_score.min(runtime_score_cap);
    let gate = resolve_score_gate(pair, &regions, runtime_score_cap, options);
    let status = ScoreStatus::from_score(final_score, gate.pass_score, gate.warn_score);

    let findings = build_findings(
        pair,
        &scoring_profile,
        &gate,
        status,
        final_score,
        visual_score,
        runtime_score_cap,
        &full_pixel,
        full_ssim,
        hash_score,
        size_score,
        &palette,
        ui_summary.as_ref(),
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
            automation_id_jaccard: ui_summary
                .as_ref()
                .and_then(|value| value.automation_id_jaccard.map(round2)),
            missing_required_semantic_tag_count: ui_summary
                .as_ref()
                .map(|value| value.missing_required_semantic_tags.len()),
            scoring_profile: scoring_profile.id.clone(),
            region_score: round2(region_score),
            visual_score: round2(visual_score),
            window_runtime_score_cap: (runtime_score_cap < 100.0)
                .then_some(round2(runtime_score_cap)),
        },
        regions,
        findings,
    })
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

fn compare_ui_summaries(metadata: Option<&ManifestScenario>) -> Option<UiSummaryComparison> {
    let metadata = metadata?;
    let reference_counts = &metadata
        .reference_ui_summary
        .as_ref()?
        .visible_control_counts;
    let candidate_counts = &metadata
        .candidate_ui_summary
        .as_ref()?
        .visible_control_counts;
    let reference_ids = to_case_insensitive_set(
        metadata
            .reference_ui_summary
            .as_ref()
            .and_then(|summary| summary.visible_automation_ids.as_ref()),
    );
    let candidate_ids = to_case_insensitive_set(
        metadata
            .candidate_ui_summary
            .as_ref()
            .and_then(|summary| summary.visible_automation_ids.as_ref()),
    );
    let required_tags = metadata
        .required_semantic_tags
        .iter()
        .filter(|tag| !tag.trim().is_empty())
        .map(|tag| tag.to_ascii_lowercase())
        .collect::<BTreeSet<_>>();
    let missing_required_tags = required_tags
        .iter()
        .filter(|tag| !candidate_ids.contains(*tag))
        .cloned()
        .collect::<Vec<_>>();
    let reference_total_empty =
        reference_counts.values().sum::<i32>() == 0 && reference_ids.is_empty();
    if reference_total_empty && !required_tags.is_empty() {
        let tag_only_score = clamp_score(100.0 - (missing_required_tags.len() as f64 * 35.0));
        return Some(UiSummaryComparison {
            score: tag_only_score,
            control_count_delta_percent: 0.0,
            automation_id_jaccard: None,
            missing_required_semantic_tags: missing_required_tags,
        });
    }

    let mut keys = BTreeSet::new();
    keys.extend(reference_counts.keys().map(|key| key.to_ascii_lowercase()));
    keys.extend(candidate_counts.keys().map(|key| key.to_ascii_lowercase()));
    if keys.is_empty() {
        return None;
    }

    let mut reference_total = 0_i32;
    let mut delta_total = 0_i32;
    for key in keys {
        let reference_count = get_case_insensitive_count(reference_counts, &key);
        let candidate_count = get_case_insensitive_count(candidate_counts, &key);
        reference_total += reference_count;
        delta_total += (reference_count - candidate_count).abs();
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
    let required_tag_score = if required_tags.is_empty() {
        None
    } else {
        Some(clamp_score(
            100.0 - (missing_required_tags.len() as f64 * 35.0),
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
    Some(UiSummaryComparison {
        score: clamp_score(weighted / weight),
        control_count_delta_percent: delta_percent,
        automation_id_jaccard,
        missing_required_semantic_tags: missing_required_tags,
    })
}

fn get_case_insensitive_count(map: &BTreeMap<String, i32>, key: &str) -> i32 {
    map.iter()
        .find(|(actual, _)| actual.eq_ignore_ascii_case(key))
        .map(|(_, value)| *value)
        .unwrap_or_default()
}

fn to_case_insensitive_set(values: Option<&Vec<String>>) -> BTreeSet<String> {
    values
        .into_iter()
        .flatten()
        .map(|value| value.to_ascii_lowercase())
        .collect()
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
    scoring_profile: &ScenarioScoringProfile,
    gate: &ScenarioScoreGate,
    status: ScoreStatus,
    score: f64,
    visual_score: f64,
    runtime_score_cap: f64,
    pixel: &PixelComparison,
    ssim: f64,
    hash_score: f64,
    size_score: f64,
    palette: &PaletteComparison,
    ui_summary: Option<&UiSummaryComparison>,
    regions: &[RegionResult],
) -> Vec<Finding> {
    let mut findings = Vec::new();
    match status {
        ScoreStatus::Fail => findings.push(Finding {
            severity: "error".to_string(),
            layer_hint: if runtime_score_cap < visual_score {
                "window_runtime".to_string()
            } else {
                "final_effect".to_string()
            },
            message: if runtime_score_cap < visual_score {
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
            layer_hint: "final_effect".to_string(),
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
            message: "Candidate image dimensions differ from the reference.".to_string(),
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
                message: "UI Automation summaries differ or required semantic tags are missing."
                    .to_string(),
                metric: "semanticScore".to_string(),
                value: round2(summary.score),
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
    out.push_str("| Status | Score | Scenario | Gate | Pass | Warn | Profile | Pixel error | SSIM | Hash score | Runtime cap | Worst region | Diff |\n");
    out.push_str(
        "| --- | ---: | --- | --- | ---: | ---: | --- | ---: | ---: | ---: | ---: | --- | --- |\n",
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
        out.push_str(&format!(
            "| {:?} | {:.2} | `{}` | {} `{}/{}` | {:.2} | {:.2} | {} | {:.2}% | {:.3} | {:.2} | {} | {} | [heatmap]({}) |\n",
            scenario.status,
            scenario.score,
            scenario.scenario_id,
            if scenario.gate.source == "score-gate" { "gate" } else { "default" },
            scenario.gate.layer,
            scenario.gate.case,
            scenario.gate.pass_score,
            scenario.gate.warn_score,
            scenario.metrics.scoring_profile,
            scenario.metrics.pixel_error_percent,
            scenario.metrics.ssim,
            scenario.metrics.hash_score,
            runtime_cap,
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
        .filter(|scenario| scenario.status != ScoreStatus::Pass)
        .map(|scenario| LlmReviewRequest {
            schema_version: "easydict.ui-parity.llm-review.v1".to_string(),
            scenario_id: scenario.scenario_id.clone(),
            status: scenario.status,
            score: scenario.score,
            reference_image: scenario.normalized_reference_path.clone(),
            candidate_image: scenario.normalized_candidate_path.clone(),
            diff_heatmap: scenario.diff_heatmap_path.clone(),
            contact_sheet: scenario.contact_sheet_path.clone(),
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
        out.push_str("No warn/fail scenarios need LLM review.\n");
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
            "Metrics: pixel error `{:.4}%`, SSIM `{:.5}`, hash score `{:.2}`, size score `{:.2}`, palette score `{:.2}`, visual score `{:.2}`, runtime cap `{}`.\n\n",
            request.metrics.pixel_error_percent,
            request.metrics.ssim,
            request.metrics.hash_score,
            request.metrics.size_score,
            request.metrics.palette_score,
            request.metrics.visual_score,
            request
                .metrics
                .window_runtime_score_cap
                .map(|value| format!("{value:.2}"))
                .unwrap_or_else(|| "n/a".to_string())
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
    let animation_reference_path =
        root.join(format!("effects.collapse-expand-animation{DOTNET_SUFFIX}"));
    let animation_candidate_path =
        root.join(format!("effects.collapse-expand-animation{RUST_SUFFIX}"));
    let service_reference_path = root.join(format!("long-doc.service-dropdown{DOTNET_SUFFIX}"));
    let service_candidate_path = root.join(format!("long-doc.service-dropdown{RUST_SUFFIX}"));
    let pop_reference_path = root.join(format!("popbutton.hover{DOTNET_SUFFIX}"));
    let pop_candidate_path = root.join(format!("popbutton.hover{RUST_SUFFIX}"));
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
        .save(&animation_reference_path)
        .map_err(|e| e.to_string())?;
    create_synthetic_frame(false)
        .save(&animation_candidate_path)
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
        && scenario("effects.collapse-expand-animation").is_some_and(|scenario| {
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
        && coverage_item("effects.floating-action-pressed")
            .is_some_and(|item| item.evidence_status == CoverageEvidenceStatus::Missing.id());

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
    automation_id_jaccard: Option<f64>,
    missing_required_semantic_tags: Vec<String>,
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
    automation_id_jaccard: Option<f64>,
    missing_required_semantic_tag_count: Option<usize>,
    scoring_profile: String,
    region_score: f64,
    visual_score: f64,
    window_runtime_score_cap: Option<f64>,
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
    reference_window: Option<ManifestWindow>,
    candidate_window: Option<ManifestWindow>,
    regions: Vec<ManifestRegion>,
    required_semantic_tags: Vec<String>,
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
            item("interaction-effects", "effects.source-input-hover", "Source input hover", CoveragePriority::Critical, &["source", "input", "hover"], &[]),
            item("interaction-effects", "effects.result-header-hover", "Result header hover", CoveragePriority::Critical, &["result", "header", "hover"], &[]),
            item("interaction-effects", "effects.settings-tab-hover", "Settings tab hover", CoveragePriority::Critical, &["settings", "tabs", "hover"], &["settings"]),
            item("interaction-effects", "effects.settings-slider-focus", "Settings TTS speed slider focus", CoveragePriority::Normal, &["tts", "speed", "slider", "focus"], &["settings"]).with_next("Add side-by-side screenshot evidence for the Settings TTS speed slider keyboard focus ring."),
            item("interaction-effects", "effects.settings-toggle-focus", "Settings auto-play toggle focus", CoveragePriority::Normal, &["auto", "play", "toggle", "focus"], &["settings"]).with_next("Add side-by-side screenshot evidence for the Settings auto-play toggle keyboard focus ring."),
            item("interaction-effects", "effects.floating-action-hover", "Floating action hover", CoveragePriority::Critical, &["floating", "action", "hover"], &[]),
            item("interaction-effects", "effects.floating-action-pressed", "Floating action pressed", CoveragePriority::Normal, &["floating", "action", "pressed"], &[]),
            item("interaction-effects", "effects.collapse-expand-animation", "Result collapse/expand animation", CoveragePriority::Critical, &["collapse", "expand"], &[]),
            item("floating", "mini.initial", "Mini window initial", CoveragePriority::Critical, &["mini", "initial"], &["mini"]),
            item("floating", "mini.after-translate", "Mini window after translate", CoveragePriority::Critical, &["mini", "after", "translate"], &["mini"]),
            item("floating", "mini.streaming", "Mini window streaming", CoveragePriority::Critical, &["mini", "streaming"], &["mini"]),
            item("floating", "fixed.initial", "Fixed window initial", CoveragePriority::Critical, &["fixed", "initial"], &["fixed"]),
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
        reference_window: get_object(value, "ReferenceWindow")
            .map(parse_manifest_window)
            .transpose()?,
        candidate_window: get_object(value, "CandidateWindow")
            .map(parse_manifest_window)
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
    Ok(ManifestUiSummary {
        visible_control_counts,
        visible_automation_ids,
    })
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

    #[test]
    fn self_test_passes() {
        assert!(run_self_test().expect("self-test should run"));
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
        assert!(prompts.contains("Findings:"));
        assert!(prompts.contains("Lowest scoring regions:"));
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
