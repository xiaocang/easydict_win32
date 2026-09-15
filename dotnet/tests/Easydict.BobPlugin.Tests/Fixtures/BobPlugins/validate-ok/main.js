function pluginValidate(completion) {
    completion({ result: true });
}

function translate(query, completion) {
    completion({ result: { from: 'en', to: query.to, toParagraphs: [query.text] } });
}
