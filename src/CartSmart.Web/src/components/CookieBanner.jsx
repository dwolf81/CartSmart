import React from 'react';
import { useCookieConsent } from '../context/CookieConsentContext';

const CookieBanner = () => {
  const { prefs, acceptAll, rejectAll } = useCookieConsent();
  if (prefs.consentGiven) return null;

  return (
    <div className="fixed bottom-0 left-0 right-0 z-50 bg-white shadow-lg border-t border-gray-200">
      <div className="max-w-5xl mx-auto px-4 py-4 flex flex-col gap-3">
        <div className="text-sm text-gray-700 leading-relaxed">
          We use essential cookies to operate the site and (with consent) performance, analytics and advertising cookies.
          Under GDPR / CPRA you must be offered consent or opt‑out on first visit regardless of landing page.
          Manage choices or opt out of sale/share of personal information below.
        </div>
        <div className="flex gap-2 self-end">
          <button
            onClick={rejectAll}
            className="px-3 py-2 text-sm rounded border border-gray-300 hover:bg-gray-50"
          >
            Reject Non‑Essential
          </button>
          <button
            onClick={acceptAll}
            className="px-3 py-2 text-sm rounded bg-[#4CAF50] text-white hover:bg-[#3d8b40]"
          >
            Accept All
          </button>
        </div>
      </div>
    </div>
  );
};

export default CookieBanner;