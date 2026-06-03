//! LZO1X decompression implementation
//!
//! This module provides LZO1X-1 decompression functionality used in MDX/MDD files.
//! The implementation follows the LZO1X algorithm specification.

use crate::error::{MdictError, Result};

/// LZO1X decompressor
pub struct Lzo1xDecompressor {
    input: Vec<u8>,
    output: Vec<u8>,
    ip: usize, // input pointer
    op: usize, // output pointer
}

impl Lzo1xDecompressor {
    /// Create a new decompressor
    pub fn new() -> Self {
        Lzo1xDecompressor {
            input: Vec::new(),
            output: Vec::new(),
            ip: 0,
            op: 0,
        }
    }

    /// Decompress LZO1X compressed data
    pub fn decompress(&mut self, input: &[u8], output_size: usize) -> Result<Vec<u8>> {
        self.input = input.to_vec();
        self.output = vec![0u8; output_size];
        self.ip = 0;
        self.op = 0;

        self.decompress_internal()?;

        self.output.truncate(self.op);
        Ok(std::mem::take(&mut self.output))
    }

    fn decompress_internal(&mut self) -> Result<()> {
        if self.input.is_empty() {
            return Ok(());
        }

        let mut t = self.input[self.ip] as usize;
        self.ip += 1;

        // Handle first literal run
        if t > 17 {
            t -= 17;
            self.copy_literal(t)?;
            t = self.input[self.ip] as usize;
            self.ip += 1;
            if t < 16 {
                return Err(MdictError::DecompressionError(
                    "Invalid LZO data".to_string(),
                ));
            }
        }

        loop {
            if t >= 64 {
                // Match with 2-byte offset
                self.match_copy_2(t)?;
            } else if t >= 32 {
                // Match with variable length
                self.match_copy_var(t)?;
            } else if t >= 16 {
                // Match with 2-byte offset (long)
                self.match_copy_long(t)?;
            } else {
                // Literal run
                self.literal_run(t)?;
            }

            if self.ip >= self.input.len() {
                break;
            }

            t = self.input[self.ip] as usize;
            self.ip += 1;
        }

        Ok(())
    }

    fn copy_literal(&mut self, mut len: usize) -> Result<()> {
        if self.ip + len > self.input.len() || self.op + len > self.output.len() {
            return Err(MdictError::DecompressionError(
                "Buffer overflow in literal copy".to_string(),
            ));
        }

        while len > 0 {
            self.output[self.op] = self.input[self.ip];
            self.op += 1;
            self.ip += 1;
            len -= 1;
        }

        Ok(())
    }

    fn match_copy(&mut self, mut len: usize, offset: usize) -> Result<()> {
        if offset > self.op || self.op + len > self.output.len() {
            return Err(MdictError::DecompressionError(
                "Invalid match offset".to_string(),
            ));
        }

        let mut src = self.op - offset;
        while len > 0 {
            self.output[self.op] = self.output[src];
            self.op += 1;
            src += 1;
            len -= 1;
        }

        Ok(())
    }

    fn match_copy_2(&mut self, t: usize) -> Result<()> {
        // 2-byte match with short offset
        let len = (t >> 5) + 1;
        let offset = ((t & 0x1f) << 3) + (self.input[self.ip] as usize >> 5) + 1;
        self.ip += 1;

        self.match_copy(len + 2, offset)?;

        // Handle trailing literal
        let next = self.input[self.ip - 1] & 0x1f;
        if next > 0 {
            self.copy_literal(next as usize)?;
        }

        Ok(())
    }

    fn match_copy_var(&mut self, t: usize) -> Result<()> {
        // Variable length match
        let mut len = t & 0x1f;

        if len == 0 {
            // Extended length
            while self.ip < self.input.len() && self.input[self.ip] == 0 {
                len += 255;
                self.ip += 1;
            }
            if self.ip >= self.input.len() {
                return Err(MdictError::DecompressionError(
                    "Unexpected end of input".to_string(),
                ));
            }
            len += 31 + self.input[self.ip] as usize;
            self.ip += 1;
        }

        if self.ip + 1 >= self.input.len() {
            return Err(MdictError::DecompressionError(
                "Unexpected end of input".to_string(),
            ));
        }

        let offset = (self.input[self.ip] as usize) + ((self.input[self.ip + 1] as usize) << 8) + 1;
        self.ip += 2;

        self.match_copy(len + 2, offset)?;

        Ok(())
    }

    fn match_copy_long(&mut self, t: usize) -> Result<()> {
        // Long match with 2-byte offset
        let mut len = t & 0x07;

        if len == 0 {
            // Extended length
            while self.ip < self.input.len() && self.input[self.ip] == 0 {
                len += 255;
                self.ip += 1;
            }
            if self.ip >= self.input.len() {
                return Err(MdictError::DecompressionError(
                    "Unexpected end of input".to_string(),
                ));
            }
            len += 7 + self.input[self.ip] as usize;
            self.ip += 1;
        }

        if self.ip + 1 >= self.input.len() {
            return Err(MdictError::DecompressionError(
                "Unexpected end of input".to_string(),
            ));
        }

        let offset = (self.input[self.ip] as usize) + ((self.input[self.ip + 1] as usize) << 8);
        self.ip += 2;

        if t >= 24 {
            // 16K offset
            let offset = offset + 0x4000;
            self.match_copy(len + 2, offset)?;
        } else {
            self.match_copy(len + 2, offset)?;
        }

        Ok(())
    }

    fn literal_run(&mut self, t: usize) -> Result<()> {
        let mut len = t;

        if len == 0 {
            // Extended length
            while self.ip < self.input.len() && self.input[self.ip] == 0 {
                len += 255;
                self.ip += 1;
            }
            if self.ip >= self.input.len() {
                return Err(MdictError::DecompressionError(
                    "Unexpected end of input".to_string(),
                ));
            }
            len += 15 + self.input[self.ip] as usize;
            self.ip += 1;
        }

        self.copy_literal(len + 3)?;

        Ok(())
    }
}

/// Decompress LZO1X data
///
/// This function uses the minilzo-rs crate for reliable decompression.
/// Falls back to our implementation if the crate fails.
pub fn decompress(input: &[u8], output_size: usize) -> Result<Vec<u8>> {
    // Try using minilzo-rs first
    match minilzo_rs::LZO::init() {
        Ok(lzo) => {
            match lzo.decompress_safe(input, output_size) {
                Ok(data) => Ok(data),
                Err(_) => {
                    // Fall back to our implementation
                    let mut decompressor = Lzo1xDecompressor::new();
                    decompressor.decompress(input, output_size)
                }
            }
        }
        Err(_) => {
            // Fall back to our implementation
            let mut decompressor = Lzo1xDecompressor::new();
            decompressor.decompress(input, output_size)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_decompress_empty() {
        let result = decompress(&[], 0);
        assert!(result.is_ok());
        assert!(result.unwrap().is_empty());
    }
}
