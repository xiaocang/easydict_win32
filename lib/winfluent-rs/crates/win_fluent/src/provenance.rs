//! Source provenance captured by the optional `parity-diagnostics` feature.
//!
//! This module is deliberately allocation-free in default builds: it is only
//! compiled when the diagnostics feature is enabled.

use std::panic::Location;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct SourceLocation {
    pub file: &'static str,
    pub line: u32,
    pub column: u32,
}

impl SourceLocation {
    #[track_caller]
    pub fn caller() -> Self {
        let location = Location::caller();
        Self {
            file: location.file(),
            line: location.line(),
            column: location.column(),
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct PropertyProvenance {
    pub property: &'static str,
    pub source: SourceLocation,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ViewProvenance {
    pub constructor: SourceLocation,
    pub properties: Vec<PropertyProvenance>,
}

impl ViewProvenance {
    #[track_caller]
    pub fn caller() -> Self {
        Self {
            constructor: SourceLocation::caller(),
            properties: Vec::new(),
        }
    }

    pub fn unavailable() -> Self {
        Self {
            constructor: SourceLocation {
                file: "<unavailable>",
                line: 0,
                column: 0,
            },
            properties: Vec::new(),
        }
    }

    #[track_caller]
    pub fn set(&mut self, property: &'static str) {
        self.set_at(property, SourceLocation::caller());
    }

    #[track_caller]
    pub fn set_many(&mut self, properties: &[&'static str]) {
        let source = SourceLocation::caller();
        for property in properties {
            self.set_at(property, source);
        }
    }

    fn set_at(&mut self, property: &'static str, source: SourceLocation) {
        if let Some(existing) = self
            .properties
            .iter_mut()
            .find(|entry| entry.property == property)
        {
            existing.source = source;
        } else {
            self.properties
                .push(PropertyProvenance { property, source });
        }
    }

    pub fn source_for(&self, property: &str) -> Option<SourceLocation> {
        self.properties
            .iter()
            .find(|entry| entry.property == property)
            .map(|entry| entry.source)
            .or(Some(self.constructor))
    }
}
