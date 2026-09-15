/*
 * A small crypto-js stand-in for plugins that `require('crypto-js')`.
 *
 * Only the operations plugins actually reach for are implemented, and each is delegated to the
 * host's .NET crypto rather than reimplemented in JavaScript. A WordArray here is just a base64
 * string with crypto-js's encoder-aware toString().
 *
 * Deliberately unsupported: passphrase-derived AES keys (crypto-js's OpenSSL KDF) and random
 * generation. Both raise a clear error instead of silently producing different bytes than
 * crypto-js would. A plugin that needs the real library can ship crypto-js.js next to main.js.
 */
(function (global) {
    'use strict';

    var CryptoJS = {};

    function WordArray(base64) {
        this.__base64 = base64 || '';
    }

    WordArray.prototype.toString = function (encoder) {
        if (encoder === CryptoJS.enc.Base64) {
            return this.__base64;
        }
        if (encoder === CryptoJS.enc.Utf8) {
            return __ed_b64ToUtf8(this.__base64);
        }
        if (encoder === CryptoJS.enc.Latin1) {
            return __ed_b64ToLatin1(this.__base64);
        }
        // crypto-js defaults to hex.
        return __ed_b64ToHex(this.__base64);
    };

    /** Accept a WordArray, a $data value or a plain string (parsed as UTF-8, like crypto-js). */
    function toWordArray(value) {
        if (value === null || value === undefined) {
            return new WordArray('');
        }
        if (value instanceof WordArray) {
            return value;
        }
        if (typeof value === 'object' && typeof value.__base64 === 'string') {
            return new WordArray(value.__base64);
        }
        return new WordArray(__ed_utf8ToB64(String(value)));
    }

    CryptoJS.enc = {
        Hex: {
            parse: function (hex) { return new WordArray(__ed_hexToB64(String(hex || ''))); },
            stringify: function (wordArray) { return toWordArray(wordArray).toString(CryptoJS.enc.Hex); }
        },
        Utf8: {
            parse: function (text) { return new WordArray(__ed_utf8ToB64(String(text === undefined ? '' : text))); },
            stringify: function (wordArray) { return __ed_b64ToUtf8(toWordArray(wordArray).__base64); }
        },
        Base64: {
            parse: function (base64) { return new WordArray(String(base64 || '')); },
            stringify: function (wordArray) { return toWordArray(wordArray).__base64; }
        },
        Latin1: {
            parse: function (text) { return new WordArray(__ed_latin1ToB64(String(text === undefined ? '' : text))); },
            stringify: function (wordArray) { return __ed_b64ToLatin1(toWordArray(wordArray).__base64); }
        }
    };

    // Encoders are compared by identity in toString(); define them before first use above.
    CryptoJS.enc.Hex.stringify = function (wordArray) {
        return __ed_b64ToHex(toWordArray(wordArray).__base64);
    };

    CryptoJS.lib = {
        WordArray: {
            create: function (value) {
                if (typeof value === 'string') {
                    return CryptoJS.enc.Utf8.parse(value);
                }
                return toWordArray(value);
            },
            random: function () {
                throw new Error('CryptoJS.lib.WordArray.random is not available in this host.');
            }
        }
    };

    function defineHash(name, algorithm) {
        CryptoJS[name] = function (message) {
            return new WordArray(__ed_hash(algorithm, toWordArray(message).__base64));
        };
    }

    defineHash('MD5', 'md5');
    defineHash('SHA1', 'sha1');
    defineHash('SHA256', 'sha256');
    defineHash('SHA512', 'sha512');

    function defineHmac(name, algorithm) {
        CryptoJS[name] = function (message, key) {
            return new WordArray(__ed_hmac(algorithm, toWordArray(key).__base64, toWordArray(message).__base64));
        };
    }

    defineHmac('HmacMD5', 'md5');
    defineHmac('HmacSHA1', 'sha1');
    defineHmac('HmacSHA256', 'sha256');
    defineHmac('HmacSHA512', 'sha512');

    CryptoJS.mode = { CBC: { name: 'CBC' } };
    CryptoJS.pad = { Pkcs7: { name: 'Pkcs7' } };

    function requireSupportedAesConfig(key, config) {
        if (typeof key === 'string') {
            throw new Error(
                'This host only supports AES with an explicit key and IV. '
                + 'Pass CryptoJS.enc.Utf8.parse(key) and { iv: ... }, or bundle crypto-js.js with the plugin.');
        }
        if (!config || !config.iv) {
            throw new Error('This host requires an explicit iv for AES.');
        }
        if (config.mode && config.mode !== CryptoJS.mode.CBC) {
            throw new Error('This host only supports AES-CBC.');
        }
        if (config.padding && config.padding !== CryptoJS.pad.Pkcs7) {
            throw new Error('This host only supports Pkcs7 padding.');
        }
    }

    CryptoJS.AES = {
        encrypt: function (message, key, config) {
            requireSupportedAesConfig(key, config);
            var cipherBase64 = __ed_aes(
                true,
                toWordArray(key).__base64,
                toWordArray(config.iv).__base64,
                toWordArray(message).__base64);
            var ciphertext = new WordArray(cipherBase64);
            return {
                ciphertext: ciphertext,
                toString: function (encoder) {
                    return encoder ? ciphertext.toString(encoder) : cipherBase64;
                }
            };
        },
        decrypt: function (cipher, key, config) {
            requireSupportedAesConfig(key, config);
            var cipherBase64;
            if (typeof cipher === 'string') {
                cipherBase64 = cipher;
            } else if (cipher && cipher.ciphertext) {
                cipherBase64 = toWordArray(cipher.ciphertext).__base64;
            } else {
                cipherBase64 = toWordArray(cipher).__base64;
            }

            return new WordArray(__ed_aes(
                false,
                toWordArray(key).__base64,
                toWordArray(config.iv).__base64,
                cipherBase64));
        }
    };

    global.CryptoJS = CryptoJS;
})(typeof globalThis !== 'undefined' ? globalThis : this);
