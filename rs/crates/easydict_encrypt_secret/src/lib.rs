use aes::cipher::{block_padding::Pkcs7, BlockDecryptMut, BlockEncryptMut, KeyIvInit};
use base64::{engine::general_purpose, Engine as _};
use ring::digest;
use std::fmt::{self, Write as _};

const LEGACY_SECRET_ASSEMBLY_NAME: &str = "Easydict.TranslationService";

type Aes128CbcEnc = cbc::Encryptor<aes::Aes128>;
type Aes128CbcDec = cbc::Decryptor<aes::Aes128>;

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum SecretCryptoError {
    InvalidBase64,
    DecryptFailed,
    InvalidUtf8,
}

impl fmt::Display for SecretCryptoError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::InvalidBase64 => formatter.write_str("secret is not valid base64"),
            Self::DecryptFailed => formatter.write_str("secret could not be decrypted"),
            Self::InvalidUtf8 => formatter.write_str("secret is not valid UTF-8"),
        }
    }
}

impl std::error::Error for SecretCryptoError {}

pub fn encrypt_secret(plaintext: &str) -> String {
    let key = legacy_secret_key();
    let encrypted = Aes128CbcEnc::new(&key.into(), &key.into())
        .encrypt_padded_vec_mut::<Pkcs7>(plaintext.as_bytes());
    general_purpose::STANDARD.encode(encrypted)
}

pub fn decrypt_secret(base64_encrypted: &str) -> Result<String, SecretCryptoError> {
    let encrypted_bytes = general_purpose::STANDARD
        .decode(base64_encrypted.trim().as_bytes())
        .map_err(|_| SecretCryptoError::InvalidBase64)?;
    let key = legacy_secret_key();
    let decrypted = Aes128CbcDec::new(&key.into(), &key.into())
        .decrypt_padded_vec_mut::<Pkcs7>(&encrypted_bytes)
        .map_err(|_| SecretCryptoError::DecryptFailed)?;

    String::from_utf8(decrypted).map_err(|_| SecretCryptoError::InvalidUtf8)
}

fn legacy_secret_key() -> [u8; 16] {
    let hash = digest::digest(&digest::SHA256, LEGACY_SECRET_ASSEMBLY_NAME.as_bytes());
    let mut hex = String::with_capacity(hash.as_ref().len() * 2);
    for byte in hash.as_ref() {
        let _ = write!(&mut hex, "{byte:02x}");
    }

    let mut key = [0_u8; 16];
    key.copy_from_slice(&hex.as_bytes()[..16]);
    key
}

#[cfg(test)]
mod tests {
    use super::{decrypt_secret, encrypt_secret};
    use std::path::Path;

    #[test]
    fn encrypt_secret_matches_legacy_dotnet_secret_key_manager_vector() {
        let encrypted = encrypt_secret("my-api-key");

        assert_eq!(encrypted, "SNtcOSNOR+8Y18pItZdXlg==");
        assert_eq!(decrypt_secret(&encrypted).as_deref(), Ok("my-api-key"));
    }

    #[test]
    fn decrypt_secret_rejects_invalid_payloads() {
        assert!(decrypt_secret("not base64").is_err());
        assert!(decrypt_secret("Zm9v").is_err());
    }

    #[test]
    fn makefile_encrypt_secret_target_uses_rust_cli_and_retired_dotnet_tool_stays_absent() {
        let workspace_manifest = include_str!("../../../Cargo.toml");
        assert!(workspace_manifest.contains("crates/easydict_encrypt_secret"));

        let makefile = include_str!("../../../../dotnet/Makefile");
        assert!(makefile
            .contains("cargo run --manifest-path ../rs/Cargo.toml -p easydict_encrypt_secret"));
        assert!(!makefile.contains("dotnet run --project tools/EncryptSecret"));
        assert!(!makefile.contains("tools/EncryptSecret/EncryptSecret.csproj"));

        let repo_root = Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("..")
            .join("..")
            .join("..");
        assert!(!repo_root
            .join("dotnet/tools/EncryptSecret/EncryptSecret.csproj")
            .exists());
    }
}
