// Runtime support for hosting Memtly behind a reverse proxy under a sub-path
// (e.g. https://host/gallery1). The server sets window.MemtlyPathBase to the
// request PathBase ("/gallery1", or "" when hosted at the root). We use it to
// keep client-side URLs inside the sub-path:
//   - $.ajax / $.get / $.post ... (via a global ajaxPrefilter)
//   - window.fetch (via a wrapper)
//   - explicit navigations / route matching (via resolveUrl / stripBasePath)
//
// Everything is a no-op when MemtlyPathBase is empty, so default root hosting is
// unchanged.

export const basePath = (window.MemtlyPathBase || '').replace(/\/+$/, '');

// Prefix a root-relative URL ("/Foo/Bar") with the base path. Absolute URLs
// (http://, https://, //cdn), non-strings, and already-prefixed URLs are left
// as-is so this is safe to apply broadly.
export function resolveUrl(url) {
    if (!basePath || typeof url !== 'string') return url;
    if (!url.startsWith('/') || url.startsWith('//')) return url;
    if (url === basePath || url.startsWith(basePath + '/')) return url;
    return basePath + url;
}

// Strip the base path from a pathname so client-side route matching (which is
// written against root-relative paths like "/gallery") keeps working.
export function stripBasePath(pathname) {
    if (!basePath) return pathname;
    const lower = pathname.toLowerCase();
    if (lower === basePath.toLowerCase()) return '/';
    if (lower.startsWith(basePath.toLowerCase() + '/')) return pathname.slice(basePath.length);
    return pathname;
}

let installed = false;

// Install the global URL interceptors. Call once, before any AJAX/fetch runs.
export function initBasePath() {
    if (installed || !basePath) return;
    installed = true;

    if (window.jQuery) {
        window.jQuery.ajaxPrefilter((options) => {
            options.url = resolveUrl(options.url);
        });
    }

    const originalFetch = window.fetch;
    if (typeof originalFetch === 'function') {
        window.fetch = function (input, init) {
            if (typeof input === 'string') {
                input = resolveUrl(input);
            } else if (input && typeof input.url === 'string') {
                input = new Request(resolveUrl(input.url), input);
            }
            return originalFetch.call(this, input, init);
        };
    }
}
