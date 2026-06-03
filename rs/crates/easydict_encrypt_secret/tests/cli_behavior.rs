use std::process::Command;

#[test]
fn cli_encrypts_secret_and_verifies_roundtrip() {
    let output = Command::new(env!("CARGO_BIN_EXE_easydict_encrypt_secret"))
        .arg("my-api-key")
        .output()
        .expect("run easydict_encrypt_secret");

    assert!(output.status.success());
    let stdout = String::from_utf8(output.stdout).expect("stdout should be utf8");
    assert!(stdout.contains("Plaintext: my-api-key"));
    assert!(stdout.contains("Encrypted: SNtcOSNOR+8Y18pItZdXlg=="));
    assert!(stdout.contains("Verification: OK (decryption successful)"));
}
