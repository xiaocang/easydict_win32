use easydict_runtime_guards::command_target_is_retained_runtime_or_script_marker;
use std::{fs, path::Path};

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DesktopShellVerb {
    pub id: String,
    pub label: String,
    pub accepts_files: bool,
    pub accepts_directory_background: bool,
    pub arguments: Vec<String>,
}

impl DesktopShellVerb {
    pub fn new(id: impl Into<String>, label: impl Into<String>) -> Self {
        Self {
            id: id.into(),
            label: label.into(),
            accepts_files: true,
            accepts_directory_background: false,
            arguments: Vec::new(),
        }
    }

    pub fn directory_background(mut self, enabled: bool) -> Self {
        self.accepts_directory_background = enabled;
        self
    }

    pub fn argument(mut self, argument: impl Into<String>) -> Self {
        self.arguments.push(argument.into());
        self
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DesktopProtocolRegistration {
    pub scheme: String,
    pub description: String,
    pub arguments: Vec<String>,
}

impl DesktopProtocolRegistration {
    pub fn new(scheme: impl Into<String>, description: impl Into<String>) -> Self {
        Self {
            scheme: scheme.into(),
            description: description.into(),
            arguments: Vec::new(),
        }
    }

    pub fn argument(mut self, argument: impl Into<String>) -> Self {
        self.arguments.push(argument.into());
        self
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DesktopShellVerbPlan {
    pub id: String,
    pub label: String,
    pub registry_key_paths: Vec<String>,
    pub command_key_paths: Vec<String>,
    pub command_arguments: Vec<String>,
}

impl DesktopShellVerbPlan {
    pub fn command_line(&self, executable_path: &str) -> String {
        windows_command_line(executable_path, &self.command_arguments, false)
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DesktopProtocolRegistrationPlan {
    pub scheme: String,
    pub description: String,
    pub registry_key_path: String,
    pub command_key_path: String,
    pub command_arguments: Vec<String>,
}

impl DesktopProtocolRegistrationPlan {
    pub fn command_line(&self, executable_path: &str) -> String {
        windows_command_line(executable_path, &self.command_arguments, true)
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DesktopStartupRegistrationPlan {
    pub registry_key_path: String,
    pub value_name: String,
    pub command_arguments: Vec<String>,
}

impl DesktopStartupRegistrationPlan {
    pub fn command_line(&self, executable_path: &str) -> String {
        windows_command_line(executable_path, &self.command_arguments, false)
    }
}

pub fn shell_verb_plan(verb: &DesktopShellVerb) -> Option<DesktopShellVerbPlan> {
    let registry_key_paths = shell_verb_registry_key_paths(verb);
    if registry_key_paths.is_empty() {
        return None;
    }

    let command_key_paths = registry_key_paths
        .iter()
        .map(|path| format!(r"{path}\command"))
        .collect();

    Some(DesktopShellVerbPlan {
        id: verb.id.clone(),
        label: verb.label.clone(),
        registry_key_paths,
        command_key_paths,
        command_arguments: verb.arguments.clone(),
    })
}

pub fn protocol_registration_plan(
    protocol: &DesktopProtocolRegistration,
) -> DesktopProtocolRegistrationPlan {
    let registry_key_path = format!(r"Software\Classes\{}", protocol.scheme);
    let command_key_path = format!(r"{registry_key_path}\shell\open\command");

    DesktopProtocolRegistrationPlan {
        scheme: protocol.scheme.clone(),
        description: protocol.description.clone(),
        registry_key_path,
        command_key_path,
        command_arguments: protocol.arguments.clone(),
    }
}

pub fn startup_registration_plan() -> DesktopStartupRegistrationPlan {
    DesktopStartupRegistrationPlan {
        registry_key_path: r"Software\Microsoft\Windows\CurrentVersion\Run".to_string(),
        value_name: "EasydictRs".to_string(),
        command_arguments: Vec::new(),
    }
}

pub fn register_shell_verb(verb: DesktopShellVerb) -> Result<(), String> {
    let Some(plan) = shell_verb_plan(&verb) else {
        return Ok(());
    };
    let executable_path = current_executable_path_string()?;
    register_shell_verb_with_executable_path(&plan, &executable_path)
}

pub fn unregister_shell_verb(verb: DesktopShellVerb) -> Result<(), String> {
    let Some(plan) = shell_verb_plan(&verb) else {
        return Ok(());
    };
    unregister_shell_verb_plan(&plan)
}

pub fn register_protocol(protocol: DesktopProtocolRegistration) -> Result<(), String> {
    let plan = protocol_registration_plan(&protocol);
    let executable_path = current_executable_path_string()?;
    register_protocol_with_executable_path(&plan, &executable_path)
}

pub fn unregister_protocol(protocol: DesktopProtocolRegistration) -> Result<(), String> {
    let plan = protocol_registration_plan(&protocol);
    unregister_protocol_plan(&plan)
}

pub fn set_startup_enabled(enabled: bool) -> Result<(), String> {
    let plan = startup_registration_plan();
    if enabled {
        let executable_path = current_executable_path_string()?;
        register_startup_with_executable_path(&plan, &executable_path)
    } else {
        unregister_startup_plan(&plan)
    }
}

pub fn register_shell_verb_with_executable_path(
    plan: &DesktopShellVerbPlan,
    executable_path: &str,
) -> Result<(), String> {
    validate_desktop_command_executable_path(executable_path)?;

    for (registry_key_path, command_key_path) in plan
        .registry_key_paths
        .iter()
        .zip(plan.command_key_paths.iter())
    {
        write_registry_string(registry_key_path, None, &plan.label)?;
        write_registry_string(registry_key_path, Some("Icon"), executable_path)?;
        write_registry_string(command_key_path, None, &plan.command_line(executable_path))?;
    }

    Ok(())
}

pub fn unregister_shell_verb_plan(plan: &DesktopShellVerbPlan) -> Result<(), String> {
    for registry_key_path in &plan.registry_key_paths {
        delete_registry_tree(registry_key_path)?;
    }

    Ok(())
}

pub fn register_protocol_with_executable_path(
    plan: &DesktopProtocolRegistrationPlan,
    executable_path: &str,
) -> Result<(), String> {
    validate_desktop_command_executable_path(executable_path)?;

    write_registry_string(&plan.registry_key_path, None, &plan.description)?;
    write_registry_string(&plan.registry_key_path, Some("URL Protocol"), "")?;
    write_registry_string(
        &plan.command_key_path,
        None,
        &plan.command_line(executable_path),
    )?;

    Ok(())
}

pub fn unregister_protocol_plan(plan: &DesktopProtocolRegistrationPlan) -> Result<(), String> {
    delete_registry_tree(&plan.registry_key_path)
}

pub fn register_startup_with_executable_path(
    plan: &DesktopStartupRegistrationPlan,
    executable_path: &str,
) -> Result<(), String> {
    validate_desktop_command_executable_path(executable_path)?;

    write_registry_string(
        &plan.registry_key_path,
        Some(&plan.value_name),
        &plan.command_line(executable_path),
    )
}

pub fn unregister_startup_plan(plan: &DesktopStartupRegistrationPlan) -> Result<(), String> {
    delete_registry_value(&plan.registry_key_path, Some(&plan.value_name))
}

fn shell_verb_registry_key_paths(verb: &DesktopShellVerb) -> Vec<String> {
    let mut paths = Vec::new();

    if verb.accepts_files {
        paths.push(format!(r"Software\Classes\*\shell\{}", verb.id));
    }

    if verb.accepts_directory_background {
        paths.push(format!(
            r"Software\Classes\Directory\Background\shell\{}",
            verb.id
        ));
    }

    paths
}

fn current_executable_path_string() -> Result<String, String> {
    std::env::current_exe()
        .map_err(|error| format!("failed to resolve current executable path: {error}"))
        .map(|path| path.to_string_lossy().into_owned())
}

fn validate_desktop_command_executable_path(executable_path: &str) -> Result<(), String> {
    if command_target_is_retained_runtime_or_script_marker(executable_path) {
        return Err(format!(
            "desktop integration command target is a retained runtime, worker, or script entry: {executable_path}"
        ));
    }

    let executable = Path::new(executable_path);
    let metadata = fs::symlink_metadata(executable).map_err(|error| {
        format!("failed to inspect desktop integration command target {executable_path}: {error}")
    })?;
    let file_type = metadata.file_type();
    if desktop_command_target_is_unsupported_by_flags(
        file_type.is_file(),
        file_type.is_symlink(),
        desktop_command_target_is_reparse_point(&metadata),
    ) {
        return Err(format!(
            "desktop integration command target must be a regular non-link executable file: {executable_path}"
        ));
    }

    let bytes = fs::read(executable).map_err(|error| {
        format!("failed to read desktop integration command target {executable_path}: {error}")
    })?;
    if easydict_runtime_guards::bytes_contain_retained_runtime_marker(&bytes) {
        return Err(format!(
            "desktop integration command target contains retained runtime marker: {executable_path}"
        ));
    }

    Ok(())
}

fn desktop_command_target_is_unsupported_by_flags(
    is_file: bool,
    is_symlink: bool,
    is_reparse_point: bool,
) -> bool {
    !is_file || is_symlink || is_reparse_point
}

#[cfg(windows)]
fn desktop_command_target_is_reparse_point(metadata: &fs::Metadata) -> bool {
    use std::os::windows::fs::MetadataExt;
    const FILE_ATTRIBUTE_REPARSE_POINT: u32 = 0x0000_0400;
    metadata.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0
}

#[cfg(not(windows))]
fn desktop_command_target_is_reparse_point(_metadata: &fs::Metadata) -> bool {
    false
}

fn write_registry_string(
    key_path: &str,
    value_name: Option<&str>,
    value: &str,
) -> Result<(), String> {
    easydict_windows_registry::write_current_user_string_value(key_path, value_name, value)
        .map_err(|error| error.to_string())
}

fn delete_registry_tree(key_path: &str) -> Result<(), String> {
    easydict_windows_registry::delete_current_user_tree(key_path).map_err(|error| error.to_string())
}

fn delete_registry_value(key_path: &str, value_name: Option<&str>) -> Result<(), String> {
    easydict_windows_registry::delete_current_user_value(key_path, value_name)
        .map_err(|error| error.to_string())
}

fn windows_command_line(
    executable_path: &str,
    arguments: &[String],
    quote_all_arguments: bool,
) -> String {
    let mut parts = vec![quote_windows_argument(executable_path, true)];
    parts.extend(
        arguments
            .iter()
            .map(|argument| quote_windows_argument(argument, quote_all_arguments)),
    );
    parts.join(" ")
}

fn quote_windows_argument(value: &str, force: bool) -> String {
    let needs_quotes = force
        || value.is_empty()
        || value
            .chars()
            .any(|character| character.is_whitespace() || character == '"');
    if !needs_quotes {
        return value.to_string();
    }

    let escaped = value.replace('"', r#"\""#);
    format!(r#""{escaped}""#)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn shell_verb_plan_matches_ocr_registry_contract() {
        let verb = DesktopShellVerb::new("EasydictRsOCR", "OCR Translate")
            .directory_background(true)
            .argument("--ocr-translate");

        let plan = shell_verb_plan(&verb).expect("shell verb should produce registry plan");

        assert_eq!(
            plan.registry_key_paths,
            [
                r"Software\Classes\*\shell\EasydictRsOCR".to_string(),
                r"Software\Classes\Directory\Background\shell\EasydictRsOCR".to_string(),
            ]
        );
        assert_eq!(
            plan.command_key_paths,
            [
                r"Software\Classes\*\shell\EasydictRsOCR\command".to_string(),
                r"Software\Classes\Directory\Background\shell\EasydictRsOCR\command".to_string(),
            ]
        );
        assert_eq!(
            plan.command_line(r"C:\Program Files\Easydict\Easydict.Rust.exe"),
            r#""C:\Program Files\Easydict\Easydict.Rust.exe" --ocr-translate"#
        );
    }

    #[test]
    fn protocol_registration_plan_quotes_uri_argument() {
        let protocol =
            DesktopProtocolRegistration::new("easydict-rs", "URL:Easydict Rust Protocol")
                .argument("%1");

        let plan = protocol_registration_plan(&protocol);

        assert_eq!(plan.registry_key_path, r"Software\Classes\easydict-rs");
        assert_eq!(
            plan.command_key_path,
            r"Software\Classes\easydict-rs\shell\open\command"
        );
        assert_eq!(
            plan.command_line(r"C:\Program Files\Easydict\Easydict.Rust.exe"),
            r#""C:\Program Files\Easydict\Easydict.Rust.exe" "%1""#
        );
    }

    #[test]
    fn startup_registration_plan_uses_rs_specific_run_value() {
        let plan = startup_registration_plan();

        assert_eq!(
            plan.registry_key_path,
            r"Software\Microsoft\Windows\CurrentVersion\Run"
        );
        assert_eq!(plan.value_name, "EasydictRs");
        assert_eq!(
            plan.command_line(r"C:\Program Files\Easydict\Easydict.exe"),
            r#""C:\Program Files\Easydict\Easydict.exe""#
        );
    }

    #[test]
    fn shell_verb_without_targets_is_noop() {
        let verb = DesktopShellVerb {
            id: "noop".to_string(),
            label: "Noop".to_string(),
            accepts_files: false,
            accepts_directory_background: false,
            arguments: Vec::new(),
        };

        assert_eq!(shell_verb_plan(&verb), None);
    }

    #[test]
    fn desktop_registry_commands_reject_retained_runtime_or_script_targets() {
        let shell_plan = shell_verb_plan(
            &DesktopShellVerb::new("EasydictRsOCR", "OCR Translate").argument("--ocr-translate"),
        )
        .expect("shell verb should produce registry plan");
        let shell_error =
            register_shell_verb_with_executable_path(&shell_plan, r"C:\Payload\dotnet\dotnet.exe")
                .expect_err("shell verb registration should reject retained runtime targets");
        assert!(shell_error.contains("retained runtime"));

        let protocol_plan = protocol_registration_plan(
            &DesktopProtocolRegistration::new("easydict-rs", "URL:Easydict Rust Protocol")
                .argument("%1"),
        );
        let protocol_error = register_protocol_with_executable_path(
            &protocol_plan,
            r"C:\Payload\tools\Register-Easydict.ps1",
        )
        .expect_err("protocol registration should reject script targets");
        assert!(protocol_error.contains("script entry"));

        let startup_plan = startup_registration_plan();
        let startup_error = register_startup_with_executable_path(
            &startup_plan,
            r"C:\Payload\workers\localai\Easydict.Workers.LocalAi.exe",
        )
        .expect_err("startup registration should reject retained worker targets");
        assert!(startup_error.contains("retained runtime"));
    }

    #[test]
    fn desktop_registry_commands_reject_retained_runtime_content_targets() {
        let temp_dir = unique_temp_dir("desktop-registry-command-content");
        fs::create_dir_all(&temp_dir).expect("create temp dir");
        let executable = temp_dir.join("Easydict.Rust.exe");
        fs::write(
            &executable,
            b"MZ fake apphost payload with hostfxr.dll retained runtime marker",
        )
        .expect("write fake executable");

        let error = validate_desktop_command_executable_path(&executable.to_string_lossy())
            .expect_err("desktop command validation should reject retained runtime bytes");
        assert!(error.contains("contains retained runtime marker"));

        let _ = fs::remove_dir_all(temp_dir);
    }

    fn unique_temp_dir(label: &str) -> std::path::PathBuf {
        let nanos = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .expect("system clock should be after unix epoch")
            .as_nanos();
        std::env::temp_dir().join(format!("easydict-{label}-{}-{nanos}", std::process::id()))
    }
}
