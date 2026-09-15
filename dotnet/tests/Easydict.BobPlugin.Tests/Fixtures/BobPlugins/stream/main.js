function translate(query, completion) {
    query.onStream({ result: { toParagraphs: ['Hel'] } });
    query.onStream({ result: { toParagraphs: ['Hello'] } });
    query.onStream({ result: { toParagraphs: ['Hello wor'] } });
    completion({
        result: {
            from: 'en',
            to: query.to,
            toParagraphs: ['Hello world'],
            toDict: { word: query.text, parts: [{ part: 'n.', means: ['greeting'] }] }
        }
    });
}
