use std::fmt;
use std::fs;
use std::path::{Path, PathBuf};

use crate::protocol::SettingsSnapshot;
use crate::resource_download::{
    download_with_retry, ordered_urls_by_probe, try_delete_file, ReqwestResourceDownloadClient,
    ResourceDownloadClient, ResourceDownloadError, ResourceDownloadProgress,
};
use crate::translation_language::TranslationLanguage;
use ttf_parser::Face;

pub const FONTS_SUBDIR: &str = "Fonts";
const CJK_FONT_PROBE_CHARS: &[char] = &['你', '漢', '日', '本', '한', '글'];

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct FontAsset {
    pub key: &'static str,
    pub file_name: &'static str,
    pub download_urls: &'static [&'static str],
}

const FONT_ASSETS: &[FontAsset] = &[
    FontAsset {
        key: "zh-Hans",
        file_name: "NotoSansSC-Regular.ttf",
        download_urls: &[
            "https://github.com/notofonts/noto-cjk/raw/main/Sans/Variable/TTF/NotoSansCJKsc-VF.ttf",
            "https://github.com/googlefonts/noto-cjk/raw/main/Sans/Variable/TTF/NotoSansCJKsc-VF.ttf",
        ],
    },
    FontAsset {
        key: "zh-Hant",
        file_name: "NotoSansTC-Regular.ttf",
        download_urls: &[
            "https://github.com/notofonts/noto-cjk/raw/main/Sans/Variable/TTF/NotoSansCJKtc-VF.ttf",
            "https://github.com/googlefonts/noto-cjk/raw/main/Sans/Variable/TTF/NotoSansCJKtc-VF.ttf",
        ],
    },
    FontAsset {
        key: "ja",
        file_name: "NotoSansJP-Regular.ttf",
        download_urls: &[
            "https://github.com/notofonts/noto-cjk/raw/main/Sans/Variable/TTF/NotoSansCJKjp-VF.ttf",
            "https://github.com/googlefonts/noto-cjk/raw/main/Sans/Variable/TTF/NotoSansCJKjp-VF.ttf",
        ],
    },
    FontAsset {
        key: "ko",
        file_name: "NotoSansKR-Regular.ttf",
        download_urls: &[
            "https://github.com/notofonts/noto-cjk/raw/main/Sans/Variable/TTF/NotoSansCJKkr-VF.ttf",
            "https://github.com/googlefonts/noto-cjk/raw/main/Sans/Variable/TTF/NotoSansCJKkr-VF.ttf",
        ],
    },
];

#[derive(Debug)]
pub enum FontDownloadError {
    UnsupportedLanguage(TranslationLanguage),
    InvalidFont(PathBuf),
    Download(ResourceDownloadError),
}

impl fmt::Display for FontDownloadError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::UnsupportedLanguage(language) => {
                write!(
                    formatter,
                    "No CJK font configured for language: {language:?}"
                )
            }
            Self::InvalidFont(path) => write!(
                formatter,
                "Downloaded CJK font '{}' is not a readable TrueType/OpenType CJK font",
                path.display()
            ),
            Self::Download(error) => write!(formatter, "{error}"),
        }
    }
}

impl std::error::Error for FontDownloadError {}

impl From<ResourceDownloadError> for FontDownloadError {
    fn from(value: ResourceDownloadError) -> Self {
        Self::Download(value)
    }
}

pub fn default_font_cache_dir() -> PathBuf {
    std::env::var_os("LOCALAPPDATA")
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from("."))
        .join("Easydict")
        .join(FONTS_SUBDIR)
}

pub fn font_cache_dir(base: impl AsRef<Path>) -> PathBuf {
    base.as_ref().join(FONTS_SUBDIR)
}

pub fn font_assets() -> &'static [FontAsset] {
    FONT_ASSETS
}

pub fn font_asset_for_language(language: TranslationLanguage) -> Option<&'static FontAsset> {
    match language {
        TranslationLanguage::SimplifiedChinese => {
            FONT_ASSETS.iter().find(|asset| asset.key == "zh-Hans")
        }
        TranslationLanguage::TraditionalChinese => {
            FONT_ASSETS.iter().find(|asset| asset.key == "zh-Hant")
        }
        TranslationLanguage::Japanese => FONT_ASSETS.iter().find(|asset| asset.key == "ja"),
        TranslationLanguage::Korean => FONT_ASSETS.iter().find(|asset| asset.key == "ko"),
        _ => None,
    }
}

pub fn requires_cjk_font(language: TranslationLanguage) -> bool {
    font_asset_for_language(language).is_some()
}

pub fn cached_font_path_for_directory(
    base: impl AsRef<Path>,
    language: TranslationLanguage,
) -> Option<PathBuf> {
    let fonts_dir = font_cache_dir(base);
    if let Some(asset) = font_asset_for_language(language) {
        let exact = fonts_dir.join(asset.file_name);
        if is_managed_cjk_font_file(&exact) {
            return Some(exact);
        }
    }

    FONT_ASSETS
        .iter()
        .map(|asset| fonts_dir.join(asset.file_name))
        .find(|path| is_managed_cjk_font_file(path))
}

pub fn cached_font_path(language: TranslationLanguage) -> Option<PathBuf> {
    cached_font_path_for_directory(default_data_directory(), language)
}

pub fn has_any_cjk_font_for_directory(base: impl AsRef<Path>) -> bool {
    let fonts_dir = font_cache_dir(base);
    FONT_ASSETS
        .iter()
        .any(|asset| is_managed_cjk_font_file(fonts_dir.join(asset.file_name)))
}

pub fn is_font_downloaded_for_directory(
    base: impl AsRef<Path>,
    language: TranslationLanguage,
) -> bool {
    cached_font_path_for_directory(base, language).is_some()
}

pub fn total_font_size_bytes_for_directory(base: impl AsRef<Path>) -> u64 {
    let fonts_dir = font_cache_dir(base);
    FONT_ASSETS
        .iter()
        .filter_map(|asset| fs::metadata(fonts_dir.join(asset.file_name)).ok())
        .filter(|metadata| metadata.is_file())
        .map(|metadata| metadata.len())
        .sum()
}

pub fn delete_all_fonts_for_directory(base: impl AsRef<Path>) {
    let fonts_dir = font_cache_dir(base);
    for asset in FONT_ASSETS {
        try_delete_file(fonts_dir.join(asset.file_name));
    }
}

pub fn ensure_font_for_directory<C: ResourceDownloadClient>(
    client: &mut C,
    base: impl AsRef<Path>,
    language: TranslationLanguage,
    progress: &mut dyn FnMut(ResourceDownloadProgress),
) -> Result<PathBuf, FontDownloadError> {
    let asset = font_asset_for_language(language)
        .ok_or(FontDownloadError::UnsupportedLanguage(language))?;
    let fonts_dir = font_cache_dir(base);
    fs::create_dir_all(&fonts_dir).map_err(ResourceDownloadError::from)?;

    let font_path = fonts_dir.join(asset.file_name);
    if is_managed_cjk_font_file(&font_path) {
        return Ok(font_path);
    }

    let urls: Vec<String> = asset
        .download_urls
        .iter()
        .map(|url| (*url).to_string())
        .collect();
    let ordered_urls = ordered_urls_by_probe(client, &urls);
    download_with_retry(
        client,
        &ordered_urls,
        &font_path,
        &format!("font-{}", asset.key),
        progress,
    )?;
    if !is_managed_cjk_font_file(&font_path) {
        return Err(FontDownloadError::InvalidFont(font_path));
    }
    Ok(font_path)
}

pub fn ensure_font(
    language: TranslationLanguage,
    progress: &mut dyn FnMut(ResourceDownloadProgress),
) -> Result<PathBuf, FontDownloadError> {
    let mut client = ReqwestResourceDownloadClient::new()?;
    ensure_font_for_directory(&mut client, default_data_directory(), language, progress)
}

pub fn ensure_font_with_settings(
    settings: &SettingsSnapshot,
    language: TranslationLanguage,
    progress: &mut dyn FnMut(ResourceDownloadProgress),
) -> Result<PathBuf, FontDownloadError> {
    let mut client = ReqwestResourceDownloadClient::from_settings(settings)?;
    ensure_font_for_directory(
        &mut client,
        data_directory_for_settings(settings),
        language,
        progress,
    )
}

fn data_directory_for_settings(settings: &SettingsSnapshot) -> PathBuf {
    settings
        .cache_dir_path()
        .unwrap_or_else(default_data_directory)
}

fn default_data_directory() -> PathBuf {
    std::env::var_os("LOCALAPPDATA")
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from("."))
        .join("Easydict")
}

pub(crate) fn is_managed_cjk_font_file(path: impl AsRef<Path>) -> bool {
    let path = path.as_ref();
    if !path.is_file() {
        return false;
    }

    let Ok(bytes) = fs::read(path) else {
        return false;
    };
    let Ok(face) = Face::parse(&bytes, 0) else {
        return false;
    };

    CJK_FONT_PROBE_CHARS
        .iter()
        .any(|probe| face.glyph_index(*probe).is_some())
}
