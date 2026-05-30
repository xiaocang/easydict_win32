#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Easing {
    Linear,
    FluentStandard,
    FluentEnter,
    FluentExit,
    FluentContent,
}

impl Easing {
    pub fn sample(self, progress: f32) -> f32 {
        let progress = progress.clamp(0.0, 1.0);

        match self {
            Self::Linear => progress,
            Self::FluentStandard => cubic_bezier_y_for_x(progress, 0.55, 0.55, 0.0, 1.0),
            Self::FluentEnter => cubic_bezier_y_for_x(progress, 0.0, 0.0, 0.0, 1.0),
            Self::FluentExit => cubic_bezier_y_for_x(progress, 1.0, 0.0, 1.0, 1.0),
            Self::FluentContent => cubic_bezier_y_for_x(progress, 0.35, 0.0, 0.15, 1.0),
        }
    }
}

pub const CONTROL_FASTER_ANIMATION_MS: u16 = 83;
pub const CONTROL_FAST_ANIMATION_MS: u16 = 167;
pub const CONTROL_NORMAL_ANIMATION_MS: u16 = 250;

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Transition {
    pub duration_ms: u16,
    pub easing: Easing,
}

impl Transition {
    pub const fn new(duration_ms: u16, easing: Easing) -> Self {
        Self {
            duration_ms,
            easing,
        }
    }

    pub const fn standard(duration_ms: u16) -> Self {
        Self::new(duration_ms, Easing::FluentStandard)
    }

    pub const fn fluent_enter(duration_ms: u16) -> Self {
        Self::new(duration_ms, Easing::FluentEnter)
    }

    pub const fn fluent_exit(duration_ms: u16) -> Self {
        Self::new(duration_ms, Easing::FluentExit)
    }

    pub const fn fluent_content(duration_ms: u16) -> Self {
        Self::new(duration_ms, Easing::FluentContent)
    }

    pub const fn instant() -> Self {
        Self::new(0, Easing::Linear)
    }

    pub fn progress_at(self, elapsed_ms: f32) -> f32 {
        if self.duration_ms == 0 {
            return 1.0;
        }

        self.easing.sample(elapsed_ms / f32::from(self.duration_ms))
    }

    pub fn value_at(self, elapsed_ms: f32, from: f32, to: f32) -> f32 {
        from + (to - from) * self.progress_at(elapsed_ms)
    }
}

fn cubic_bezier_y_for_x(x: f32, x1: f32, y1: f32, x2: f32, y2: f32) -> f32 {
    if x <= 0.0 {
        return 0.0;
    }

    if x >= 1.0 {
        return 1.0;
    }

    let mut t = x;
    for _ in 0..6 {
        let x_at_t = cubic_bezier(t, x1, x2);
        let derivative = cubic_bezier_derivative(t, x1, x2);

        if derivative.abs() < f32::EPSILON {
            break;
        }

        t = (t - (x_at_t - x) / derivative).clamp(0.0, 1.0);
    }

    cubic_bezier(t, y1, y2).clamp(0.0, 1.0)
}

fn cubic_bezier(t: f32, p1: f32, p2: f32) -> f32 {
    let one_minus_t = 1.0 - t;
    3.0 * one_minus_t * one_minus_t * t * p1 + 3.0 * one_minus_t * t * t * p2 + t * t * t
}

fn cubic_bezier_derivative(t: f32, p1: f32, p2: f32) -> f32 {
    let one_minus_t = 1.0 - t;
    3.0 * one_minus_t * one_minus_t * p1
        + 6.0 * one_minus_t * t * (p2 - p1)
        + 3.0 * t * t * (1.0 - p2)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn transition_samples_start_and_end() {
        let transition = Transition::standard(70);

        assert_eq!(transition.progress_at(0.0), 0.0);
        assert_eq!(transition.progress_at(70.0), 1.0);
    }

    #[test]
    fn transition_interpolates_values() {
        let transition = Transition::new(100, Easing::Linear);

        assert_eq!(transition.value_at(50.0, 4.0, 10.0), 7.0);
    }
}
