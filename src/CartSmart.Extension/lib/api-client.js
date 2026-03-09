/**
 * CartSmart Extension – API Client
 *
 * Handles all communication with the CartSmart API:
 *   1. Authenticate via the standard CartSmart login endpoint
 *   2. Fetch scrape-configured store configs (URL patterns + selectors)
 *   3. Submit extracted price reports
 *
 * The API base URL defaults to the production domain (see config.js).
 * Users only need to log in with their CartSmart account — no manual
 * token/key management.
 *
 * Depends on: lib/config.js (loaded first) for DEFAULT_API_BASE.
 */

const STORAGE_KEYS = {
  API_BASE: "cartsmart_api_base",       // override for local dev only
  API_TOKEN: "cartsmart_api_token",     // JWT from /api/auth/login
  USER: "cartsmart_user",              // { id, email, displayName }
  STORES: "cartsmart_stores",
  STORES_FETCHED_AT: "cartsmart_stores_fetched_at",
};

const STORE_CACHE_TTL_MS = 60 * 60 * 1000; // 1 hour

// ─── Storage helpers ──────────────────────────────────────────────────────

function storageGet(key) {
  return new Promise((resolve) => {
    chrome.storage.local.get(key, (result) => resolve(result[key]));
  });
}

function storageSet(obj) {
  return new Promise((resolve) => {
    chrome.storage.local.set(obj, resolve);
  });
}

function storageRemove(keys) {
  return new Promise((resolve) => {
    chrome.storage.local.remove(keys, resolve);
  });
}

// ─── Config ───────────────────────────────────────────────────────────────

/**
 * Get the API base URL.
 * Uses a custom override if set (for development), otherwise the production default.
 */
async function getApiBase() {
  const override = await storageGet(STORAGE_KEYS.API_BASE);
  const base = override || DEFAULT_API_BASE;
  return base.replace(/\/+$/, "");
}

/**
 * Get the stored JWT.
 */
async function getApiToken() {
  return (await storageGet(STORAGE_KEYS.API_TOKEN)) || "";
}

/**
 * Get the stored user profile.
 */
async function getUser() {
  return (await storageGet(STORAGE_KEYS.USER)) || null;
}

/**
 * Check if the user is logged in (has a token).
 */
async function isLoggedIn() {
  const token = await getApiToken();
  return !!token;
}

// ─── Authentication ───────────────────────────────────────────────────────

/**
 * Log in with CartSmart credentials.
 * Calls the existing /api/auth/login endpoint and stores the JWT.
 *
 * @param {string} email
 * @param {string} password
 * @returns {{ ok: boolean, message?: string, user?: object }}
 */
async function login(email, password) {
  const apiBase = await getApiBase();

  try {
    const res = await fetch(`${apiBase}/api/auth/login`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ emailAddress: email, password }),
    });

    if (!res.ok) {
      const body = await res.json().catch(() => ({}));
      return {
        ok: false,
        message: body.message || `Login failed (${res.status})`,
      };
    }

    const data = await res.json();

    if (!data.success || !data.token) {
      return {
        ok: false,
        message: data.message || "Login failed – no token received.",
      };
    }

    // Store the JWT and basic user info
    await storageSet({
      [STORAGE_KEYS.API_TOKEN]: data.token,
      [STORAGE_KEYS.USER]: data.user
        ? {
            id: data.user.id,
            email: data.user.email,
            displayName: data.user.displayName || data.user.userName,
          }
        : { email },
    });

    return { ok: true, user: data.user };
  } catch (err) {
    console.error("[CartSmart] Login error:", err);
    return { ok: false, message: "Network error – could not connect to CartSmart." };
  }
}

/**
 * Log out – clear stored token and user info.
 */
async function logout() {
  await storageRemove([
    STORAGE_KEYS.API_TOKEN,
    STORAGE_KEYS.USER,
    STORAGE_KEYS.STORES,
    STORAGE_KEYS.STORES_FETCHED_AT,
  ]);
}

// ─── Store configs ────────────────────────────────────────────────────────

/**
 * Fetch the list of scrape-configured stores from the CartSmart API.
 * Each store entry includes: id, name, url, scrapeConfig (with price_selectors).
 * The API returns stores with scrape_mode_id > 0 (All or BrowserOnly).
 */
async function fetchStoreConfigs(forceRefresh = false) {
  if (!forceRefresh) {
    const cached = await storageGet(STORAGE_KEYS.STORES);
    const fetchedAt = await storageGet(STORAGE_KEYS.STORES_FETCHED_AT);
    if (cached && fetchedAt && Date.now() - fetchedAt < STORE_CACHE_TTL_MS) {
      return cached;
    }
  }

  const apiBase = await getApiBase();
  const token = await getApiToken();

  try {
    const res = await fetch(`${apiBase}/api/extension/stores`, {
      cache: forceRefresh ? "no-cache" : "default",
      headers: {
        "Content-Type": "application/json",
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
      },
    });

    if (!res.ok) {
      console.error("[CartSmart] Failed to fetch store configs:", res.status);
      return (await storageGet(STORAGE_KEYS.STORES)) || [];
    }

    const stores = await res.json();
    await storageSet({
      [STORAGE_KEYS.STORES]: stores,
      [STORAGE_KEYS.STORES_FETCHED_AT]: Date.now(),
    });
    return stores;
  } catch (err) {
    console.error("[CartSmart] Error fetching store configs:", err);
    return (await storageGet(STORAGE_KEYS.STORES)) || [];
  }
}

// ─── Price reports ────────────────────────────────────────────────────────

/**
 * Submit an extracted price report to the CartSmart API.
 * Requires the user to be logged in (JWT is sent as Bearer token).
 */
async function submitPriceReport(report) {
  const apiBase = await getApiBase();
  const token = await getApiToken();

  if (!token) {
    console.warn("[CartSmart] Not logged in; price report not sent");
    return { ok: false, reason: "not_logged_in" };
  }

  try {
    const endpoint = `${apiBase}/api/extension/price-report`;
    console.log("[CartSmart] Submitting price report to:", endpoint, report);
    const res = await fetch(endpoint, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        Authorization: `Bearer ${token}`,
      },
      body: JSON.stringify(report),
    });

    if (res.status === 401) {
      console.warn("[CartSmart] Token expired – clearing session");
      await logout();
      return { ok: false, reason: "token_expired" };
    }

    if (!res.ok) {
      const text = await res.text();
      console.error("[CartSmart] Price report submission failed:", res.status, text);
      return { ok: false, reason: "api_error", status: res.status };
    }

    const data = await res.json();
    return { ok: true, data };
  } catch (err) {
    console.error("[CartSmart] Error submitting price report:", err);
    return { ok: false, reason: "network_error" };
  }
}

// ─── URL matching ─────────────────────────────────────────────────────────

/**
 * Find the store config that matches a given URL.
 * Matches if the page URL's hostname matches the store's hostname.
 * Store URLs in the database may be bare domains (e.g. "amazon.com")
 * so we normalise them with a protocol before parsing.
 */
function findMatchingStore(pageUrl, stores) {
  if (!stores || !pageUrl) return null;
  try {
    const pageHostname = new URL(pageUrl).hostname.replace(/^www\./, "");
    for (const store of stores) {
      if (!store.url) continue;
      try {
        // Store URLs may lack a protocol — add one so new URL() can parse them
        const raw = store.url.includes("://") ? store.url : `https://${store.url}`;
        const storeHostname = new URL(raw).hostname.replace(/^www\./, "");
        if (
          pageHostname === storeHostname ||
          pageHostname.endsWith("." + storeHostname)
        ) {
          return store;
        }
      } catch {
        continue;
      }
    }
  } catch {
    return null;
  }
  return null;
}
