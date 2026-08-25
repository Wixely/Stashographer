// Runs synchronously in <head> so the browser knows the preferred colour scheme before
// first paint. The cookie lets Blazor prerender the same palette on the next request.
(function () {
    'use strict';

    const storageKey = 'stash-theme';
    const cookieName = 'stash-theme';
    const temporaryDarkPalette = {
        '--mud-palette-primary': '#4CAF82',
        '--mud-palette-primary-text': '#071B12',
        '--mud-palette-secondary': '#9AA5FF',
        '--mud-palette-secondary-text': '#161A36',
        '--mud-palette-info': '#7DA2E6',
        '--mud-palette-info-text': '#0A1A33',
        '--mud-palette-success': '#4CAF82',
        '--mud-palette-success-text': '#071B12',
        '--mud-palette-warning': '#E5A34B',
        '--mud-palette-warning-text': '#271A06',
        '--mud-palette-error': '#EF7F8C',
        '--mud-palette-error-text': '#2A0B10',
        '--mud-palette-text-primary': '#E7ECE8',
        '--mud-palette-text-secondary': '#B4BDB7',
        '--mud-palette-appbar-background': '#18221C',
        '--mud-palette-appbar-text': '#F4F7F5',
        '--mud-palette-background': '#101512',
        '--mud-palette-background-gray': '#151B17',
        '--mud-palette-surface': '#1B211C',
        '--mud-palette-drawer-background': '#171D19',
        '--mud-palette-drawer-text': '#D8DFDA',
        '--mud-palette-drawer-icon': '#AAB4AD',
        '--mud-palette-lines-default': '#354139',
        '--mud-palette-lines-inputs': '#536258',
        '--mud-palette-table-lines': '#354139',
        '--mud-palette-divider': '#354139'
    };

    function normalize(value) {
        return value === 'dark' || value === 'light' ? value : null;
    }

    function readStorage() {
        try {
            return normalize(localStorage.getItem(storageKey));
        } catch {
            return null;
        }
    }

    function readCookie() {
        const prefix = cookieName + '=';
        const part = document.cookie
            .split(';')
            .map(value => value.trim())
            .find(value => value.startsWith(prefix));

        return part ? normalize(decodeURIComponent(part.substring(prefix.length))) : null;
    }

    function writeCookie(value) {
        document.cookie = cookieName + '=' + encodeURIComponent(value)
            + '; Path=/; Max-Age=31536000; SameSite=Lax';
    }

    function clearTemporaryPalette() {
        for (const name of Object.keys(temporaryDarkPalette)) {
            document.documentElement.style.removeProperty(name);
        }
    }

    function apply(value) {
        document.documentElement.dataset.stashTheme = value;
        document.documentElement.style.colorScheme = value;
        if (value === 'dark') {
            for (const [name, colour] of Object.entries(temporaryDarkPalette)) {
                document.documentElement.style.setProperty(name, colour);
            }
        } else {
            clearTemporaryPalette();
        }
    }

    function set(value) {
        const normalized = normalize(value) || 'light';

        try {
            localStorage.setItem(storageKey, normalized);
        } catch {
            // Storage can be unavailable in privacy modes; the cookie still works.
        }

        writeCookie(normalized);
        apply(normalized);
    }

    const preferred = readStorage()
        || readCookie()
        || (window.matchMedia?.('(prefers-color-scheme: dark)').matches ? 'dark' : 'light');

    set(preferred);

    window.stashTheme = {
        get: () => readStorage() || readCookie(),
        set,
        ready: clearTemporaryPalette,
        prefersDark: () => window.matchMedia?.('(prefers-color-scheme: dark)').matches === true
    };
}());
