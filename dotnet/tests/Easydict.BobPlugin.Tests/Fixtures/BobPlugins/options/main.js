function translate(query, completion) {
    completion({
        result: {
            from: 'en',
            to: query.to,
            toParagraphs: [
                'apiKey=' + $option.apiKey,
                'model=' + $option.model,
                'identifier=' + $info.identifier,
                'version=' + $info.version,
                'platform=' + $env.platform
            ]
        }
    });
}
