use easydict_encrypt_secret::{decrypt_secret, encrypt_secret};

fn main() {
    std::process::exit(run(std::env::args().skip(1).collect()));
}

fn run(args: Vec<String>) -> i32 {
    if args.is_empty() {
        print_usage();
        return 1;
    }

    if args[0] == "-h" || args[0] == "--help" {
        print_usage();
        return 0;
    }

    let plaintext = &args[0];
    let encrypted = encrypt_secret(plaintext);

    println!("Plaintext: {plaintext}");
    println!("Encrypted: {encrypted}");
    println!();
    println!("Add to EncryptedSecrets.json:");
    println!("  \"keyName\": \"{encrypted}\"");

    match decrypt_secret(&encrypted) {
        Ok(decrypted) if decrypted == *plaintext => {
            println!();
            println!("Verification: OK (decryption successful)");
            0
        }
        _ => {
            println!();
            println!("Verification: FAILED (decryption mismatch)");
            1
        }
    }
}

fn print_usage() {
    println!("Usage: easydict_encrypt_secret <secret>");
    println!();
    println!("Encrypts a secret using the same AES encryption as SecretKeyManager.");
    println!("The output can be added to EncryptedSecrets.json.");
    println!();
    println!("Example:");
    println!(
        "  cargo run --manifest-path rs/Cargo.toml -p easydict_encrypt_secret -- \"my-api-key\""
    );
}
