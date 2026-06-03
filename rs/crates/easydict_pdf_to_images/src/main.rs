use easydict_pdf_render::{parse_pdf_to_images_args, render_pdf_to_images};

fn main() {
    std::process::exit(run(std::env::args().skip(1).collect()));
}

fn run(args: Vec<String>) -> i32 {
    if args.is_empty() || args.iter().any(|arg| arg == "-h" || arg == "--help") {
        print_usage();
        return 0;
    }

    let options = match parse_pdf_to_images_args(args) {
        Ok(options) => options,
        Err(error) => {
            eprintln!("{error}");
            return 1;
        }
    };

    match render_pdf_to_images(&options) {
        Ok(summary) => {
            println!("Input PDF : {}", summary.input_pdf.display());
            println!("Output dir: {}", summary.output_dir.display());
            println!("Pages     : {}", summary.page_summary);
            println!("Format    : {}", summary.format);
            println!(
                "Scale     : {:.2} ({:.0} DPI)",
                summary.scale, summary.effective_dpi
            );
            println!();
            for (index, page) in summary.rendered_pages.iter().enumerate() {
                println!(
                    "[{}/{}] {}",
                    index + 1,
                    summary.rendered_pages.len(),
                    page.output_path.display()
                );
            }
            println!();
            println!("Done.");
            0
        }
        Err(error) => {
            eprintln!("{error}");
            1
        }
    }
}

fn print_usage() {
    println!("Usage:");
    println!(
        "  easydict_pdf_to_images --input <file.pdf> [--output-dir <dir>] [--dpi 144] [--format png]"
    );
    println!();
    println!("Options:");
    println!("  -i, --input        Input PDF path. Positional input path is also supported.");
    println!("  -o, --output-dir   Output directory. Default: <pdf-name>_pages");
    println!("      --dpi          Target DPI. Default: 144");
    println!("      --scale        Render scale. Overrides DPI if provided.");
    println!("  -f, --format       png or jpg. Default: png");
    println!("      --page         Single page to export, e.g. 2.");
    println!("      --page-range   Page range to export, e.g. 1-3,5.");
    println!("      --pdfium-dir   Directory containing pdfium.dll. Defaults to EASYDICT_PDFIUM_DIR, executable directory, then system lookup.");
}
