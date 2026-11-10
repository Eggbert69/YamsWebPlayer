// Minimal Service Worker – macht die App auf iOS/Android installierbar.
// Kein Caching: alle Requests gehen direkt ans Netz (Live-Daten bleiben aktuell).
self.addEventListener('fetch', e => {
  e.respondWith(fetch(e.request).catch(() =>
    new Response('Offline – bitte mit YAMS-Netzwerk verbinden.', {
      status: 503,
      headers: { 'Content-Type': 'text/plain; charset=utf-8' }
    })
  ));
});
