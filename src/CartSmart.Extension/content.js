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
// Product metadata extraction
//   Returns { name, brand, msrp, imageUrl, description, dealPrice, currency,
//             condition, inStock, rawTitle } for admin "Add Product" submissions.
//   Reuses extractPrice() for dealPrice and isStruckThrough() for MSRP detection.
// ═══════════════════════════════════════════════════════════════════════════

function readMetaContent(selectors) {
  for (const sel of selectors) {
    const el = document.querySelector(sel);
    const v = el?.getAttribute("content") || el?.textContent;
    if (v && v.trim()) return v.trim();
  }
  return null;
}

function extractJsonLdProduct() {
  const scripts = document.querySelectorAll('script[type="application/ld+json"]');
  for (const s of scripts) {
    try {
      const raw = JSON.parse(s.textContent || "null");
      const items = Array.isArray(raw) ? raw : [raw];
      for (const item of items) {
        if (!item) continue;
        const t = item["@type"];
        const isProduct = t === "Product" ||
          (Array.isArray(t) && t.includes("Product"));
        if (isProduct) return item;
        // Some sites nest under graph
        if (Array.isArray(item["@graph"])) {
          for (const g of item["@graph"]) {
            const gt = g?.["@type"];
            if (gt === "Product" || (Array.isArray(gt) && gt.includes("Product")))
              return g;
          }
        }
      }
    } catch {
      /* ignore malformed JSON-LD */
    }
  }
  return null;
}

function extractProductName(jsonLd) {
  return (
    jsonLd?.name ||
    readMetaContent([
      'meta[property="og:title"]',
      'meta[name="twitter:title"]',
      'meta[itemprop="name"]',
    ]) ||
    document.querySelector("h1")?.textContent?.trim() ||
    document.title?.trim() ||
    null
  );
}

function extractProductBrand(jsonLd) {
  const fromLd = jsonLd?.brand;
  if (typeof fromLd === "string") return fromLd;
  if (fromLd && typeof fromLd === "object" && fromLd.name) return String(fromLd.name);

  return readMetaContent([
    'meta[itemprop="brand"]',
    '[itemprop="brand"]',
    'meta[property="product:brand"]',
    'meta[property="og:brand"]',
  ]);
}

function extractProductDescription(jsonLd) {
  return (
    jsonLd?.description ||
    readMetaContent([
      'meta[property="og:description"]',
      'meta[name="description"]',
      'meta[name="twitter:description"]',
    ])
  );
}

function extractProductImage(jsonLd) {
  // JSON-LD image can be string, array, or object with `url`
  const ld = jsonLd?.image;
  if (typeof ld === "string") return absolutizeUrl(ld);
  if (Array.isArray(ld) && ld.length > 0) {
    const first = ld[0];
    if (typeof first === "string") return absolutizeUrl(first);
    if (first?.url) return absolutizeUrl(first.url);
  }
  if (ld && typeof ld === "object" && ld.url) return absolutizeUrl(ld.url);

  const og = readMetaContent([
    'meta[property="og:image:secure_url"]',
    'meta[property="og:image"]',
    'meta[name="twitter:image"]',
  ]);
  if (og) return absolutizeUrl(og);

  // Last resort: largest <img> inside a product/main area
  const containers = document.querySelectorAll(
    '[class*="product"], [id*="product"], main, [class*="gallery"]'
  );
  let best = null;
  let bestArea = 0;
  for (const c of containers) {
    for (const img of c.querySelectorAll("img")) {
      const w = img.naturalWidth || img.width || 0;
      const h = img.naturalHeight || img.height || 0;
      const area = w * h;
      if (area > bestArea && img.src) {
        bestArea = area;
        best = img.src;
      }
    }
  }
  return best ? absolutizeUrl(best) : null;
}

function absolutizeUrl(maybeRelative) {
  try {
    return new URL(maybeRelative, window.location.href).toString();
  } catch {
    return maybeRelative;
  }
}

function extractMsrp(jsonLd) {
  // Common JSON-LD shapes — listPrice / priceSpecification.listPrice are
  // explicit MSRP fields. highPrice is the top of an offer range and only
  // counts as MSRP when there's a real range (highPrice > lowPrice);
  // otherwise it's just the current selling price.
  const offers = jsonLd?.offers;
  const offerList = Array.isArray(offers) ? offers : offers ? [offers] : [];
  for (const o of offerList) {
    const explicit = [o.listPrice, o.priceSpecification?.listPrice];
    for (const c of explicit) {
      const n = tryParsePrice(String(c ?? ""));
      if (n) return n;
    }
    const high = tryParsePrice(String(o.highPrice ?? ""));
    const low = tryParsePrice(String(o.lowPrice ?? ""));
    if (high && low && high > low) return high;
  }

  // DOM-based: any element whose class/id hints at a "was / list / strike" price
  const selectors = [
    '[class*="was-price" i]',
    '[class*="list-price" i]',
    '[class*="compare-at" i]',
    '[class*="compare-price" i]',
    '[class*="msrp" i]',
    '[class*="rrp" i]',
    '[class*="strike" i]',
    "del",
    "s",
  ];
  for (const sel of selectors) {
    for (const el of document.querySelectorAll(sel)) {
      if (!isStruckThrough(el)) continue;
      const n = tryParsePrice(el.textContent || "");
      if (n) return n;
    }
  }
  return null;
}

// Detect whether the current page is a product detail page. Used to refuse
// "Add Product" submissions from category/home pages where extractProductName
// will fall back to <h1>/<title> and silently return a non-product title.
function detectIsProductPage(jsonLd, dealPrice) {
  if (jsonLd) return true;
  const ogType = readMetaContent(['meta[property="og:type"]']);
  if (ogType && /product/i.test(ogType)) return true;
  if (document.querySelector('[itemtype*="schema.org/Product" i]')) return true;
  if (dealPrice && dealPrice > 0) return true;
  return false;
}

function detectStock() {
  const bodyText = (document.body?.textContent || "").toLowerCase();
  if (OOS_KEYWORDS.some((k) => bodyText.includes(k))) return false;
  if (STOCK_KEYWORDS.some((k) => bodyText.includes(k))) return true;
  return null;
}

// Maps to condition_category_id: 1=New, 2=Used, 3=Refurbished.
// Defaults to 1 (New). We deliberately only check authoritative product-context
// fields here — JSON-LD itemCondition, schema.org microdata, page title, and
// the first product heading — and NOT the full body text. A retailer page for
// a new putter typically contains "used"/"pre-owned" strings in nav links,
// footer ("Sell your used clubs"), and related-category widgets; scanning the
// whole body silently mis-classifies new product candidates as used.
function detectConditionCategoryId(jsonLd) {
  // 1. JSON-LD itemCondition (most authoritative).
  const ldCond = jsonLd?.itemCondition || jsonLd?.offers?.itemCondition;
  if (typeof ldCond === "string") {
    const c = ldCond.toLowerCase();
    if (c.includes("refurbished")) return 3;
    if (c.includes("used") || c.includes("preowned") || c.includes("pre-owned") || c.includes("damaged")) return 2;
    if (c.includes("new")) return 1;
  }

  // 2. Schema.org microdata.
  const itemCondEl = document.querySelector('[itemprop="itemCondition"]');
  if (itemCondEl) {
    const v = (itemCondEl.getAttribute("content") || itemCondEl.textContent || "").toLowerCase();
    if (v.includes("refurbished")) return 3;
    if (v.includes("used") || v.includes("preowned") || v.includes("pre-owned")) return 2;
    if (v.includes("new")) return 1;
  }

  // 3. Narrow text scan: title + first H1 only. Anything outside this window
  //    (footer links, related categories, reviews) gets ignored to prevent
  //    false-positive "used" matches.
  const productText = [
    document.title || "",
    document.querySelector("h1")?.textContent || ""
  ].join(" ").toLowerCase();

  if (/\brefurbished\b|\bcertified pre[- ]?owned\b|\bmanufacturer refurbished\b/.test(productText)) return 3;
  if (/\bopen box\b|\bused\b|\bpre[- ]?owned\b/.test(productText)) return 2;
  return 1;
}

function extractProductMetadata(priceSelectors) {
  const jsonLd = extractJsonLdProduct();
  const priceResult = extractPrice(priceSelectors || []);
  const dealPrice = priceResult.price;

  // Discard MSRP that isn't actually higher than the current selling price —
  // common when JSON-LD's highPrice equals the offer price on single-variant
  // listings, which would otherwise file the deal as having no discount.
  let msrp = extractMsrp(jsonLd);
  if (msrp != null && dealPrice != null && msrp <= dealPrice) {
    msrp = null;
  }

  return {
    isProductPage: detectIsProductPage(jsonLd, dealPrice),
    name: extractProductName(jsonLd),
    brand: extractProductBrand(jsonLd),
    msrp,
    imageUrl: extractProductImage(jsonLd),
    description: extractProductDescription(jsonLd),
    dealPrice,
    currency: priceResult.currency || "USD",
    conditionCategoryId: detectConditionCategoryId(jsonLd),
    inStock: detectStock(),
    rawTitle: document.title?.trim() || null,
    url: window.location.href,
    extractedAt: new Date().toISOString(),
  };
}

// ═══════════════════════════════════════════════════════════════════════════
// Message listener
// ═══════════════════════════════════════════════════════════════════════════

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message.type === "EXTRACT_PRODUCT") {
    // Synchronous response — background will await it.
    try {
      const result = extractProductMetadata(message.selectors || []);
      sendResponse({ ok: true, result });
    } catch (err) {
      sendResponse({ ok: false, error: err?.message || String(err) });
    }
    return; // false: response is synchronous
  }

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

// ═══════════════════════════════════════════════════════════════════════════
// Admin test-scrape relay  (web page ↔ extension via CustomEvent)
// ═══════════════════════════════════════════════════════════════════════════

// Mark our presence so the web app can detect the extension
document.documentElement.dataset.cartsmartExtension = "1";

// The web app dispatches "cartsmart-test-scrape" with { url, selectors, requestId }.
// We relay to background, which opens the page in a real tab, runs the
// content-script extraction, and returns the result.
window.addEventListener("cartsmart-test-scrape", (evt) => {
  const { url, selectors, requestId } = evt.detail || {};
  chrome.runtime.sendMessage(
    { type: "TEST_SCRAPE_CONFIG", url, selectors, requestId },
    (response) => {
      window.dispatchEvent(
        new CustomEvent("cartsmart-test-scrape-result", {
          detail: response || { requestId, error: "No response from extension" },
        })
      );
    }
  );
});

// Let the background script know we're ready
console.log("[CartSmart] Content script loaded on", window.location.href);
