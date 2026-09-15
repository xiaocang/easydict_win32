exports.supportLanguages = function () {
    return ['en', 'ja'];
};

exports.translate = function (query, completion) {
    completion({
        result: {
            from: query.from,
            to: query.to,
            toParagraphs: ['EXPORTS: ' + query.text, 'second line']
        }
    });
};
