/**
 * CartSmart Extension – Content Script
 *
 * Injected into every page. Listens for EXTRACT_PRICE messages from the
 * background service worker. When triggered, runs the configured CSS
 * selectors against the live DOM to extract the current product price,
 * using the same logic as the server-side GenericHtmlScraper.
 *
 * The price-parser.js helpers are inlined here (content scripts cannot
 * use ES module imports in Manifest V3 without bundling).
 */

// ═══════════════════════════════════════════════════════════════════════════
// Inline price-parser.js  (must be self-contained in MV3 content scripts)
// ═══════════════════════════════════════════════════════════════════════════

const STOCK_KEYWORDS = ["in stock", "available"];
const OOS_KEYWORDS = ["out of stock", "unavailable"];

function cleanPriceText(s) {
  let trimmed = s.trim().replace(/\s+/g, " ");
  const halfLen = Math.floor(trimmed.length / 2);
  if (
    halfLen > 0 &&
    trimmed.substring(0, halfLen).toLowerCase() ===
      trimmed.substring(halfLen).toLowerCase()
  ) {
    trimmed = trimmed.substring(0, halfLen);
  }
  return trimmed;
}

function looksPromotional(s) {
  const t = s.toLowerCase();
  return t.includes("save") || t.includes("discount") || t.includes("off");
}

function isStruckThrough(el) {
  const style = (el.getAttribute("style") || "").toLowerCase();
  if (style.includes("line-through")) return true;

  const cls = (el.className || "").toString().toLowerCase();
  if (
    cls.includes("strike") ||
    cls.includes("strikethrough") ||
    cls.includes("line-through") ||
    cls.includes("text-decor_line-through") ||
    cls.includes("was-price") ||
    cls.includes("old-price") ||
    cls.includes("list-price")
  )
    return true;

  try {
    const computed = window.getComputedStyle(el);
    if (computed.textDecorationLine?.includes("line-through")) return true;
  } catch {
    /* ignore */
  }

  const parent = el.parentElement;
  if (parent) {
    const pStyle = (parent.getAttribute("style") || "").toLowerCase();
    const pCls = (parent.className || "").toString().toLowerCase();
    if (pStyle.includes("line-through")) return true;
    if (
      pCls.includes("strike") ||
      pCls.includes("strikethrough") ||
      pCls.includes("was-price") ||
      pCls.includes("old-price") ||
      pCls.includes("list-price")
    )
      return true;
  }

  return false;
}

function tryParsePrice(s) {
  if (!s) return null;
  const m = s.match(
    /(?<![A-Za-z0-9])(\d{1,3}(?:,\d{3})*(?:\.\d{1,2})?|\d+(?:\.\d{1,2})?)/
  );
  if (!m) return null;
  const num = m[1].replace(/,/g, "");
  const parsed = parseFloat(num);
  return isNaN(parsed) || parsed <= 0 ? null : parsed;
}

function detectCurrency(s) {
  const u = s.toUpperCase();
  if (u.includes("USD") || u.includes("US $") || u.includes("$")) return "USD";
  if (u.includes("EUR") || u.includes("€")) return "EUR";
  if (u.includes("GBP") || u.includes("£")) return "GBP";
  return null;
}

function selectRegionRoot(el) {
  let cur = el;
  while (cur.parentElement && cur.parentElement.tagName !== "BODY") {
    const clsId = (
      (cur.className || "").toString() +
      " " +
      (cur.id || "")
    ).toLowerCase();
    if (
      clsId.includes("product") ||
      clsId.includes("price") ||
      clsId.includes("buy") ||
      clsId.includes("main") ||
      clsId.includes("summary") ||
      clsId.includes("detail")
    )
      return cur;
    cur = cur.parentElement;
  }
  return el.parentElement || el;
}

function regionContains(regionRoot, el) {
  if (!regionRoot) return false;
  let cur = el;
  while (cur) {
    if (cur === regionRoot) return true;
    cur = cur.parentElement;
  }
  return false;
}

// ═══════════════════════════════════════════════════════════════════════════
// Core extraction (mirrors GenericHtmlScraper.ExtractPriceCandidates)
// ═══════════════════════════════════════════════════════════════════════════

const COMPARE_PRICE_PATTERNS = /compare|was|original|regular|list.?price|old.?price|retail|msrp|rrp|strikethrough|strike/i;

/**
 * Extract the most relevant price text from an element.
 * If the element contains child elements that look like compare/original prices,
 * exclude their text so we don't accidentally pick the wrong price.
 */
function getRelevantText(el) {
  // Prefer explicit attributes first — these are unambiguous
  const ariaLabel = el.getAttribute("aria-label");
  if (ariaLabel) return ariaLabel;

  const content = el.getAttribute("content");
  if (content) return content;

  // Check if the element has children that look like compare/was prices
  const compareChildren = el.querySelectorAll(
    '[class*="compare"], [class*="was"], [class*="original"], [class*="regular"], ' +
    '[class*="list-price"], [class*="old-price"], [class*="retail"], [class*="msrp"], ' +
    '[class*="rrp"], [class*="strike"], del, s'
  );

  if (compareChildren.length === 0) {
    // No compare-price children — safe to use full textContent
    return el.textContent;
  }

  // Collect text from this element, excluding compare-price children
  const excludeSet = new Set();
  for (const child of compareChildren) {
    excludeSet.add(child);
  }

  // Walk direct text nodes + non-excluded child elements
  let text = "";
  for (const node of el.childNodes) {
    if (node.nodeType === Node.TEXT_NODE) {
      text += node.textContent;
    } else if (node.nodeType === Node.ELEMENT_NODE && !excludeSet.has(node)) {
      // Also check if this element IS a compare price by class
      const cls = (node.className || "").toString();
      if (!COMPARE_PRICE_PATTERNS.test(cls) && !isStruckThrough(node)) {
        text += node.textContent;
      }
    }
  }

  return text || el.textContent; // fallback to full text if filtering removed everything
}

function extractPrice(selectors) {
  const candidates = [];
  let regionRoot = null;

  for (const sel of selectors) {
    let els;
    try {
      els = document.querySelectorAll(sel);
    } catch {
      console.warn(`[CartSmart] Invalid selector: "${sel}"`);
      continue;
    }

    console.log(`[CartSmart] Selector "${sel}" matched ${els.length} element(s)`);

    for (const el of els) {
      if (regionRoot && !regionContains(regionRoot, el)) continue;

      // If the element has child elements that look like compare/original prices,
      // extract only the direct text content (not nested compare-price text)
      const raw = getRelevantText(el);
      if (!raw || !raw.trim()) continue;

      const promo = looksPromotional(raw);
      const struck = isStruckThrough(el);
      const cleaned = cleanPriceText(raw);
      const amount = tryParsePrice(cleaned);

      if (amount !== null) {
        const currency = detectCurrency(raw || el.textContent || "");
        candidates.push({ amount, currency, struck, promo });

        if (!regionRoot) {
          regionRoot = selectRegionRoot(el);
        }
      }
    }

    if (regionRoot && candidates.length >= 6) break;
  }

  // ── Price selection ───────────────────────────────────────────────────
  let price = null;
  let currency = null;

  if (candidates.length > 0) {
    const preferred = candidates
      .filter((c) => !c.struck && !c.promo)
      .sort((a, b) => a.amount - b.amount);

    if (preferred.length > 0 && preferred[0].amount !== 0) {
      price = preferred[0].amount;
      currency = preferred[0].currency;
    } else {
      const alt = candidates
        .filter((c) => !c.struck)
        .sort((a, b) => a.amount - b.amount);
      if (alt.length > 0 && alt[0].amount !== 0) {
        price = alt[0].amount;
        currency = alt[0].currency;
      } else {
        const any = [...candidates].sort((a, b) => a.amount - b.amount);
        price = any[0].amount;
        currency = any[0].currency;
      }
    }
  }

  // ── Stock detection ─────────────────────────────────────────────────
  const bodyText = (document.body?.textContent || "").toLowerCase();
  let inStock = null;
  if (STOCK_KEYWORDS.some((k) => bodyText.includes(k))) inStock = true;
  if (OOS_KEYWORDS.some((k) => bodyText.includes(k))) inStock = false;

  return {
    price,
    currency: currency || "USD",
    inStock,
    candidates,
    url: window.location.href,
    extractedAt: new Date().toISOString(),
  };
}

// ═══════════════════════════════════════════════════════════════════════════
// Message listener
// ═══════════════════════════════════════════════════════════════════════════

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message.type !== "EXTRACT_PRICE") return;

  const { storeId, storeName, selectors } = message;

  console.log(
    `[CartSmart] Extracting price for ${storeName} using ${selectors.length} selector(s):`,
    selectors
  );

  // Small delay to ensure dynamic content has rendered
  setTimeout(() => {
    const result = extractPrice(selectors);

    console.log(
      `[CartSmart] Extraction result: price=${result.price}, currency=${result.currency}, candidates=${result.candidates.length}`
    );
    if (result.candidates.length > 0) {
      console.log("[CartSmart] All candidates:", JSON.stringify(result.candidates, null, 2));
    }

    // Send result back to background
    chrome.runtime.sendMessage({
      type: "PRICE_EXTRACTED",
      storeId,
      storeName,
      result,
    });
  }, 1500);

  sendResponse({ ack: true });
});

// Let the background script know we're ready
console.log("[CartSmart] Content script loaded on", window.location.href);
