import React, { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react';
import { useAuth } from './AuthContext';
import TermsConsentModal from '../components/TermsConsentModal';

const TERMS_VERSION = 'v1';
const makeStorageKey = (userId) => `termsAccepted_${TERMS_VERSION}_${userId || 'anon'}`;

const TermsConsentContext = createContext({
  accepted: false,
  version: TERMS_VERSION,
  open: false,
  requestAcceptance: () => {},
});

export function TermsConsentProvider({ children }) {
  const [accepted, setAccepted] = useState(false);
  const [open, setOpen] = useState(false);
  const [pendingAction, setPendingAction] = useState(null);
  const { user } = useAuth();
  const userId = user?.id ?? user?.Id ?? null;

  // Hydrate when user changes
  useEffect(() => {
    const key = makeStorageKey(userId);
    const val = localStorage.getItem(key);
    const localAccepted = val === 'true';
    setAccepted(localAccepted);

    if (!userId) return; // only hydrate from server for logged-in users
    fetch(`${process.env.REACT_APP_API_URL || 'http://localhost:5000'}/api/users/terms`, {
      credentials: 'include'
    }).then(async (resp) => {
      if (!resp.ok) return;
      const data = await resp.json();
      if (data?.termsAccepted && data?.termsVersion === TERMS_VERSION) {
        setAccepted(true);
        localStorage.setItem(key, 'true');
      } else {
        setAccepted(false);
        localStorage.removeItem(key);
      }
    }).catch(() => {})
  }, [userId]);

  const requestAcceptance = useCallback((actionCallback) => {
    if (accepted) {
      actionCallback?.();
      return;
    }
    setPendingAction(() => actionCallback);
    setOpen(true);
  }, [accepted]);

  const onAccept = useCallback(async () => {
    try {
      await fetch(`${process.env.REACT_APP_API_URL || 'http://localhost:5000'}/api/users/terms/accept`, {
        method: 'POST',
        credentials: 'include',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ version: TERMS_VERSION })
      });
    } catch {}
    const key = makeStorageKey(userId);
    localStorage.setItem(key, 'true');
    setAccepted(true);
    setOpen(false);
    const cb = pendingAction;
    setPendingAction(null);
    cb?.();
  }, [pendingAction, userId]);

  const onCancel = useCallback(() => {
    setOpen(false);
    setPendingAction(null);
  }, []);

  const value = useMemo(() => ({
    accepted,
    version: TERMS_VERSION,
    open,
    requestAcceptance,
  }), [accepted, open]);

  return (
    <TermsConsentContext.Provider value={value}>
      {children}
      <TermsConsentModal open={open} onAccept={onAccept} onCancel={onCancel} />
    </TermsConsentContext.Provider>
  );
}

export function useTermsConsent() {
  return useContext(TermsConsentContext);
}
