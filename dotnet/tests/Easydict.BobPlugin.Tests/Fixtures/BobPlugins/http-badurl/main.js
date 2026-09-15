function translate(query, completion) {
    $http.get({
        url: 'file:///etc/passwd',
        handler: function (response) {
            completion({ error: { type: 'network', message: response.error ? response.error.message : 'no error' } });
        }
    });
}
