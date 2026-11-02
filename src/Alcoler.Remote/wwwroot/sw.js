// Alcoler Remote shell cache.
// Strategy: network-first for /api (never serve stale API data), cache-first
// with background update for same-origin shell assets. WebSockets are never
// intercepted by the fetch handler, so the stream is untouched.

const VERSION = 'alcoler-shell-v4';

const SHELL = [
  './',
  './index.html',
  './styles.css',
  './app.js',
  './session-list.js',
  './composer.js',
  './api.js',
  './ws.js',
  './render.js',
  './markdown.js',
  './util.js',
  './manifest.webmanifest',
  './icons/icon-192.png',
  './icons/icon-512.png',
  './icons/icon-maskable-192.png',
  './icons/icon-maskable-512.png',
  './icons/apple-touch-icon.png',
  './icons/favicon.svg',
  './fonts/JetBrainsMono-Regular.ttf',
  './fonts/JetBrainsMono-Bold.ttf',
];

self.addEventListener('install', event => {
  event.waitUntil((async () => {
    const cache = await caches.open(VERSION);
    await cache.addAll(SHELL);
    await self.skipWaiting();
  })());
});

self.addEventListener('activate', event => {
  event.waitUntil((async () => {
    const keys = await caches.keys();
    await Promise.all(keys.filter(k => k !== VERSION).map(k => caches.delete(k)));
    await self.clients.claim();
  })());
});

self.addEventListener('fetch', event => {
  const req = event.request;
  if (req.method !== 'GET') return;
  const url = new URL(req.url);
  if (url.origin !== self.location.origin) return;

  if (url.pathname === '/api' || url.pathname.startsWith('/api/')) {
    event.respondWith(fetch(req).catch(() => new Response(
      JSON.stringify({ error: 'offline' }),
      { status: 503, headers: { 'Content-Type': 'application/json' } }
    )));
    return;
  }

  event.respondWith((async () => {
    const cache = await caches.open(VERSION);
    const cached = await cache.match(req, { ignoreSearch: true });
    // The network promise runs regardless, so a cached hit returns
    // immediately while the cache refreshes in the background.
    const network = fetch(req).then(res => {
      if (res && res.ok) cache.put(req, res.clone());
      return res;
    }).catch(() => null);
    if (cached) return cached;
    const res = await network;
    if (res) return res;
    const fallback = await cache.match('./index.html');
    return fallback || new Response('Offline', { status: 503 });
  })());
});
