//! A small state machine for asynchronously-loaded values.
//!
//! `Loadable<T, E>` is the framework's abstraction for the "kick off async work
//! → show a loading state → settle on a value or error" pattern. Pair it with a
//! [`Task`](crate::task::Task) that produces the result message, drive a loading
//! overlay from [`is_loading`](Loadable::is_loading), and read the settled value
//! from [`value`](Loadable::value).
//!
//! ```
//! use win_fluent::loadable::Loadable;
//!
//! let mut status: Loadable<u32> = Loadable::default();
//! assert!(status.is_idle());
//!
//! status.begin(); // about to await an async Task
//! assert!(status.is_loading());
//!
//! status.resolve(Ok(42)); // message with the result arrived
//! assert_eq!(status.value(), Some(&42));
//! ```

/// The lifecycle of an asynchronously-loaded value.
///
/// The error type defaults to `String` for the common case of human-readable
/// failure messages.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum Loadable<T, E = String> {
    /// No load has been requested yet.
    Idle,
    /// A load is in flight.
    Loading,
    /// A load completed successfully.
    Loaded(T),
    /// A load failed.
    Failed(E),
}

impl<T, E> Default for Loadable<T, E> {
    fn default() -> Self {
        Self::Idle
    }
}

impl<T, E> Loadable<T, E> {
    /// Constructs a settled, loaded value.
    pub fn loaded(value: T) -> Self {
        Self::Loaded(value)
    }

    /// Constructs a settled failure.
    pub fn failed(error: E) -> Self {
        Self::Failed(error)
    }

    pub fn is_idle(&self) -> bool {
        matches!(self, Self::Idle)
    }

    pub fn is_loading(&self) -> bool {
        matches!(self, Self::Loading)
    }

    pub fn is_loaded(&self) -> bool {
        matches!(self, Self::Loaded(_))
    }

    pub fn is_failed(&self) -> bool {
        matches!(self, Self::Failed(_))
    }

    /// The loaded value, if settled successfully.
    pub fn value(&self) -> Option<&T> {
        match self {
            Self::Loaded(value) => Some(value),
            _ => None,
        }
    }

    /// The failure, if settled unsuccessfully.
    pub fn error(&self) -> Option<&E> {
        match self {
            Self::Failed(error) => Some(error),
            _ => None,
        }
    }

    /// Transitions into the [`Loading`](Self::Loading) state, to be called when
    /// the async [`Task`](crate::task::Task) is dispatched.
    pub fn begin(&mut self) {
        *self = Self::Loading;
    }

    /// Settles the load from a `Result`, becoming [`Loaded`](Self::Loaded) or
    /// [`Failed`](Self::Failed). Call this from the result message handler.
    pub fn resolve(&mut self, result: Result<T, E>) {
        *self = match result {
            Ok(value) => Self::Loaded(value),
            Err(error) => Self::Failed(error),
        };
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn defaults_to_idle() {
        let status: Loadable<u32> = Loadable::default();
        assert!(status.is_idle());
        assert_eq!(status.value(), None);
    }

    #[test]
    fn begin_then_resolve_ok_transitions_to_loaded() {
        let mut status: Loadable<u32> = Loadable::default();
        status.begin();
        assert!(status.is_loading());
        status.resolve(Ok(7));
        assert!(status.is_loaded());
        assert_eq!(status.value(), Some(&7));
    }

    #[test]
    fn resolve_err_transitions_to_failed() {
        let mut status: Loadable<u32> = Loadable::Loading;
        status.resolve(Err("boom".to_string()));
        assert!(status.is_failed());
        assert_eq!(status.error().map(String::as_str), Some("boom"));
    }
}
