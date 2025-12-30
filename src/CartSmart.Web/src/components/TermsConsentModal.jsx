import React from 'react';

export default function TermsConsentModal({ open, onAccept, onCancel }) {
  if (!open) return null;

  return (
    <div className="fixed inset-0 bg-black/50 z-50 flex items-center justify-center">
      <div className="bg-white rounded-lg shadow-lg max-w-md w-full p-6">
        <h2 className="text-xl font-semibold mb-3">Agree to Terms</h2>
        <p className="text-sm mb-4">
          To post to CartSmart, please confirm that you agree to our
          <a href="/terms" className="underline ml-1" target="_blank" rel="noopener noreferrer">Terms of Service</a>
           {' '}and
          <a href="/privacy" className="underline ml-1" target="_blank" rel="noopener noreferrer">Privacy Policy</a>.
        </p>
        <div className="flex gap-2 justify-end">
          <button type="button" onClick={onCancel} className="px-4 py-2 rounded border border-gray-300">Cancel</button>
          <button type="button" onClick={onAccept} className="px-4 py-2 rounded bg-blue-600 text-white">I Agree</button>
        </div>
      </div>
    </div>
  );
}
