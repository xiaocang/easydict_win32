function translate(query, completion) {
    $timer.setTimeout(function () {
        completion({
            result: {
                from: 'en',
                to: query.to,
                toParagraphs: ['delayed: ' + query.text]
            }
        });
    }, 20);
}
