import { useCookieConsent } from '../context/CookieConsentContext';
import { useEffect } from 'react';

// Example hook to load scripts only if allowed
export const useConditionalScripts = () => {
  const { prefs } = useCookieConsent();

  useEffect(() => {
    // Analytics script
    if (prefs.analytics && !prefs.saleShareOptOut) {
      // load analytics
      // e.g. dynamically insert script tag
    }
    // Advertising script
    if (prefs.advertising && !prefs.saleShareOptOut) {
      // load advertising tags
    }
  }, [prefs]);
};