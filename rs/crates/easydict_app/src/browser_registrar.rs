use std::collections::BTreeMap;
use std::fmt;
use std::fs;
use std::io;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};

pub const NATIVE_HOST_NAME: &str = "com.easydict.bridge";
pub const BRIDGE_EXE_NAME: &str = "easydict-native-bridge.exe";
pub const CHROME_MANIFEST_FILE: &str = "chrome-manifest.json";
pub const FIREFOX_MANIFEST_FILE: &str = "firefox-manifest.json";
pub const LEGACY_BRIDGE_ROOT_NAME: &str = "Easydict";
pub const RUST_BRIDGE_ROOT_NAME: &str = "EasydictRs";
pub const DEFAULT_BRIDGE_ROOT_NAME: &str = RUST_BRIDGE_ROOT_NAME;
pub const DEFAULT_CHROME_EXT_IDS: &str =
    "dmokdfinnomehfpmhoeekomncpobgagf,cbhpnmadpnoedfgonddpmlhaclbicllg";
pub const DEFAULT_FIREFOX_EXT_ID: &str = "easydict-ocr@easydict.app";

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum BrowserRegistrarCommand {
    Install,
    Uninstall,
    Status,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct BrowserRegistrarOptions {
    pub command: BrowserRegistrarCommand,
    pub chrome: bool,
    pub firefox: bool,
    pub bridge_path: Option<PathBuf>,
    pub bridge_root_name: String,
    pub chrome_ext_ids: Vec<String>,
    pub firefox_ext_id: String,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum BrowserRegistrarParseError {
    Help,
    MissingCommand,
    UnknownCommand(String),
    MissingValue(String),
    InvalidValue { option: String, value: String },
}

impl fmt::Display for BrowserRegistrarParseError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Help => formatter.write_str("help requested"),
            Self::MissingCommand => formatter.write_str("missing command"),
            Self::UnknownCommand(command) => write!(formatter, "unknown command: {command}"),
            Self::MissingValue(option) => write!(formatter, "missing value for {option}"),
            Self::InvalidValue { option, value } => {
                write!(formatter, "invalid value for {option}: {value}")
            }
        }
    }
}

impl std::error::Error for BrowserRegistrarParseError {}

pub fn usage() -> &'static str {
    "BrowserHostRegistrar - Easydict browser Native Messaging host registrar

Usage:
  BrowserHostRegistrar install [options]    Register native messaging host
  BrowserHostRegistrar uninstall [options]  Remove native messaging host
  BrowserHostRegistrar status               Show installation status

Options:
  --chrome              Target Chrome/Edge (default: both)
  --firefox             Target Firefox (default: both)
  --bridge-path PATH    Path to easydict-native-bridge.exe
  --bridge-root-name N  LOCALAPPDATA child directory for manifests (default: EasydictRs)
  --chrome-ext-id IDS   Chrome extension ID(s), comma-separated (default: built-in)
  --firefox-ext-id ID   Firefox extension ID (default: built-in)"
}

pub fn parse_browser_registrar_args<I, S>(
    args: I,
) -> Result<BrowserRegistrarOptions, BrowserRegistrarParseError>
where
    I: IntoIterator<Item = S>,
    S: Into<String>,
{
    let mut args = args.into_iter().map(Into::into);
    let command = args
        .next()
        .ok_or(BrowserRegistrarParseError::MissingCommand)?;
    let command = match command.to_ascii_lowercase().as_str() {
        "-h" | "--help" | "help" => return Err(BrowserRegistrarParseError::Help),
        "install" => BrowserRegistrarCommand::Install,
        "uninstall" => BrowserRegistrarCommand::Uninstall,
        "status" => BrowserRegistrarCommand::Status,
        _ => return Err(BrowserRegistrarParseError::UnknownCommand(command)),
    };

    let mut chrome = false;
    let mut firefox = false;
    let mut bridge_path = None;
    let mut bridge_root_name = DEFAULT_BRIDGE_ROOT_NAME.to_string();
    let mut chrome_ext_ids = default_chrome_ext_ids();
    let mut firefox_ext_id = DEFAULT_FIREFOX_EXT_ID.to_string();
    let mut rest = args.peekable();

    while let Some(arg) = rest.next() {
        if let Some((name, value)) = split_long_option(&arg) {
            match name {
                "--bridge-path" => bridge_path = Some(PathBuf::from(value)),
                "--bridge-root-name" => {
                    bridge_root_name = parse_bridge_root_name("--bridge-root-name", value)?
                }
                "--chrome-ext-id" => chrome_ext_ids = parse_chrome_ext_ids(value),
                "--firefox-ext-id" => firefox_ext_id = value.to_string(),
                _ => {}
            }
            continue;
        }

        match arg.as_str() {
            "-h" | "--help" => return Err(BrowserRegistrarParseError::Help),
            "--chrome" => chrome = true,
            "--firefox" => firefox = true,
            "--bridge-path" => {
                bridge_path = Some(PathBuf::from(next_value(&mut rest, "--bridge-path")?));
            }
            "--bridge-root-name" => {
                bridge_root_name = parse_bridge_root_name(
                    "--bridge-root-name",
                    &next_value(&mut rest, "--bridge-root-name")?,
                )?;
            }
            "--chrome-ext-id" => {
                chrome_ext_ids = parse_chrome_ext_ids(&next_value(&mut rest, "--chrome-ext-id")?);
            }
            "--firefox-ext-id" => {
                firefox_ext_id = next_value(&mut rest, "--firefox-ext-id")?;
            }
            _ => {}
        }
    }

    if command != BrowserRegistrarCommand::Status && !chrome && !firefox {
        chrome = true;
        firefox = true;
    }

    Ok(BrowserRegistrarOptions {
        command,
        chrome,
        firefox,
        bridge_path,
        bridge_root_name,
        chrome_ext_ids,
        firefox_ext_id,
    })
}

pub fn default_chrome_ext_ids() -> Vec<String> {
    parse_chrome_ext_ids(DEFAULT_CHROME_EXT_IDS)
}

pub fn default_bridge_directory(local_app_data: impl AsRef<Path>) -> PathBuf {
    bridge_directory_for_root(local_app_data, DEFAULT_BRIDGE_ROOT_NAME)
}

pub fn legacy_bridge_directory(local_app_data: impl AsRef<Path>) -> PathBuf {
    bridge_directory_for_root(local_app_data, LEGACY_BRIDGE_ROOT_NAME)
}

pub fn rust_bridge_directory(local_app_data: impl AsRef<Path>) -> PathBuf {
    bridge_directory_for_root(local_app_data, RUST_BRIDGE_ROOT_NAME)
}

pub fn bridge_directory_for_root(
    local_app_data: impl AsRef<Path>,
    bridge_root_name: impl AsRef<str>,
) -> PathBuf {
    local_app_data
        .as_ref()
        .join(bridge_root_name.as_ref())
        .join("browser-bridge")
}

pub fn chrome_registry_path() -> String {
    format!(r"Software\Google\Chrome\NativeMessagingHosts\{NATIVE_HOST_NAME}")
}

pub fn firefox_registry_path() -> String {
    format!(r"Software\Mozilla\NativeMessagingHosts\{NATIVE_HOST_NAME}")
}

pub trait BrowserRegistry {
    fn write_default_value(&mut self, key_path: &str, value: &str) -> io::Result<()>;
    fn delete_key(&mut self, key_path: &str) -> io::Result<()>;
    fn read_default_value(&self, key_path: &str) -> io::Result<Option<String>>;
}

#[derive(Default)]
pub struct MemoryBrowserRegistry {
    values: BTreeMap<String, String>,
}

impl MemoryBrowserRegistry {
    pub fn value(&self, key_path: &str) -> Option<&str> {
        self.values.get(key_path).map(String::as_str)
    }
}

impl BrowserRegistry for MemoryBrowserRegistry {
    fn write_default_value(&mut self, key_path: &str, value: &str) -> io::Result<()> {
        self.values.insert(key_path.to_string(), value.to_string());
        Ok(())
    }

    fn delete_key(&mut self, key_path: &str) -> io::Result<()> {
        self.values.remove(key_path);
        Ok(())
    }

    fn read_default_value(&self, key_path: &str) -> io::Result<Option<String>> {
        Ok(self.values.get(key_path).cloned())
    }
}

pub struct BrowserRegistrarCore<R> {
    bridge_directory: PathBuf,
    registry: R,
}

impl<R> BrowserRegistrarCore<R>
where
    R: BrowserRegistry,
{
    pub fn new(bridge_directory: impl Into<PathBuf>, registry: R) -> Self {
        Self {
            bridge_directory: bridge_directory.into(),
            registry,
        }
    }

    pub fn registry(&self) -> &R {
        &self.registry
    }

    pub fn bridge_directory(&self) -> &Path {
        &self.bridge_directory
    }

    pub fn bridge_exe_path(&self) -> PathBuf {
        self.bridge_directory.join(BRIDGE_EXE_NAME)
    }

    pub fn install(
        &mut self,
        chrome: bool,
        firefox: bool,
        source_bridge_path: &Path,
        chrome_ext_ids: &[String],
        firefox_ext_id: &str,
    ) -> InstallOutput {
        if !source_bridge_path.exists() {
            return InstallOutput::error(format!(
                "Bridge exe not found: {}",
                source_bridge_path.display()
            ));
        }

        if !source_bridge_path_is_rust_native_bridge(source_bridge_path) {
            return InstallOutput::error(format!(
                "Bridge source must be {BRIDGE_EXE_NAME}; refusing to register non-Rust native bridge: {}",
                source_bridge_path.display()
            ));
        }

        if let Err(error) = fs::create_dir_all(&self.bridge_directory) {
            return InstallOutput::error(error.to_string());
        }

        let bridge_path = self.bridge_exe_path();
        match fs::copy(source_bridge_path, &bridge_path) {
            Ok(_) => {}
            Err(error) => return InstallOutput::error(error.to_string()),
        }

        let mut installed = Vec::new();

        if chrome {
            match self.write_chrome_manifest(chrome_ext_ids) {
                Ok(manifest_path) => {
                    if let Err(error) = self
                        .registry
                        .write_default_value(&chrome_registry_path(), &manifest_path)
                    {
                        return InstallOutput::error(error.to_string());
                    }
                    installed.push("chrome".to_string());
                }
                Err(error) => return InstallOutput::error(error.to_string()),
            }
        }

        if firefox {
            match self.write_firefox_manifest(firefox_ext_id) {
                Ok(manifest_path) => {
                    if let Err(error) = self
                        .registry
                        .write_default_value(&firefox_registry_path(), &manifest_path)
                    {
                        return InstallOutput::error(error.to_string());
                    }
                    installed.push("firefox".to_string());
                }
                Err(error) => return InstallOutput::error(error.to_string()),
            }
        }

        InstallOutput::success(installed, bridge_path.display().to_string())
    }

    pub fn uninstall(&mut self, chrome: bool, firefox: bool) -> UninstallOutput {
        let mut uninstalled = Vec::new();

        if chrome {
            self.uninstall_browser_if_owned(
                &chrome_registry_path(),
                CHROME_MANIFEST_FILE,
                "chrome",
                &mut uninstalled,
            );
        }

        if firefox {
            self.uninstall_browser_if_owned(
                &firefox_registry_path(),
                FIREFOX_MANIFEST_FILE,
                "firefox",
                &mut uninstalled,
            );
        }

        if !self.is_registered(&chrome_registry_path())
            && !self.is_registered(&firefox_registry_path())
        {
            let _ = fs::remove_dir_all(&self.bridge_directory);
        }

        UninstallOutput {
            success: true,
            uninstalled,
        }
    }

    pub fn status(&self) -> StatusOutput {
        StatusOutput {
            chrome: BrowserStatusEntry {
                installed: self.is_registered(&chrome_registry_path()),
            },
            firefox: BrowserStatusEntry {
                installed: self.is_registered(&firefox_registry_path()),
            },
            bridge_exists: self.bridge_exe_path().exists(),
            bridge_directory: self.bridge_directory.display().to_string(),
        }
    }

    pub fn write_chrome_manifest(&self, chrome_ext_ids: &[String]) -> io::Result<String> {
        let manifest = ChromeManifest {
            name: NATIVE_HOST_NAME.to_string(),
            description: "Easydict native messaging bridge".to_string(),
            path: self.bridge_exe_path().display().to_string(),
            manifest_type: "stdio".to_string(),
            allowed_origins: chrome_ext_ids
                .iter()
                .map(|id| format!("chrome-extension://{id}/"))
                .collect(),
        };
        let path = self.bridge_directory.join(CHROME_MANIFEST_FILE);
        write_manifest_file(&path, &manifest)?;
        Ok(path.display().to_string())
    }

    pub fn write_firefox_manifest(&self, firefox_ext_id: &str) -> io::Result<String> {
        let manifest = FirefoxManifest {
            name: NATIVE_HOST_NAME.to_string(),
            description: "Easydict native messaging bridge".to_string(),
            path: self.bridge_exe_path().display().to_string(),
            manifest_type: "stdio".to_string(),
            allowed_extensions: vec![firefox_ext_id.to_string()],
        };
        let path = self.bridge_directory.join(FIREFOX_MANIFEST_FILE);
        write_manifest_file(&path, &manifest)?;
        Ok(path.display().to_string())
    }

    fn is_registered(&self, registry_path: &str) -> bool {
        let Ok(Some(manifest_path)) = self.registry.read_default_value(registry_path) else {
            return false;
        };
        let Ok(json) = fs::read_to_string(&manifest_path) else {
            return false;
        };
        let Ok(manifest) = serde_json::from_str::<ManifestIntegrityProbe>(&json) else {
            return false;
        };

        self.manifest_integrity_is_valid(&manifest)
    }

    fn manifest_integrity_is_valid(&self, manifest: &ManifestIntegrityProbe) -> bool {
        manifest.name == NATIVE_HOST_NAME
            && manifest.manifest_type == "stdio"
            && path_points_to_bridge(Path::new(&manifest.path), &self.bridge_exe_path())
    }

    fn uninstall_browser_if_owned(
        &mut self,
        registry_path: &str,
        manifest_file: &str,
        browser_name: &str,
        uninstalled: &mut Vec<String>,
    ) {
        if !self.registry_points_to_owned_manifest(registry_path, manifest_file) {
            return;
        }

        let _ = self.registry.delete_key(registry_path);
        delete_file(self.bridge_directory.join(manifest_file));
        uninstalled.push(browser_name.to_string());
    }

    fn registry_points_to_owned_manifest(&self, registry_path: &str, manifest_file: &str) -> bool {
        let Ok(Some(manifest_path)) = self.registry.read_default_value(registry_path) else {
            return false;
        };

        path_matches(
            Path::new(&manifest_path),
            &self.bridge_directory.join(manifest_file),
        )
    }
}

#[derive(Clone, Debug, Eq, PartialEq, Serialize)]
pub struct ErrorOutput {
    pub success: bool,
    pub error: String,
}

#[derive(Clone, Debug, Eq, PartialEq, Serialize)]
pub struct InstallOutput {
    pub success: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub installed: Option<Vec<String>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub bridge_path: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub error: Option<String>,
}

impl InstallOutput {
    fn success(installed: Vec<String>, bridge_path: String) -> Self {
        Self {
            success: true,
            installed: Some(installed),
            bridge_path: Some(bridge_path),
            error: None,
        }
    }

    fn error(error: String) -> Self {
        Self {
            success: false,
            installed: None,
            bridge_path: None,
            error: Some(error),
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq, Serialize)]
pub struct UninstallOutput {
    pub success: bool,
    pub uninstalled: Vec<String>,
}

#[derive(Clone, Debug, Eq, PartialEq, Serialize)]
pub struct BrowserStatusEntry {
    pub installed: bool,
}

#[derive(Clone, Debug, Eq, PartialEq, Serialize)]
pub struct StatusOutput {
    pub chrome: BrowserStatusEntry,
    pub firefox: BrowserStatusEntry,
    pub bridge_exists: bool,
    pub bridge_directory: String,
}

#[derive(Clone, Debug, Eq, PartialEq, Serialize)]
pub struct ChromeManifest {
    pub name: String,
    pub description: String,
    pub path: String,
    #[serde(rename = "type")]
    pub manifest_type: String,
    pub allowed_origins: Vec<String>,
}

#[derive(Clone, Debug, Eq, PartialEq, Serialize)]
pub struct FirefoxManifest {
    pub name: String,
    pub description: String,
    pub path: String,
    #[serde(rename = "type")]
    pub manifest_type: String,
    pub allowed_extensions: Vec<String>,
}

#[derive(Deserialize)]
struct ManifestIntegrityProbe {
    name: String,
    path: String,
    #[serde(rename = "type")]
    manifest_type: String,
}

pub fn parse_chrome_ext_ids(raw: &str) -> Vec<String> {
    raw.split(',')
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(str::to_string)
        .collect()
}

pub fn serialize_cli_json<T: Serialize>(value: &T) -> String {
    serde_json::to_string(value).expect("browser registrar output must serialize")
}

fn split_long_option(value: &str) -> Option<(&str, &str)> {
    let (name, value) = value.split_once('=')?;
    name.starts_with("--").then_some((name, value))
}

fn next_value<I>(
    args: &mut std::iter::Peekable<I>,
    option: &str,
) -> Result<String, BrowserRegistrarParseError>
where
    I: Iterator<Item = String>,
{
    args.next()
        .filter(|value| !value.is_empty())
        .ok_or_else(|| BrowserRegistrarParseError::MissingValue(option.to_string()))
}

fn parse_bridge_root_name(option: &str, value: &str) -> Result<String, BrowserRegistrarParseError> {
    let value = value.trim();
    let forbidden_chars = ['\\', '/', '<', '>', ':', '"', '|', '?', '*'];
    if value.is_empty()
        || value == "."
        || value == ".."
        || value.eq_ignore_ascii_case(LEGACY_BRIDGE_ROOT_NAME)
        || value.chars().any(|ch| forbidden_chars.contains(&ch))
    {
        return Err(BrowserRegistrarParseError::InvalidValue {
            option: option.to_string(),
            value: value.to_string(),
        });
    }

    Ok(value.to_string())
}

fn write_manifest_file<T: Serialize>(path: &Path, manifest: &T) -> io::Result<()> {
    let json = serde_json::to_string_pretty(manifest).map_err(json_io_error)?;
    fs::write(path, json)
}

fn delete_file(path: impl AsRef<Path>) {
    let path = path.as_ref();
    if path.exists() {
        let _ = fs::remove_file(path);
    }
}

fn source_bridge_path_is_rust_native_bridge(path: &Path) -> bool {
    let has_expected_name = path
        .file_name()
        .and_then(|name| name.to_str())
        .map(|name| name.eq_ignore_ascii_case(BRIDGE_EXE_NAME))
        .unwrap_or(false);

    has_expected_name && !path_has_forbidden_bridge_source_component(path)
}

fn path_has_forbidden_bridge_source_component(path: &Path) -> bool {
    path.components().any(|component| {
        let Some(value) = component.as_os_str().to_str() else {
            return false;
        };
        value.eq_ignore_ascii_case("workers") || value.eq_ignore_ascii_case("dotnet")
    })
}

fn path_points_to_bridge(path: &Path, bridge_path: &Path) -> bool {
    let Ok(path) = fs::canonicalize(path) else {
        return false;
    };
    let Ok(bridge_path) = fs::canonicalize(bridge_path) else {
        return false;
    };

    path == bridge_path
}

fn path_matches(path: &Path, expected_path: &Path) -> bool {
    match (fs::canonicalize(path), fs::canonicalize(expected_path)) {
        (Ok(path), Ok(expected_path)) => path == expected_path,
        _ => path == expected_path,
    }
}

fn json_io_error(error: serde_json::Error) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, error)
}
