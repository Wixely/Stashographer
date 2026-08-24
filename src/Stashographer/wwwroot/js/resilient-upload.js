// Circuit-independent image uploads for Interactive Server components.
// The browser owns the File and posts it with fetch; Blazor only receives a small,
// durable server receipt after the upload/queue operation has completed.
window.stashResilientUpload = (() => {
    'use strict';

    const storageKey = 'stashographer.pending-browser-uploads.v1';
    const controllers = new Map();
    const files = new Map();
    const scheduled = new Set();
    const delivering = new Set();
    const transmitting = new Set();
    const polling = new Set();

    function readPending() {
        try {
            const value = JSON.parse(sessionStorage.getItem(storageKey) || '[]');
            return Array.isArray(value) ? value : [];
        } catch {
            return [];
        }
    }

    function writePending(entries) {
        try {
            if (entries.length === 0) sessionStorage.removeItem(storageKey);
            else sessionStorage.setItem(storageKey, JSON.stringify(entries));
        } catch {
            // The upload itself remains safe when storage is unavailable. Only recovery
            // across a complete page/circuit replacement is reduced.
        }
    }

    function updatePending(token, update) {
        const entries = readPending();
        const index = entries.findIndex(entry => entry.token === token);
        if (index < 0) return null;
        entries[index] = { ...entries[index], ...update };
        writePending(entries);
        return entries[index];
    }

    function removePending(token) {
        writePending(readPending().filter(entry => entry.token !== token));
        files.delete(token);
        scheduled.delete(token);
    }

    function createToken() {
        if (window.crypto && typeof window.crypto.randomUUID === 'function') return window.crypto.randomUUID();
        return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, character => {
            const random = Math.random() * 16 | 0;
            const value = character === 'x' ? random : (random & 0x3) | 0x8;
            return value.toString(16);
        });
    }

    function controllerFor(ownerKey) {
        return Array.from(controllers.values()).find(controller => controller.ownerKey === ownerKey);
    }

    async function notify(controller, method, ...args) {
        if (!controller || !controller.dotNetRef) return false;
        try {
            await controller.dotNetRef.invokeMethodAsync(method, ...args);
            return true;
        } catch {
            return false;
        }
    }

    function schedule(token, delay = 1500) {
        if (scheduled.has(token)) return;
        scheduled.add(token);
        setTimeout(() => {
            scheduled.delete(token);
            const entry = readPending().find(candidate => candidate.token === token);
            if (entry) resume(entry);
        }, delay);
    }

    async function deliver(entry) {
        if (delivering.has(entry.token)) return;
        const controller = controllerFor(entry.ownerKey);
        if (!controller || !entry.result) {
            schedule(entry.token);
            return;
        }
        delivering.add(entry.token);
        try {
            if (await notify(controller, 'OnBrowserUploadCompleted', entry.result)) {
                removePending(entry.token);
                return;
            }
        } finally {
            delivering.delete(entry.token);
        }
        schedule(entry.token);
    }

    async function poll(entry) {
        if (polling.has(entry.token)) return;
        polling.add(entry.token);
        try {
            await pollCore(entry);
        } finally {
            polling.delete(entry.token);
        }
    }

    async function pollCore(entry) {
        try {
            const response = await fetch(
                new URL(`browser-uploads/${encodeURIComponent(entry.token)}`, document.baseURI),
                { credentials: 'same-origin', cache: 'no-store' });
            if (response.ok) {
                const result = await response.json();
                const completed = updatePending(entry.token, {
                    state: 'complete', result, error: null
                });
                if (completed) await deliver(completed);
                return;
            }
            if (response.status === 202 || response.status === 409) {
                schedule(entry.token);
                return;
            }
            if (response.status === 404 && !files.has(entry.token)) {
                const message = 'The interrupted upload did not reach the server. Choose the image again.';
                updatePending(entry.token, { state: 'failed', error: message, retryable: false });
                await notify(controllerFor(entry.ownerKey), 'OnBrowserUploadFailed', message, false);
                removePending(entry.token);
                return;
            }
        } catch {
            // Offline/reconnecting: keep the durable token and try again when visible/online.
        }
        schedule(entry.token);
    }

    async function upload(entry, file) {
        if (transmitting.has(entry.token)) return;
        transmitting.add(entry.token);
        try {
            await uploadCore(entry, file);
        } finally {
            transmitting.delete(entry.token);
        }
    }

    async function uploadCore(entry, file) {
        const controller = controllerFor(entry.ownerKey);
        updatePending(entry.token, { state: 'uploading', error: null });
        await notify(controller, 'OnBrowserUploadStarted', file.name || 'image');

        const antiforgery = document.querySelector(
            '#stash-upload-antiforgery input[name="__RequestVerificationToken"]');
        if (!antiforgery || !antiforgery.value) {
            const message = 'The upload page is not ready. Reload it and try again.';
            updatePending(entry.token, { state: 'failed', error: message, retryable: false });
            await notify(controller, 'OnBrowserUploadFailed', message, false);
            removePending(entry.token);
            return;
        }

        const form = new FormData();
        form.append('__RequestVerificationToken', antiforgery.value);
        form.append('token', entry.token);
        form.append('kind', entry.kind);
        form.append('multipleItems', String(entry.multipleItems));
        form.append('photo', file, file.name || 'image');

        try {
            const response = await fetch(new URL('browser-uploads', document.baseURI), {
                method: 'POST',
                body: form,
                credentials: 'same-origin'
            });
            if (response.ok) {
                const result = await response.json();
                const completed = updatePending(entry.token, {
                    state: 'complete', result, error: null
                });
                files.delete(entry.token);
                if (completed) await deliver(completed);
                return;
            }
            if (response.status === 409) {
                await poll(entry);
                return;
            }

            let message = 'The image could not be uploaded.';
            try {
                const body = await response.json();
                if (body && typeof body.error === 'string') message = body.error;
            } catch { /* keep the safe generic message */ }
            const retryable = response.status >= 500 || response.status === 429;
            updatePending(entry.token, { state: 'failed', error: message, retryable });
            await notify(controller, 'OnBrowserUploadFailed', message, retryable);
            if (retryable) schedule(entry.token, 2500);
            else removePending(entry.token);
        } catch {
            const message = 'Connection interrupted; the selected image will retry automatically.';
            updatePending(entry.token, { state: 'failed', error: message, retryable: true });
            await notify(controller, 'OnBrowserUploadFailed', message, true);
            schedule(entry.token, 2000);
        }
    }

    function resume(entry) {
        if (entry.state === 'complete' && entry.result) {
            deliver(entry);
            return;
        }
        const file = files.get(entry.token);
        if (file && entry.retryable !== false) {
            upload(entry, file);
            return;
        }
        if (entry.state === 'uploading' || entry.retryable) poll(entry);
    }

    async function selected(controller) {
        const selectedFiles = Array.from(controller.input.files || []);
        controller.input.value = '';
        if (selectedFiles.length === 0) return;

        if (controller.completesClipboardQueue
            && window.stashClipboardImages
            && typeof window.stashClipboardImages.complete === 'function') {
            window.stashClipboardImages.complete();
        }

        // Each file gets its own durable token and queue entry. Upload sequentially so
        // Blazor receives predictable start/completion pairs and the final completion
        // reliably clears the page's busy state. HTTP uploads continue even if the
        // interactive circuit is suspended while a mobile picker is open.
        for (const file of selectedFiles) {
            const entry = {
                token: createToken(),
                ownerKey: controller.ownerKey,
                kind: controller.kind,
                multipleItems: controller.multipleItems,
                fileName: file.name || 'image',
                createdAt: new Date().toISOString(),
                state: 'uploading',
                retryable: true
            };
            const entries = readPending();
            entries.push(entry);
            writePending(entries);
            files.set(entry.token, file);
            await upload(entry, file);
        }
    }

    function register(inputId, ownerKey, kind, multipleItems, completesClipboardQueue, dotNetRef) {
        const input = document.getElementById(inputId);
        if (!(input instanceof HTMLInputElement)) return false;

        const previous = controllers.get(inputId);
        if (previous) previous.input.removeEventListener('change', previous.onChange);
        const controller = {
            input,
            ownerKey,
            kind,
            multipleItems: !!multipleItems,
            completesClipboardQueue: !!completesClipboardQueue,
            dotNetRef,
            onChange: null
        };
        controller.onChange = () => { void selected(controller); };
        input.addEventListener('change', controller.onChange);
        controllers.set(inputId, controller);

        for (const entry of readPending().filter(candidate => candidate.ownerKey === ownerKey)) {
            resume(entry);
        }
        return true;
    }

    function unregister(inputId) {
        const controller = controllers.get(inputId);
        if (!controller) return;
        controller.input.removeEventListener('change', controller.onChange);
        controllers.delete(inputId);
    }

    function retry(ownerKey) {
        for (const entry of readPending().filter(candidate => candidate.ownerKey === ownerKey)) {
            if (entry.state === 'failed') {
                entry.retryable = files.has(entry.token);
                updatePending(entry.token, entry);
            }
            resume(entry);
        }
    }

    window.addEventListener('online', () => {
        for (const entry of readPending()) resume(entry);
    });
    document.addEventListener('visibilitychange', () => {
        if (document.visibilityState === 'visible') {
            for (const entry of readPending()) resume(entry);
        }
    });
    setInterval(() => {
        if (document.visibilityState === 'visible') {
            for (const entry of readPending()) resume(entry);
        }
    }, 3000);

    return { register, unregister, retry };
})();
