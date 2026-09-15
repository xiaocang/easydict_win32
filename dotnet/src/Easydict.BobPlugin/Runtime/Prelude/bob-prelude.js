/*
 * Host runtime presented to Bob plugins.
 *
 * Everything a plugin can touch ($http, $log, $data, ...) is defined here in JavaScript on top of
 * a small set of host functions named __ed_*, all of which take and return only strings and
 * numbers. Keeping the boundary that narrow means the host never has to build JavaScript objects,
 * and the plugin-visible surface can be adjusted without touching C#.
 *
 * Written in ES5 style on purpose: it has to run identically on whichever engine version is
 * resolved, and plugins are evaluated in the same global scope.
 *
 * The shapes below follow the Bob plugin SDK; verify against https://bobtranslate.com/plugin.
 */
(function (global) {
    'use strict';

    function toText(value) {
        if (value === null || value === undefined) {
            return String(value);
        }
        if (typeof value === 'string') {
            return value;
        }
        try {
            return JSON.stringify(value);
        } catch (e) {
            return String(value);
        }
    }

    /* ---------------------------------------------------------------- $log */

    var $log = {
        info: function (message) { __ed_log('info', toText(message)); },
        warn: function (message) { __ed_log('warn', toText(message)); },
        error: function (message) { __ed_log('error', toText(message)); },
        debug: function (message) { __ed_log('info', toText(message)); }
    };

    /* --------------------------------------------------------------- $data */

    function BobData(base64) {
        this.__base64 = base64 || '';
    }

    BobData.prototype.toUTF8 = function () { return __ed_b64ToUtf8(this.__base64); };
    BobData.prototype.toBase64 = function () { return this.__base64; };
    BobData.prototype.toHex = function () { return __ed_b64ToHex(this.__base64); };
    BobData.prototype.toString = function () { return this.toUTF8(); };
    BobData.prototype.valueOf = function () { return this.toUTF8(); };

    Object.defineProperty(BobData.prototype, 'length', {
        get: function () { return __ed_b64Length(this.__base64); }
    });

    var $data = {
        fromUTF8: function (text) { return new BobData(__ed_utf8ToB64(String(text === undefined ? '' : text))); },
        fromBase64: function (base64) { return new BobData(String(base64 || '')); },
        fromHex: function (hex) { return new BobData(__ed_hexToB64(String(hex || ''))); },
        fromData: function (data) { return new BobData(data && data.__base64 ? data.__base64 : ''); }
    };

    function isData(value) {
        return value instanceof BobData;
    }

    /* -------------------------------------------------------------- $timer */

    var $timer = {
        setTimeout: function (callback, milliseconds) {
            if (typeof callback !== 'function') {
                return -1;
            }
            return __ed_setTimeout(callback, Number(milliseconds) || 0);
        },
        clearTimeout: function (id) { __ed_clearTimeout(Number(id) || 0); }
    };

    // Plugins written against a browser-ish environment reach for the bare globals.
    if (typeof global.setTimeout !== 'function') {
        global.setTimeout = $timer.setTimeout;
        global.clearTimeout = $timer.clearTimeout;
    }

    /* ------------------------------------------------------------- $signal */

    function BobSignal() {
        this.__subscribers = [];
        this.isSent = false;
    }

    BobSignal.prototype.send = function (value) {
        if (this.isSent) {
            return;
        }
        this.isSent = true;
        var subscribers = this.__subscribers.slice();
        for (var i = 0; i < subscribers.length; i++) {
            try {
                subscribers[i](value);
            } catch (e) {
                $log.error('cancelSignal subscriber failed: ' + toText(e && e.message ? e.message : e));
            }
        }
    };

    BobSignal.prototype.subscribe = function (callback) {
        var self = this;
        if (typeof callback !== 'function') {
            return { dispose: function () { } };
        }
        if (this.isSent) {
            callback();
            return { dispose: function () { } };
        }
        this.__subscribers.push(callback);
        return {
            dispose: function () {
                var index = self.__subscribers.indexOf(callback);
                if (index >= 0) {
                    self.__subscribers.splice(index, 1);
                }
            }
        };
    };

    var $signal = {
        new: function () { return new BobSignal(); }
    };

    /* --------------------------------------------------------------- $http */

    function headerValue(header, name) {
        if (!header) {
            return null;
        }
        for (var key in header) {
            if (Object.prototype.hasOwnProperty.call(header, key)
                && key.toLowerCase() === name.toLowerCase()) {
                return header[key];
            }
        }
        return null;
    }

    function formEncode(body) {
        var parts = [];
        for (var key in body) {
            if (Object.prototype.hasOwnProperty.call(body, key)) {
                parts.push(encodeURIComponent(key) + '=' + encodeURIComponent(body[key]));
            }
        }
        return parts.join('&');
    }

    function appendQuery(url, params) {
        if (!params) {
            return url;
        }
        var query = formEncode(params);
        if (!query) {
            return url;
        }
        return url + (url.indexOf('?') >= 0 ? '&' : '?') + query;
    }

    /**
     * Turn a plugin's request options into the flat shape the host understands: the body is
     * already serialized and the content type resolved, so the host only has to send it.
     */
    function normalizeRequest(options) {
        options = options || {};
        var header = {};
        if (options.header) {
            for (var key in options.header) {
                if (Object.prototype.hasOwnProperty.call(options.header, key)) {
                    header[key] = String(options.header[key]);
                }
            }
        }

        var request = {
            url: appendQuery(String(options.url || ''), options.params),
            method: String(options.method || 'GET').toUpperCase(),
            header: header,
            body: null,
            bodyBase64: null,
            timeoutMs: null
        };

        if (typeof options.timeout === 'number' && options.timeout > 0) {
            // Bob expresses timeouts in seconds.
            request.timeoutMs = Math.round(options.timeout * 1000);
        }

        var body = options.body;
        if (body !== null && body !== undefined) {
            var contentType = headerValue(header, 'Content-Type');
            if (isData(body)) {
                request.bodyBase64 = body.toBase64();
            } else if (typeof body === 'string') {
                request.body = body;
            } else if (contentType && contentType.indexOf('application/x-www-form-urlencoded') >= 0) {
                request.body = formEncode(body);
            } else {
                request.body = JSON.stringify(body);
                if (!contentType) {
                    header['Content-Type'] = 'application/json';
                }
            }
        }

        if (options.files) {
            throw new Error('$http file uploads are not supported by this host.');
        }

        return request;
    }

    /** Rebuild Bob's response object from the host envelope. */
    function buildResponse(envelope) {
        var response = {
            response: {
                statusCode: envelope.statusCode || 0,
                headers: envelope.headers || {},
                url: envelope.url || ''
            },
            rawData: $data.fromBase64(envelope.rawDataBase64 || ''),
            data: undefined
        };

        if (envelope.error) {
            response.error = { message: envelope.error };
            response.data = null;
            return response;
        }

        var text = envelope.text || '';
        var contentType = envelope.contentType || '';
        if (contentType.indexOf('json') >= 0 || (text && (text.charAt(0) === '{' || text.charAt(0) === '['))) {
            try {
                response.data = JSON.parse(text);
            } catch (e) {
                response.data = text;
            }
        } else {
            response.data = text;
        }

        return response;
    }

    function performRequest(options, method, streamHandler) {
        var request;
        try {
            request = normalizeRequest(options);
            if (method) {
                request.method = method;
            }
        } catch (e) {
            var failure = { response: { statusCode: 0, headers: {}, url: '' }, error: { message: String(e && e.message ? e.message : e) } };
            if (typeof options.handler === 'function') {
                options.handler(failure);
                return undefined;
            }
            return Promise.reject(e);
        }

        var callId = currentCallId;
        var streamCallback = null;
        if (typeof streamHandler === 'function') {
            streamCallback = function (json) {
                var chunk = JSON.parse(json);
                streamHandler({
                    text: chunk.text || '',
                    rawData: $data.fromBase64(chunk.rawDataBase64 || '')
                });
            };
        }

        if (typeof options.handler === 'function') {
            var handler = options.handler;
            __ed_http(callId, JSON.stringify(request), streamCallback, function (json) {
                handler(buildResponse(JSON.parse(json)));
            });
            return undefined;
        }

        // No handler: Bob resolves a promise instead.
        return new Promise(function (resolve) {
            __ed_http(callId, JSON.stringify(request), streamCallback, function (json) {
                resolve(buildResponse(JSON.parse(json)));
            });
        });
    }

    var $http = {
        request: function (options) { return performRequest(options, null, options && options.streamHandler); },
        get: function (options) { return performRequest(options, 'GET', null); },
        post: function (options) { return performRequest(options, 'POST', null); },
        streamRequest: function (options) {
            return performRequest(options, null, options && options.streamHandler);
        }
    };

    /* --------------------------------------------------------------- $file */

    var $file = {
        read: function (path) {
            var base64 = __ed_fileRead(String(path || ''));
            return base64 === null || base64 === undefined ? null : $data.fromBase64(base64);
        },
        write: function (options) {
            options = options || {};
            var data = options.data;
            var base64 = isData(data) ? data.toBase64() : __ed_utf8ToB64(String(data === undefined ? '' : data));
            return __ed_fileWrite(String(options.path || ''), base64);
        },
        exists: function (path) { return __ed_fileExists(String(path || '')); },
        delete: function (path) { return __ed_fileDelete(String(path || '')); },
        list: function (path) { return JSON.parse(__ed_fileList(String(path || ''))); },
        mkdir: function (path) { return __ed_fileMkdir(String(path || '')); }
    };

    /* ------------------------------------------------------------- require */

    var moduleCache = {};

    function requireModule(name) {
        name = String(name || '');

        if (name === 'crypto-js' || name === 'crypto-js/crypto-js') {
            return global.CryptoJS;
        }

        if (Object.prototype.hasOwnProperty.call(moduleCache, name)) {
            return moduleCache[name].exports;
        }

        var source = __ed_readModule(name);
        if (source === null || source === undefined) {
            throw new Error("Cannot find module '" + name + "'");
        }

        var module = { exports: {} };
        moduleCache[name] = module;
        try {
            // CommonJS wrapper so a required file gets its own module scope.
            var factory = new Function('exports', 'require', 'module', '__filename', source);
            factory(module.exports, requireModule, module, name);
        } catch (e) {
            delete moduleCache[name];
            throw e;
        }

        return module.exports;
    }

    /* ----------------------------------------------------- plugin entry points */

    var pendingCalls = {};
    var currentCallId = 0;

    function resolveEntryPoint(name) {
        if (global.module && global.module.exports && typeof global.module.exports[name] === 'function') {
            return global.module.exports[name];
        }
        if (global.exports && typeof global.exports[name] === 'function') {
            return global.exports[name];
        }
        if (typeof global[name] === 'function') {
            return global[name];
        }
        return null;
    }

    /** True when the plugin exposes the named entry point. */
    global.__ed_hasEntryPoint = function (name) {
        return resolveEntryPoint(name) !== null;
    };

    /**
     * Serialize a plugin payload, dropping the debug-only `raw` field (it can hold huge or cyclic
     * structures) and anything not representable as JSON.
     */
    function stringifyPayload(payload) {
        try {
            return JSON.stringify(payload, function (key, value) {
                if (key === 'raw') {
                    return undefined;
                }
                if (typeof value === 'function') {
                    return undefined;
                }
                return value;
            });
        } catch (e) {
            return JSON.stringify({ error: { type: 'unknown', message: 'The plugin returned a value that could not be serialized: ' + toText(e && e.message ? e.message : e) } });
        }
    }

    function finishCall(callId, payload) {
        if (!Object.prototype.hasOwnProperty.call(pendingCalls, callId)) {
            return;   // already completed, timed out or cancelled
        }
        var pending = pendingCalls[callId];
        delete pendingCalls[callId];

        // currentCallId is left pointing at this call for its whole async lifetime (see
        // __ed_callTranslate/__ed_callValidate below) so a follow-up $http call made from a
        // .then()/async continuation - not just the initial synchronous invocation - still carries
        // the right call id for cancellation/timeout. Only restore it here, once the call is
        // actually done, and only if nothing else has already moved it on.
        if (currentCallId === callId) {
            currentCallId = pending.previousCallId || 0;
        }

        __ed_onCompletion(callId, stringifyPayload(payload || {}));
    }

    /** Run the plugin's translate entry point for one query. */
    global.__ed_callTranslate = function (callId, queryJson) {
        var translate = resolveEntryPoint('translate');
        if (!translate) {
            __ed_onCompletion(callId, JSON.stringify({ error: { type: 'unknown', message: 'The plugin does not export a translate function.' } }));
            return;
        }

        var args = JSON.parse(queryJson);
        var cancelSignal = new BobSignal();
        var query = {
            text: args.text,
            originalText: args.originalText,
            from: args.from,
            to: args.to,
            detectFrom: args.detectFrom,
            detectTo: args.detectTo,
            cancelSignal: cancelSignal,
            onStream: function (payload) {
                if (Object.prototype.hasOwnProperty.call(pendingCalls, callId)) {
                    __ed_onStream(callId, stringifyPayload(payload || {}));
                }
            },
            onCompletion: function (payload) { finishCall(callId, payload); }
        };

        pendingCalls[callId] = { query: query, cancelSignal: cancelSignal, previousCallId: currentCallId };

        // Left set for the whole async lifetime of the call (reset by finishCall above), not just
        // this synchronous invocation - see the comment there for why.
        currentCallId = callId;
        try {
            var returned = translate(query, query.onCompletion);
            if (returned && typeof returned.then === 'function') {
                // An async translate() that rejects would otherwise never complete the call.
                returned.then(undefined, function (error) {
                    finishCall(callId, { error: { type: 'unknown', message: toText(error && error.message ? error.message : error) } });
                });
            }
        } catch (e) {
            finishCall(callId, { error: { type: 'unknown', message: toText(e && e.message ? e.message : e) } });
        }
    };

    /** Run the plugin's optional pluginValidate entry point. */
    global.__ed_callValidate = function (callId) {
        var validate = resolveEntryPoint('pluginValidate');
        if (!validate) {
            __ed_onCompletion(callId, JSON.stringify({ result: null }));
            return;
        }

        pendingCalls[callId] = { query: null, cancelSignal: new BobSignal(), previousCallId: currentCallId };

        currentCallId = callId;
        try {
            var returned = validate(function (payload) { finishCall(callId, payload); });
            if (returned && typeof returned.then === 'function') {
                returned.then(undefined, function (error) {
                    finishCall(callId, { error: { type: 'unknown', message: toText(error && error.message ? error.message : error) } });
                });
            }
        } catch (e) {
            finishCall(callId, { error: { type: 'unknown', message: toText(e && e.message ? e.message : e) } });
        }
    };

    /** Notify a running call that the host cancelled it. */
    global.__ed_cancel = function (callId) {
        var pending = pendingCalls[callId];
        if (!pending) {
            return;
        }
        delete pendingCalls[callId];

        // Same accounting as finishCall: a cancelled call otherwise leaves currentCallId pinned to
        // a dead call id forever, since finishCall will never run for it.
        if (currentCallId === callId) {
            currentCallId = pending.previousCallId || 0;
        }

        try {
            pending.cancelSignal.send();
        } catch (e) {
            $log.error('cancelSignal.send failed: ' + toText(e && e.message ? e.message : e));
        }
    };

    /** Languages the plugin claims to support, as a JSON array. */
    global.__ed_supportLanguages = function () {
        var fn = resolveEntryPoint('supportLanguages');
        if (!fn) {
            return '[]';
        }
        try {
            var languages = fn();
            return JSON.stringify(languages || []);
        } catch (e) {
            $log.error('supportLanguages failed: ' + toText(e && e.message ? e.message : e));
            return '[]';
        }
    };

    /** The plugin's preferred timeout in seconds, or -1 when it does not declare one. */
    global.__ed_pluginTimeout = function () {
        var fn = resolveEntryPoint('pluginTimeoutInterval');
        if (!fn) {
            return -1;
        }
        try {
            var seconds = Number(fn());
            return isFinite(seconds) && seconds > 0 ? seconds : -1;
        } catch (e) {
            return -1;
        }
    };

    /* ------------------------------------------------------------- exposure */

    global.$log = $log;
    global.$data = $data;
    global.$timer = $timer;
    global.$signal = $signal;
    global.$http = $http;
    global.$file = $file;
    global.require = requireModule;

    // CommonJS shape, so plugins may use either `exports.translate` or a top-level function.
    global.module = { exports: {} };
    global.exports = global.module.exports;
})(typeof globalThis !== 'undefined' ? globalThis : this);
