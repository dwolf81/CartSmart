import { createContext, useState, useEffect } from "react";

export const AuthContext = createContext();
const API_URL = process.env.REACT_APP_API_URL || 'http://localhost:5000';

export const AuthProvider = ({ children }) => {
  const [isAuthenticated, setIsAuthenticated] = useState(false);

  useEffect(() => {
    const checkAuth = async () => {
      const token = sessionStorage.getItem("token");
      if (!token) return;

      const response = await fetch(`${API_URL}/api/auth/check-auth`, {
        method: "GET",
        headers: { Authorization: `Bearer ${token}` },
      });

      if (response.ok) {
        setIsAuthenticated(true);
      } else {
        sessionStorage.removeItem("token");
      }
    };

    checkAuth();
  }, []);

  return <AuthContext.Provider value={{ isAuthenticated }}>{children}</AuthContext.Provider>;
};
