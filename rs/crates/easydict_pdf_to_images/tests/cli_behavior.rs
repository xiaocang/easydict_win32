use std::process::Command;

#[test]
fn cli_help_describes_rust_pdf_to_images_tool() {
    let output = Command::new(env!("CARGO_BIN_EXE_easydict_pdf_to_images"))
        .arg("--help")
        .output()
        .expect("run easydict_pdf_to_images --help");

    assert!(output.status.success());
    let stdout = String::from_utf8(output.stdout).expect("stdout should be utf8");
    assert!(stdout.contains("easydict_pdf_to_images --input <file.pdf>"));
    assert!(stdout.contains("--pdfium-dir"));
}
