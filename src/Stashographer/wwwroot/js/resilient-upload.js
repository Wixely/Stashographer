// Stashographer adapter for DnaX.Uploads. DNAX owns file capture persistence, chunking,
// resume, retry and transport receipts. This adapter maps a completed transport receipt to
// Stashographer's existing idempotent image/queue operation and treats .NET callbacks as hints.
window.stashResilientUpload = (() => {
    'use strict';

    const assignmentStorageKey = 'stashographer.dnax-upload-assignments.v1';
    const endpoint = 'dnax-uploads';
    const controllers = new Map();
    const profiles = new Map();
    const completing = new Set();
    let activeClaim = null;
    let dnaxModulePromise;

    function dnaxModule() {
        dnaxModulePromise ??= import(new URL('_content/DnaX.Uploads/uploads.js', document.baseURI).href);
        return dnaxModulePromise;
    }

    function readAssignments() {
        try {
            const parsed = JSON.parse(localStorage.getItem(assignmentStorageKey) || '{}');
            return parsed && typeof parsed === 'object' ? parsed : {};
        } catch {
            return {};
        }
    }

    function writeAssignments(assignments) {
        try {
            if (Object.keys(assignments).length === 0)
                localStorage.removeItem(assignmentStorageKey);
            else
                localStorage.setItem(assignmentStorageKey, JSON.stringify(assignments));
        } catch {
            // DNAX still retains transport metadata and bytes. The current document can
            // finish delivery, but a full reload may require the user to select again.
        }
    }

    function updateAssignment(id, update) {
        const assignments = readAssignments();
        if (!assignments[id]) return null;
        assignments[id] = { ...assignments[id], ...update };
        writeAssignments(assignments);
        return assignments[id];
    }

    function removeAssignment(id) {
        const assignments = readAssignments();
        delete assignments[id];
        writeAssignments(assignments);
    }

    function controllerForOwner(ownerKey) {
        return [...controllers.values()].find(controller => controller.ownerKey === ownerKey);
    }

    async function notify(controller, method, ...args) {
        if (!controller?.dotNetRef) return false;
        try {
            await controller.dotNetRef.invokeMethodAsync(method, ...args);
            return true;
        } catch {
            return false;
        }
    }

    function captureAssignments(profile, snapshot) {
        if (!activeClaim || activeClaim.profile !== profile) return;
        const assignments = readAssignments();
        let changed = false;
        for (const job of snapshot.jobs) {
            if (activeClaim.before.has(job.id) || assignments[job.id]) continue;
            assignments[job.id] = {
                ownerKey: activeClaim.controller.ownerKey,
                kind: activeClaim.controller.kind,
                multipleItems: activeClaim.controller.multipleItems,
                fileName: job.name,
                createdAt: new Date().toISOString()
            };
            changed = true;
        }
        if (changed) writeAssignments(assignments);
    }

    async function antiforgeryToken() {
        const response = await fetch(new URL('browser-uploads/antiforgery-token', document.baseURI), {
            credentials: 'same-origin',
            cache: 'no-store'
        });
        if (!response.ok) throw new Error('Upload authorization is unavailable.');
        const body = await response.json();
        if (!body?.token) throw new Error('Upload authorization is unavailable.');
        return body.token;
    }

    async function completeBusiness(job, assignment, profileState) {
        if (completing.has(job.id)) return;
        completing.add(job.id);
        try {
            const token = await antiforgeryToken();
            const url = new URL(`browser-uploads/${encodeURIComponent(job.id)}/complete`, document.baseURI);
            url.searchParams.set('multipleItems', String(assignment.multipleItems));
            const response = await fetch(url, {
                method: 'POST',
                credentials: 'same-origin',
                cache: 'no-store',
                headers: { 'X-DnaX-Antiforgery': token }
            });
            if (!response.ok) {
                let message = 'The uploaded image could not be saved.';
                let retryable = response.status === 409 || response.status === 429 || response.status >= 500;
                try {
                    const body = await response.json();
                    if (typeof body?.error === 'string') message = body.error;
                    retryable ||= body?.retryable === true;
                } catch { /* retain safe message */ }
                throw Object.assign(new Error(message), { retryable });
            }
            const result = await response.json();
            assignment = updateAssignment(job.id, {
                result,
                businessError: null,
                retryable: false
            }) || assignment;
            await deliver(job, assignment, profileState);
        } catch (error) {
            const retryable = error.retryable !== false;
            updateAssignment(job.id, {
                businessError: error.message || 'The uploaded image could not be saved.',
                retryable
            });
            await notify(
                controllerForOwner(assignment.ownerKey),
                'OnBrowserUploadFailed',
                error.message || 'The uploaded image could not be saved.',
                retryable);
            if (retryable)
                setTimeout(() => {
                    updateAssignment(job.id, { businessError: null });
                    reconcile(profileState);
                }, 2500);
        } finally {
            completing.delete(job.id);
        }
    }

    async function deliver(job, assignment, profileState) {
        if (!assignment.result) return;
        const delivered = await notify(
            controllerForOwner(assignment.ownerKey),
            'OnBrowserUploadCompleted',
            assignment.result);
        if (!delivered) return;
        removeAssignment(job.id);
        try {
            await profileState.actions?.dismiss(job.id);
        } catch {
            // A stale DNAX receipt is harmless; Stashographer's operation is idempotent.
        }
    }

    function reconcile(profileState) {
        if (!profileState?.snapshot?.jobs) return;
        const assignments = readAssignments();
        for (const job of profileState.snapshot.jobs) {
            const assignment = assignments[job.id];
            if (!assignment) continue;
            if (assignment.result) {
                void deliver(job, assignment, profileState);
                continue;
            }
            if (job.state === 'complete' && !assignment.businessError)
                void completeBusiness(job, assignment, profileState);
            else if ((job.state === 'failed' || job.state === 'reselect')
                     && assignment.lastTransportState !== job.state) {
                updateAssignment(job.id, { lastTransportState: job.state });
                void notify(
                    controllerForOwner(assignment.ownerKey),
                    'OnBrowserUploadFailed',
                    job.error || (job.state === 'reselect'
                        ? 'Select the original image again to resume the upload.'
                        : 'The image upload failed.'),
                    true);
            }
        }
    }

    async function selected(controller) {
        const files = Array.from(controller.input.files || []);
        controller.input.value = '';
        controller.actions?.setPickerOpen(false);
        if (files.length === 0) return;

        if (controller.completesClipboardQueue
            && window.stashClipboardImages
            && typeof window.stashClipboardImages.complete === 'function')
            window.stashClipboardImages.complete();

        for (const file of files)
            await notify(controller, 'OnBrowserUploadStarted', file.name || 'image');

        const before = new Set(controller.profileState.snapshot?.jobs.map(job => job.id) || []);
        activeClaim = { profile: controller.profile, controller, before };
        let adding;
        try {
            // DNAX publishes the new jobs synchronously before its first persistence await,
            // allowing this adapter to bind them to the initiating Stashographer control.
            adding = controller.actions.addFiles(files);
        } finally {
            activeClaim = null;
        }
        try {
            await adding;
        } catch (error) {
            await notify(controller, 'OnBrowserUploadFailed', error.message || 'The image upload failed.', true);
        }
    }

    async function register(
        inputId,
        ownerKey,
        kind,
        multipleItems,
        completesClipboardQueue,
        host,
        dotNetRef) {
        const input = document.getElementById(inputId);
        if (!(input instanceof HTMLInputElement) || !(host instanceof HTMLElement)) return false;

        unregister(inputId);
        const profile = kind;
        const controller = {
            inputId,
            input,
            host,
            ownerKey,
            kind,
            profile,
            multipleItems: !!multipleItems,
            completesClipboardQueue: !!completesClipboardQueue,
            dotNetRef,
            actions: null,
            profileState: null
        };
        controller.onClick = () => controller.actions?.setPickerOpen(true);
        controller.onCancel = () => controller.actions?.setPickerOpen(false);
        controller.onChange = () => { void selected(controller); };
        input.addEventListener('click', controller.onClick);
        input.addEventListener('cancel', controller.onCancel);
        input.addEventListener('change', controller.onChange);
        controllers.set(inputId, controller);

        try {
            const module = await dnaxModule();
            let profileState = profiles.get(profile);
            if (!profileState) {
                profileState = { profile, snapshot: null, actions: null };
                profiles.set(profile, profileState);
            }
            controller.profileState = profileState;
            await module.mountCustom(host, endpoint, profile, (snapshot, actions) => {
                profileState.snapshot = snapshot;
                profileState.actions = actions;
                controller.actions = actions;
                captureAssignments(profile, snapshot);
                reconcile(profileState);
            });
            return true;
        } catch (error) {
            await notify(controller, 'OnBrowserUploadFailed', error.message || 'Uploads are unavailable.', true);
            unregister(inputId);
            return false;
        }
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
        const assignments = readAssignments();
        for (const profileState of profiles.values()) {
            for (const job of profileState.snapshot?.jobs || []) {
                const assignment = assignments[job.id];
                if (assignment?.ownerKey !== ownerKey) continue;
                if (assignment.businessError) {
                    updateAssignment(job.id, { businessError: null, retryable: null });
                    void completeBusiness(job, assignment, profileState);
                } else if (job.state === 'failed' || job.state === 'paused' || job.state === 'reselect') {
                    void profileState.actions?.resume(job.id);
                }
            }
        }
    }

    async function beforeCircuitReload() {
        try {
            const module = await dnaxModule();
            const deadline = Date.now() + 15000;
            while (!module.canReloadSafely() && Date.now() < deadline)
                await new Promise(resolve => setTimeout(resolve, 100));
        } catch {
            // Reload remains the only way to recover a rejected Blazor circuit. DNAX will
            // reconcile persisted transport state when the page mounts again.
        }
    }

    return { register, unregister, retry, beforeCircuitReload };
})();
