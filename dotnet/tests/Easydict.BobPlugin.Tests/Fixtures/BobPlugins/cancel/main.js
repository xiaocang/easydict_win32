var cancelled = false;

function translate(query, completion) {
    query.cancelSignal.subscribe(function () {
        cancelled = true;
        $log.info('cancelSignal received');
    });
    // Deliberately never completes.
}

function pluginValidate(completion) {
    if (cancelled) {
        completion({ result: true });
        return;
    }
    completion({ error: { type: 'unknown', message: 'cancelSignal never fired' } });
}
