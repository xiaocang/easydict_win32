#[derive(Clone, Debug, Eq, PartialEq)]
pub struct IconToken {
    pub name: &'static str,
    pub glyph: Option<char>,
}

impl IconToken {
    pub const fn named(name: &'static str) -> Self {
        Self { name, glyph: None }
    }

    pub const fn with_glyph(name: &'static str, glyph: char) -> Self {
        Self {
            name,
            glyph: Some(glyph),
        }
    }
}

pub const fn add() -> IconToken {
    IconToken::named("add")
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

pub const fn microphone() -> IconToken {
    IconToken::named("microphone")
}

pub const fn more() -> IconToken {
    IconToken::named("more")
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
