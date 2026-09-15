var CryptoJS = require('crypto-js');

function translate(query, completion) {
    var md5 = CryptoJS.MD5('abc').toString();
    var sha256 = CryptoJS.SHA256('abc').toString(CryptoJS.enc.Hex);
    var hmac = CryptoJS.HmacSHA256('message', 'key').toString();
    var base64 = CryptoJS.enc.Base64.stringify(CryptoJS.enc.Utf8.parse('hello'));

    completion({
        result: {
            from: 'en',
            to: query.to,
            toParagraphs: [md5, sha256, hmac, base64]
        }
    });
}
