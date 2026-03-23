/**
 * CartSmart Extension – Background Service Worker (Manifest V3)
 *
 * Responsibilities:
 *   1. Periodically refresh scrape-enabled store configs from the API
 *      (stores with scrape_mode_id = 1 "All" or 2 "BrowserOnly")
 *   2. Listen for tab navigation events; when the URL matches a store,
 *      tell the content script to extract the price
 *   3. Receive extraction results from the content script and submit
 *      them to the API
 *   4. Maintain badge / icon state
 */

importScripts("lib/config.js", "lib/api-client.js");

// ─── Constants ────────────────────────────────────────────────────────────
const ALARM_REFRESH_STORES = "refresh-stores";
const REFRESH_INTERVAL_MIN = 60; // re-sync store configs every hour

// ─── State ────────────────────────────────────────────────────────────────
let storeConfigs = [];
let recentReports = []; // last N reports for popup display
const MAX_RECENT = 50;
const testTabs = new Set(); // tabs opened for admin test-scrape (skip normal flow)

// ─── No-match URL cache ──────────────────────────────────────────────────
// Tracks URLs the server says have no matching deal_products, so we don't
// keep sending useless reports for every page on a tracked store.
const NO_MATCH_CACHE_KEY = "cartsmart_no_match_urls";
const NO_MATCH_TTL_MS = 24 * 60 * 60 * 1000; // 24 hours
const NO_MATCH_MAX_ENTRIES = 1000;

function normalizeUrlKey(url) {
  try {
    const u = new URL(url);
    return (u.hostname.replace(/^www\./, "") + u.pathname).toLowerCase().replace(/\/+$/, "");
  } catch {
    return url.toLowerCase();
  }
}

async function isNoMatchUrl(url) {
  const cache = (await storageGet(NO_MATCH_CACHE_KEY)) || {};
  const key = normalizeUrlKey(url);
  const ts = cache[key];
  if (!ts) return false;
  if (Date.now() - ts > NO_MATCH_TTL_MS) {
    delete cache[key];
    await storageSet({ [NO_MATCH_CACHE_KEY]: cache });
    return false;
  }
  return true;
}

async function markNoMatchUrl(url) {
  const cache = (await storageGet(NO_MATCH_CACHE_KEY)) || {};
  const key = normalizeUrlKey(url);
  cache[key] = Date.now();
  const entries = Object.entries(cache);
  if (entries.length > NO_MATCH_MAX_ENTRIES) {
    entries.sort((a, b) => a[1] - b[1]);
    await storageSet({ [NO_MATCH_CACHE_KEY]: Object.fromEntries(entries.slice(-NO_MATCH_MAX_ENTRIES)) });
  } else {
    await storageSet({ [NO_MATCH_CACHE_KEY]: cache });
  }
}

/**
 * MV3 service workers are terminated after ~30s of inactivity.
 * When Chrome restarts the worker for a new event, in-memory state is lost.
 * This function restores storeConfigs from chrome.storage.local if needed.
 */
async function ensureStoresLoaded() {
  if (storeConfigs.length > 0) return;
  const cached = await storageGet("cartsmart_stores");
  if (cached && cached.length > 0) {
    storeConfigs = cached;
    console.log(`[CartSmart] Restored ${storeConfigs.length} store(s) from storage`);
  } else {
    // Nothing cached — do a fresh fetch
    await refreshStores();
  }
  // Also restore recent reports
  if (recentReports.length === 0) {
    const saved = await storageGet("cartsmart_recent_reports");
    if (saved) recentReports = saved;
  }
}

// ─── Lifecycle ────────────────────────────────────────────────────────────

chrome.runtime.onInstalled.addListener(async () => {
  console.log("[CartSmart] Extension installed / updated");
  await refreshStores();
  chrome.alarms.create(ALARM_REFRESH_STORES, { periodInMinutes: REFRESH_INTERVAL_MIN });
});

chrome.runtime.onStartup.addListener(async () => {
  await refreshStores();
  chrome.alarms.create(ALARM_REFRESH_STORES, { periodInMinutes: REFRESH_INTERVAL_MIN });
});

chrome.alarms.onAlarm.addListener(async (alarm) => {
  if (alarm.name === ALARM_REFRESH_STORES) {
    await refreshStores();
  }
});

// ─── Helpers: ensure content script is injected ──────────────────────────

/**
 * Send a message to a tab's content script. If the content script isn't
 * loaded yet ("Receiving end does not exist"), inject it programmatically
 * and retry once.
 */
async function sendToContentScript(tabId, message) {
  try {
    return await chrome.tabs.sendMessage(tabId, message);
  } catch (err) {
    if (
      err.message &&
      err.message.includes("Receiving end does not exist")
    ) {
      console.log("[CartSmart] Content script not present, injecting...");
      await chrome.scripting.executeScript({
        target: { tabId },
        files: ["content.js"],
      });
      // Small delay to let the content script set up its listener
      await new Promise((r) => setTimeout(r, 200));
      return await chrome.tabs.sendMessage(tabId, message);
    }
    throw err;
  }
}

// ─── Tab navigation listener ──────────────────────────────────────────────

// Default price selectors matching the server-side GenericHtmlScraper
const DEFAULT_PRICE_SELECTORS = [
  "meta[itemprop=price]",
  "meta[property='product:price:amount']",
  "*[itemprop='price']",
  "span[class*='price']",
  "div[class*='price']",
  "span[id*='price']",
  "div[id*='price']",
  "span.text_sale",
  "span[class*='text_sale']",
  "span[class*='price-lg']",
];

chrome.tabs.onUpdated.addListener(async (tabId, changeInfo, tab) => {
  if (testTabs.has(tabId)) return; // skip tabs opened for admin test-scrape
  if (changeInfo.status !== "complete" || !tab.url) return;

  await ensureStoresLoaded();
  const store = findMatchingStore(tab.url, storeConfigs);
  if (!store) {
    setBadge(tabId, ""); // no match, clear badge
    return;
  }

  // Parse the price_selectors from scrapeConfig
  let customSelectors = [];
  if (store.scrapeConfig) {
    try {
      const cfg =
        typeof store.scrapeConfig === "string"
          ? JSON.parse(store.scrapeConfig)
          : store.scrapeConfig;
      customSelectors = cfg.price_selectors || [];
    } catch {
      console.warn("[CartSmart] Failed to parse scrapeConfig for store", store.id);
    }
  }

  // Use ONLY custom selectors when defined; fall back to defaults otherwise
  const allSelectors = customSelectors.length > 0 ? customSelectors : DEFAULT_PRICE_SELECTORS;

  // Inject and trigger extraction
  setBadge(tabId, "…", "#2563EB"); // blue = working
  console.log(`[CartSmart] Store "${store.name}" scrapeConfig:`, JSON.stringify(store.scrapeConfig));
  console.log(`[CartSmart] Custom selectors: [${customSelectors.join(", ")}]`);
  console.log(`[CartSmart] Using ${customSelectors.length > 0 ? "CUSTOM" : "DEFAULT"} selectors (${allSelectors.length}):`, allSelectors);

  try {
    await sendToContentScript(tabId, {
      type: "EXTRACT_PRICE",
      storeId: store.id,
      storeName: store.name,
      selectors: allSelectors,
    });
  } catch (err) {
    console.error("[CartSmart] Error sending message to tab:", err);
    setBadge(tabId, "!", "#EF4444");
  }
});

// ─── Message handler (from content script / popup) ────────────────────────

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message.type === "PRICE_EXTRACTED") {
    // Skip normal API submission for test-scrape tabs (handled by TEST_SCRAPE_CONFIG)
    if (!testTabs.has(sender.tab?.id)) {
      handlePriceExtracted(message, sender);
    }
    sendResponse({ ok: true });
    return;
  }

  // ── Admin test-scrape: open URL in a real tab, extract price, return ───
  if (message.type === "TEST_SCRAPE_CONFIG") {
    const { url, selectors, requestId } = message;
    const TIMEOUT_MS = 25000;
    let tabId = null;
    let resolved = false;
    let timeoutId = null;

    function cleanup() {
      if (timeoutId) clearTimeout(timeoutId);
      chrome.runtime.onMessage.removeListener(onResult);
      if (tabId) {
        testTabs.delete(tabId);
        chrome.tabs.remove(tabId).catch(() => {});
      }
    }

    function finish(result) {
      if (resolved) return;
      resolved = true;
      cleanup();
      sendResponse({ success: true, requestId, ...result });
    }

    function fail(msg) {
      if (resolved) return;
      resolved = true;
      cleanup();
      sendResponse({ success: false, requestId, error: msg });
    }

    function onResult(msg, snd) {
      if (msg.type === "PRICE_EXTRACTED" && snd.tab?.id === tabId) {
        const r = msg.result || {};
        finish({
          price: r.price,
          currency: r.currency || "USD",
          inStock: r.inStock,
          candidates: r.candidates || [],
          url: r.url,
        });
      }
    }

    chrome.runtime.onMessage.addListener(onResult);
    timeoutId = setTimeout(() => fail("Timed out waiting for extraction"), TIMEOUT_MS);

    chrome.tabs.create({ url, active: false }, (tab) => {
      if (chrome.runtime.lastError) {
        fail(chrome.runtime.lastError.message);
        return;
      }
      tabId = tab.id;
      testTabs.add(tabId);

      chrome.tabs.onUpdated.addListener(function onUpdated(updatedId, info) {
        if (updatedId !== tabId || info.status !== "complete") return;
        chrome.tabs.onUpdated.removeListener(onUpdated);

        // Small delay for dynamic content to render
        setTimeout(() => {
          sendToContentScript(tabId, {
            type: "EXTRACT_PRICE",
            storeId: 0,
            storeName: "_test_",
            selectors: selectors || DEFAULT_PRICE_SELECTORS,
          }).catch((err) => fail("Content script error: " + err.message));
        }, 2000);
      });
    });

    return true; // keep async channel open
  }

  if (message.type === "GET_STATUS") {
    ensureStoresLoaded().then(() => {
      sendResponse({
        storeCount: storeConfigs.length,
        recentReports: recentReports.slice(0, 10),
      });
    });
    return true; // keep channel open for async
  }

  if (message.type === "REFRESH_STORES") {
    refreshStores().then(() => {
      sendResponse({ ok: true, storeCount: storeConfigs.length });
    });
    return true; // keep channel open for async response
  }

  if (message.type === "GET_STORE_MATCH") {
    ensureStoresLoaded().then(() => {
      const store = findMatchingStore(message.url, storeConfigs);
      sendResponse({ store: store || null });
    });
    return true; // keep channel open for async
  }

  // Allow popup or user to trigger extraction on the current tab
  if (message.type === "EXTRACT_CURRENT") {
    (async () => {
      try {
        const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
        if (!tab?.url) { sendResponse({ ok: false, reason: "no_tab" }); return; }

        await ensureStoresLoaded();
        const store = findMatchingStore(tab.url, storeConfigs);
        if (!store) { sendResponse({ ok: false, reason: "no_store_match" }); return; }

        let customSel = [];
        if (store.scrapeConfig) {
          try {
            const cfg = typeof store.scrapeConfig === "string" ? JSON.parse(store.scrapeConfig) : store.scrapeConfig;
            customSel = cfg.price_selectors || [];
          } catch {}
        }
        const selectors = customSel.length > 0 ? customSel : DEFAULT_PRICE_SELECTORS;

        setBadge(tab.id, "…", "#2563EB");
        await sendToContentScript(tab.id, {
          type: "EXTRACT_PRICE",
          storeId: store.id,
          storeName: store.name,
          selectors,
        });
        sendResponse({ ok: true, storeName: store.name });
      } catch (err) {
        sendResponse({ ok: false, reason: err.message });
      }
    })();
    return true;
  }

  // ── Auto-scan messages ────────────────────────────────────────────────
  if (message.type === "START_AUTOSCAN") {
    if (autoScanRunning) {
      sendResponse({ ok: false, reason: "already_running" });
      return;
    }
    const originTabId = sender.tab?.id;
    if (!originTabId) {
      sendResponse({ ok: false, reason: "no_origin_tab" });
      return;
    }
    sendResponse({ ok: true });
    runAutoScan(message.tasks || [], originTabId);
    return;
  }

  if (message.type === "GET_AUTOSCAN_STATUS") {
    sendResponse({
      running: autoScanRunning,
      progress: autoScanProgress,
      results: autoScanResults,
    });
    return;
  }

  if (message.type === "STOP_AUTOSCAN") {
    autoScanRunning = false;
    sendResponse({ ok: true });
    return;
  }
});

// ─── Handlers ─────────────────────────────────────────────────────────────

async function handlePriceExtracted(message, sender) {
  const tabId = sender.tab?.id;
  const { storeId, storeName, result } = message;
  const pageUrl = result?.url;

  // Skip if this URL is already known to have no matching deal products
  if (pageUrl && await isNoMatchUrl(pageUrl)) {
    console.log("[CartSmart] Skipping report — no matching deal products:", pageUrl);
    if (tabId) setBadge(tabId, "–", "#9CA3AF");
    return;
  }

  if (!result || result.price === null) {
    console.log("[CartSmart] No price extracted for store", storeName);
    if (tabId) setBadge(tabId, "–", "#9CA3AF"); // gray = no price

    // Report the failure so admins know the scrape config may be stale
    if (result?.url && storeId) {
      submitScrapeFailure({
        url: result.url,
        storeId,
        errorMessage: `No price found (${result.candidates?.length || 0} candidate(s))`,
        candidateCount: result.candidates?.length || 0,
      }).catch(() => {});
    }
    return;
  }

  console.log(
    `[CartSmart] Price extracted: $${result.price} ${result.currency} from ${storeName} (${result.url})`
  );

  // Submit to API
  const report = {
    url: result.url,
    storeId,
    price: result.price,
    currency: result.currency,
    inStock: result.inStock,
    candidateCount: result.candidates?.length || 0,
    extractedAt: result.extractedAt,
  };

  const submitResult = await submitPriceReport(report);

  // If the server says no deal products matched this URL, cache it
  // so we don't keep sending reports for this page
  if (submitResult.ok && submitResult.data?.matchedDealProducts === 0) {
    await markNoMatchUrl(result.url);
    console.log("[CartSmart] No matching deal products — URL cached:", result.url);
    if (tabId) setBadge(tabId, "–", "#9CA3AF");
    return;
  }

  // Track recent reports — deduplicate by URL so the same page doesn't appear twice
  const existingIdx = recentReports.findIndex(r => r.url === report.url);
  if (existingIdx !== -1) recentReports.splice(existingIdx, 1);
  recentReports.unshift({
    ...report,
    storeName,
    submitted: submitResult.ok,
    throttled: submitResult.throttled || false,
    timestamp: Date.now(),
  });
  if (recentReports.length > MAX_RECENT) {
    recentReports = recentReports.slice(0, MAX_RECENT);
  }
  await storageSet({ cartsmart_recent_reports: recentReports });

  // Update badge
  if (tabId) {
    if (submitResult.throttled) {
      setBadge(tabId, "⏳", "#6B7280"); // gray = throttled, already updated recently
    } else if (submitResult.ok) {
      setBadge(tabId, "✓", "#10B981"); // green
    } else {
      setBadge(tabId, "!", "#F59E0B"); // amber = price found but submit failed
    }
  }
}

async function refreshStores() {
  console.log("[CartSmart] Refreshing store configs...");
  storeConfigs = await fetchStoreConfigs(true);
  console.log(`[CartSmart] Loaded ${storeConfigs.length} scrape-configured store(s)`);
  // Clear no-match URL cache since deal products may have changed
  await storageSet({ [NO_MATCH_CACHE_KEY]: {} });
}

// ─── Auto-scan state ──────────────────────────────────────────────────────
let autoScanRunning = false;
let autoScanResults = [];
let autoScanProgress = { current: 0, total: 0, currentUrl: "" };

// ─── Badge helpers ────────────────────────────────────────────────────────

function setBadge(tabId, text, color) {
  chrome.action.setBadgeText({ text, tabId });
  if (color) {
    chrome.action.setBadgeBackgroundColor({ color, tabId });
  }
}

// ─── Auto-scan: loop through manual-price task URLs ───────────────────────

/**
 * Opens each task URL in a temporary tab, waits for the content script to
 * extract a price, collects the result, then closes the tab and moves on.
 *
 * @param {Array<{taskId, url, currentPrice}>} tasks - from the manual-price page
 * @param {number} originTabId - the tab that initiated the scan (to send updates)
 */
async function runAutoScan(tasks, originTabId) {
  if (autoScanRunning) {
    console.warn("[CartSmart] Auto-scan already running");
    return;
  }

  autoScanRunning = true;
  autoScanResults = [];
  autoScanProgress = { current: 0, total: tasks.length, currentUrl: "" };

  await ensureStoresLoaded();
  console.log(`[CartSmart] Auto-scan starting: ${tasks.length} task(s)`);

  for (let i = 0; i < tasks.length; i++) {
    const task = tasks[i];
    autoScanProgress = { current: i + 1, total: tasks.length, currentUrl: task.url };

    // Notify origin tab of progress
    try {
      await chrome.tabs.sendMessage(originTabId, {
        type: "AUTOSCAN_PROGRESS",
        progress: autoScanProgress,
      });
    } catch { /* origin tab may not have listener yet */ }

    console.log(`[CartSmart] Auto-scan [${i + 1}/${tasks.length}]: ${task.url}`);

    try {
      const result = await extractFromUrl(task.url, task.taskId);
      autoScanResults.push(result);
    } catch (err) {
      console.error(`[CartSmart] Auto-scan error for ${task.url}:`, err);
      autoScanResults.push({
        taskId: task.taskId,
        url: task.url,
        price: null,
        currency: null,
        inStock: null,
        error: err.message,
      });
    }
  }

  autoScanRunning = false;
  console.log(`[CartSmart] Auto-scan complete: ${autoScanResults.length} result(s)`);

  // Send final results to origin tab
  try {
    await chrome.tabs.sendMessage(originTabId, {
      type: "AUTOSCAN_COMPLETE",
      results: autoScanResults,
    });
  } catch (err) {
    console.warn("[CartSmart] Could not send results to origin tab:", err);
  }
}

/**
 * Opens a URL in a new tab, extracts the price, and closes the tab.
 * Returns a promise that resolves with the extraction result.
 */
function extractFromUrl(url, taskId) {
  return new Promise((resolve, reject) => {
    const TIMEOUT_MS = 15000; // 15s max per page
    let tabId = null;
    let resolved = false;
    let timeoutId = null;

    function cleanup() {
      if (timeoutId) clearTimeout(timeoutId);
      chrome.runtime.onMessage.removeListener(onMessage);
      if (tabId) {
        chrome.tabs.remove(tabId).catch(() => {});
      }
    }

    function finish(result) {
      if (resolved) return;
      resolved = true;
      cleanup();
      resolve(result);
    }

    function fail(msg) {
      if (resolved) return;
      resolved = true;
      cleanup();
      resolve({
        taskId,
        url,
        price: null,
        currency: null,
        inStock: null,
        error: msg,
      });
    }

    // Listen for the extraction result from the content script
    function onMessage(message, sender) {
      if (
        message.type === "PRICE_EXTRACTED" &&
        sender.tab?.id === tabId
      ) {
        const r = message.result || {};
        finish({
          taskId,
          url,
          price: r.price,
          currency: r.currency || "USD",
          inStock: r.inStock,
          candidates: r.candidates?.length || 0,
        });
      }
    }

    chrome.runtime.onMessage.addListener(onMessage);

    // Set timeout
    timeoutId = setTimeout(() => fail("Timed out"), TIMEOUT_MS);

    // Open the tab (inactive so it doesn't steal focus)
    chrome.tabs.create({ url, active: false }, (tab) => {
      if (chrome.runtime.lastError) {
        fail(chrome.runtime.lastError.message);
        return;
      }
      tabId = tab.id;

      // Wait for tab to finish loading, then trigger extraction
      chrome.tabs.onUpdated.addListener(function onUpdated(updatedTabId, changeInfo) {
        if (updatedTabId !== tabId || changeInfo.status !== "complete") return;
        chrome.tabs.onUpdated.removeListener(onUpdated);

        // Find matching store for selectors
        const store = findMatchingStore(url, storeConfigs);
        let selectors = [...DEFAULT_PRICE_SELECTORS];

        if (store?.scrapeConfig) {
          try {
            const cfg = typeof store.scrapeConfig === "string"
              ? JSON.parse(store.scrapeConfig)
              : store.scrapeConfig;
            if (cfg.price_selectors?.length) {
              selectors = cfg.price_selectors;
            }
          } catch {}
        }

        // Send extraction request
        sendToContentScript(tabId, {
          type: "EXTRACT_PRICE",
          storeId: store?.id || 0,
          storeName: store?.name || "Unknown",
          selectors,
        }).catch((err) => fail("Could not inject content script: " + err.message));
      });
    });
  });
}
