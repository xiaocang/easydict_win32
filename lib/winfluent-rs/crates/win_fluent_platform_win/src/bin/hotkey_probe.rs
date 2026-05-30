use std::time::Duration;

use win_fluent::platform::{Hotkey, HotkeyKey, HotkeyModifier};
use win_fluent_platform_win::WindowsPlatformAdapter;

fn main() -> Result<(), String> {
    let hotkey = Hotkey::new("hotkey-probe", HotkeyKey::Function(24))
        .modifier(HotkeyModifier::Control)
        .modifier(HotkeyModifier::Alt)
        .modifier(HotkeyModifier::Shift);

    let handle = WindowsPlatformAdapter::register_global_hotkey(&hotkey)
        .map_err(|error| format!("HOTKEY_REGISTER_FAILED error={error:?}"))?;
    println!(
        "HOTKEY_REGISTERED id={} native_id={} modifiers=0x{:x} vk=0x{:x}",
        handle.hotkey().id,
        handle.hotkey().native_id,
        handle.hotkey().modifiers,
        handle.hotkey().virtual_key
    );

    let sender_hotkey = hotkey.clone();
    let sender = std::thread::spawn(move || {
        std::thread::sleep(Duration::from_millis(150));
        WindowsPlatformAdapter::send_hotkey_input_for_probe(&sender_hotkey)
    });

    let event = WindowsPlatformAdapter::wait_for_hotkey_event(&[&handle], Duration::from_secs(3))
        .map_err(|error| format!("HOTKEY_WAIT_FAILED error={error:?}"))?
        .ok_or_else(|| "HOTKEY_TIMEOUT".to_string())?;

    let sender_result = sender
        .join()
        .map_err(|_| "HOTKEY_SEND_THREAD_PANICKED".to_string())?;
    sender_result.map_err(|error| format!("HOTKEY_SEND_FAILED error={error:?}"))?;

    println!(
        "HOTKEY_RECEIVED id={} native_id={} modifiers=0x{:x} vk=0x{:x}",
        event.id, event.native_id, event.modifiers, event.virtual_key
    );

    Ok(())
}
