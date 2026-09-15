function translate(query, completion) {
    completion({ error: { type: 'notFound', message: 'No entry for ' + query.text } });
}
