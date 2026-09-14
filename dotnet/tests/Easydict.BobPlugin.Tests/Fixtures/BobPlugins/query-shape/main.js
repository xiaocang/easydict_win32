function translate(query, completion) {
    completion({
        result: {
            from: query.from,
            to: query.to,
            toParagraphs: [
                'text=' + query.text,
                'originalText=' + query.originalText,
                'from=' + query.from,
                'to=' + query.to,
                'detectFrom=' + query.detectFrom,
                'detectTo=' + query.detectTo
            ]
        }
    });
}
