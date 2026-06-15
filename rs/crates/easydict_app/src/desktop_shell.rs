use win_fluent::Task;

use crate::Message;

pub fn open_url_task(url: &'static str) -> Task<Message> {
    Task::perform(
        async move {
            let _ = easydict_windows_shell::open_url(url);
        },
        |_| Message::Noop,
    )
}

pub fn run_bundled_executable_task(
    executable_name: &'static str,
    arguments: Vec<String>,
) -> Task<Message> {
    Task::perform(
        async move {
            let _ = easydict_windows_shell::run_bundled_executable(executable_name, &arguments);
        },
        |_| Message::Noop,
    )
}
