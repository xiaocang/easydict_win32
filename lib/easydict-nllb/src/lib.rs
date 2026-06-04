use std::collections::{HashMap, HashSet};
use std::fmt;
use std::fs;
use std::path::{Path, PathBuf};
use tokenizers::Tokenizer as HuggingFaceTokenizerCore;
use unicode_normalization::UnicodeNormalization;

pub const DEFAULT_MAX_NEW_TOKENS: usize = 256;
pub const NLLB_MODEL_ID: &str = "nllb-200-distilled-600M-int8";
pub const NLLB_HUGGINGFACE_REPO: &str = "Xenova/nllb-200-distilled-600M";
pub const NLLB_MODEL_REVISION: &str = "261c31d1a5732c67cdd16d80e8d6088507c7ccea";
pub const NLLB_MODEL_DIRECTORY: &str = "nllb-200-distilled-600M";
pub const OPENVINO_RUNTIME_VERSION: &str = "1.21.0";
pub const OPENVINO_RUNTIME_IDENTIFIER: &str = "win-x64";
pub const OPENVINO_RUNTIME_PACKAGE_URL: &str =
    "https://www.nuget.org/api/v2/package/Intel.ML.OnnxRuntime.OpenVino/1.21.0";
pub const OPENVINO_RUNTIME_PACKAGE_SHA256: &str =
    "a70be78c7ce5c0ff82538f8934fffaafa5f63409ee0604d3990c8b393e178e15";
pub const OPENVINO_EP_ENABLE_ENVIRONMENT_VARIABLE: &str = "EASYDICT_ENABLE_OPENVINO_EP";
pub const MODEL_COMPLETION_SENTINEL: &str = ".complete";

pub const NLLB_MODEL_FILES: &[&str] = &[
    "encoder_model_quantized.onnx",
    "decoder_model_quantized.onnx",
    "sentencepiece.bpe.model",
    "tokenizer.json",
    "config.json",
];

pub const OPENVINO_RUNTIME_FILES: &[&str] = &[
    "onnxruntime.dll",
    "onnxruntime.lib",
    "onnxruntime_providers_openvino.dll",
    "onnxruntime_providers_shared.dll",
    "openvino.dll",
    "openvino_auto_batch_plugin.dll",
    "openvino_auto_plugin.dll",
    "openvino_c.dll",
    "openvino_hetero_plugin.dll",
    "openvino_intel_cpu_plugin.dll",
    "openvino_intel_gpu_plugin.dll",
    "openvino_intel_npu_plugin.dll",
    "openvino_ir_frontend.dll",
    "openvino_onnx_frontend.dll",
    "openvino_paddle_frontend.dll",
    "openvino_pytorch_frontend.dll",
    "openvino_tensorflow_frontend.dll",
    "openvino_tensorflow_lite_frontend.dll",
    "tbb12.dll",
    "tbb12_debug.dll",
    "tbbbind_2_5.dll",
    "tbbbind_2_5_debug.dll",
    "tbbmalloc.dll",
    "tbbmalloc_debug.dll",
    "tbbmalloc_proxy.dll",
    "tbbmalloc_proxy_debug.dll",
];

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct NllbModelFile {
    pub local_file_name: &'static str,
    pub remote_relative_path: &'static str,
    pub approximate_bytes: u64,
    pub sha256: Option<&'static str>,
}

impl NllbModelFile {
    pub fn download_url(&self) -> String {
        nllb_model_download_url(self.remote_relative_path)
    }
}

pub const NLLB_MODEL_MANIFEST_FILES: &[NllbModelFile] = &[
    NllbModelFile {
        local_file_name: "encoder_model_quantized.onnx",
        remote_relative_path: "onnx/encoder_model_quantized.onnx",
        approximate_bytes: 419_120_483,
        sha256: Some("5cde664eacba07a62f198857ec6c06e09572b1ebb77c8137f1fa99ac604a3a28"),
    },
    NllbModelFile {
        local_file_name: "decoder_model_quantized.onnx",
        remote_relative_path: "onnx/decoder_model_quantized.onnx",
        approximate_bytes: 470_533_055,
        sha256: Some("ddea619b640379609719becf91a488c5e6ce4c4b2052efbb5388edaed465a552"),
    },
    NllbModelFile {
        local_file_name: "sentencepiece.bpe.model",
        remote_relative_path: "sentencepiece.bpe.model",
        approximate_bytes: 4_852_054,
        sha256: Some("14bb8dfb35c0ffdea7bc01e56cea38b9e3d5efcdcb9c251d6b40538e1aab555a"),
    },
    NllbModelFile {
        local_file_name: "tokenizer.json",
        remote_relative_path: "tokenizer.json",
        approximate_bytes: 17_331_224,
        sha256: Some("8ac789ad7dabea44d41537822d48c516ba358374c51813e2cba78c006e150c94"),
    },
    NllbModelFile {
        local_file_name: "config.json",
        remote_relative_path: "config.json",
        approximate_bytes: 800,
        sha256: None,
    },
];

pub fn nllb_model_download_url(remote_relative_path: &str) -> String {
    format!(
        "https://huggingface.co/{NLLB_HUGGINGFACE_REPO}/resolve/{NLLB_MODEL_REVISION}/{remote_relative_path}"
    )
}

pub fn nllb_model_file(local_file_name: &str) -> Option<&'static NllbModelFile> {
    NLLB_MODEL_MANIFEST_FILES
        .iter()
        .find(|file| file.local_file_name.eq_ignore_ascii_case(local_file_name))
}

pub fn nllb_model_total_approximate_bytes() -> u64 {
    NLLB_MODEL_MANIFEST_FILES
        .iter()
        .map(|file| file.approximate_bytes)
        .sum()
}

pub fn nllb_model_revision_is_pinned() -> bool {
    is_hex_string(NLLB_MODEL_REVISION, 40)
}

pub fn openvino_runtime_package_file_name() -> String {
    format!("intel.ml.onnxruntime.openvino.{OPENVINO_RUNTIME_VERSION}.nupkg")
}

fn is_hex_string(value: &str, expected_len: usize) -> bool {
    value.len() == expected_len && value.as_bytes().iter().all(u8::is_ascii_hexdigit)
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum OpenVinoDevice {
    Auto,
    Npu,
    Gpu,
    Cpu,
}

impl OpenVinoDevice {
    pub fn from_setting(value: Option<&str>) -> Self {
        match value
            .unwrap_or_default()
            .trim()
            .to_ascii_lowercase()
            .as_str()
        {
            "npu" => Self::Npu,
            "gpu" => Self::Gpu,
            "cpu" => Self::Cpu,
            _ => Self::Auto,
        }
    }

    pub fn as_openvino_device_type(self) -> &'static str {
        match self {
            Self::Auto => "AUTO:NPU,GPU,CPU",
            Self::Npu => "NPU",
            Self::Gpu => "GPU",
            Self::Cpu => "CPU",
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct NllbModelPaths {
    pub model_dir: PathBuf,
    pub runtime_dir: PathBuf,
}

impl NllbModelPaths {
    pub fn from_cache_base(base: impl AsRef<Path>) -> Self {
        let base = base.as_ref();
        Self {
            model_dir: base.join("models").join(NLLB_MODEL_DIRECTORY),
            runtime_dir: base
                .join("runtimes")
                .join("openvino")
                .join(OPENVINO_RUNTIME_VERSION)
                .join(OPENVINO_RUNTIME_IDENTIFIER)
                .join("native"),
        }
    }

    pub fn encoder_path(&self) -> PathBuf {
        self.model_dir.join("encoder_model_quantized.onnx")
    }

    pub fn decoder_path(&self) -> PathBuf {
        self.model_dir.join("decoder_model_quantized.onnx")
    }

    pub fn sentencepiece_path(&self) -> PathBuf {
        self.model_dir.join("sentencepiece.bpe.model")
    }

    pub fn tokenizer_json_path(&self) -> PathBuf {
        self.model_dir.join("tokenizer.json")
    }

    pub fn missing_required_files(&self) -> Vec<PathBuf> {
        required_files_present(&self.model_dir, NLLB_MODEL_FILES)
            .into_iter()
            .chain(required_files_present(
                &self.runtime_dir,
                OPENVINO_RUNTIME_FILES,
            ))
            .collect()
    }

    pub fn is_cache_complete(&self) -> bool {
        self.missing_required_files().is_empty()
    }
}

fn required_files_present(dir: &Path, files: &[&str]) -> Vec<PathBuf> {
    let mut missing = Vec::new();
    if !dir.join(MODEL_COMPLETION_SENTINEL).is_file() {
        missing.push(dir.join(MODEL_COMPLETION_SENTINEL));
    }

    for file in files {
        let path = dir.join(file);
        if !path.is_file() {
            missing.push(path);
        }
    }

    missing
}

#[derive(Debug, Clone, Eq, PartialEq)]
pub struct NllbError {
    message: String,
}

impl NllbError {
    pub fn new(message: impl Into<String>) -> Self {
        Self {
            message: message.into(),
        }
    }

    pub fn message(&self) -> &str {
        &self.message
    }
}

impl fmt::Display for NllbError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.message)
    }
}

impl std::error::Error for NllbError {}

pub trait NllbTokenizer {
    fn encode_source(&self, text: &str, source_flores_code: &str) -> Result<Vec<i32>, NllbError>;
    fn decode(&self, token_ids: &[i32]) -> Result<String, NllbError>;
    fn language_token_id(&self, flores_code: &str) -> Result<i32, NllbError>;
}

pub struct HuggingFaceNllbTokenizer {
    tokenizer: HuggingFaceTokenizerCore,
    added_tokens: AddedTokenTable,
    eos_token_id: i32,
}

impl HuggingFaceNllbTokenizer {
    pub fn from_model_paths(paths: &NllbModelPaths) -> Result<Self, NllbError> {
        Self::from_tokenizer_json_path(paths.tokenizer_json_path())
    }

    pub fn from_tokenizer_json_path(path: impl AsRef<Path>) -> Result<Self, NllbError> {
        let path = path.as_ref();
        let json_text = fs::read_to_string(path).map_err(|error| {
            NllbError::new(format!(
                "failed to read NLLB tokenizer.json '{}': {error}",
                path.display()
            ))
        })?;
        Self::from_tokenizer_json_str(&json_text)
    }

    pub fn from_tokenizer_json_str(json_text: &str) -> Result<Self, NllbError> {
        let tokenizer = HuggingFaceTokenizerCore::from_bytes(json_text.as_bytes())
            .map_err(|error| NllbError::new(format!("failed to load tokenizer.json: {error}")))?;
        let added_tokens = AddedTokenTable::from_tokenizer_json_str(json_text)?;
        let eos_token_id = added_tokens.required_token_id("</s>")?;
        for token in ["<s>", "<pad>", "<unk>"] {
            added_tokens.required_token_id(token)?;
        }

        Ok(Self {
            tokenizer,
            added_tokens,
            eos_token_id,
        })
    }

    pub fn added_tokens(&self) -> &AddedTokenTable {
        &self.added_tokens
    }

    pub fn eos_token_id(&self) -> i32 {
        self.eos_token_id
    }
}

impl NllbTokenizer for HuggingFaceNllbTokenizer {
    fn encode_source(&self, text: &str, source_flores_code: &str) -> Result<Vec<i32>, NllbError> {
        let language_token_id = self.language_token_id(source_flores_code)?;
        let normalized_text = normalize_input_for_nllb_tokenizer(text);
        let encoding = self
            .tokenizer
            .encode(normalized_text, false)
            .map_err(|error| {
                NllbError::new(format!("failed to encode NLLB source text: {error}"))
            })?;
        let mut token_ids = Vec::with_capacity(encoding.get_ids().len() + 2);
        token_ids.push(language_token_id);
        for id in encoding.get_ids() {
            token_ids.push(i32::try_from(*id).map_err(|_| {
                NllbError::new(format!(
                    "NLLB tokenizer produced token id outside i32: {id}"
                ))
            })?);
        }
        token_ids.push(self.eos_token_id);
        Ok(token_ids)
    }

    fn decode(&self, token_ids: &[i32]) -> Result<String, NllbError> {
        let content_ids = token_ids
            .iter()
            .copied()
            .filter(|id| !self.added_tokens.is_special_token_id(*id))
            .map(|id| {
                u32::try_from(id).map_err(|_| {
                    NllbError::new(format!("NLLB generated invalid negative token id: {id}"))
                })
            })
            .collect::<Result<Vec<_>, _>>()?;
        self.tokenizer
            .decode(&content_ids, true)
            .map_err(|error| NllbError::new(format!("failed to decode NLLB token ids: {error}")))
    }

    fn language_token_id(&self, flores_code: &str) -> Result<i32, NllbError> {
        self.added_tokens.required_token_id(flores_code)
    }
}

pub fn normalize_input_for_nllb_tokenizer(text: &str) -> String {
    text.nfkc().collect()
}

pub trait NllbInferenceEngine {
    fn generate(
        &mut self,
        encoder_input_ids: &[i32],
        forced_bos_token_id: i32,
        max_new_tokens: usize,
    ) -> Result<Vec<i32>, NllbError>;
}

#[cfg(feature = "ort-openvino")]
pub use ort_engine::OrtNllbInferenceEngine;

#[cfg(feature = "ort-openvino")]
mod ort_engine {
    use super::*;
    use ort::session::Session;
    use ort::value::TensorRef;
    use std::sync::{Mutex, OnceLock};

    const PAD_TOKEN_ID: i32 = 1;
    const EOS_TOKEN_ID: i32 = 2;

    static ORT_RUNTIME_INIT_LOCK: Mutex<()> = Mutex::new(());
    static ORT_RUNTIME_DLL_PATH: OnceLock<PathBuf> = OnceLock::new();

    pub struct OrtNllbInferenceEngine {
        encoder: Session,
        decoder: Session,
        encoder_path: PathBuf,
        encoder_device: OpenVinoDevice,
        encoder_input_ids_name: String,
        encoder_attention_mask_name: String,
        encoder_hidden_state_output_name: String,
        decoder_input_ids_name: String,
        decoder_encoder_attention_mask_name: String,
        decoder_encoder_hidden_states_name: String,
        decoder_logits_output_name: String,
        encoder_cpu_fallback: Option<Session>,
        use_encoder_cpu_fallback: bool,
    }

    impl OrtNllbInferenceEngine {
        pub fn from_model_paths(
            paths: &NllbModelPaths,
            device: OpenVinoDevice,
            precision_hint: Option<&str>,
        ) -> Result<Self, NllbError> {
            ensure_ort_runtime_initialized(&paths.runtime_dir)?;

            let encoder_path = paths.encoder_path();
            let decoder_path = paths.decoder_path();
            require_file(&encoder_path, "NLLB encoder")?;
            require_file(&decoder_path, "NLLB decoder")?;

            let encoder = create_encoder_session(&encoder_path, device, precision_hint)?;
            let decoder = create_cpu_session(&decoder_path, "NLLB decoder")?;

            Ok(Self {
                encoder_input_ids_name: resolve_input_name(&encoder, "input_ids")?,
                encoder_attention_mask_name: resolve_input_name(&encoder, "attention_mask")?,
                encoder_hidden_state_output_name: resolve_output_name(
                    &encoder,
                    "last_hidden_state",
                )?,
                decoder_input_ids_name: resolve_input_name(&decoder, "input_ids")?,
                decoder_encoder_attention_mask_name: resolve_input_name(
                    &decoder,
                    "encoder_attention_mask",
                )?,
                decoder_encoder_hidden_states_name: resolve_input_name(
                    &decoder,
                    "encoder_hidden_states",
                )?,
                decoder_logits_output_name: resolve_output_name(&decoder, "logits")?,
                encoder,
                decoder,
                encoder_path,
                encoder_device: device,
                encoder_cpu_fallback: None,
                use_encoder_cpu_fallback: device == OpenVinoDevice::Cpu,
            })
        }

        pub fn initialized_ort_runtime_path() -> Option<&'static Path> {
            ORT_RUNTIME_DLL_PATH.get().map(PathBuf::as_path)
        }

        fn run_encoder(
            &mut self,
            encoder_input_ids: &[i32],
        ) -> Result<EncoderRunResult, NllbError> {
            if encoder_input_ids.is_empty() {
                return Ok(EncoderRunResult {
                    hidden_shape: vec![1, 0, 0],
                    hidden: Vec::new(),
                    attention_mask: Vec::new(),
                    source_len: 0,
                });
            }

            let names = EncoderIoNames {
                input_ids: self.encoder_input_ids_name.clone(),
                attention_mask: self.encoder_attention_mask_name.clone(),
                hidden_state_output: self.encoder_hidden_state_output_name.clone(),
            };

            if self.use_encoder_cpu_fallback {
                let session = self.encoder_cpu_fallback_session()?;
                return run_encoder_with_session(session, &names, encoder_input_ids);
            }

            match run_encoder_with_session(&mut self.encoder, &names, encoder_input_ids) {
                Ok(result) => Ok(result),
                Err(error)
                    if self.encoder_device != OpenVinoDevice::Cpu
                        && is_openvino_runtime_failure(error.message()) =>
                {
                    self.use_encoder_cpu_fallback = true;
                    let session = self.encoder_cpu_fallback_session()?;
                    run_encoder_with_session(session, &names, encoder_input_ids)
                }
                Err(error) => Err(error),
            }
        }

        fn encoder_cpu_fallback_session(&mut self) -> Result<&mut Session, NllbError> {
            if self.encoder_cpu_fallback.is_none() {
                self.encoder_cpu_fallback = Some(create_cpu_session(
                    &self.encoder_path,
                    "NLLB encoder CPU fallback",
                )?);
            }
            Ok(self
                .encoder_cpu_fallback
                .as_mut()
                .expect("fallback session was inserted"))
        }

        fn run_decoder_step(
            &mut self,
            encoder: &EncoderRunResult,
            decoder_input_so_far: &[i64],
        ) -> Result<i32, NllbError> {
            let decoder_input = TensorRef::<i64>::from_array_view((
                [1, decoder_input_so_far.len()],
                decoder_input_so_far,
            ))
            .map_err(map_ort_error("create NLLB decoder input_ids tensor"))?;
            let attention_mask = TensorRef::<i64>::from_array_view((
                [1, encoder.source_len],
                encoder.attention_mask.as_slice(),
            ))
            .map_err(map_ort_error(
                "create NLLB decoder encoder_attention_mask tensor",
            ))?;
            let hidden_states = TensorRef::<f32>::from_array_view((
                encoder.hidden_shape.as_slice(),
                encoder.hidden.as_slice(),
            ))
            .map_err(map_ort_error(
                "create NLLB decoder encoder_hidden_states tensor",
            ))?;

            let outputs = self
                .decoder
                .run(ort::inputs! {
                    self.decoder_encoder_attention_mask_name.as_str() => attention_mask,
                    self.decoder_input_ids_name.as_str() => decoder_input,
                    self.decoder_encoder_hidden_states_name.as_str() => hidden_states,
                })
                .map_err(map_ort_error("run NLLB decoder session"))?;
            let logits = outputs
                .get(self.decoder_logits_output_name.as_str())
                .ok_or_else(|| {
                    NllbError::new(format!(
                        "NLLB decoder output '{}' was not returned",
                        self.decoder_logits_output_name
                    ))
                })?;
            let (shape, data) = logits
                .try_extract_tensor::<f32>()
                .map_err(map_ort_error("extract NLLB decoder logits"))?;
            best_token_from_logits(shape, data)
        }
    }

    impl NllbInferenceEngine for OrtNllbInferenceEngine {
        fn generate(
            &mut self,
            encoder_input_ids: &[i32],
            forced_bos_token_id: i32,
            max_new_tokens: usize,
        ) -> Result<Vec<i32>, NllbError> {
            if encoder_input_ids.is_empty() || max_new_tokens == 0 {
                return Ok(Vec::new());
            }

            let encoder = self.run_encoder(encoder_input_ids)?;
            let mut decoder_input = Vec::with_capacity(max_new_tokens + 2);
            decoder_input.push(i64::from(EOS_TOKEN_ID));
            decoder_input.push(i64::from(forced_bos_token_id));

            let mut generated = Vec::new();
            for _ in 0..max_new_tokens {
                let next_token = self.run_decoder_step(&encoder, &decoder_input)?;
                if next_token == EOS_TOKEN_ID || next_token == PAD_TOKEN_ID {
                    break;
                }
                generated.push(next_token);
                decoder_input.push(i64::from(next_token));
            }

            Ok(generated)
        }
    }

    struct EncoderIoNames {
        input_ids: String,
        attention_mask: String,
        hidden_state_output: String,
    }

    struct EncoderRunResult {
        hidden_shape: Vec<i64>,
        hidden: Vec<f32>,
        attention_mask: Vec<i64>,
        source_len: usize,
    }

    pub fn ensure_ort_runtime_initialized(runtime_dir: impl AsRef<Path>) -> Result<(), NllbError> {
        let dll_path = runtime_dir.as_ref().join("onnxruntime.dll");
        require_file(&dll_path, "ONNX Runtime DLL")?;

        let _guard = ORT_RUNTIME_INIT_LOCK
            .lock()
            .map_err(|_| NllbError::new("ONNX Runtime initialization lock is poisoned"))?;

        if let Some(existing) = ORT_RUNTIME_DLL_PATH.get() {
            if existing == &dll_path {
                return Ok(());
            }
            return Err(NllbError::new(format!(
                "ONNX Runtime is already initialized from '{}', cannot switch to '{}'",
                existing.display(),
                dll_path.display()
            )));
        }

        let committed = ort::init_from(&dll_path)
            .map_err(map_ort_error("load ONNX Runtime DLL"))?
            .with_name("easydict-nllb")
            .with_telemetry(false)
            .commit();
        if !committed {
            return Err(NllbError::new(
                "ONNX Runtime was already initialized before the Easydict NLLB runtime path was set",
            ));
        }

        ORT_RUNTIME_DLL_PATH
            .set(dll_path)
            .map_err(|_| NllbError::new("ONNX Runtime path was initialized concurrently"))?;
        Ok(())
    }

    fn run_encoder_with_session(
        session: &mut Session,
        names: &EncoderIoNames,
        encoder_input_ids: &[i32],
    ) -> Result<EncoderRunResult, NllbError> {
        let source_len = encoder_input_ids.len();
        let input_ids = encoder_input_ids
            .iter()
            .copied()
            .map(i64::from)
            .collect::<Vec<_>>();
        let attention_mask = vec![1_i64; source_len];
        let input_ids_tensor =
            TensorRef::<i64>::from_array_view(([1, source_len], input_ids.as_slice()))
                .map_err(map_ort_error("create NLLB encoder input_ids tensor"))?;
        let attention_tensor =
            TensorRef::<i64>::from_array_view(([1, source_len], attention_mask.as_slice()))
                .map_err(map_ort_error("create NLLB encoder attention_mask tensor"))?;

        let outputs = session
            .run(ort::inputs! {
                names.input_ids.as_str() => input_ids_tensor,
                names.attention_mask.as_str() => attention_tensor,
            })
            .map_err(map_ort_error("run NLLB encoder session"))?;
        let hidden_output = outputs
            .get(names.hidden_state_output.as_str())
            .ok_or_else(|| {
                NllbError::new(format!(
                    "NLLB encoder output '{}' was not returned",
                    names.hidden_state_output
                ))
            })?;
        let (shape, hidden) = hidden_output
            .try_extract_tensor::<f32>()
            .map_err(map_ort_error("extract NLLB encoder hidden states"))?;

        Ok(EncoderRunResult {
            hidden_shape: shape.iter().copied().collect(),
            hidden: hidden.to_vec(),
            attention_mask,
            source_len,
        })
    }

    fn create_encoder_session(
        encoder_path: &Path,
        device: OpenVinoDevice,
        precision_hint: Option<&str>,
    ) -> Result<Session, NllbError> {
        if device == OpenVinoDevice::Cpu {
            return create_cpu_session(encoder_path, "NLLB encoder");
        }

        create_openvino_session(encoder_path, device, precision_hint)
            .or_else(|_| create_cpu_session(encoder_path, "NLLB encoder CPU fallback"))
    }

    fn create_openvino_session(
        model_path: &Path,
        device: OpenVinoDevice,
        precision_hint: Option<&str>,
    ) -> Result<Session, NllbError> {
        let mut provider =
            ort::ep::OpenVINO::default().with_device_type(device.as_openvino_device_type());
        if let Some(precision_hint) = precision_hint.filter(|value| !value.trim().is_empty()) {
            provider = provider.with_precision(precision_hint.trim());
        }

        let builder = Session::builder().map_err(map_ort_error("create NLLB ONNX session"))?;
        let mut builder = builder
            .with_execution_providers([provider.build()])
            .unwrap_or_else(|error| error.recover());
        builder
            .commit_from_file(model_path)
            .map_err(map_ort_error("load NLLB encoder ONNX model"))
    }

    fn create_cpu_session(model_path: &Path, label: &str) -> Result<Session, NllbError> {
        let mut builder =
            Session::builder().map_err(map_ort_error(format!("create {label} session")))?;
        builder
            .commit_from_file(model_path)
            .map_err(map_ort_error(format!("load {label} ONNX model")))
    }

    fn resolve_input_name(session: &Session, expected: &str) -> Result<String, NllbError> {
        resolve_io_name(
            session.inputs().iter().map(|input| input.name()),
            expected,
            "input",
        )
    }

    fn resolve_output_name(session: &Session, expected: &str) -> Result<String, NllbError> {
        resolve_io_name(
            session.outputs().iter().map(|output| output.name()),
            expected,
            "output",
        )
    }

    fn resolve_io_name<'a>(
        names: impl Iterator<Item = &'a str>,
        expected: &str,
        kind: &str,
    ) -> Result<String, NllbError> {
        let names = names.map(str::to_string).collect::<Vec<_>>();
        if let Some(name) = names
            .iter()
            .find(|name| name.eq_ignore_ascii_case(expected))
            .or_else(|| {
                names
                    .iter()
                    .find(|name| name.to_ascii_lowercase().ends_with(expected))
            })
        {
            return Ok(name.clone());
        }

        Err(NllbError::new(format!(
            "expected NLLB ONNX {kind} '{expected}' not found; available {kind}s: {}",
            names.join(", ")
        )))
    }

    fn best_token_from_logits(shape: &[i64], logits: &[f32]) -> Result<i32, NllbError> {
        if shape.len() != 3 || shape[0] != 1 {
            return Err(NllbError::new(format!(
                "NLLB decoder logits must have shape [1, tgt_len, vocab_size], got {shape:?}"
            )));
        }

        let target_len = usize::try_from(shape[1]).map_err(|_| {
            NllbError::new(format!(
                "NLLB decoder logits has invalid target length {}",
                shape[1]
            ))
        })?;
        let vocab_size = usize::try_from(shape[2]).map_err(|_| {
            NllbError::new(format!(
                "NLLB decoder logits has invalid vocabulary size {}",
                shape[2]
            ))
        })?;
        if target_len == 0 || vocab_size == 0 {
            return Err(NllbError::new(format!(
                "NLLB decoder logits must have non-empty target/vocabulary dimensions, got {shape:?}"
            )));
        }

        let expected_len = target_len
            .checked_mul(vocab_size)
            .ok_or_else(|| NllbError::new("NLLB decoder logits shape overflows element count"))?;
        if logits.len() != expected_len {
            return Err(NllbError::new(format!(
                "NLLB decoder logits length {} does not match shape {shape:?}",
                logits.len()
            )));
        }

        let start = (target_len - 1) * vocab_size;
        let last_step = &logits[start..start + vocab_size];
        let (index, _) = last_step
            .iter()
            .copied()
            .enumerate()
            .max_by(|(_, left), (_, right)| {
                left.partial_cmp(right).unwrap_or(std::cmp::Ordering::Less)
            })
            .ok_or_else(|| NllbError::new("NLLB decoder logits has no vocabulary entries"))?;
        i32::try_from(index).map_err(|_| NllbError::new("NLLB decoder token id exceeds i32 range"))
    }

    fn require_file(path: &Path, label: &str) -> Result<(), NllbError> {
        if path.is_file() {
            return Ok(());
        }

        Err(NllbError::new(format!(
            "{label} not found at '{}'",
            path.display()
        )))
    }

    fn is_openvino_runtime_failure(message: &str) -> bool {
        let message = message.to_ascii_lowercase();
        message.contains("openvino-ep")
            || message.contains("openvinoexecutionprovider")
            || message.contains("openvino_ep")
    }

    fn map_ort_error(context: impl Into<String>) -> impl FnOnce(ort::Error) -> NllbError {
        let context = context.into();
        move |error| NllbError::new(format!("{context}: {error}"))
    }

    #[cfg(test)]
    mod tests {
        use super::*;

        #[test]
        fn selects_argmax_from_last_decoder_timestep() {
            let logits = vec![
                0.1, 4.0, 3.0, 0.0, //
                0.2, 0.3, 0.4, 9.0,
            ];

            assert_eq!(best_token_from_logits(&[1, 2, 4], &logits).unwrap(), 3);
        }

        #[test]
        fn rejects_logits_shape_mismatch() {
            let error = best_token_from_logits(&[1, 2, 4], &[0.0; 7]).unwrap_err();

            assert!(error.message().contains("does not match shape [1, 2, 4]"));
        }

        #[test]
        fn resolves_exact_or_suffix_io_names() {
            let names = ["encoder.input_ids", "attention_mask"]
                .into_iter()
                .map(str::to_string)
                .collect::<Vec<_>>();

            assert_eq!(
                resolve_io_name(names.iter().map(String::as_str), "input_ids", "input").unwrap(),
                "encoder.input_ids"
            );
        }
    }
}

pub struct NllbTranslator<T, E> {
    tokenizer: T,
    engine: E,
    max_new_tokens: usize,
}

impl<T, E> NllbTranslator<T, E> {
    pub fn new(tokenizer: T, engine: E) -> Self {
        Self {
            tokenizer,
            engine,
            max_new_tokens: DEFAULT_MAX_NEW_TOKENS,
        }
    }

    pub fn with_max_new_tokens(mut self, max_new_tokens: usize) -> Self {
        self.max_new_tokens = max_new_tokens;
        self
    }

    pub fn tokenizer(&self) -> &T {
        &self.tokenizer
    }

    pub fn engine(&self) -> &E {
        &self.engine
    }
}

impl<T: NllbTokenizer, E: NllbInferenceEngine> NllbTranslator<T, E> {
    pub fn translate_stream_chunks(
        &mut self,
        text: &str,
        source_language_name: &str,
        target_language_name: &str,
    ) -> Result<NllbTranslation, NllbError> {
        let source_flores = source_flores_code_for_dotnet_language_name(source_language_name)?;
        let target_flores = target_flores_code_for_dotnet_language_name(target_language_name)?;
        let input_ids = self.tokenizer.encode_source(text, source_flores)?;
        let forced_bos = self.tokenizer.language_token_id(target_flores)?;
        let generated = self
            .engine
            .generate(&input_ids, forced_bos, self.max_new_tokens)?;

        let mut chunks = Vec::new();
        let mut previous_decoded = String::new();
        for index in 0..generated.len() {
            let decoded = self.tokenizer.decode(&generated[..=index])?;
            let delta = streaming_decode_delta(&previous_decoded, &decoded);
            if !delta.is_empty() {
                previous_decoded = decoded;
                chunks.push(delta);
            }
        }

        Ok(NllbTranslation {
            text: previous_decoded.trim().to_string(),
            chunks,
            generated_token_ids: generated,
        })
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct NllbTranslation {
    pub text: String,
    pub chunks: Vec<String>,
    pub generated_token_ids: Vec<i32>,
}

pub fn streaming_decode_delta(previous_decoded_text: &str, decoded_text: &str) -> String {
    if decoded_text.is_empty() || previous_decoded_text == decoded_text {
        return String::new();
    }

    if let Some(delta) = decoded_text.strip_prefix(previous_decoded_text) {
        return delta.to_string();
    }

    let common_prefix_bytes = previous_decoded_text
        .char_indices()
        .zip(decoded_text.char_indices())
        .take_while(|((_, left), (_, right))| left == right)
        .map(|((index, ch), _)| index + ch.len_utf8())
        .last()
        .unwrap_or(0);
    decoded_text[common_prefix_bytes..].to_string()
}

pub fn source_flores_code_for_dotnet_language_name(
    language_name: &str,
) -> Result<&'static str, NllbError> {
    if language_name.trim().eq_ignore_ascii_case("auto") {
        return Ok("eng_Latn");
    }

    target_flores_code_for_dotnet_language_name(language_name)
}

pub fn target_flores_code_for_dotnet_language_name(
    language_name: &str,
) -> Result<&'static str, NllbError> {
    flores_code_for_dotnet_language_name(language_name).ok_or_else(|| {
        NllbError::new(format!(
            "NLLB-200 does not support language '{}'",
            language_name.trim()
        ))
    })
}

pub fn nllb_language_name_from_code(
    code: Option<&str>,
    default_name: &str,
) -> Option<&'static str> {
    let code = code
        .map(str::trim)
        .filter(|code| !code.is_empty())
        .unwrap_or_else(|| default_name.trim());
    if code.is_empty() {
        return None;
    }

    let normalized = code.to_ascii_lowercase();
    let primary_subtag = normalized
        .split_once('-')
        .map(|(primary, _)| primary)
        .unwrap_or(normalized.as_str());
    let language_name = match normalized.as_str() {
        "auto" => "Auto",
        "zh-cn" | "zh-hans" | "zh" | "simplifiedchinese" | "chinesesimplified" => {
            "SimplifiedChinese"
        }
        "zh-tw" | "zh-hant" | "traditionalchinese" | "chinesetraditional" => "TraditionalChinese",
        "zh-classical" | "classicalchinese" | "chineseclassical" => "ClassicalChinese",
        "english" => "English",
        "japanese" => "Japanese",
        "korean" => "Korean",
        "french" => "French",
        "spanish" => "Spanish",
        "portuguese" => "Portuguese",
        "italian" => "Italian",
        "german" => "German",
        "russian" => "Russian",
        "arabic" => "Arabic",
        "swedish" => "Swedish",
        "romanian" => "Romanian",
        "thai" => "Thai",
        "dutch" => "Dutch",
        "hungarian" => "Hungarian",
        "greek" => "Greek",
        "danish" => "Danish",
        "finnish" => "Finnish",
        "polish" => "Polish",
        "czech" => "Czech",
        "turkish" => "Turkish",
        "ukrainian" => "Ukrainian",
        "bulgarian" => "Bulgarian",
        "slovak" => "Slovak",
        "slovenian" => "Slovenian",
        "estonian" => "Estonian",
        "latvian" => "Latvian",
        "lithuanian" => "Lithuanian",
        "indonesian" => "Indonesian",
        "malay" => "Malay",
        "vietnamese" => "Vietnamese",
        "persian" => "Persian",
        "hindi" => "Hindi",
        "telugu" => "Telugu",
        "tamil" => "Tamil",
        "urdu" => "Urdu",
        "filipino" => "Filipino",
        "bengali" => "Bengali",
        "norwegian" => "Norwegian",
        "hebrew" => "Hebrew",
        _ => match primary_subtag {
            "en" => "English",
            "ja" => "Japanese",
            "ko" => "Korean",
            "fr" => "French",
            "es" => "Spanish",
            "pt" => "Portuguese",
            "it" => "Italian",
            "de" => "German",
            "ru" => "Russian",
            "ar" => "Arabic",
            "sv" => "Swedish",
            "ro" => "Romanian",
            "th" => "Thai",
            "nl" => "Dutch",
            "hu" => "Hungarian",
            "el" => "Greek",
            "da" => "Danish",
            "fi" => "Finnish",
            "pl" => "Polish",
            "cs" => "Czech",
            "tr" => "Turkish",
            "uk" => "Ukrainian",
            "bg" => "Bulgarian",
            "sk" => "Slovak",
            "sl" => "Slovenian",
            "et" => "Estonian",
            "lv" => "Latvian",
            "lt" => "Lithuanian",
            "id" => "Indonesian",
            "ms" => "Malay",
            "vi" => "Vietnamese",
            "fa" => "Persian",
            "hi" => "Hindi",
            "te" => "Telugu",
            "ta" => "Tamil",
            "ur" => "Urdu",
            "tl" | "fil" => "Filipino",
            "bn" => "Bengali",
            "no" | "nb" => "Norwegian",
            "he" | "iw" => "Hebrew",
            _ => return None,
        },
    };

    Some(language_name)
}

pub fn flores_code_for_dotnet_language_name(language_name: &str) -> Option<&'static str> {
    match normalize_language_name(language_name).as_str() {
        "simplifiedchinese" | "chinesesimplified" => Some("zho_Hans"),
        "traditionalchinese" | "chinesetraditional" => Some("zho_Hant"),
        "classicalchinese" | "chineseclassical" => Some("zho_Hans"),
        "japanese" => Some("jpn_Jpan"),
        "korean" => Some("kor_Hang"),
        "english" => Some("eng_Latn"),
        "german" => Some("deu_Latn"),
        "dutch" => Some("nld_Latn"),
        "swedish" => Some("swe_Latn"),
        "norwegian" => Some("nob_Latn"),
        "danish" => Some("dan_Latn"),
        "french" => Some("fra_Latn"),
        "spanish" => Some("spa_Latn"),
        "portuguese" => Some("por_Latn"),
        "italian" => Some("ita_Latn"),
        "romanian" => Some("ron_Latn"),
        "russian" => Some("rus_Cyrl"),
        "polish" => Some("pol_Latn"),
        "czech" => Some("ces_Latn"),
        "ukrainian" => Some("ukr_Cyrl"),
        "bulgarian" => Some("bul_Cyrl"),
        "slovak" => Some("slk_Latn"),
        "slovenian" => Some("slv_Latn"),
        "estonian" => Some("est_Latn"),
        "latvian" => Some("lvs_Latn"),
        "lithuanian" => Some("lit_Latn"),
        "greek" => Some("ell_Grek"),
        "hungarian" => Some("hun_Latn"),
        "finnish" => Some("fin_Latn"),
        "turkish" => Some("tur_Latn"),
        "arabic" => Some("arb_Arab"),
        "persian" => Some("pes_Arab"),
        "hebrew" => Some("heb_Hebr"),
        "hindi" => Some("hin_Deva"),
        "bengali" => Some("ben_Beng"),
        "tamil" => Some("tam_Taml"),
        "telugu" => Some("tel_Telu"),
        "urdu" => Some("urd_Arab"),
        "vietnamese" => Some("vie_Latn"),
        "thai" => Some("tha_Thai"),
        "indonesian" => Some("ind_Latn"),
        "malay" => Some("zsm_Latn"),
        "filipino" => Some("tgl_Latn"),
        _ => None,
    }
}

fn normalize_language_name(language_name: &str) -> String {
    language_name
        .chars()
        .filter(|ch| ch.is_ascii_alphanumeric())
        .flat_map(char::to_lowercase)
        .collect()
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct AddedTokenTable {
    by_content: HashMap<String, i32>,
    special_ids: HashSet<i32>,
}

impl AddedTokenTable {
    pub fn from_tokenizer_json_str(json_text: &str) -> Result<Self, NllbError> {
        let root: serde_json::Value =
            serde_json::from_str(json_text).map_err(|error| NllbError::new(error.to_string()))?;
        let added_tokens = root
            .get("added_tokens")
            .and_then(serde_json::Value::as_array)
            .ok_or_else(|| NllbError::new("tokenizer.json has no 'added_tokens' array"))?;

        let mut by_content = HashMap::new();
        let mut special_ids = HashSet::new();
        for token in added_tokens {
            let Some(content) = token.get("content").and_then(serde_json::Value::as_str) else {
                continue;
            };
            let Some(id) = token.get("id").and_then(serde_json::Value::as_i64) else {
                continue;
            };
            let Ok(id) = i32::try_from(id) else {
                continue;
            };

            by_content.insert(content.to_string(), id);
            if token
                .get("special")
                .and_then(serde_json::Value::as_bool)
                .unwrap_or(false)
            {
                special_ids.insert(id);
            }
        }

        if by_content.is_empty() {
            return Err(NllbError::new(
                "tokenizer.json has no usable entries in 'added_tokens'",
            ));
        }

        Ok(Self {
            by_content,
            special_ids,
        })
    }

    pub fn token_id(&self, content: &str) -> Option<i32> {
        self.by_content.get(content).copied()
    }

    pub fn required_token_id(&self, content: &str) -> Result<i32, NllbError> {
        self.token_id(content).ok_or_else(|| {
            NllbError::new(format!(
                "NLLB tokenizer is missing required token '{content}'"
            ))
        })
    }

    pub fn is_special_token_id(&self, id: i32) -> bool {
        self.special_ids.contains(&id)
    }

    pub fn len(&self) -> usize {
        self.by_content.len()
    }

    pub fn is_empty(&self) -> bool {
        self.by_content.is_empty()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn maps_dotnet_language_names_to_flores_codes() {
        assert_eq!(
            target_flores_code_for_dotnet_language_name("SimplifiedChinese").unwrap(),
            "zho_Hans"
        );
        assert_eq!(
            target_flores_code_for_dotnet_language_name("Slovak").unwrap(),
            "slk_Latn"
        );
        assert_eq!(
            target_flores_code_for_dotnet_language_name("Chinese Traditional").unwrap(),
            "zho_Hant"
        );
        assert_eq!(
            source_flores_code_for_dotnet_language_name("Auto").unwrap(),
            "eng_Latn"
        );
        assert!(target_flores_code_for_dotnet_language_name("Auto").is_err());
    }

    #[test]
    fn resolves_nllb_language_codes_to_dotnet_language_names() {
        assert_eq!(nllb_language_name_from_code(None, "Auto"), Some("Auto"));
        assert_eq!(
            nllb_language_name_from_code(Some("zh-Hant"), "English"),
            Some("TraditionalChinese")
        );
        assert_eq!(
            nllb_language_name_from_code(Some("sk-SK"), "English"),
            Some("Slovak")
        );
        assert_eq!(
            nllb_language_name_from_code(Some("fil-PH"), "English"),
            Some("Filipino")
        );
        assert_eq!(nllb_language_name_from_code(Some("xx"), "English"), None);
    }

    #[test]
    fn maps_openvino_devices_to_ort_device_type_values() {
        assert_eq!(
            OpenVinoDevice::from_setting(Some("GPU")).as_openvino_device_type(),
            "GPU"
        );
        assert_eq!(
            OpenVinoDevice::from_setting(None).as_openvino_device_type(),
            "AUTO:NPU,GPU,CPU"
        );
    }

    #[test]
    fn model_paths_follow_easydict_cache_layout() {
        let paths = NllbModelPaths::from_cache_base(r"C:\Users\me\AppData\Local\Easydict");

        assert!(paths
            .encoder_path()
            .ends_with(r"models\nllb-200-distilled-600M\encoder_model_quantized.onnx"));
        assert!(paths
            .runtime_dir
            .ends_with(r"runtimes\openvino\1.21.0\win-x64\native"));
    }

    #[test]
    fn cache_completion_requires_the_full_nllb_and_openvino_manifest() {
        let dir = temp_test_dir("cache-complete");
        let paths = NllbModelPaths::from_cache_base(&dir);
        install_file_set(&paths.model_dir, NLLB_MODEL_FILES);

        assert!(!paths.is_cache_complete());

        install_file_set(&paths.runtime_dir, OPENVINO_RUNTIME_FILES);
        assert!(paths.is_cache_complete());

        std::fs::remove_file(paths.runtime_dir.join("openvino_intel_npu_plugin.dll")).unwrap();
        assert!(!paths.is_cache_complete());

        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn model_download_manifest_matches_cache_manifest_and_pinned_revision() {
        assert!(nllb_model_revision_is_pinned());
        let manifest_names = NLLB_MODEL_MANIFEST_FILES
            .iter()
            .map(|file| file.local_file_name)
            .collect::<Vec<_>>();
        assert_eq!(manifest_names, NLLB_MODEL_FILES);
        assert_eq!(NLLB_MODEL_ID, "nllb-200-distilled-600M-int8");
        assert_eq!(NLLB_HUGGINGFACE_REPO, "Xenova/nllb-200-distilled-600M");
        assert_eq!(nllb_model_total_approximate_bytes(), 911_837_616);

        let encoder = nllb_model_file("ENCODER_MODEL_QUANTIZED.ONNX").unwrap();
        assert_eq!(
            encoder.download_url(),
            "https://huggingface.co/Xenova/nllb-200-distilled-600M/resolve/261c31d1a5732c67cdd16d80e8d6088507c7ccea/onnx/encoder_model_quantized.onnx"
        );
        assert!(encoder
            .sha256
            .unwrap()
            .chars()
            .all(|ch| ch.is_ascii_hexdigit()));
        assert_eq!(encoder.sha256.unwrap().len(), 64);
        assert_eq!(nllb_model_file("config.json").unwrap().sha256, None);
    }

    #[test]
    fn openvino_runtime_download_manifest_is_pinned() {
        assert_eq!(OPENVINO_RUNTIME_VERSION, "1.21.0");
        assert_eq!(OPENVINO_RUNTIME_IDENTIFIER, "win-x64");
        assert_eq!(
            OPENVINO_RUNTIME_PACKAGE_URL,
            "https://www.nuget.org/api/v2/package/Intel.ML.OnnxRuntime.OpenVino/1.21.0"
        );
        assert_eq!(
            OPENVINO_RUNTIME_PACKAGE_SHA256,
            "a70be78c7ce5c0ff82538f8934fffaafa5f63409ee0604d3990c8b393e178e15"
        );
        assert_eq!(
            openvino_runtime_package_file_name(),
            "intel.ml.onnxruntime.openvino.1.21.0.nupkg"
        );
        assert_eq!(
            OPENVINO_EP_ENABLE_ENVIRONMENT_VARIABLE,
            "EASYDICT_ENABLE_OPENVINO_EP"
        );
        assert_eq!(OPENVINO_RUNTIME_PACKAGE_SHA256.len(), 64);
        assert!(OPENVINO_RUNTIME_PACKAGE_SHA256
            .chars()
            .all(|ch| ch.is_ascii_hexdigit()));
    }

    #[test]
    fn parses_added_tokens_from_huggingface_tokenizer_json() {
        let table = AddedTokenTable::from_tokenizer_json_str(
            r#"{
                "added_tokens": [
                    {"id": 0, "content": "<s>", "special": true},
                    {"id": 1, "content": "<pad>", "special": true},
                    {"id": 2, "content": "</s>", "special": true},
                    {"id": 3, "content": "<unk>", "special": true},
                    {"id": 256047, "content": "eng_Latn", "special": true}
                ]
            }"#,
        )
        .unwrap();

        assert_eq!(table.required_token_id("<s>").unwrap(), 0);
        assert_eq!(table.required_token_id("eng_Latn").unwrap(), 256047);
        assert!(table.is_special_token_id(256047));
        assert_eq!(table.len(), 5);
    }

    #[test]
    fn huggingface_tokenizer_encodes_source_with_language_prefix_and_eos() {
        let tokenizer = HuggingFaceNllbTokenizer::from_tokenizer_json_str(tiny_tokenizer_json())
            .expect("tiny tokenizer should load");

        assert_eq!(tokenizer.language_token_id("eng_Latn").unwrap(), 4);
        assert_eq!(tokenizer.eos_token_id(), 2);
        assert_eq!(
            tokenizer.encode_source("hello world", "eng_Latn").unwrap(),
            vec![4, 10, 11, 2]
        );
        assert_eq!(
            tokenizer.encode_source("ＡＢＣ１２３", "eng_Latn").unwrap(),
            vec![4, 12, 2],
            "Rust NLLB tokenizer should preserve the .NET NFKC input normalization behavior"
        );
    }

    #[test]
    fn huggingface_tokenizer_decode_strips_language_and_control_tokens() {
        let tokenizer = HuggingFaceNllbTokenizer::from_tokenizer_json_str(tiny_tokenizer_json())
            .expect("tiny tokenizer should load");

        assert_eq!(tokenizer.decode(&[4, 10, 11, 2, 5]).unwrap(), "hello world");
        assert!(tokenizer
            .decode(&[-1])
            .unwrap_err()
            .message()
            .contains("invalid negative token id"));
    }

    #[test]
    fn streaming_delta_matches_csharp_decode_delta_policy() {
        assert_eq!(streaming_decode_delta("", "Hello"), "Hello");
        assert_eq!(streaming_decode_delta("Hello", "Hello world"), " world");
        assert_eq!(streaming_decode_delta("Hello", "Help"), "p");
        assert_eq!(streaming_decode_delta("你好", "你好世界"), "世界");
        assert_eq!(streaming_decode_delta("same", "same"), "");
    }

    #[test]
    fn translator_encodes_forced_bos_and_returns_stream_chunks() {
        let tokenizer = RecordingTokenizer::default();
        let engine = RecordingEngine {
            generated: vec![10, 11, 12],
            ..RecordingEngine::default()
        };
        let mut translator = NllbTranslator::new(tokenizer, engine).with_max_new_tokens(3);

        let result = translator
            .translate_stream_chunks("Hello", "English", "SimplifiedChinese")
            .unwrap();

        assert_eq!(result.text, "hello world");
        assert_eq!(result.chunks, vec!["hello", " world"]);
        assert_eq!(result.generated_token_ids, vec![10, 11, 12]);
        assert_eq!(
            translator.engine().last_call.as_ref().unwrap(),
            &EngineCall {
                input_ids: vec![101, 42, 2],
                forced_bos: 256001,
                max_new_tokens: 3,
            }
        );
    }

    #[derive(Default)]
    struct RecordingTokenizer;

    impl NllbTokenizer for RecordingTokenizer {
        fn encode_source(
            &self,
            text: &str,
            source_flores_code: &str,
        ) -> Result<Vec<i32>, NllbError> {
            assert_eq!(text, "Hello");
            assert_eq!(source_flores_code, "eng_Latn");
            Ok(vec![101, 42, 2])
        }

        fn decode(&self, token_ids: &[i32]) -> Result<String, NllbError> {
            match token_ids {
                [10] => Ok("hello".to_string()),
                [10, 11] => Ok("hello".to_string()),
                [10, 11, 12] => Ok("hello world".to_string()),
                _ => Err(NllbError::new("unexpected token ids")),
            }
        }

        fn language_token_id(&self, flores_code: &str) -> Result<i32, NllbError> {
            assert_eq!(flores_code, "zho_Hans");
            Ok(256001)
        }
    }

    #[derive(Default)]
    struct RecordingEngine {
        generated: Vec<i32>,
        last_call: Option<EngineCall>,
    }

    impl NllbInferenceEngine for RecordingEngine {
        fn generate(
            &mut self,
            encoder_input_ids: &[i32],
            forced_bos_token_id: i32,
            max_new_tokens: usize,
        ) -> Result<Vec<i32>, NllbError> {
            self.last_call = Some(EngineCall {
                input_ids: encoder_input_ids.to_vec(),
                forced_bos: forced_bos_token_id,
                max_new_tokens,
            });
            Ok(self.generated.clone())
        }
    }

    #[derive(Debug, Eq, PartialEq)]
    struct EngineCall {
        input_ids: Vec<i32>,
        forced_bos: i32,
        max_new_tokens: usize,
    }

    fn tiny_tokenizer_json() -> &'static str {
        r#"{
            "version": "1.0",
            "truncation": null,
            "padding": null,
            "added_tokens": [
                {
                    "id": 0,
                    "content": "<s>",
                    "single_word": false,
                    "lstrip": false,
                    "rstrip": false,
                    "normalized": false,
                    "special": true
                },
                {
                    "id": 1,
                    "content": "<pad>",
                    "single_word": false,
                    "lstrip": false,
                    "rstrip": false,
                    "normalized": false,
                    "special": true
                },
                {
                    "id": 2,
                    "content": "</s>",
                    "single_word": false,
                    "lstrip": false,
                    "rstrip": false,
                    "normalized": false,
                    "special": true
                },
                {
                    "id": 3,
                    "content": "<unk>",
                    "single_word": false,
                    "lstrip": false,
                    "rstrip": false,
                    "normalized": false,
                    "special": true
                },
                {
                    "id": 4,
                    "content": "eng_Latn",
                    "single_word": false,
                    "lstrip": false,
                    "rstrip": false,
                    "normalized": false,
                    "special": true
                },
                {
                    "id": 5,
                    "content": "zho_Hans",
                    "single_word": false,
                    "lstrip": false,
                    "rstrip": false,
                    "normalized": false,
                    "special": true
                }
            ],
            "normalizer": null,
            "pre_tokenizer": {"type": "WhitespaceSplit"},
            "post_processor": null,
            "decoder": null,
            "model": {
                "type": "WordLevel",
                "vocab": {
                    "<s>": 0,
                    "<pad>": 1,
                    "</s>": 2,
                    "<unk>": 3,
                    "eng_Latn": 4,
                    "zho_Hans": 5,
                    "hello": 10,
                    "world": 11,
                    "ABC123": 12
                },
                "unk_token": "<unk>"
            }
        }"#
    }

    fn temp_test_dir(name: &str) -> PathBuf {
        std::env::temp_dir().join(format!(
            "easydict-nllb-{name}-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ))
    }

    fn install_file_set(dir: &Path, files: &[&str]) {
        std::fs::create_dir_all(dir).unwrap();
        std::fs::write(dir.join(MODEL_COMPLETION_SENTINEL), b"x").unwrap();
        for file in files {
            std::fs::write(dir.join(file), b"x").unwrap();
        }
    }
}
