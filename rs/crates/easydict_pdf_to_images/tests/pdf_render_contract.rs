use easydict_pdf_render::{
    build_default_output_dir, format_page_summary, parse_pdf_to_images_args,
    resolve_selected_pages, PdfImageFormat,
};
use std::path::Path;

#[test]
fn parse_args_preserves_legacy_options() {
    let options = parse_pdf_to_images_args([
        "--input",
        "paper.pdf",
        "--output-dir",
        "pages",
        "--dpi",
        "216",
        "--scale",
        "3",
        "--format",
        "jpeg",
        "--page-range",
        "1-3,5",
        "--pdfium-dir",
        "pdfium",
    ])
    .expect("options");

    assert!(options.input_pdf.ends_with("paper.pdf"));
    assert!(options.output_dir.unwrap().ends_with("pages"));
    assert_eq!(options.dpi, 216.0);
    assert_eq!(options.scale, Some(3.0));
    assert_eq!(options.format, PdfImageFormat::Jpg);
    assert_eq!(options.page_selection.as_deref(), Some("1-3,5"));
    assert!(options.pdfium_dir.unwrap().ends_with("pdfium"));
}

#[test]
fn page_ranges_match_legacy_parser_behavior() {
    assert_eq!(resolve_selected_pages(None, 10).unwrap(), None);
    assert_eq!(resolve_selected_pages(Some("all"), 10).unwrap(), None);
    assert_eq!(
        resolve_selected_pages(Some("1-3,5,100"), 10).unwrap(),
        Some(vec![1, 2, 3, 5])
    );
    assert_eq!(
        resolve_selected_pages(Some("-2,0,3-12"), 5).unwrap(),
        Some(vec![3, 4, 5])
    );
    assert!(resolve_selected_pages(Some("99"), 5).is_err());
}

#[test]
fn default_output_dir_and_summary_match_legacy_shape() {
    assert_eq!(
        build_default_output_dir(Path::new("C:/docs/paper.pdf"))
            .to_string_lossy()
            .replace('\\', "/"),
        "C:/docs/paper_pages"
    );
    assert_eq!(format_page_summary(None, 7), "7 (all)");
    assert_eq!(
        format_page_summary(Some(&[1, 3, 4]), 7),
        "3 selected (1, 3, 4)"
    );
}
