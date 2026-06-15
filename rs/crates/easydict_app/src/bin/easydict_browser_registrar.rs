use easydict_app::browser_registrar::{
    bridge_directory_for_root, parse_browser_registrar_args, serialize_cli_json, usage,
    BrowserRegistrarCommand, BrowserRegistrarCore, BrowserRegistrarParseError, BrowserRegistry,
    ErrorOutput, BRIDGE_EXE_NAME,
};
use std::env;
use std::fmt;
use std::io;
use std::path::PathBuf;
use std::process::ExitCode;

fn main() -> ExitCode {
    match run() {
        Ok(code) => code,
        Err(BrowserRegistrarCliError::Parse(BrowserRegistrarParseError::Help)) => {
            println!("{}", usage());
            ExitCode::SUCCESS
        }
        Err(BrowserRegistrarCliError::Parse(BrowserRegistrarParseError::MissingCommand))
        | Err(BrowserRegistrarCliError::Parse(BrowserRegistrarParseError::UnknownCommand(_))) => {
            println!("{}", usage());
            ExitCode::from(1)
        }
        Err(error) => {
            println!(
                "{}",
                serialize_cli_json(&ErrorOutput {
                    success: false,
                    error: error.to_string(),
                })
            );
            ExitCode::from(1)
        }
    }
}

fn run() -> Result<ExitCode, BrowserRegistrarCliError> {
    let options = parse_browser_registrar_args(env::args().skip(1))
        .map_err(BrowserRegistrarCliError::Parse)?;
    let bridge_directory = bridge_directory_for_root(local_app_data()?, &options.bridge_root_name);
    let mut core = BrowserRegistrarCore::new(bridge_directory, SystemBrowserRegistry);

    match options.command {
        BrowserRegistrarCommand::Install => {
            let source_bridge_path = options
                .bridge_path
                .unwrap_or_else(default_source_bridge_path);
            let output = core.install(
                options.chrome,
                options.firefox,
                &source_bridge_path,
                &options.chrome_ext_ids,
                &options.firefox_ext_id,
            );
            let success = output.success;
            println!("{}", serialize_cli_json(&output));
            Ok(if success {
                ExitCode::SUCCESS
            } else {
                ExitCode::from(1)
            })
        }
        BrowserRegistrarCommand::Uninstall => {
            let output = core.uninstall(options.chrome, options.firefox);
            let success = output.success;
            println!("{}", serialize_cli_json(&output));
            Ok(if success {
                ExitCode::SUCCESS
            } else {
                ExitCode::from(1)
            })
        }
        BrowserRegistrarCommand::Status => {
            let output = core.status();
            let success = output.error.is_none();
            println!("{}", serialize_cli_json(&output));
            Ok(if success {
                ExitCode::SUCCESS
            } else {
                ExitCode::from(1)
            })
        }
    }
}

struct SystemBrowserRegistry;

impl BrowserRegistry for SystemBrowserRegistry {
    fn write_default_value(&mut self, key_path: &str, value: &str) -> io::Result<()> {
        easydict_windows_registry::write_current_user_default_string(key_path, value)
            .map_err(platform_error)
    }

    fn delete_key(&mut self, key_path: &str) -> io::Result<()> {
        easydict_windows_registry::delete_current_user_key(key_path).map_err(platform_error)
    }

    fn read_default_value(&self, key_path: &str) -> io::Result<Option<String>> {
        easydict_windows_registry::read_current_user_default_string(key_path)
            .map_err(platform_error)
    }
}

#[derive(Debug)]
enum BrowserRegistrarCliError {
    Parse(BrowserRegistrarParseError),
    Io(io::Error),
}

impl fmt::Display for BrowserRegistrarCliError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Parse(error) => write!(formatter, "{error}"),
            Self::Io(error) => write!(formatter, "{error}"),
        }
    }
}

impl std::error::Error for BrowserRegistrarCliError {}

impl From<io::Error> for BrowserRegistrarCliError {
    fn from(error: io::Error) -> Self {
        Self::Io(error)
    }
}

fn local_app_data() -> Result<PathBuf, BrowserRegistrarCliError> {
    env::var_os("LOCALAPPDATA")
        .filter(|value| !value.is_empty())
        .map(PathBuf::from)
        .ok_or_else(|| {
            io::Error::new(
                io::ErrorKind::NotFound,
                "LOCALAPPDATA is not set; cannot resolve browser bridge directory",
            )
            .into()
        })
}

fn default_source_bridge_path() -> PathBuf {
    env::current_exe()
        .ok()
        .and_then(|path| path.parent().map(|parent| parent.join(BRIDGE_EXE_NAME)))
        .unwrap_or_else(|| PathBuf::from(BRIDGE_EXE_NAME))
}

fn platform_error(error: easydict_windows_registry::WindowsRegistryError) -> io::Error {
    io::Error::other(error.to_string())
}
