use easydict_app::lex_index::LexIndex;
use std::env;
use std::fs;
use std::path::Path;
use std::process::ExitCode;

const USAGE: &str = "Usage: easydict-lex-index <input-keys.txt> <output-index.bin>";

fn main() -> ExitCode {
    match run(env::args().skip(1)) {
        Ok(message) => {
            println!("{message}");
            ExitCode::SUCCESS
        }
        Err(error) => {
            eprintln!("{error}");
            ExitCode::FAILURE
        }
    }
}

fn run(args: impl IntoIterator<Item = impl Into<String>>) -> Result<String, String> {
    let args = args.into_iter().map(Into::into).collect::<Vec<_>>();
    let [input_path, output_path] = args.as_slice() else {
        return Err(USAGE.to_string());
    };

    let input_path = Path::new(input_path);
    if !input_path.exists() {
        return Err(format!("Input file not found: {}", input_path.display()));
    }

    let input = fs::read_to_string(input_path).map_err(|error| {
        format!(
            "Could not read input file '{}': {error}",
            input_path.display()
        )
    })?;
    let bytes = LexIndex::build_bytes(input.lines());
    fs::write(output_path, bytes)
        .map_err(|error| format!("Could not write output index '{}': {error}", output_path))?;

    Ok(format!("Built index: {output_path}"))
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::{SystemTime, UNIX_EPOCH};

    #[test]
    fn cli_rejects_wrong_argument_count() {
        let error = run(["only-input.txt"]).expect_err("one arg should fail");

        assert_eq!(error, USAGE);
    }

    #[test]
    fn cli_rejects_missing_input_file() {
        let dir = unique_temp_dir("missing");
        let input = dir.join("missing.txt");
        let output = dir.join("index.bin");

        let error = run([
            input.to_string_lossy().to_string(),
            output.to_string_lossy().to_string(),
        ])
        .expect_err("missing input should fail");

        assert!(error.contains("Input file not found"));
    }

    #[test]
    fn cli_builds_readable_lxdx_index() {
        let dir = unique_temp_dir("roundtrip");
        fs::create_dir_all(&dir).expect("temp dir should be created");
        let input = dir.join("keys.txt");
        let output = dir.join("index.bin");
        fs::write(&input, "apple\napplication\ntealight\n").expect("input should be written");

        let message = run([
            input.to_string_lossy().to_string(),
            output.to_string_lossy().to_string(),
        ])
        .expect("index should build");
        let index = LexIndex::open(&output).expect("generated index should open");

        assert!(message.contains("Built index"));
        assert_eq!(index.complete("app", 10), ["apple", "application"]);
        assert_eq!(index.match_pattern("tea*t", 10), ["tealight"]);

        fs::remove_dir_all(dir).ok();
    }

    fn unique_temp_dir(name: &str) -> std::path::PathBuf {
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("clock should be after epoch")
            .as_nanos();
        env::temp_dir().join(format!(
            "easydict-lex-index-cli-{name}-{}-{nanos}",
            std::process::id()
        ))
    }
}
