/**
 * CartSmart Price Parser
 *
 * Mirrors the extraction logic in GenericHtmlScraper.cs:
 *   - Run configured CSS selectors against the live DOM
 *   - Extract price candidates from matching elements
 *   - Detect currency, struck-through / promo indicators
 *   - Select the best price (prefer non-struck, lowest)
 *   - Detect stock status
 */

// ─── Default selectors (fallback; overridden by store config) ──────────────
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
  "span[class*='price']",
];

const STOCK_KEYWORDS = ["in stock", "available"];
const OOS_KEYWORDS = ["out of stock", "unavailable"];

// ─── Utilities ─────────────────────────────────────────────────────────────

/**
 * Collapse duplicate substrings and normalise whitespace.
 */
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

/**
 * Returns true if the text looks like a promotional / savings line
 * rather than an actual price (e.g. "Save $100", "20% off").
 */
function looksPromotional(s) {
  const t = s.toLowerCase();
  return t.includes("save") || t.includes("discount") || t.includes("off");
}

/**
 * Heuristic check for strike-through styling (CSS or class names).
 */
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

  // Check computed style
  try {
    const computed = window.getComputedStyle(el);
    if (computed.textDecorationLine?.includes("line-through")) return true;
  } catch {
    /* ignore */
  }

  // Check parent
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

/**
 * Extract the first monetary number from a string.
 * Supports comma-thousands and dot decimals (e.g. "1,299.99").
 */
function tryParsePrice(s) {
  if (!s) return null;
  const m = s.match(
    /(?<![A-Za-z0-9])(\d{1,3}(?:,\d{3})*(?:\.\d{2})|\d+(?:\.\d{1,2})?)/
  );
  if (!m) return null;
  const num = m[1].replace(/,/g, "");
  const parsed = parseFloat(num);
  return isNaN(parsed) || parsed <= 0 ? null : parsed;
}

/**
 * Detect currency from surrounding text.
 */
function detectCurrency(s) {
  const u = s.toUpperCase();
  if (u.includes("USD") || u.includes("US $") || u.includes("$")) return "USD";
  if (u.includes("EUR") || u.includes("€")) return "EUR";
  if (u.includes("GBP") || u.includes("£")) return "GBP";
  return null;
}

// ─── Region scoping (match C# SelectRegionRoot) ───────────────────────────

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

// ─── Main extraction ──────────────────────────────────────────────────────

/**
 * Extract price candidates from the current DOM.
 *
 * @param {string[]} selectors - CSS selectors to probe (from store ScrapeConfig).
 * @returns {{ price: number|null, currency: string|null, inStock: boolean|null, candidates: Array }}
 */
function extractPrice(selectors) {
  const activeSelectors =
    selectors && selectors.length > 0 ? selectors : DEFAULT_PRICE_SELECTORS;

  const candidates = [];
  let regionRoot = null;

  for (const sel of activeSelectors) {
    let els;
    try {
      els = document.querySelectorAll(sel);
    } catch {
      continue; // invalid selector
    }

    for (const el of els) {
      if (regionRoot && !regionContains(regionRoot, el)) continue;

      const raw =
        el.getAttribute("aria-label") ||
        el.getAttribute("content") ||
        el.textContent;
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

  // ── Price selection (mirrors C#) ────────────────────────────────────────
  let price = null;
  let currency = null;

  if (candidates.length > 0) {
    // Prefer non-struck, non-promo; lowest amount
    const preferred = candidates
      .filter((c) => !c.struck && !c.promo)
      .sort((a, b) => a.amount - b.amount);

    if (preferred.length > 0 && preferred[0].amount !== 0) {
      price = preferred[0].amount;
      currency = preferred[0].currency;
    } else {
      // Fallback: any non-struck
      const alt = candidates
        .filter((c) => !c.struck)
        .sort((a, b) => a.amount - b.amount);
      if (alt.length > 0 && alt[0].amount !== 0) {
        price = alt[0].amount;
        currency = alt[0].currency;
      } else {
        // Last resort
        const any = [...candidates].sort((a, b) => a.amount - b.amount);
        price = any[0].amount;
        currency = any[0].currency;
      }
    }
  }

  // ── Stock detection ─────────────────────────────────────────────────────
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

// Make available in both module and non-module contexts
if (typeof module !== "undefined" && module.exports) {
  module.exports = { extractPrice, tryParsePrice, detectCurrency, cleanPriceText };
}
