use std::time::{Duration, Instant};

pub struct FrameCoalescer<T> {
    interval: Duration,
    pending: Vec<T>,
    last_flush: Option<Instant>,
}

impl<T> FrameCoalescer<T> {
    pub fn new(interval: Duration) -> Self {
        Self {
            interval,
            pending: Vec::new(),
            last_flush: None,
        }
    }

    pub fn for_refresh_rate(hz: u32) -> Self {
        let hz = hz.max(1);
        Self::new(Duration::from_micros(1_000_000 / hz as u64))
    }

    pub fn push(&mut self, value: T) {
        self.pending.push(value);
    }

    pub fn pending_len(&self) -> usize {
        self.pending.len()
    }

    pub fn is_empty(&self) -> bool {
        self.pending.is_empty()
    }

    pub fn should_flush(&self, now: Instant) -> bool {
        if self.pending.is_empty() {
            return false;
        }

        match self.last_flush {
            Some(last_flush) => now.duration_since(last_flush) >= self.interval,
            None => true,
        }
    }

    pub fn flush(&mut self, now: Instant) -> Option<Vec<T>> {
        if !self.should_flush(now) {
            return None;
        }

        self.last_flush = Some(now);
        Some(std::mem::take(&mut self.pending))
    }

    pub fn force_flush(&mut self, now: Instant) -> Vec<T> {
        self.last_flush = Some(now);
        std::mem::take(&mut self.pending)
    }
}

impl<T> Default for FrameCoalescer<T> {
    fn default() -> Self {
        Self::for_refresh_rate(60)
    }
}

pub struct TextStreamCoalescer {
    chunks: FrameCoalescer<String>,
}

impl TextStreamCoalescer {
    pub fn new(interval: Duration) -> Self {
        Self {
            chunks: FrameCoalescer::new(interval),
        }
    }

    pub fn push_chunk(&mut self, chunk: impl Into<String>) {
        self.chunks.push(chunk.into());
    }

    pub fn flush_text(&mut self, now: Instant) -> Option<String> {
        self.chunks.flush(now).map(|chunks| chunks.concat())
    }

    pub fn force_flush_text(&mut self, now: Instant) -> String {
        self.chunks.force_flush(now).concat()
    }
}

impl Default for TextStreamCoalescer {
    fn default() -> Self {
        Self {
            chunks: FrameCoalescer::for_refresh_rate(30),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn coalesces_text_chunks_until_interval() {
        let start = Instant::now();
        let mut coalescer = TextStreamCoalescer::new(Duration::from_millis(16));

        coalescer.push_chunk("a");
        assert_eq!(coalescer.flush_text(start), Some("a".to_string()));

        coalescer.push_chunk("b");
        assert_eq!(coalescer.flush_text(start + Duration::from_millis(1)), None);
        assert_eq!(
            coalescer.flush_text(start + Duration::from_millis(17)),
            Some("b".to_string())
        );
    }
}
