function translate(query, completion) {
    $http.post({
        url: 'https://plugin.invalid/translate',
        header: { 'X-Test': 'yes' },
        body: { text: query.text, to: query.to },
        handler: function (response) {
            if (response.error) {
                completion({ error: { type: 'network', message: response.error.message } });
                return;
            }
            completion({
                result: {
                    from: query.detectFrom,
                    to: query.to,
                    toParagraphs: [response.data.translation]
                }
            });
        }
    });
}
