/// A half-open byte range into the source text.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct Span {
    pub start: usize,
    pub end: usize,
}

impl Span {
    pub fn new(start: usize, end: usize) -> Self {
        Self {
            start,
            end: end.max(start),
        }
    }

    pub fn empty(at: usize) -> Self {
        Self { start: at, end: at }
    }

    pub fn len(&self) -> usize {
        self.end - self.start
    }

    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }

    /// Extends this span's end without moving its start.
    pub fn extend_to(&mut self, end: usize) {
        if end > self.end {
            self.end = end;
        }
    }
}

/// Maps byte offsets to 1-based (line, column) pairs.
///
/// Columns are byte offsets within the line. That matches ASCII XAML exactly and may drift on
/// lines containing non-ASCII text, which v0 accepts.
#[derive(Debug, Clone)]
pub struct LineIndex {
    line_starts: Vec<usize>,
}

impl LineIndex {
    pub fn new(source: &str) -> Self {
        let mut line_starts = vec![0usize];
        for (offset, byte) in source.bytes().enumerate() {
            if byte == b'\n' {
                line_starts.push(offset + 1);
            }
        }
        Self { line_starts }
    }

    /// Returns the 1-based line and column containing `offset`.
    pub fn location(&self, offset: usize) -> (usize, usize) {
        match self.line_starts.binary_search(&offset) {
            Ok(line) => (line + 1, 1),
            Err(next) => {
                // `next` is never 0: line_starts always begins with 0, so any offset >= 0
                // either matches exactly (the Ok arm) or sorts after it.
                let line = next - 1;
                (line + 1, offset - self.line_starts[line] + 1)
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn locates_offsets_across_lines() {
        let index = LineIndex::new("ab\ncd\n\nef");
        assert_eq!(index.location(0), (1, 1));
        assert_eq!(index.location(1), (1, 2));
        assert_eq!(index.location(3), (2, 1));
        assert_eq!(index.location(4), (2, 2));
        assert_eq!(index.location(6), (3, 1));
        assert_eq!(index.location(7), (4, 1));
        assert_eq!(index.location(8), (4, 2));
    }

    #[test]
    fn span_extend_never_shrinks() {
        let mut span = Span::new(4, 10);
        span.extend_to(6);
        assert_eq!(span.end, 10);
        span.extend_to(20);
        assert_eq!(span.end, 20);
    }
}
