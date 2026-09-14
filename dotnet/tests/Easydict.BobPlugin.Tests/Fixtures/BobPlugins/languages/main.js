function supportLanguages() {
    return ['auto', 'en', 'zh-Hans', 'ja', 'not-a-language'];
}

function translate(query, completion) {
    completion({ result: { from: query.detectFrom, to: query.to, toParagraphs: [query.text] } });
}
