import { useEffect, useState, useCallback } from 'react';

const API_URL = process.env.REACT_APP_API_URL || 'http://localhost:5000';

export const useUsers = () => {
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);
;


  const getUserBySlug = useCallback(async (slug) => {
    try {
      const response = await fetch(`${API_URL}/api/Users/${slug}/getprofile`, {
        credentials: 'include'
      });

      if (!response.ok) {
        throw new Error('Failed to fetch user');
      }

      const data = await response.json();
      return data;
    } catch (err) {
      console.error('Error fetching user:', err);
      throw err;
    }
  }, []);



  return {
    loading,
    error,
    getUserBySlug
  };
}; 