function translate(query, completion) {
    var accumulated = '';
    $http.streamRequest({
        url: 'https://plugin.invalid/stream',
        method: 'GET',
        streamHandler: function (chunk) {
            accumulated += chunk.text;
            query.onStream({ result: { toParagraphs: [accumulated] } });
        },
        handler: function (response) {
            completion({
                result: {
                    from: query.detectFrom,
                    to: query.to,
                    toParagraphs: [accumulated]
                }
            });
        }
    });
}
