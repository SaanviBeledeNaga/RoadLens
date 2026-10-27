'use strict';
(() => {
  const configured = globalThis.ROADLENS_CONFIG?.apiBaseUrl || '';
  const base = configured.replace(/\/+$/, '');
  if (base && !/^https:\/\//.test(base)) throw Error('The deployed API must use HTTPS.');
  let clientKey;
  try {
    clientKey = localStorage.getItem('roadlens-client-key');
    if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(clientKey || '')) {
      clientKey = crypto.randomUUID();
      localStorage.setItem('roadlens-client-key', clientKey);
    }
  } catch { clientKey = crypto.randomUUID(); }
  function url(path) {
    if (/^https?:\/\//.test(path)) return path;
    return base + (path.startsWith('/') ? path : '/' + path);
  }
  function apiFetch(path, options = {}) {
    const headers = new Headers(options.headers);
    headers.set('X-RoadLens-Client', clientKey);
    return fetch(url(path), {...options, headers});
  }
  globalThis.RoadLensApi = Object.freeze({url, fetch:apiFetch, clientKey});
})();
