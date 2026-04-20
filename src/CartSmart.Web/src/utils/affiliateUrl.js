export const appendAffiliateParam = (rawUrl, affiliateCodeVar, affiliateCode, affiliateUrlTemplate) => {
  if (!rawUrl) return rawUrl;

  const input = String(rawUrl).trim();
  if (!input) return rawUrl;

  // Template mode: supports two styles via {url} / {url_encoded} placeholders.
  //
  // 1. Wrapper URL  – template is a full URL that wraps the product URL:
  //    e.g. https://www.awin1.com/cread.php?awinmid=123&awinaffid=456&ued={url_encoded}
  //
  // 2. Extra query params – template starts with "?" or "&" and lists params
  //    to append to the product URL. The separator is chosen automatically:
  //    e.g. ?tag=abc&ref=123
  const tpl = (affiliateUrlTemplate ?? '').trim();
  if (tpl) {
    try {
      const withScheme = /^https?:\/\//i.test(input) ? input : `https://${input}`;

      // Wrapper: template contains {url} or {url_encoded} placeholders
      if (tpl.includes('{url}') || tpl.includes('{url_encoded}')) {
        return tpl
          .replace(/\{url_encoded\}/g, encodeURIComponent(withScheme))
          .replace(/\{url\}/g, withScheme);
      }

      // Extra params: template starts with ? or & (e.g. "?tag=abc&ref=123")
      if (tpl.startsWith('?') || tpl.startsWith('&')) {
        const u = new URL(withScheme);
        const extra = new URLSearchParams(tpl.startsWith('?') ? tpl.slice(1) : tpl);
        for (const [k, v] of extra) {
          u.searchParams.set(k, v);
        }
        return u.toString();
      }
    } catch {
      return rawUrl;
    }
  }

  // Legacy single-param mode
  const key = (affiliateCodeVar ?? '').toString().trim();
  const val = (affiliateCode ?? '').toString().trim();
  if (!key || !val) return rawUrl;

  try {
    const withScheme = /^https?:\/\//i.test(input) ? input : `https://${input}`;
    const u = new URL(withScheme);
    u.searchParams.set(key, val);
    return u.toString();
  } catch {
    return rawUrl;
  }
};

/**
 * Returns the affiliate query param key/value for a deal/row.
 *
 * - `kind='normal'` reads `affiliate_code_var` / `affiliate_code`
 * - `kind='external'` reads `external_affiliate_code_var` / `external_affiliate_code`
 *
 * For compatibility, `kind='external'` falls back to normal fields when external
 * fields are not present.
 */
export const getAffiliateFields = (dealOrRow, kind = 'normal') => {
  const d = dealOrRow || {};

  const normal = {
    affiliateCodeVar: d.affiliate_code_var ?? d.affiliateCodeVar ?? null,
    affiliateCode: d.affiliate_code ?? d.affiliateCode ?? null,
    affiliateUrlTemplate: d.affiliate_url_template ?? d.affiliateUrlTemplate ?? null
  };

  if (kind !== 'external') return normal;

  return {
    affiliateCodeVar:
      d.external_affiliate_code_var ??
      d.externalAffiliateCodeVar ??
      normal.affiliateCodeVar ??
      null,
    affiliateCode:
      d.external_affiliate_code ??
      d.externalAffiliateCode ??
      normal.affiliateCode ??
      null,
    affiliateUrlTemplate:
      d.external_affiliate_url_template ??
      d.externalAffiliateUrlTemplate ??
      normal.affiliateUrlTemplate ??
      null
  };
};
