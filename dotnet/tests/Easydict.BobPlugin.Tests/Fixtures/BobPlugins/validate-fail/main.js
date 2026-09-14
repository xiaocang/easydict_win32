function pluginValidate(completion) {
    completion({ error: { type: 'secretKey', message: 'The key was rejected.' } });
}

function translate(query, completion) {
    completion({ result: { from: 'en', to: query.to, toParagraphs: [query.text] } });
}
