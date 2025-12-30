import React, { createContext, useContext, useEffect, useState } from 'react';

const LS_KEY = 'cartsmart_cookie_prefs';
const CONSENT_COOKIE = 'cc_prefs';

const defaultPrefs = {
  essential: true,            // always true
  performance: false,
  analytics: false,
  advertising: false,
  saleShareOptOut: false,     // CCPA/CPRA “Do Not Sell or Share” flag
  consentGiven: false,
  updatedAt: null
};

const CookieConsentContext = createContext({
  prefs: defaultPrefs,
  setCategory: () => {},
  setSaleShareOptOut: () => {},
  acceptAll: () => {},
  rejectAll: () => {},
  savePrefs: () => {}
});

const isHttps = () => typeof window !== 'undefined' && window.location && window.location.protocol === 'https:';

const writeConsentCookie = (prefs) => {
  try {
    const val = encodeURIComponent(JSON.stringify({
      p: !!prefs.performance,
      a: !!prefs.analytics,
      ad: !!prefs.advertising,
      sso: !!prefs.saleShareOptOut,
      v: 1,
      t: new Date().toISOString()
    }));
    const days = 365;
    const expires = new Date(Date.now() + days*24*60*60*1000).toUTCString();
    const secure = isHttps() ? ';secure' : '';
    document.cookie = `${CONSENT_COOKIE}=${val};expires=${expires};path=/;samesite=lax${secure}`;
  } catch {}
};

const readConsentCookie = () => {
  try {
    const all = document.cookie || '';
    const parts = all.split(';').map(s => s.trim());
    const kv = parts.find(p => p.startsWith(`${CONSENT_COOKIE}=`));
    if (!kv) return null;
    const raw = decodeURIComponent(kv.split('=')[1] || '');
    const parsed = JSON.parse(raw);
    return {
      performance: !!parsed.p,
      analytics: !!parsed.a,
      advertising: !!parsed.ad,
      saleShareOptOut: !!parsed.sso,
    };
  } catch {
    return null;
  }
};

const mergePrefs = (base, incoming) => {
  if (!incoming) return base;
  // stricter wins: false overrides true; saleShareOptOut true overrides false
  return {
    ...base,
    performance: !!(base.performance && incoming.performance),
    analytics: !!(base.analytics && incoming.analytics),
    advertising: !!(base.advertising && incoming.advertising),
    saleShareOptOut: !!(base.saleShareOptOut || incoming.saleShareOptOut)
  };
};

const apiUrl = typeof process !== 'undefined' && process.env && process.env.REACT_APP_API_URL;

export const CookieConsentProvider = ({ children }) => {
  const [prefs, setPrefs] = useState(defaultPrefs);

  useEffect(() => {
    try {
      // Seed from cookie first (available to server/edge), then localStorage fallback
      const cookieSeed = readConsentCookie();
      const raw = localStorage.getItem(LS_KEY);
      const lsSeed = raw ? JSON.parse(raw) : null;
      const initial = { ...defaultPrefs, ...(cookieSeed || {}), ...(lsSeed || {}) };
      setPrefs({ ...initial, essential: true });
    } catch {}
  }, []);

  // On mount, if logged-in, hydrate from server and merge (stricter wins)
  useEffect(() => {
    const hydrate = async () => {
      try {
        if (!apiUrl) return;
        const res = await fetch(`${apiUrl}/api/privacy/preferences`, {
          credentials: 'include'
        });
        if (!res.ok) return; // not logged in or other error
        const server = await res.json();
        const merged = mergePrefs(prefs, {
          performance: !!server.performance,
          analytics: !!server.analytics,
          advertising: !!server.advertising,
          saleShareOptOut: !!server.saleShareOptOut
        });
        // persist merged without flipping consentGiven unless already true
        persist({ ...merged, consentGiven: !!prefs.consentGiven });
      } catch {}
    };
    // only run after initial state is set
    if (prefs !== defaultPrefs) hydrate();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [apiUrl]);

  const persist = (next) => {
    const stamped = { ...next, updatedAt: new Date().toISOString() };
    setPrefs(stamped);
    try { localStorage.setItem(LS_KEY, JSON.stringify(stamped)); } catch {}
    // write consent cookie for server/edge enforcement
    writeConsentCookie(stamped);
    // try to persist to server if logged in
    try {
      if (apiUrl && stamped.consentGiven) {
        fetch(`${apiUrl}/api/privacy/preferences`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          credentials: 'include',
          body: JSON.stringify({
            performance: !!stamped.performance,
            analytics: !!stamped.analytics,
            advertising: !!stamped.advertising,
            saleShareOptOut: !!stamped.saleShareOptOut
          })
        }).catch(() => {});
      }
    } catch {}
  };

  const setCategory = (key, value) => {
    if (key === 'essential') return;
    persist({ ...prefs, [key]: value });
  };

  const setSaleShareOptOut = (value) => {
    persist({ ...prefs, saleShareOptOut: value, consentGiven: true });
  };

  const acceptAll = () => {
    persist({
      ...prefs,
      performance: true,
      analytics: true,
      advertising: true,
      consentGiven: true
    });
  };

  const rejectAll = () => {
    persist({
      ...prefs,
      performance: false,
      analytics: false,
      advertising: false,
      consentGiven: true
    });
  };

  const savePrefs = () => {
    persist({ ...prefs, consentGiven: true });
  };

  return (
    <CookieConsentContext.Provider value={{
      prefs,
      setCategory,
      setSaleShareOptOut,
      acceptAll,
      rejectAll,
      savePrefs
    }}>
      {children}
    </CookieConsentContext.Provider>
  );
};

export const useCookieConsent = () => useContext(CookieConsentContext);