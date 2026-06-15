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

pub fn register_shell_verb_with_executable_path(
    plan: &DesktopShellVerbPlan,
    executable_path: &str,
) -> Result<(), String> {
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
        let verb = DesktopShellVerb::new("EasydictOCR", "OCR Translate")
            .directory_background(true)
            .argument("--ocr-translate");

        let plan = shell_verb_plan(&verb).expect("shell verb should produce registry plan");

        assert_eq!(
            plan.registry_key_paths,
            [
                r"Software\Classes\*\shell\EasydictOCR".to_string(),
                r"Software\Classes\Directory\Background\shell\EasydictOCR".to_string(),
            ]
        );
        assert_eq!(
            plan.command_key_paths,
            [
                r"Software\Classes\*\shell\EasydictOCR\command".to_string(),
                r"Software\Classes\Directory\Background\shell\EasydictOCR\command".to_string(),
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
            DesktopProtocolRegistration::new("easydict", "URL:Easydict Protocol").argument("%1");

        let plan = protocol_registration_plan(&protocol);

        assert_eq!(plan.registry_key_path, r"Software\Classes\easydict");
        assert_eq!(
            plan.command_key_path,
            r"Software\Classes\easydict\shell\open\command"
        );
        assert_eq!(
            plan.command_line(r"C:\Program Files\Easydict\Easydict.Rust.exe"),
            r#""C:\Program Files\Easydict\Easydict.Rust.exe" "%1""#
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
}
