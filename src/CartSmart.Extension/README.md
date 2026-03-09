# CartSmart Chrome Extension – Price Tracker

A Chrome browser extension that automatically extracts product prices from supported stores to keep CartSmart pricing up to date.

## How It Works

1. **Store Config Sync** – On startup (and hourly), the extension fetches the list of stores with `scrape_mode_id` > 0 (All or BrowserOnly) from the CartSmart API, including each store's `scrape_config` (CSS price selectors).

2. **URL Matching** – As you browse, the extension checks whether the current page's hostname matches any tracked store.

3. **Price Extraction** – When a match is found, the content script runs the store's configured CSS selectors against the live DOM — the same logic used by the server-side `GenericHtmlScraper`:
   - Probes selectors for price elements
   - Detects struck-through / promotional text
   - Scopes to a "price region" to avoid false positives
   - Parses monetary amounts and detects currency
   - Selects the best candidate (non-struck, non-promo, lowest)

4. **Price Reporting** – Extracted prices are submitted to `POST /api/extension/price-report`, which matches the URL to existing `deal_product` rows and updates prices / records price history.

## Project Structure

```
CartSmart.Extension/
├── manifest.json            # Chrome Manifest V3
├── background.js            # Service worker: store sync, tab monitoring, API submission
├── content.js               # Content script: DOM price extraction (injected per page)
├── lib/
│   ├── api-client.js        # API communication helpers (used by background)
│   └── price-parser.js      # Standalone price-parsing module (reference / testing)
├── popup/
│   ├── popup.html           # Extension popup UI
│   ├── popup.css            # Popup styles
│   └── popup.js             # Popup logic
├── options/
│   ├── options.html         # Settings page
│   └── options.js           # Settings logic
├── icons/
│   ├── icon.svg             # Source SVG
│   ├── icon16.png           # 16×16 icon
│   ├── icon48.png           # 48×48 icon
│   └── icon128.png          # 128×128 icon
└── generate-icons.js        # Helper to generate placeholder PNGs
```

## Installation (Development)

1. Open Chrome and navigate to `chrome://extensions/`
2. Enable **Developer mode** (toggle in top-right)
3. Click **Load unpacked** and select the `src/CartSmart.Extension` folder
4. Click the extension icon → **Settings** to configure:
   - **API Base URL**: e.g. `https://api.cartsmart.com` or `http://localhost:5000`
   - **API Token**: (optional) for authenticated submissions

## API Endpoints

The extension consumes two endpoints added to the CartSmart API:

### `GET /api/extension/stores`
Returns all stores with `scrape_mode_id` > 0 (1 = All, 2 = BrowserOnly):
```json
[
  {
    "id": 42,
    "name": "Example Store",
    "url": "https://www.example.com",
    "slug": "example-store",
    "scrapeConfig": {
      "price_selectors": ["#price", ".offer-price", "*[itemprop='price']"]
    }
  }
]
```

### `POST /api/extension/price-report`
Submits an extracted price:
```json
{
  "url": "https://www.example.com/product/12345",
  "storeId": 42,
  "price": 29.99,
  "currency": "USD",
  "inStock": true,
  "candidateCount": 3,
  "extractedAt": "2026-03-05T12:00:00.000Z"
}
```

Response:
```json
{
  "accepted": true,
  "matchedDealProducts": 2,
  "updatedDealProducts": 1,
  "message": "Updated 1 deal product(s) with new price $29.99."
}
```

## Badge Indicators

| Badge | Meaning |
|-------|---------|
| `…` (blue) | Extracting price… |
| `✓` (green) | Price extracted and submitted successfully |
| `!` (amber) | Price found but API submission failed |
| `!` (red) | Content script error |
| `–` (gray) | Matched store but no price found |
| _(none)_ | Page is not a tracked store |

## Architecture Notes

- **Manifest V3** – Uses service workers (no persistent background page)
- **No bundler required** – Ships as plain JS; content script inlines the parser since MV3 content scripts can't use ES module imports
- **Store configs cached** – In `chrome.storage.local` with 1-hour TTL; also in API response cache (15 min)
- **Price region scoping** – Mirrors `GenericHtmlScraper.SelectRegionRoot` to avoid extracting prices from unrelated page sections
- **URL normalisation** – Strips tracking params (utm_*, fbclid, gclid, ref, tag) when matching deal product URLs
