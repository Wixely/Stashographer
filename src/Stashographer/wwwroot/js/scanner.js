// Stashographer browser interop: theme persistence + barcode scanning.
// Hand-written, dependency-free. Uses the native BarcodeDetector API where the browser
// provides it (Chrome/Edge/Android). On browsers without it (notably iOS Safari) scanning
// degrades gracefully and the UI falls back to manual entry.

window.stashTheme = {
    get: () => localStorage.getItem('stash-theme'),
    set: (value) => localStorage.setItem('stash-theme', value),
    prefersDark: () => window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches
};

// Clipboard image intake for the Scan page. Only image/file clipboard entries are handled;
// ordinary pasted text (including barcodes) continues to the focused control unchanged.
window.stashClipboardImages = (() => {
    let targetInputId = null;
    let active = false;
    let sequence = 0;
    const pending = [];

    function extensionFor(contentType) {
        switch ((contentType || '').toLowerCase()) {
            case 'image/jpeg': return 'jpg';
            case 'image/gif': return 'gif';
            case 'image/webp': return 'webp';
            default: return 'png';
        }
    }

    function onPaste(event) {
        if (!targetInputId || !event.clipboardData) return;

        const clipboardItem = Array.from(event.clipboardData.items || [])
            .find(item => item.kind === 'file' && item.type.startsWith('image/'));
        if (!clipboardItem) return;

        const pasted = clipboardItem.getAsFile();
        if (!pasted) return;

        // Clipboard providers commonly reuse "image.png" and its timestamp. Always clone
        // with a unique identity so repeated pastes cannot be coalesced as the same file.
        const fileName =
            `clipboard-${Date.now()}-${++sequence}.${extensionFor(pasted.type)}`;
        pending.push(new File([pasted], fileName, {
            type: pasted.type || 'image/png',
            lastModified: Date.now()
        }));

        event.preventDefault();
        pump();
    }

    function pump() {
        if (active || pending.length === 0 || !targetInputId) return;
        const input = document.getElementById(targetInputId);
        if (!(input instanceof HTMLInputElement)) {
            setTimeout(pump, 50);
            return;
        }

        const file = pending.shift();
        const transfer = new DataTransfer();
        transfer.items.add(file);
        input.value = '';
        input.files = transfer.files;
        active = true;
        input.dispatchEvent(new Event('change', { bubbles: true }));
    }

    function enable(inputId) {
        targetInputId = inputId;
        active = false;
        pending.length = 0;
        document.removeEventListener('paste', onPaste);
        document.addEventListener('paste', onPaste);
    }

    function complete() {
        active = false;
        setTimeout(pump, 0);
    }

    function disable() {
        document.removeEventListener('paste', onPaste);
        targetInputId = null;
        active = false;
        pending.length = 0;
    }

    return { enable, complete, disable };
})();

// Broken-image fallback: any <img> that fails to load (dead lookup cover, missing stored
// file) is swapped for a neutral inline-SVG placeholder instead of the browser's broken
// icon. Capture phase because the error event does not bubble. data-fallback guards
// against loops if the placeholder itself ever failed.
(() => {
    const placeholder = 'data:image/svg+xml,' + encodeURIComponent(
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 96 96'>" +
        "<rect width='96' height='96' rx='8' fill='#9e9e9e' fill-opacity='0.15'/>" +
        "<circle cx='36' cy='36' r='7' fill='#9e9e9e' fill-opacity='0.6'/>" +
        "<path d='M22 70l16-18 11 12 9-10 16 16z' fill='#9e9e9e' fill-opacity='0.6'/>" +
        "</svg>");
    document.addEventListener('error', e => {
        const img = e.target;
        if (img instanceof HTMLImageElement && !img.dataset.fallback) {
            img.dataset.fallback = '1';
            img.src = placeholder;
        }
    }, true);
})();

window.stashScanner = (() => {
    let stream = null;
    let detector = null;
    let rafId = null;
    let video = null;

    const unavailableReason = () => {
        if (!window.isSecureContext) return 'insecure';
        if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) return 'camera-api';
        if (!('BarcodeDetector' in window)) return 'barcode-api';
        return null;
    };

    const supported = () => unavailableReason() === null;

    async function start(videoEl, dotNetRef) {
        const unavailable = unavailableReason();
        if (unavailable) {
            return { ok: false, reason: unavailable };
        }
        try {
            detector = new BarcodeDetector({
                formats: ['ean_13', 'ean_8', 'upc_a', 'upc_e', 'code_128', 'qr_code']
            });
            video = videoEl;
            stream = await navigator.mediaDevices.getUserMedia({
                video: { facingMode: 'environment' }
            });
            video.srcObject = stream;
            await video.play();

            let lastCode = null;
            let lastAt = 0;
            const scan = async () => {
                if (!detector || !video) return;
                try {
                    const codes = await detector.detect(video);
                    if (codes && codes.length > 0) {
                        const value = codes[0].rawValue;
                        const now = Date.now();
                        // Debounce repeat reads of the same code within 3s.
                        if (value && (value !== lastCode || now - lastAt > 3000)) {
                            lastCode = value;
                            lastAt = now;
                            await dotNetRef.invokeMethodAsync('OnCodeScanned', value);
                        }
                    }
                } catch (e) {
                    // transient detect failures are ignored; keep scanning
                }
                rafId = requestAnimationFrame(scan);
            };
            rafId = requestAnimationFrame(scan);
            return { ok: true };
        } catch (err) {
            stop();
            return { ok: false, reason: (err && err.name) || 'error' };
        }
    }

    function stop() {
        if (rafId) { cancelAnimationFrame(rafId); rafId = null; }
        if (stream) { stream.getTracks().forEach(t => t.stop()); stream = null; }
        if (video) { video.srcObject = null; video = null; }
        detector = null;
    }

    return { start, stop, supported };
})();
