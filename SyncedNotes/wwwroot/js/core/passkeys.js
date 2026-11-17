/* SyncedNotes – WebAuthn (passkey) helpers used by the sign-in pages, settings and the app lock. */
(function (root) {
    'use strict';
    const SN = (root.SN = root.SN || {});

    function b64urlToBuffer(value) {
        let s = String(value).replace(/-/g, '+').replace(/_/g, '/');
        while (s.length % 4) s += '=';
        const bin = atob(s);
        const bytes = new Uint8Array(bin.length);
        for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
        return bytes.buffer;
    }

    function bufferToB64url(buffer) {
        const bytes = new Uint8Array(buffer);
        let bin = '';
        for (let i = 0; i < bytes.length; i++) bin += String.fromCharCode(bytes[i]);
        return btoa(bin).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
    }

    function parseCreationOptions(json) {
        if (typeof PublicKeyCredential.parseCreationOptionsFromJSON === 'function') {
            return PublicKeyCredential.parseCreationOptionsFromJSON(json);
        }
        const o = JSON.parse(JSON.stringify(json));
        o.challenge = b64urlToBuffer(o.challenge);
        o.user.id = b64urlToBuffer(o.user.id);
        (o.excludeCredentials || []).forEach((c) => { c.id = b64urlToBuffer(c.id); });
        return o;
    }

    function parseRequestOptions(json) {
        if (typeof PublicKeyCredential.parseRequestOptionsFromJSON === 'function') {
            return PublicKeyCredential.parseRequestOptionsFromJSON(json);
        }
        const o = JSON.parse(JSON.stringify(json));
        o.challenge = b64urlToBuffer(o.challenge);
        (o.allowCredentials || []).forEach((c) => { c.id = b64urlToBuffer(c.id); });
        return o;
    }

    function credentialToJSON(cred) {
        if (typeof cred.toJSON === 'function') {
            try { return cred.toJSON(); } catch { /* fall back to manual serialisation */ }
        }
        const r = cred.response;
        const json = {
            id: cred.id,
            rawId: bufferToB64url(cred.rawId),
            type: cred.type,
            authenticatorAttachment: cred.authenticatorAttachment || null,
            clientExtensionResults: typeof cred.getClientExtensionResults === 'function' ? cred.getClientExtensionResults() : {},
            response: { clientDataJSON: bufferToB64url(r.clientDataJSON) },
        };
        if (r.attestationObject) {
            json.response.attestationObject = bufferToB64url(r.attestationObject);
            json.response.transports = typeof r.getTransports === 'function' ? r.getTransports() : [];
            if (typeof r.getAuthenticatorData === 'function') json.response.authenticatorData = bufferToB64url(r.getAuthenticatorData());
            if (typeof r.getPublicKey === 'function') {
                const pk = r.getPublicKey();
                if (pk) json.response.publicKey = bufferToB64url(pk);
            }
            if (typeof r.getPublicKeyAlgorithm === 'function') json.response.publicKeyAlgorithm = r.getPublicKeyAlgorithm();
        } else {
            json.response.authenticatorData = bufferToB64url(r.authenticatorData);
            json.response.signature = bufferToB64url(r.signature);
            json.response.userHandle = r.userHandle ? bufferToB64url(r.userHandle) : null;
        }
        return json;
    }

    async function postJson(url, body) {
        const res = await fetch(url, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json', 'X-SyncedNotes': '1', Accept: 'application/json' },
            body: JSON.stringify(body || {}),
            credentials: 'same-origin',
        });
        let data = null;
        try { data = await res.json(); } catch { /* empty */ }
        if (!res.ok) {
            const err = new Error((data && (data.detail ? `${data.title} ${data.detail}` : data.title)) || `Request failed (${res.status}).`);
            err.status = res.status;
            err.problem = data;
            throw err;
        }
        return data;
    }

    function describe() {
        const ua = navigator.userAgent;
        const browser = /Edg\//.test(ua) ? 'Edge' : /OPR\//.test(ua) ? 'Opera' : /Firefox\//.test(ua) ? 'Firefox'
            : /Chrome\//.test(ua) ? 'Chrome' : /Safari\//.test(ua) ? 'Safari' : 'Browser';
        const os = /iPhone/.test(ua) ? 'iPhone' : /iPad/.test(ua) ? 'iPad' : /Android/.test(ua) ? 'Android'
            : /Windows/.test(ua) ? 'Windows' : /Mac OS X|Macintosh/.test(ua) ? 'Mac' : /CrOS/.test(ua) ? 'ChromeOS'
                : /Linux/.test(ua) ? 'Linux' : 'this device';
        return `${browser} on ${os}`;
    }

    SN.passkeys = {
        b64urlToBuffer,
        bufferToB64url,
        postJson,

        supported() {
            return !!(root.PublicKeyCredential && navigator.credentials && navigator.credentials.create);
        },

        async conditionalMediationAvailable() {
            try {
                return !!(root.PublicKeyCredential && PublicKeyCredential.isConditionalMediationAvailable &&
                    await PublicKeyCredential.isConditionalMediationAvailable());
            } catch { return false; }
        },

        /** Runs navigator.credentials.create() for server-provided options; returns the credential JSON string. */
        async create(optionsJson, signal) {
            const publicKey = parseCreationOptions(optionsJson);
            const cred = await navigator.credentials.create({ publicKey, signal });
            if (!cred) throw new Error('No passkey was created.');
            return JSON.stringify(credentialToJSON(cred));
        },

        /** Runs navigator.credentials.get(); returns { json, id }. */
        async get(optionsJson, opts) {
            const publicKey = parseRequestOptions(optionsJson);
            const cred = await navigator.credentials.get({ publicKey, mediation: opts && opts.mediation, signal: opts && opts.signal });
            if (!cred) throw new Error('No passkey was selected.');
            return { json: JSON.stringify(credentialToJSON(cred)), id: cred.id };
        },

        /**
         * Offline presence check used by the app lock when the server can't be reached: asks the
         * authenticator for one of the account's passkeys with user verification (biometrics / device PIN).
         */
        async verifyLocally(credentialIds, rpId) {
            const challenge = new Uint8Array(32);
            crypto.getRandomValues(challenge);
            const publicKey = {
                challenge,
                rpId: rpId || location.hostname,
                userVerification: 'required',
                timeout: 120000,
                allowCredentials: (credentialIds || []).map((id) => ({ type: 'public-key', id: b64urlToBuffer(id) })),
            };
            const cred = await navigator.credentials.get({ publicKey });
            return !!cred && (credentialIds || []).includes(cred.id);
        },

        defaultName: describe,

        friendlyError(error) {
            if (!error) return 'Something went wrong.';
            switch (error.name) {
                case 'NotAllowedError': return 'The passkey request was cancelled or timed out.';
                case 'InvalidStateError': return 'This device already has a passkey for this account.';
                case 'SecurityError': return 'Passkeys need a secure connection (HTTPS) and a matching domain.';
                case 'NotSupportedError': return 'This browser or device doesn\'t support passkeys.';
                case 'AbortError': return 'The passkey request was cancelled.';
                default: return error.message || 'Something went wrong.';
            }
        },
    };
})(typeof self !== 'undefined' ? self : this);
