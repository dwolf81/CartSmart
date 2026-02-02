export const appendAffiliateParam = (rawUrl, affiliateCodeVar, affiliateCode) => {
  const key = (affiliateCodeVar ?? '').toString().trim();
  const val = (affiliateCode ?? '').toString().trim();
  if (!rawUrl) return rawUrl;
  if (!key || !val) return rawUrl;

  const input = String(rawUrl).trim();
  if (!input) return rawUrl;

  try {
    const withScheme = /^https?:\/\//i.test(input) ? input : `https://${input}`;
    const u = new URL(withScheme);
    u.searchParams.set(key, val);
    return u.toString();
  } catch {
    // Best-effort fallback: if URL parsing fails, don't mutate.
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
    affiliateCode: d.affiliate_code ?? d.affiliateCode ?? null
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
      null
  };
};
