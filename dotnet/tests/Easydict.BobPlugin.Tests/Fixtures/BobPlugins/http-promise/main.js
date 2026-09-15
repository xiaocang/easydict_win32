async function translate(query, completion) {
    var response = await $http.request({
        url: 'https://plugin.invalid/translate',
        method: 'GET',
        params: { q: query.text }
    });

    completion({
        result: {
            from: query.detectFrom,
            to: query.to,
            toParagraphs: [response.data.translation, 'status=' + response.response.statusCode]
        }
    });
}
