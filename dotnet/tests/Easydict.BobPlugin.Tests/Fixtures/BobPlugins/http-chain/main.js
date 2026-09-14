async function translate(query, completion) {
    await $http.request({ url: 'https://plugin.invalid/first' });

    $log.info('sending second request');
    var second = await $http.request({ url: 'https://plugin.invalid/second' });

    completion({
        result: {
            from: query.detectFrom,
            to: query.to,
            toParagraphs: [second.data.translation]
        }
    });
}
