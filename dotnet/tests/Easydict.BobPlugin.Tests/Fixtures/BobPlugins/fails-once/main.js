var attempts = 0;

function translate(query, completion) {
    attempts += 1;
    if (attempts === 1) {
        completion({ error: { type: 'api', message: 'transient failure' } });
        return;
    }
    completion({
        result: {
            from: 'en',
            to: query.to,
            toParagraphs: ['attempt ' + attempts]
        }
    });
}
