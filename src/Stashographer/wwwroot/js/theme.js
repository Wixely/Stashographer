// Runs synchronously in <head> so the browser knows the preferred colour scheme before
// first paint. The cookie lets Blazor prerender the same palette on the next request.
(function () {
    'use strict';

    const storageKey = 'stash-theme';
    const cookieName = 'stash-theme';

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

    function apply(value) {
        document.documentElement.dataset.stashTheme = value;
        document.documentElement.style.colorScheme = value;
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
        prefersDark: () => window.matchMedia?.('(prefers-color-scheme: dark)').matches === true
    };
}());
