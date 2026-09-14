var cancelled = false;

function translate(query, completion) {
    query.cancelSignal.subscribe(function () {
        cancelled = true;
        $log.info('cancelSignal received');
    });

    // Logged last, so a test can wait for this line and know the subscriber is in place.
    $log.info('translate running');
    // Deliberately never completes.
}

function pluginValidate(completion) {
    if (cancelled) {
        completion({ result: true });
        return;
    }
    completion({ error: { type: 'unknown', message: 'cancelSignal never fired' } });
}
