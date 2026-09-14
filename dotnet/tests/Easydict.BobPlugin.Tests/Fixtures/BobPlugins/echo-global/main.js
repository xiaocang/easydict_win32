var callCount = 0;

function supportLanguages() {
    return ['auto', 'en', 'zh-Hans'];
}

function translate(query, completion) {
    callCount += 1;
    completion({
        result: {
            from: query.detectFrom,
            to: query.to,
            toParagraphs: ['ECHO(' + callCount + '): ' + query.text]
        }
    });
}
