use std::fmt;

#[derive(Clone, Debug, PartialEq)]
pub struct WindowScreenshot {
    pub width_physical: u32,
    pub height_physical: u32,
    pub width_dips: f32,
    pub height_dips: f32,
    pub dpi: u32,
    pub scale_factor: f32,
    pub rgba: Vec<u8>,
}

impl WindowScreenshot {
    pub fn from_physical_rgba(
        width_physical: u32,
        height_physical: u32,
        scale_factor: f32,
        rgba: Vec<u8>,
    ) -> Result<Self, ScreenshotError> {
        let expected = expected_rgba_len(width_physical, height_physical)?;
        if rgba.len() != expected {
            return Err(ScreenshotError::BufferLengthMismatch {
                expected,
                actual: rgba.len(),
            });
        }

        let scale_factor = if scale_factor > f32::EPSILON {
            scale_factor
        } else {
            1.0
        };
        let dpi = (scale_factor * 96.0).round().max(1.0) as u32;

        Ok(Self {
            width_physical,
            height_physical,
            width_dips: width_physical as f32 / scale_factor,
            height_dips: height_physical as f32 / scale_factor,
            dpi,
            scale_factor,
            rgba,
        })
    }

    pub fn checksum(&self) -> u64 {
        self.rgba.iter().fold(0u64, |acc, value| {
            acc.wrapping_mul(16777619) ^ u64::from(*value)
        })
    }

    pub fn to_ppm_rgb(&self) -> Vec<u8> {
        let mut bytes = format!(
            "P6\n{} {}\n255\n",
            self.width_physical, self.height_physical
        )
        .into_bytes();
        bytes.reserve(self.rgba.len() / 4 * 3);

        for pixel in self.rgba.chunks_exact(4) {
            bytes.extend_from_slice(&pixel[..3]);
        }

        bytes
    }

    pub fn physical_size(&self) -> (u32, u32) {
        (self.width_physical, self.height_physical)
    }

    pub fn dip_size(&self) -> (f32, f32) {
        (self.width_dips, self.height_dips)
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum ScreenshotError {
    BufferLengthMismatch { expected: usize, actual: usize },
    DimensionsOverflow { width: u32, height: u32 },
}

impl fmt::Display for ScreenshotError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::BufferLengthMismatch { expected, actual } => {
                write!(
                    formatter,
                    "invalid screenshot RGBA buffer length: expected {expected}, got {actual}"
                )
            }
            Self::DimensionsOverflow { width, height } => {
                write!(
                    formatter,
                    "screenshot dimensions overflow RGBA buffer size: {width}x{height}"
                )
            }
        }
    }
}

impl std::error::Error for ScreenshotError {}

fn expected_rgba_len(width: u32, height: u32) -> Result<usize, ScreenshotError> {
    let bytes = u64::from(width)
        .checked_mul(u64::from(height))
        .and_then(|pixels| pixels.checked_mul(4))
        .ok_or(ScreenshotError::DimensionsOverflow { width, height })?;

    usize::try_from(bytes).map_err(|_| ScreenshotError::DimensionsOverflow { width, height })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn screenshot_frame_tracks_physical_and_dip_sizes() {
        let screenshot =
            WindowScreenshot::from_physical_rgba(200, 100, 2.0, vec![0; 200 * 100 * 4]).unwrap();

        assert_eq!(screenshot.dpi, 192);
        assert_eq!(screenshot.physical_size(), (200, 100));
        assert_eq!(screenshot.dip_size(), (100.0, 50.0));
    }

    #[test]
    fn screenshot_frame_rejects_invalid_buffers() {
        let error = WindowScreenshot::from_physical_rgba(2, 1, 1.0, vec![0; 3]).unwrap_err();

        assert_eq!(
            error,
            ScreenshotError::BufferLengthMismatch {
                expected: 8,
                actual: 3
            }
        );
    }

    #[test]
    fn screenshot_frame_emits_ppm_rgb_bytes() {
        let screenshot =
            WindowScreenshot::from_physical_rgba(1, 2, 1.0, vec![1, 2, 3, 255, 4, 5, 6, 128])
                .unwrap();

        let ppm = screenshot.to_ppm_rgb();

        assert!(ppm.starts_with(b"P6\n1 2\n255\n"));
        assert!(ppm.ends_with(&[1, 2, 3, 4, 5, 6]));
    }
}
