import React from 'react';
import { useCookieConsent } from '../context/CookieConsentContext';

const categories = [
  { key: 'performance', label: 'Performance', desc: 'Measure site speed and stability.' },
  { key: 'analytics', label: 'Analytics', desc: 'Understand usage and improve features.' },
  { key: 'advertising', label: 'Advertising', desc: 'Deliver and measure personalized ads.' }
];

const CookiePreferencesModal = ({ open, onClose }) => {
  const { prefs, setCategory, savePrefs, setSaleShareOptOut } = useCookieConsent();
  if (!open) return null;

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40">
      <div className="bg-white rounded-lg shadow-xl w-full max-w-lg p-6 max-h-[90vh] overflow-y-auto">
        <div className="flex justify-between items-start mb-4">
            <h2 className="text-xl font-semibold">Cookie & Privacy Choices</h2>
            <button onClick={onClose} className="text-gray-500 hover:text-gray-700">
              <span aria-hidden>×</span>
            </button>
        </div>
        <p className="text-sm text-gray-600 mb-4">
          Adjust categories. Essential cookies are always active. Toggle the opt‑out for California (CPRA) “Do Not Sell or Share”.
        </p>

        <div className="space-y-4">
          <div className="border rounded p-3 bg-gray-50">
            <div className="flex items-center justify-between mb-1">
              <span className="font-medium">Essential</span>
              <span className="text-xs text-green-700 font-semibold">Always Active</span>
            </div>
            <p className="text-xs text-gray-600">Needed to operate site, security, authentication.</p>
          </div>

          {categories.map(c => (
            <div key={c.key} className="border rounded p-3">
              <div className="flex items-center justify-between">
                <label className="flex items-center gap-2 cursor-pointer">
                  <input
                    type="checkbox"
                    checked={prefs[c.key]}
                    onChange={e => setCategory(c.key, e.target.checked)}
                  />
                  <span className="font-medium">{c.label}</span>
                </label>
                <span className="text-xs text-gray-500">
                  {prefs[c.key] ? 'Enabled' : 'Disabled'}
                </span>
              </div>
              <p className="mt-1 text-xs text-gray-600">{c.desc}</p>
            </div>
          ))}

          <div className="border rounded p-3">
            <label className="flex items-center gap-2 cursor-pointer">
              <input
                type="checkbox"
                checked={prefs.saleShareOptOut}
                onChange={e => setSaleShareOptOut(e.target.checked)}
              />
              <span className="font-medium">Do Not Sell or Share My Personal Information (California)</span>
            </label>
            <p className="mt-1 text-xs text-gray-600">
              If checked, we will not use identifiers for cross‑context behavioral advertising or sell/share personal data with third parties except as permitted.
            </p>
          </div>
        </div>

        <div className="flex justify-end gap-3 mt-6">
                      <button
            onClick={onClose}
            className="px-4 py-2 rounded border border-gray-300 text-gray-700 hover:bg-gray-50"
          >
            Cancel
          </button>
          <button
            onClick={() => { savePrefs(); onClose(); }}
            className="px-4 py-2 bg-[#4CAF50] text-white rounded hover:bg-[#3d8b40]"
          >
            Save Choices
          </button>

        </div>
      </div>
    </div>
  );
};

export default CookiePreferencesModal;