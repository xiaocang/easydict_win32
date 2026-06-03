//! RIPEMD-128 hash implementation for MDX decryption
//!
//! This is a pure Rust implementation of RIPEMD-128 hash algorithm,
//! used for generating decryption keys in MDX files.

/// RIPEMD-128 hash function
pub fn ripemd128(data: &[u8]) -> [u8; 16] {
    // Initial hash values
    let mut hash: [u32; 4] = [0x67452301, 0xefcdab89, 0x98badcfe, 0x10325476];

    // Pad the message
    let padded = pad_message(data);

    // Process each 64-byte block
    for chunk in padded.chunks(64) {
        let mut x = [0u32; 16];
        for (i, word) in chunk.chunks(4).enumerate() {
            x[i] = u32::from_le_bytes([word[0], word[1], word[2], word[3]]);
        }

        compress(&mut hash, &x);
    }

    // Convert hash to bytes
    let mut result = [0u8; 16];
    for (i, &h) in hash.iter().enumerate() {
        result[i * 4..(i + 1) * 4].copy_from_slice(&h.to_le_bytes());
    }

    result
}

/// Pad message to 64-byte boundary
fn pad_message(data: &[u8]) -> Vec<u8> {
    let bit_len = (data.len() as u64) * 8;
    let mut padded = data.to_vec();

    // Append bit '1' (0x80)
    padded.push(0x80);

    // Append zeros until length ≡ 56 (mod 64)
    while padded.len() % 64 != 56 {
        padded.push(0x00);
    }

    // Append original length in bits as 64-bit little-endian
    padded.extend_from_slice(&bit_len.to_le_bytes());

    padded
}

/// Left rotation
#[inline]
fn rotl(x: u32, n: u32) -> u32 {
    (x << n) | (x >> (32 - n))
}

/// Boolean functions
#[inline]
fn f(x: u32, y: u32, z: u32) -> u32 {
    x ^ y ^ z
}

#[inline]
fn g(x: u32, y: u32, z: u32) -> u32 {
    (x & y) | (!x & z)
}

#[inline]
fn h(x: u32, y: u32, z: u32) -> u32 {
    (x | !y) ^ z
}

#[inline]
fn i(x: u32, y: u32, z: u32) -> u32 {
    (x & z) | (y & !z)
}

/// Compression function
fn compress(hash: &mut [u32; 4], x: &[u32; 16]) {
    // Constants
    const K: [u32; 4] = [0x00000000, 0x5a827999, 0x6ed9eba1, 0x8f1bbcdc];
    const KK: [u32; 4] = [0x50a28be6, 0x5c4dd124, 0x6d703ef3, 0x00000000];

    // Message schedule for left rounds
    const R: [[usize; 16]; 4] = [
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15],
        [7, 4, 13, 1, 10, 6, 15, 3, 12, 0, 9, 5, 2, 14, 11, 8],
        [3, 10, 14, 4, 9, 15, 8, 1, 2, 7, 0, 6, 13, 11, 5, 12],
        [1, 9, 11, 10, 0, 8, 12, 4, 13, 3, 7, 15, 14, 5, 6, 2],
    ];

    // Message schedule for right rounds
    const RR: [[usize; 16]; 4] = [
        [5, 14, 7, 0, 9, 2, 11, 4, 13, 6, 15, 8, 1, 10, 3, 12],
        [6, 11, 3, 7, 0, 13, 5, 10, 14, 15, 8, 12, 4, 9, 1, 2],
        [15, 5, 1, 3, 7, 14, 6, 9, 11, 8, 12, 2, 10, 0, 4, 13],
        [8, 6, 4, 1, 3, 11, 15, 0, 5, 12, 2, 13, 9, 7, 10, 14],
    ];

    // Shift amounts for left rounds
    const S: [[u32; 16]; 4] = [
        [11, 14, 15, 12, 5, 8, 7, 9, 11, 13, 14, 15, 6, 7, 9, 8],
        [7, 6, 8, 13, 11, 9, 7, 15, 7, 12, 15, 9, 11, 7, 13, 12],
        [11, 13, 6, 7, 14, 9, 13, 15, 14, 8, 13, 6, 5, 12, 7, 5],
        [11, 12, 14, 15, 14, 15, 9, 8, 9, 14, 5, 6, 8, 6, 5, 12],
    ];

    // Shift amounts for right rounds
    const SS: [[u32; 16]; 4] = [
        [8, 9, 9, 11, 13, 15, 15, 5, 7, 7, 8, 11, 14, 14, 12, 6],
        [9, 13, 15, 7, 12, 8, 9, 11, 7, 7, 12, 7, 6, 15, 13, 11],
        [9, 7, 15, 11, 8, 6, 6, 14, 12, 13, 5, 14, 13, 13, 7, 5],
        [15, 5, 8, 11, 14, 14, 6, 14, 6, 9, 12, 9, 12, 5, 15, 8],
    ];

    let (mut a, mut b, mut c, mut d) = (hash[0], hash[1], hash[2], hash[3]);
    let (mut aa, mut bb, mut cc, mut dd) = (hash[0], hash[1], hash[2], hash[3]);

    // Left rounds
    for j in 0..16 {
        let t = a
            .wrapping_add(f(b, c, d))
            .wrapping_add(x[R[0][j]])
            .wrapping_add(K[0]);
        a = d;
        d = c;
        c = b;
        b = rotl(t, S[0][j]);
    }

    for j in 0..16 {
        let t = a
            .wrapping_add(g(b, c, d))
            .wrapping_add(x[R[1][j]])
            .wrapping_add(K[1]);
        a = d;
        d = c;
        c = b;
        b = rotl(t, S[1][j]);
    }

    for j in 0..16 {
        let t = a
            .wrapping_add(h(b, c, d))
            .wrapping_add(x[R[2][j]])
            .wrapping_add(K[2]);
        a = d;
        d = c;
        c = b;
        b = rotl(t, S[2][j]);
    }

    for j in 0..16 {
        let t = a
            .wrapping_add(i(b, c, d))
            .wrapping_add(x[R[3][j]])
            .wrapping_add(K[3]);
        a = d;
        d = c;
        c = b;
        b = rotl(t, S[3][j]);
    }

    // Right rounds
    for j in 0..16 {
        let t = aa
            .wrapping_add(i(bb, cc, dd))
            .wrapping_add(x[RR[0][j]])
            .wrapping_add(KK[0]);
        aa = dd;
        dd = cc;
        cc = bb;
        bb = rotl(t, SS[0][j]);
    }

    for j in 0..16 {
        let t = aa
            .wrapping_add(h(bb, cc, dd))
            .wrapping_add(x[RR[1][j]])
            .wrapping_add(KK[1]);
        aa = dd;
        dd = cc;
        cc = bb;
        bb = rotl(t, SS[1][j]);
    }

    for j in 0..16 {
        let t = aa
            .wrapping_add(g(bb, cc, dd))
            .wrapping_add(x[RR[2][j]])
            .wrapping_add(KK[2]);
        aa = dd;
        dd = cc;
        cc = bb;
        bb = rotl(t, SS[2][j]);
    }

    for j in 0..16 {
        let t = aa
            .wrapping_add(f(bb, cc, dd))
            .wrapping_add(x[RR[3][j]])
            .wrapping_add(KK[3]);
        aa = dd;
        dd = cc;
        cc = bb;
        bb = rotl(t, SS[3][j]);
    }

    // Final addition
    let t = hash[1].wrapping_add(c).wrapping_add(dd);
    hash[1] = hash[2].wrapping_add(d).wrapping_add(aa);
    hash[2] = hash[3].wrapping_add(a).wrapping_add(bb);
    hash[3] = hash[0].wrapping_add(b).wrapping_add(cc);
    hash[0] = t;
}
#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_ripemd128_empty() {
        let result = ripemd128(&[]);
        let expected = [
            0xcd, 0xf2, 0x62, 0x13, 0xa1, 0x50, 0xdc, 0x3e, 0xcb, 0x61, 0x0f, 0x18, 0xf6, 0xb3,
            0x8b, 0x46,
        ];
        assert_eq!(result, expected);
    }

    #[test]
    fn test_ripemd128_a() {
        let result = ripemd128(b"a");
        let expected = [
            0x86, 0xbe, 0x7a, 0xfa, 0x33, 0x9d, 0x0f, 0xc7, 0xcf, 0xc7, 0x85, 0xe7, 0x2f, 0x57,
            0x8d, 0x33,
        ];
        assert_eq!(result, expected);
    }

    #[test]
    fn test_ripemd128_abc() {
        let result = ripemd128(b"abc");
        let expected = [
            0xc1, 0x4a, 0x12, 0x19, 0x9c, 0x66, 0xe4, 0xba, 0x84, 0x63, 0x6b, 0x0f, 0x69, 0x14,
            0x4c, 0x77,
        ];
        assert_eq!(result, expected);
    }

    #[test]
    fn test_ripemd128_message_digest() {
        let result = ripemd128(b"message digest");
        let expected = [
            0x9e, 0x32, 0x7b, 0x3d, 0x6e, 0x52, 0x30, 0x62, 0xaf, 0xc1, 0x13, 0x2d, 0x7d, 0xf9,
            0xd1, 0xb8,
        ];
        assert_eq!(result, expected);
    }

    #[test]
    fn test_ripemd128_alphabet() {
        let result = ripemd128(b"abcdefghijklmnopqrstuvwxyz");
        let expected = [
            0xfd, 0x2a, 0xa6, 0x07, 0xf7, 0x1d, 0xc8, 0xf5, 0x10, 0x71, 0x49, 0x22, 0xb3, 0x71,
            0x83, 0x4e,
        ];
        assert_eq!(result, expected);
    }

    #[test]
    fn test_ripemd128_alphanumeric() {
        let result = ripemd128(b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789");
        let expected = [
            0xd1, 0xe9, 0x59, 0xeb, 0x17, 0x9c, 0x91, 0x1f, 0xae, 0xa4, 0x62, 0x4c, 0x60, 0xc5,
            0xc7, 0x02,
        ];
        assert_eq!(result, expected);
    }
}
