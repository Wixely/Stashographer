// Circuit-independent image uploads for Interactive Server components.
// The browser owns the File, persists it before transmission, and posts it with fetch;
// Blazor only receives a small, durable server receipt after the operation completes.
window.stashResilientUpload = (() => {
    'use strict';

    const storageKey = 'stashographer.pending-browser-uploads.v2';
    const databaseName = 'stashographer-resilient-uploads';
    const databaseVersion = 1;
    const fileStoreName = 'files';
    const controllers = new Map();
    const files = new Map();
    const scheduled = new Set();
    const delivering = new Set();
    const transmitting = new Set();
    const polling = new Set();
    let databasePromise;
    let pickerActive = false;
    let persistenceCount = 0;

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
            // The upload remains safe when session storage is unavailable. Recovery still
            // works while this document remains alive, but not across a complete reload.
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

    function openDatabase() {
        if (databasePromise) return databasePromise;
        if (!window.indexedDB) return Promise.resolve(null);

        databasePromise = new Promise(resolve => {
            let request;
            try {
                request = window.indexedDB.open(databaseName, databaseVersion);
            } catch {
                resolve(null);
                return;
            }
            request.onupgradeneeded = () => {
                if (!request.result.objectStoreNames.contains(fileStoreName))
                    request.result.createObjectStore(fileStoreName, { keyPath: 'token' });
            };
            request.onsuccess = () => resolve(request.result);
            request.onerror = () => resolve(null);
            request.onblocked = () => resolve(null);
        });
        return databasePromise;
    }

    async function storeFile(token, file) {
        const database = await openDatabase();
        if (!database) return false;
        try {
            await new Promise((resolve, reject) => {
                const transaction = database.transaction(fileStoreName, 'readwrite');
                transaction.objectStore(fileStoreName).put({
                    token,
                    file,
                    fileName: file.name || 'image',
                    contentType: file.type || 'application/octet-stream',
                    lastModified: file.lastModified || Date.now(),
                    storedAt: Date.now()
                });
                transaction.oncomplete = () => resolve();
                transaction.onerror = () => reject(transaction.error);
                transaction.onabort = () => reject(transaction.error);
            });
            return true;
        } catch {
            return false;
        }
    }

    async function loadStoredFile(token) {
        const database = await openDatabase();
        if (!database) return null;
        try {
            const record = await new Promise((resolve, reject) => {
                const request = database.transaction(fileStoreName, 'readonly')
                    .objectStore(fileStoreName).get(token);
                request.onsuccess = () => resolve(request.result || null);
                request.onerror = () => reject(request.error);
            });
            if (!record || !(record.file instanceof Blob)) return null;
            if (record.file instanceof File) return record.file;
            return new File([record.file], record.fileName || 'image', {
                type: record.contentType || record.file.type || 'application/octet-stream',
                lastModified: record.lastModified || Date.now()
            });
        } catch {
            return null;
        }
    }

    async function hasStoredFile(token) {
        const database = await openDatabase();
        if (!database) return false;
        try {
            return await new Promise((resolve, reject) => {
                const request = database.transaction(fileStoreName, 'readonly')
                    .objectStore(fileStoreName).getKey(token);
                request.onsuccess = () => resolve(request.result !== undefined);
                request.onerror = () => reject(request.error);
            });
        } catch {
            return false;
        }
    }

    async function deleteStoredFile(token) {
        const database = await openDatabase();
        if (!database) return;
        try {
            await new Promise((resolve, reject) => {
                const transaction = database.transaction(fileStoreName, 'readwrite');
                transaction.objectStore(fileStoreName).delete(token);
                transaction.oncomplete = () => resolve();
                transaction.onerror = () => reject(transaction.error);
                transaction.onabort = () => reject(transaction.error);
            });
        } catch {
            // A stale browser copy is harmless because tokens are unguessable and the
            // server operation is idempotent. A later successful cleanup can remove it.
        }
    }

    async function cleanupAbandonedFiles() {
        const database = await openDatabase();
        if (!database) return;
        const cutoff = Date.now() - (7 * 24 * 60 * 60 * 1000);
        try {
            await new Promise((resolve, reject) => {
                const transaction = database.transaction(fileStoreName, 'readwrite');
                const request = transaction.objectStore(fileStoreName).openCursor();
                request.onsuccess = () => {
                    const cursor = request.result;
                    if (!cursor) return;
                    if (!cursor.value.storedAt || cursor.value.storedAt < cutoff)
                        cursor.delete();
                    cursor.continue();
                };
                request.onerror = () => reject(request.error);
                transaction.oncomplete = () => resolve();
                transaction.onerror = () => reject(transaction.error);
                transaction.onabort = () => reject(transaction.error);
            });
        } catch {
            // Cleanup must never prevent a current upload.
        }
    }

    function removePending(token) {
        writePending(readPending().filter(entry => entry.token !== token));
        files.delete(token);
        scheduled.delete(token);
        void deleteStoredFile(token);
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
            if (entry) void resume(entry);
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
                    state: 'complete', result, error: null, retryable: false
                });
                files.delete(entry.token);
                void deleteStoredFile(entry.token);
                if (completed) await deliver(completed);
                return;
            }
            if (response.status === 202 || response.status === 409) {
                schedule(entry.token);
                return;
            }
            if (response.status === 404
                && !files.has(entry.token)
                && !await hasStoredFile(entry.token)) {
                const message = 'The interrupted upload did not reach the server. Choose the image again.';
                updatePending(entry.token, { state: 'failed', error: message, retryable: false });
                await notify(controllerFor(entry.ownerKey), 'OnBrowserUploadFailed', message, false);
                removePending(entry.token);
                return;
            }
        } catch {
            // Offline/reconnecting: keep the durable token and browser file, then retry.
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

    async function freshAntiforgeryToken() {
        try {
            const response = await fetch(new URL('browser-uploads/antiforgery-token', document.baseURI), {
                credentials: 'same-origin',
                cache: 'no-store'
            });
            if (response.ok) {
                const body = await response.json();
                if (body && typeof body.token === 'string' && body.token) return body.token;
            }
        } catch {
            // Fall back to the server-rendered token below.
        }
        const antiforgery = document.querySelector(
            '#stash-upload-antiforgery input[name="__RequestVerificationToken"]');
        return antiforgery && antiforgery.value ? antiforgery.value : null;
    }

    async function uploadCore(entry, file) {
        const controller = controllerFor(entry.ownerKey);
        updatePending(entry.token, { state: 'uploading', error: null });
        await notify(controller, 'OnBrowserUploadStarted', file.name || entry.fileName || 'image');

        const antiforgeryToken = await freshAntiforgeryToken();
        if (!antiforgeryToken) {
            const message = 'The upload connection is not ready; the selected image will retry automatically.';
            updatePending(entry.token, { state: 'failed', error: message, retryable: true });
            await notify(controller, 'OnBrowserUploadFailed', message, true);
            schedule(entry.token, 2500);
            return;
        }

        const form = new FormData();
        form.append('__RequestVerificationToken', antiforgeryToken);
        form.append('token', entry.token);
        form.append('kind', entry.kind);
        form.append('multipleItems', String(entry.multipleItems));
        form.append('photo', file, file.name || entry.fileName || 'image');

        try {
            const response = await fetch(new URL('browser-uploads', document.baseURI), {
                method: 'POST',
                body: form,
                credentials: 'same-origin'
            });
            if (response.ok) {
                const result = await response.json();
                const completed = updatePending(entry.token, {
                    state: 'complete', result, error: null, retryable: false
                });
                files.delete(entry.token);
                void deleteStoredFile(entry.token);
                if (completed) await deliver(completed);
                return;
            }
            if (response.status === 409) {
                await poll(entry);
                return;
            }

            let message = 'The image could not be uploaded.';
            let serverRetryable = false;
            try {
                const body = await response.json();
                if (body && typeof body.error === 'string') message = body.error;
                serverRetryable = body && body.retryable === true;
            } catch { /* keep the safe generic message */ }
            const retryable = serverRetryable || response.status >= 500 || response.status === 429;
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

    async function resume(entry) {
        if (entry.state === 'complete' && entry.result) {
            await deliver(entry);
            return;
        }
        let file = files.get(entry.token);
        if (!file && entry.retryable !== false) {
            file = await loadStoredFile(entry.token);
            if (file) files.set(entry.token, file);
        }
        if (file && entry.retryable !== false) {
            await upload(entry, file);
            return;
        }
        if (entry.state === 'uploading' || entry.retryable) await poll(entry);
    }

    async function persistSelection(entry, file) {
        files.set(entry.token, file);
        persistenceCount++;
        try {
            const stored = await storeFile(entry.token, file);
            updatePending(entry.token, { fileStored: stored });
            return stored;
        } finally {
            persistenceCount--;
        }
    }

    async function selected(controller) {
        const selectedFiles = Array.from(controller.input.files || []);
        controller.input.value = '';
        pickerActive = false;
        if (selectedFiles.length === 0) return;

        if (controller.completesClipboardQueue
            && window.stashClipboardImages
            && typeof window.stashClipboardImages.complete === 'function') {
            window.stashClipboardImages.complete();
        }

        // Register every selected file before asynchronous work so a batch cannot lose its
        // later entries. IndexedDB persistence then makes each File survive a full page reload.
        const selections = selectedFiles.map(file => {
            const entry = {
                token: createToken(),
                ownerKey: controller.ownerKey,
                kind: controller.kind,
                multipleItems: controller.multipleItems,
                fileName: file.name || 'image',
                createdAt: new Date().toISOString(),
                state: 'persisting',
                retryable: true,
                fileStored: false
            };
            const entries = readPending();
            entries.push(entry);
            writePending(entries);
            files.set(entry.token, file);
            return { entry, file };
        });

        await Promise.all(selections.map(selection =>
            persistSelection(selection.entry, selection.file)));

        // Keep network uploads sequential for predictable progress and queue ordering. A
        // reload can safely interrupt these fetches because the files now live in IndexedDB.
        for (const selection of selections) {
            const current = readPending().find(entry => entry.token === selection.entry.token);
            if (current) await upload(current, selection.file);
        }
    }

    function markPickerOpened() {
        pickerActive = true;
    }

    function markPickerCancelled() {
        pickerActive = false;
    }

    function register(inputId, ownerKey, kind, multipleItems, completesClipboardQueue, dotNetRef) {
        const input = document.getElementById(inputId);
        if (!(input instanceof HTMLInputElement)) return false;

        const previous = controllers.get(inputId);
        if (previous) {
            previous.input.removeEventListener('click', previous.onClick);
            previous.input.removeEventListener('cancel', previous.onCancel);
            previous.input.removeEventListener('change', previous.onChange);
        }
        const controller = {
            input,
            ownerKey,
            kind,
            multipleItems: !!multipleItems,
            completesClipboardQueue: !!completesClipboardQueue,
            dotNetRef,
            onClick: markPickerOpened,
            onCancel: markPickerCancelled,
            onChange: null
        };
        controller.onChange = () => { void selected(controller); };
        input.addEventListener('click', controller.onClick);
        input.addEventListener('cancel', controller.onCancel);
        input.addEventListener('change', controller.onChange);
        controllers.set(inputId, controller);

        for (const entry of readPending().filter(candidate => candidate.ownerKey === ownerKey)) {
            void resume(entry);
        }
        return true;
    }

    function unregister(inputId) {
        const controller = controllers.get(inputId);
        if (!controller) return;
        controller.input.removeEventListener('click', controller.onClick);
        controller.input.removeEventListener('cancel', controller.onCancel);
        controller.input.removeEventListener('change', controller.onChange);
        controllers.delete(inputId);
    }

    function retry(ownerKey) {
        for (const entry of readPending().filter(candidate => candidate.ownerKey === ownerKey)) {
            if (entry.state === 'failed') {
                entry.retryable = true;
                updatePending(entry.token, entry);
            }
            void resume(entry);
        }
    }

    function memoryOnlyUploadExists() {
        return readPending().some(entry =>
            entry.state !== 'complete'
            && entry.fileStored !== true
            && files.has(entry.token));
    }

    async function beforeCircuitReload() {
        // A native camera can suspend the page longer than Blazor retains its circuit. When
        // it returns, give the input change event time to run and IndexedDB time to commit.
        // If IndexedDB is unavailable, also let the in-memory HTTP upload finish when possible.
        const pickerDeadline = Date.now() + 15000;
        const overallDeadline = Date.now() + 45000;
        while (Date.now() < overallDeadline) {
            if (pickerActive && Date.now() >= pickerDeadline) pickerActive = false;
            if (!pickerActive && persistenceCount === 0 && !memoryOnlyUploadExists()) return;
            await new Promise(resolve => setTimeout(resolve, 100));
        }
    }

    window.addEventListener('online', () => {
        for (const entry of readPending()) void resume(entry);
    });
    document.addEventListener('visibilitychange', () => {
        if (document.visibilityState === 'visible') {
            for (const entry of readPending()) void resume(entry);
        }
    });
    setInterval(() => {
        if (document.visibilityState === 'visible') {
            for (const entry of readPending()) void resume(entry);
        }
    }, 3000);

    void cleanupAbandonedFiles();

    return { register, unregister, retry, beforeCircuitReload };
})();
