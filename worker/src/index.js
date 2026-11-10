// Chest Counter stats proxy.
// The app calls this instead of Fortnite-API.com when the player hasn't entered their own key,
// so the shared key stays a Cloudflare secret (FORTNITE_API_KEY) and never ships in the app.
// Only the one season stats lookup is allowed.

const UPSTREAM = "https://fortnite-api.com/v2/stats/br/v2";
const CACHE_MS = 60_000; // one lookup per name per minute is plenty (the app checks once a minute)
const PLATFORMS = new Set(["epic", "psn", "xbl"]);
const cache = new Map(); // per-instance; workers.dev has no shared Cache API

function json(status, body) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json; charset=utf-8" },
  });
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (request.method !== "GET" || url.pathname !== "/v2/stats/br/v2")
      return json(404, { status: 404, error: "Not found" });

    const name = (url.searchParams.get("name") || "").trim();
    const accountType = (url.searchParams.get("accountType") || "epic").toLowerCase();
    if (name.length < 1 || name.length > 32 || !PLATFORMS.has(accountType))
      return json(400, { status: 400, error: "Bad name or platform" });
    if (!env.FORTNITE_API_KEY)
      return json(503, { status: 503, error: "Shared key not set up" });

    const cacheKey = `${accountType}:${name.toLowerCase()}`;
    const hit = cache.get(cacheKey);
    if (hit && Date.now() - hit.at < CACHE_MS)
      return new Response(hit.body, { status: hit.status, headers: { "content-type": "application/json; charset=utf-8", "x-cache": "hit" } });

    const upstream = `${UPSTREAM}?name=${encodeURIComponent(name)}&accountType=${accountType}&timeWindow=season`;
    let res;
    try {
      res = await fetch(upstream, { headers: { Authorization: env.FORTNITE_API_KEY } });
    } catch {
      return json(502, { status: 502, error: "Couldn't reach Fortnite-API.com" });
    }
    const body = await res.text();

    // Never tell callers the shared key itself was rejected in Fortnite-API's words.
    if (res.status === 401) return json(503, { status: 503, error: "Shared key not working" });

    if (res.status === 200 || res.status === 403 || res.status === 404) {
      if (cache.size > 5000) cache.clear();
      cache.set(cacheKey, { at: Date.now(), status: res.status, body });
    }
    return new Response(body, { status: res.status, headers: { "content-type": "application/json; charset=utf-8" } });
  },
};
