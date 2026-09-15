function translate(query, completion) {
    completion({
        error: {
            type: 'secretKey',
            message: 'The API key is missing.',
            addition: 'Set it in the plugin options.',
            troubleshootingLink: 'https://example.invalid/help'
        }
    });
}
