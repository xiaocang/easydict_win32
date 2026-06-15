use clap::{error::ErrorKind, Parser, Subcommand};
use ico::{IconDir, IconDirEntry, IconImage, ResourceType};
use image::imageops::{self, FilterType};
use image::{ImageFormat, Rgba, RgbaImage};
use std::ffi::OsString;
use std::fs::{self, File};
use std::path::{Path, PathBuf};

const DEFAULT_SIZES: &[u32] = &[16, 24, 32, 48, 64, 128, 256];
const TRAY_ICON_SIZE: u32 = 32;

pub fn run_cli(args: impl IntoIterator<Item = OsString>) -> i32 {
    match run(args) {
        Ok(code) => code,
        Err(error) => {
            eprintln!("Icon generation failed:");
            eprintln!("{error}");
            1
        }
    }
}

fn run(args: impl IntoIterator<Item = OsString>) -> Result<i32, String> {
    let options = match CliOptions::try_parse_from(args) {
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

    match options.command {
        Some(GeneratorCommand::WindowsAssets(command)) => {
            generate_windows_assets(WindowsAssetOptions {
                source_icon: command.source_icon,
                unplated_icon: command.unplated_icon,
                output_dir: command.output_dir,
            })?;
        }
        Some(GeneratorCommand::RefreshAssetsFromMacosIcon(command)) => {
            refresh_existing_windows_assets(RefreshAssetOptions {
                source_icon: command.source_icon,
                assets_dir: command.assets_dir,
            })?;
        }
        Some(GeneratorCommand::ServiceIcons(command)) => {
            convert_service_icons(ServiceIconOptions {
                source_dir: command.source_dir,
                output_dir: command.output_dir,
            })?;
        }
        None => {
            let sizes = if options.sizes.is_empty() {
                DEFAULT_SIZES.to_vec()
            } else {
                options.sizes
            };

            generate_app_icons(IconGenerationOptions {
                source_png: options
                    .source_png
                    .ok_or("Missing required --source-png for AppIcon.ico generation.")?,
                output_ico: options
                    .output_ico
                    .ok_or("Missing required --output-ico for AppIcon.ico generation.")?,
                output_tray_png: options.output_tray_png,
                sizes,
            })?;
        }
    }

    Ok(0)
}

#[derive(Parser, Debug)]
#[command(
    name = "easydict_icon_generator",
    about = "Generates Easydict Windows AppIcon.ico and TrayIcon.png from a source PNG.",
    disable_version_flag = true
)]
struct CliOptions {
    #[command(subcommand)]
    command: Option<GeneratorCommand>,
    #[arg(long, help = "Source PNG for legacy AppIcon.ico generation mode.")]
    source_png: Option<PathBuf>,
    #[arg(long, help = "Output ICO for legacy AppIcon.ico generation mode.")]
    output_ico: Option<PathBuf>,
    #[arg(long)]
    output_tray_png: Option<PathBuf>,
    #[arg(long, value_delimiter = ',', default_value = "16,24,32,48,64,128,256")]
    sizes: Vec<u32>,
}

#[derive(Subcommand, Debug)]
enum GeneratorCommand {
    #[command(name = "windows-assets")]
    WindowsAssets(WindowsAssetsCommand),
    #[command(name = "refresh-assets-from-macos-icon")]
    RefreshAssetsFromMacosIcon(RefreshAssetsCommand),
    #[command(name = "service-icons")]
    ServiceIcons(ServiceIconsCommand),
}

#[derive(Parser, Debug)]
struct WindowsAssetsCommand {
    #[arg(long)]
    source_icon: PathBuf,
    #[arg(long)]
    unplated_icon: PathBuf,
    #[arg(long)]
    output_dir: PathBuf,
}

#[derive(Parser, Debug)]
struct RefreshAssetsCommand {
    #[arg(long)]
    source_icon: PathBuf,
    #[arg(long)]
    assets_dir: PathBuf,
}

#[derive(Parser, Debug)]
struct ServiceIconsCommand {
    #[arg(long)]
    source_dir: PathBuf,
    #[arg(long)]
    output_dir: PathBuf,
}

#[derive(Debug, Clone)]
pub struct IconGenerationOptions {
    pub source_png: PathBuf,
    pub output_ico: PathBuf,
    pub output_tray_png: Option<PathBuf>,
    pub sizes: Vec<u32>,
}

pub fn generate_app_icons(options: IconGenerationOptions) -> Result<(), String> {
    let source_full = options
        .source_png
        .canonicalize()
        .map_err(|error| format!("Source PNG does not exist: {error}"))?;
    let sizes = normalize_sizes(&options.sizes)?;
    let source = image::open(&source_full)
        .map_err(|error| {
            format!(
                "Could not load source PNG {}: {error}",
                source_full.display()
            )
        })?
        .to_rgba8();

    println!("Source PNG : {}", source_full.display());
    println!("Output ICO : {}", options.output_ico.display());
    println!(
        "Sizes      : {}",
        sizes
            .iter()
            .map(u32::to_string)
            .collect::<Vec<_>>()
            .join(", ")
    );

    ensure_parent_dir(&options.output_ico)?;
    write_ico(&source, &sizes, &options.output_ico)?;

    if let Some(tray_path) = options.output_tray_png {
        println!("Generating TrayIcon.png...");
        ensure_parent_dir(&tray_path)?;
        let tray = resize_square(&source, TRAY_ICON_SIZE);
        tray.save_with_format(&tray_path, ImageFormat::Png)
            .map_err(|error| {
                format!("Could not write tray PNG {}: {error}", tray_path.display())
            })?;
        println!("TrayIcon.png saved to: {}", tray_path.display());
    }

    Ok(())
}

#[derive(Debug, Clone)]
pub struct WindowsAssetOptions {
    pub source_icon: PathBuf,
    pub unplated_icon: PathBuf,
    pub output_dir: PathBuf,
}

#[derive(Debug, Clone)]
pub struct RefreshAssetOptions {
    pub source_icon: PathBuf,
    pub assets_dir: PathBuf,
}

#[derive(Debug, Clone)]
pub struct ServiceIconOptions {
    pub source_dir: PathBuf,
    pub output_dir: PathBuf,
}

pub fn generate_windows_assets(options: WindowsAssetOptions) -> Result<(), String> {
    let source = load_rgba(&options.source_icon, "source icon")?;
    let unplated_source = if options.unplated_icon.exists() {
        load_rgba(&options.unplated_icon, "unplated icon")?
    } else {
        println!(
            "Unplated icon not found: {}; using source icon instead.",
            options.unplated_icon.display()
        );
        source.clone()
    };

    ensure_dir(&options.output_dir)?;
    println!("Generating multi-scale Windows assets...");
    println!("Source: {}", options.source_icon.display());
    println!("Output: {}", options.output_dir.display());

    const SQUARE_ASSETS: &[(&str, &[(u32, u32)])] = &[
        (
            "Square44x44Logo",
            &[(100, 44), (125, 55), (150, 66), (175, 77), (200, 88)],
        ),
        (
            "Square150x150Logo",
            &[(100, 150), (125, 188), (150, 225), (175, 263), (200, 300)],
        ),
        (
            "LockScreenLogo",
            &[(100, 24), (125, 30), (150, 36), (175, 42), (200, 48)],
        ),
        (
            "StoreLogo",
            &[(100, 50), (125, 63), (150, 75), (175, 88), (200, 100)],
        ),
    ];
    const WIDE_ASSETS: &[(&str, &[(u32, u32, u32)])] = &[
        (
            "Wide310x150Logo",
            &[
                (100, 310, 150),
                (125, 388, 188),
                (150, 465, 225),
                (175, 543, 263),
                (200, 620, 300),
            ],
        ),
        (
            "SplashScreen",
            &[
                (100, 620, 300),
                (125, 775, 375),
                (150, 930, 450),
                (175, 1085, 525),
                (200, 1240, 600),
            ],
        ),
    ];

    for (name, scales) in SQUARE_ASSETS {
        println!("Generating: {name}");
        for &(scale, size) in *scales {
            let output_path = options.output_dir.join(format!("{name}.scale-{scale}.png"));
            save_png(&resize_square(&source, size), &output_path)?;
            println!("  Created: {} ({} x {})", output_path.display(), size, size);
        }
    }

    for (name, scales) in WIDE_ASSETS {
        println!("Generating: {name}");
        for &(scale, width, height) in *scales {
            let output_path = options.output_dir.join(format!("{name}.scale-{scale}.png"));
            let asset = resize_centered_square_on_canvas(&source, width, height);
            save_png(&asset, &output_path)?;
            println!(
                "  Created: {} ({} x {})",
                output_path.display(),
                width,
                height
            );
        }
    }

    println!("Generating: Square44x44Logo targetsize variants");
    for size in [16, 24, 32, 48, 256] {
        let plated_path = options
            .output_dir
            .join(format!("Square44x44Logo.targetsize-{size}.png"));
        save_png(&resize_square(&source, size), &plated_path)?;

        let unplated_path = options.output_dir.join(format!(
            "Square44x44Logo.targetsize-{size}_altform-unplated.png"
        ));
        save_png(&resize_square(&unplated_source, size), &unplated_path)?;
    }

    Ok(())
}

pub fn refresh_existing_windows_assets(options: RefreshAssetOptions) -> Result<(), String> {
    let source = load_rgba(&options.source_icon, "source icon")?;
    let assets_dir = options
        .assets_dir
        .canonicalize()
        .map_err(|error| format!("Assets directory does not exist: {error}"))?;

    println!("Source icon: {}", options.source_icon.display());
    println!("Assets dir : {}", assets_dir.display());

    let mut targets = list_png_files(&assets_dir)?;
    targets.retain(|path| should_refresh_asset(path));
    targets.sort();

    for file in targets {
        let (width, height) = image::image_dimensions(&file)
            .map_err(|error| format!("Could not read dimensions {}: {error}", file.display()))?;
        let scale =
            if file_name_contains(&file, "Wide") || file_name_contains(&file, "SplashScreen") {
                0.70
            } else {
                1.0
            };
        let refreshed = resize_centered_scaled(&source, width, height, scale);
        save_png_atomically(&refreshed, &file)?;
        println!(
            "Updated {} -> {}x{} (scale {})",
            file.file_name()
                .map(|name| name.to_string_lossy())
                .unwrap_or_default(),
            width,
            height,
            scale
        );
    }

    Ok(())
}

pub fn convert_service_icons(options: ServiceIconOptions) -> Result<(), String> {
    let source_dir = options
        .source_dir
        .canonicalize()
        .map_err(|error| format!("Source directory does not exist: {error}"))?;
    ensure_dir(&options.output_dir)?;

    println!("[Service Icon Converter] Source: {}", source_dir.display());
    println!(
        "[Service Icon Converter] Output: {}",
        options.output_dir.display()
    );

    let mut imagesets = fs::read_dir(&source_dir)
        .map_err(|error| {
            format!(
                "Could not read source directory {}: {error}",
                source_dir.display()
            )
        })?
        .filter_map(Result::ok)
        .map(|entry| entry.path())
        .filter(|path| path.is_dir())
        .filter(|path| {
            path.file_name()
                .and_then(|name| name.to_str())
                .map(|name| name.ends_with(".imageset"))
                .unwrap_or(false)
        })
        .collect::<Vec<_>>();
    imagesets.sort();

    println!(
        "[Service Icon Converter] Found {} service icons to convert",
        imagesets.len()
    );

    let mut processed_services = 0;
    let mut processed_icons = 0;

    for imageset in imagesets {
        let service_name = imageset
            .file_name()
            .and_then(|name| name.to_str())
            .and_then(|name| name.strip_suffix(".imageset"))
            .ok_or_else(|| format!("Invalid imageset name: {}", imageset.display()))?
            .to_string();

        let Some(source_file) = find_service_icon_source(&imageset)? else {
            println!("  [SKIP] {service_name} - No PNG file found");
            continue;
        };
        let source = load_rgba(&source_file, "service icon")?;
        println!(
            "[Service Icon Converter] Processing: {service_name} ({})",
            source_file
                .file_name()
                .map(|name| name.to_string_lossy())
                .unwrap_or_default()
        );

        for scale in [100, 125, 150, 175, 200] {
            let target_size = 32 * scale / 100;
            let output_path = options
                .output_dir
                .join(format!("{service_name}.scale-{scale}.png"));
            save_png(&resize_square(&source, target_size), &output_path)?;
            processed_icons += 1;
        }

        processed_services += 1;
    }

    println!("[Service Icon Converter] Processed: {processed_services} services");
    println!("[Service Icon Converter] Generated: {processed_icons} icon files");

    Ok(())
}

fn normalize_sizes(sizes: &[u32]) -> Result<Vec<u32>, String> {
    if sizes.is_empty() {
        return Err("At least one icon size is required.".to_string());
    }

    let mut normalized = Vec::with_capacity(sizes.len());
    for &size in sizes {
        if !(1..=256).contains(&size) {
            return Err(format!(
                "Icon size must be between 1 and 256 pixels: {size}."
            ));
        }

        if !normalized.contains(&size) {
            normalized.push(size);
        }
    }

    Ok(normalized)
}

fn write_ico(source: &RgbaImage, sizes: &[u32], output_path: &Path) -> Result<(), String> {
    let mut icon_dir = IconDir::new(ResourceType::Icon);
    for &size in sizes {
        let resized = resize_square(source, size);
        let image = IconImage::from_rgba_data(size, size, resized.into_raw());
        let entry = IconDirEntry::encode_as_png(&image)
            .map_err(|error| format!("Could not encode {size}x{size} ICO entry: {error}"))?;
        icon_dir.add_entry(entry);
    }

    let file = File::create(output_path)
        .map_err(|error| format!("Could not create ICO {}: {error}", output_path.display()))?;
    icon_dir
        .write(file)
        .map_err(|error| format!("Could not write ICO {}: {error}", output_path.display()))
}

fn resize_square(source: &RgbaImage, size: u32) -> RgbaImage {
    imageops::resize(source, size, size, FilterType::Lanczos3)
}

fn resize_exact(source: &RgbaImage, width: u32, height: u32) -> RgbaImage {
    imageops::resize(source, width, height, FilterType::Lanczos3)
}

fn resize_centered_square_on_canvas(source: &RgbaImage, width: u32, height: u32) -> RgbaImage {
    let icon_size = height.max(1);
    let x_offset = ((width.saturating_sub(icon_size)) / 2) as i64;
    let resized = resize_exact(source, icon_size, icon_size);
    let mut canvas = transparent_canvas(width, height);
    imageops::overlay(&mut canvas, &resized, x_offset, 0);
    canvas
}

fn resize_centered_scaled(source: &RgbaImage, width: u32, height: u32, scale: f64) -> RgbaImage {
    let max_side = width.min(height).max(1);
    let icon_side = ((max_side as f64) * scale).round().max(1.0) as u32;
    let ratio =
        (icon_side as f64 / source.width() as f64).min(icon_side as f64 / source.height() as f64);
    let draw_width = ((source.width() as f64) * ratio).round().max(1.0) as u32;
    let draw_height = ((source.height() as f64) * ratio).round().max(1.0) as u32;
    let x = ((width.saturating_sub(draw_width)) / 2) as i64;
    let y = ((height.saturating_sub(draw_height)) / 2) as i64;

    let resized = resize_exact(source, draw_width, draw_height);
    let mut canvas = transparent_canvas(width, height);
    imageops::overlay(&mut canvas, &resized, x, y);
    canvas
}

fn transparent_canvas(width: u32, height: u32) -> RgbaImage {
    RgbaImage::from_pixel(width, height, Rgba([0, 0, 0, 0]))
}

fn load_rgba(path: &Path, label: &str) -> Result<RgbaImage, String> {
    image::open(path)
        .map_err(|error| format!("Could not load {label} {}: {error}", path.display()))
        .map(|image| image.to_rgba8())
}

fn save_png(image: &RgbaImage, path: &Path) -> Result<(), String> {
    ensure_parent_dir(path)?;
    image
        .save_with_format(path, ImageFormat::Png)
        .map_err(|error| format!("Could not write PNG {}: {error}", path.display()))
}

fn save_png_atomically(image: &RgbaImage, path: &Path) -> Result<(), String> {
    let tmp_path = path.with_extension(format!(
        "{}.tmp",
        path.extension()
            .and_then(|extension| extension.to_str())
            .unwrap_or("png")
    ));
    save_png(image, &tmp_path)?;
    fs::copy(&tmp_path, path)
        .map_err(|error| format!("Could not replace PNG {}: {error}", path.display()))?;
    fs::remove_file(&tmp_path)
        .map_err(|error| format!("Could not remove temp PNG {}: {error}", tmp_path.display()))
}

fn ensure_parent_dir(path: &Path) -> Result<(), String> {
    if let Some(parent) = path
        .parent()
        .filter(|parent| !parent.as_os_str().is_empty())
    {
        fs::create_dir_all(parent).map_err(|error| {
            format!(
                "Could not create output directory {}: {error}",
                parent.display()
            )
        })?;
    }

    Ok(())
}

fn ensure_dir(path: &Path) -> Result<(), String> {
    fs::create_dir_all(path)
        .map_err(|error| format!("Could not create directory {}: {error}", path.display()))
}

fn list_png_files(dir: &Path) -> Result<Vec<PathBuf>, String> {
    Ok(fs::read_dir(dir)
        .map_err(|error| format!("Could not read directory {}: {error}", dir.display()))?
        .filter_map(Result::ok)
        .map(|entry| entry.path())
        .filter(|path| path.is_file() && is_png_file(path))
        .collect())
}

fn should_refresh_asset(path: &Path) -> bool {
    let name = path
        .file_name()
        .and_then(|name| name.to_str())
        .unwrap_or_default();
    name != "icon_unplated_1024.png" && !name.contains("_altform-unplated")
}

fn file_name_contains(path: &Path, needle: &str) -> bool {
    path.file_name()
        .and_then(|name| name.to_str())
        .map(|name| name.contains(needle))
        .unwrap_or(false)
}

fn find_service_icon_source(imageset: &Path) -> Result<Option<PathBuf>, String> {
    let mut pngs = fs::read_dir(imageset)
        .map_err(|error| format!("Could not read imageset {}: {error}", imageset.display()))?
        .filter_map(Result::ok)
        .map(|entry| entry.path())
        .filter(|path| path.is_file() && is_png_file(path))
        .collect::<Vec<_>>();
    pngs.sort();

    Ok(pngs
        .iter()
        .find(|path| file_name_contains(path, "@2x"))
        .cloned()
        .or_else(|| pngs.first().cloned()))
}

fn is_png_file(path: &Path) -> bool {
    path.extension()
        .and_then(|extension| extension.to_str())
        .map(|extension| extension.eq_ignore_ascii_case("png"))
        .unwrap_or(false)
}

#[cfg(test)]
mod tests {
    use super::*;
    use tempfile::tempdir;

    #[test]
    fn build_time_app_icon_generation_uses_rust_tool_not_system_drawing_script() {
        let workspace_manifest = include_str!("../../../Cargo.toml");
        assert!(workspace_manifest.contains("crates/easydict_icon_generator"));

        let winui_project =
            include_str!("../../../../dotnet/src/Easydict.WinUI/Easydict.WinUI.csproj");
        assert!(winui_project.contains("cargo run --manifest-path"));
        assert!(winui_project.contains("-p easydict_icon_generator"));
        assert!(winui_project.contains("--source-png"));
        assert!(winui_project.contains("--output-ico"));
        assert!(winui_project.contains("--output-tray-png"));
        assert!(!winui_project.contains("generate-app-icon-ico.ps1"));
        assert!(!winui_project.contains("System.Drawing"));

        let repo_root = Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("..")
            .join("..")
            .join("..");
        assert!(!repo_root
            .join("dotnet/scripts/generate-app-icon-ico.ps1")
            .exists());
    }

    #[test]
    fn asset_generation_shims_use_rust_icon_generator_not_system_drawing() {
        let scripts = [
            (
                include_str!("../../../../dotnet/scripts/generate-windows-assets.ps1"),
                "windows-assets",
            ),
            (
                include_str!("../../../../dotnet/scripts/generate-assets-from-macos-icon.ps1"),
                "refresh-assets-from-macos-icon",
            ),
            (
                include_str!("../../../../dotnet/scripts/convert-service-icons.ps1"),
                "service-icons",
            ),
        ];

        for (script, subcommand) in scripts {
            assert!(script.contains("cargo run --manifest-path"));
            assert!(script.contains("-p easydict_icon_generator"));
            assert!(script.contains(subcommand));
            assert!(!script.contains("System.Drawing"));
            assert!(!script.contains("Add-Type -AssemblyName"));
        }
    }

    #[test]
    fn generates_png_encoded_ico_entries_and_tray_icon() {
        let dir = tempdir().expect("temp dir");
        let source = dir.path().join("source.png");
        let ico_path = dir.path().join("AppIcon.ico");
        let tray_path = dir.path().join("TrayIcon.png");
        create_source_icon().save(&source).expect("source png");

        generate_app_icons(IconGenerationOptions {
            source_png: source,
            output_ico: ico_path.clone(),
            output_tray_png: Some(tray_path.clone()),
            sizes: vec![16, 32, 256],
        })
        .expect("icons should generate");

        let icon_dir = IconDir::read(File::open(&ico_path).expect("open ico")).expect("read ico");
        let sizes = icon_dir
            .entries()
            .iter()
            .map(|entry| (entry.width(), entry.height(), entry.is_png()))
            .collect::<Vec<_>>();
        assert_eq!(
            sizes,
            vec![(16, 16, true), (32, 32, true), (256, 256, true)]
        );

        let tray = image::open(&tray_path).expect("open tray").to_rgba8();
        assert_eq!(
            (tray.width(), tray.height()),
            (TRAY_ICON_SIZE, TRAY_ICON_SIZE)
        );
    }

    #[test]
    fn rejects_invalid_sizes() {
        let error = normalize_sizes(&[16, 0, 257]).expect_err("invalid sizes should fail");
        assert!(error.contains("between 1 and 256"));
    }

    #[test]
    fn help_returns_zero_without_process_exit() {
        let code = run([
            OsString::from("easydict_icon_generator"),
            OsString::from("--help"),
        ])
        .expect("help should be handled");
        assert_eq!(code, 0);
    }

    #[test]
    fn generates_windows_asset_dimensions() {
        let dir = tempdir().expect("temp dir");
        let source = dir.path().join("source.png");
        let unplated = dir.path().join("unplated.png");
        let output = dir.path().join("assets");
        create_source_icon().save(&source).expect("source png");
        create_source_icon().save(&unplated).expect("unplated png");

        generate_windows_assets(WindowsAssetOptions {
            source_icon: source,
            unplated_icon: unplated,
            output_dir: output.clone(),
        })
        .expect("windows assets");

        assert_png_dimensions(&output.join("Square44x44Logo.scale-100.png"), 44, 44);
        assert_png_dimensions(&output.join("Wide310x150Logo.scale-100.png"), 310, 150);
        assert_png_dimensions(
            &output.join("Square44x44Logo.targetsize-24_altform-unplated.png"),
            24,
            24,
        );
    }

    #[test]
    fn refreshes_existing_assets_with_matching_dimensions() {
        let dir = tempdir().expect("temp dir");
        let source = dir.path().join("source.png");
        let assets = dir.path().join("assets");
        fs::create_dir_all(&assets).expect("assets dir");
        create_source_icon().save(&source).expect("source png");
        image::RgbaImage::new(44, 44)
            .save(assets.join("Square44x44Logo.scale-100.png"))
            .expect("square target");
        image::RgbaImage::new(310, 150)
            .save(assets.join("Wide310x150Logo.scale-100.png"))
            .expect("wide target");
        image::RgbaImage::new(1024, 1024)
            .save(assets.join("icon_unplated_1024.png"))
            .expect("excluded unplated");

        refresh_existing_windows_assets(RefreshAssetOptions {
            source_icon: source,
            assets_dir: assets.clone(),
        })
        .expect("refresh assets");

        assert_png_dimensions(&assets.join("Square44x44Logo.scale-100.png"), 44, 44);
        assert_png_dimensions(&assets.join("Wide310x150Logo.scale-100.png"), 310, 150);
        assert_png_dimensions(&assets.join("icon_unplated_1024.png"), 1024, 1024);
    }

    #[test]
    fn converts_service_imagesets_to_scale_variants() {
        let dir = tempdir().expect("temp dir");
        let source_dir = dir.path().join("service-icon");
        let google_set = source_dir.join("google.imageset");
        let output_dir = dir.path().join("ServiceIcons");
        fs::create_dir_all(&google_set).expect("imageset dir");
        create_source_icon()
            .save(google_set.join("google@2x.png"))
            .expect("source service icon");

        convert_service_icons(ServiceIconOptions {
            source_dir,
            output_dir: output_dir.clone(),
        })
        .expect("service icons");

        assert_png_dimensions(&output_dir.join("google.scale-100.png"), 32, 32);
        assert_png_dimensions(&output_dir.join("google.scale-175.png"), 56, 56);
        assert_png_dimensions(&output_dir.join("google.scale-200.png"), 64, 64);
    }

    #[test]
    fn asset_subcommands_parse_without_process_exit() {
        let dir = tempdir().expect("temp dir");
        let source = dir.path().join("source.png");
        let output = dir.path().join("assets");
        create_source_icon().save(&source).expect("source png");

        let code = run([
            OsString::from("easydict_icon_generator"),
            OsString::from("windows-assets"),
            OsString::from("--source-icon"),
            source.clone().into_os_string(),
            OsString::from("--unplated-icon"),
            source.into_os_string(),
            OsString::from("--output-dir"),
            output.into_os_string(),
        ])
        .expect("subcommand should run");

        assert_eq!(code, 0);
    }

    fn create_source_icon() -> RgbaImage {
        let mut image = RgbaImage::new(64, 64);
        for y in 0..64 {
            for x in 0..64 {
                let alpha = if x < 4 || y < 4 { 0 } else { 255 };
                image.put_pixel(
                    x,
                    y,
                    Rgba([(x * 3).min(255) as u8, (y * 3).min(255) as u8, 220, alpha]),
                );
            }
        }
        image
    }

    fn assert_png_dimensions(path: &Path, width: u32, height: u32) {
        let dimensions = image::image_dimensions(path).expect("png dimensions");
        assert_eq!(dimensions, (width, height));
    }
}
