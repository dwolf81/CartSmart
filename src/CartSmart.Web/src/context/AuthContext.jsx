import React, { createContext, useContext, useEffect, useState, useCallback } from 'react';

const API_URL = process.env.REACT_APP_API_URL || 'http://localhost:5000';

const AuthContext = createContext({
  user: null,
  setUser: () => {},
  isAuthenticated: false,
  loading: true,
  logout: () => {},
  rehydrate: async () => {},
  authFetch: async () => {}
});

export const AuthProvider = ({ children }) => {
  const [user, setUser] = useState(null);
  const [isAuthenticated, setIsAuthenticated] = useState(false);
  const [loading, setLoading] = useState(true);

  const logout = useCallback(async () => {
    try {
      await fetch(`${API_URL}/api/auth/logout`, { method: 'POST', credentials: 'include' });
    } catch {}
    localStorage.removeItem('user');
    setUser(null);
    setIsAuthenticated(false);
  }, []);

  // Centralized fetch that auto-logs out on invalid/locked session
  const authFetch = useCallback(async (input, init = {}) => {
    const res = await fetch(input, { credentials: 'include', ...init });
    if (res.status === 401 || res.status === 423) {
      // Session invalid or locked
      await logout();
    }
    return res;
  }, [logout]);

  const rehydrate = useCallback(async () => {
    setLoading(true);
    try {
      const res = await authFetch(`${API_URL}/api/users/profile`);
      if (!res.ok) {
        setUser(null);
        setIsAuthenticated(false);
      } else {
        const data = await res.json();
        setUser(data);
        setIsAuthenticated(true);
        localStorage.setItem('user', JSON.stringify(data));
      }
    } catch {
      setUser(null);
      setIsAuthenticated(false);
    } finally {
      setLoading(false);
    }
  }, [authFetch]);

  useEffect(() => { rehydrate(); }, [rehydrate]);

  return (
    <AuthContext.Provider value={{ user, setUser, isAuthenticated, loading, logout, rehydrate, authFetch }}>
      {children}
    </AuthContext.Provider>
  );
};

export const useAuth = () => useContext(AuthContext);