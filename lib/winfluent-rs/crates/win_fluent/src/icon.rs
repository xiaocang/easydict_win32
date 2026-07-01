#[derive(Clone, Debug, Eq, PartialEq)]
pub struct IconToken {
    pub name: &'static str,
    pub glyph: Option<char>,
    pub image: Option<&'static [u8]>,
}

impl IconToken {
    pub const fn named(name: &'static str) -> Self {
        Self {
            name,
            glyph: None,
            image: None,
        }
    }

    pub const fn with_glyph(name: &'static str, glyph: char) -> Self {
        Self {
            name,
            glyph: Some(glyph),
            image: None,
        }
    }

    pub const fn with_image(name: &'static str, image: &'static [u8]) -> Self {
        Self {
            name,
            glyph: None,
            image: Some(image),
        }
    }

    pub fn resolved_glyph(&self) -> Option<char> {
        self.glyph.or_else(|| fluent_icon_glyph(self.name))
    }
}

pub const STANDARD_ICON_NAMES: &[&str] = &[
    "add",
    "app",
    "camera",
    "check",
    "clear",
    "copy",
    "delete",
    "edit",
    "help",
    "keyboard",
    "microphone",
    "more",
    "pin",
    "play",
    "refresh",
    "search",
    "settings",
    "speaker",
    "swap",
    "translate",
];

pub fn fluent_icon_glyph(name: &str) -> Option<char> {
    Some(match name {
        "add" => '\u{E710}',
        "app" => '\u{ECAA}',
        "camera" => '\u{E722}',
        "check" => '\u{E8FB}',
        "clear" => '\u{E711}',
        "copy" => '\u{E8C8}',
        "delete" => '\u{E74D}',
        "edit" => '\u{E70F}',
        "help" => '\u{E897}',
        "keyboard" => '\u{E765}',
        "microphone" => '\u{E720}',
        "more" => '\u{E712}',
        "pin" => '\u{E718}',
        "play" => '\u{E768}',
        "refresh" => '\u{E72C}',
        "search" => '\u{E721}',
        "settings" => '\u{E713}',
        "speaker" => '\u{E767}',
        "swap" => '\u{E8AB}',
        "translate" => '\u{E8C1}',
        _ => return None,
    })
}

pub const fn add() -> IconToken {
    IconToken::named("add")
}

pub const fn app() -> IconToken {
    IconToken::named("app")
}

pub const fn camera() -> IconToken {
    IconToken::named("camera")
}

pub const fn check() -> IconToken {
    IconToken::named("check")
}

pub const fn clear() -> IconToken {
    IconToken::named("clear")
}

pub const fn copy() -> IconToken {
    IconToken::named("copy")
}

pub const fn delete() -> IconToken {
    IconToken::named("delete")
}

pub const fn edit() -> IconToken {
    IconToken::named("edit")
}

pub const fn help() -> IconToken {
    IconToken::named("help")
}

pub const fn microphone() -> IconToken {
    IconToken::named("microphone")
}

pub const fn more() -> IconToken {
    IconToken::named("more")
}

pub const fn play() -> IconToken {
    IconToken::named("play")
}

pub const fn refresh() -> IconToken {
    IconToken::named("refresh")
}

pub const fn pin() -> IconToken {
    IconToken::named("pin")
}

pub const fn search() -> IconToken {
    IconToken::named("search")
}

pub const fn settings() -> IconToken {
    IconToken::named("settings")
}

pub const fn speaker() -> IconToken {
    IconToken::named("speaker")
}

pub const fn swap() -> IconToken {
    IconToken::named("swap")
}

pub const fn translate() -> IconToken {
    IconToken::named("translate")
}
