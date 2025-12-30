import { useEffect, useState, useCallback } from 'react';

const API_URL = process.env.REACT_APP_API_URL || 'http://localhost:5000';

export const useProducts = () => {
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);
  const [products, setProducts] = useState([]);

  const fetchProducts = useCallback(async () => {
    try {
      setLoading(true);
      
      const response = await fetch(`${API_URL}/api/products/`, {
        credentials: 'include'
      });
      
      if (!response.ok) {
        throw new Error('Failed to fetch products');
      }

      const data = await response.json();
      setProducts(data || []);
    } catch (err) {
      console.error('Error fetching products:', err);
      setError(err.message);
    } finally {
      setLoading(false);
    }
  }, []);

  const getProductBySlug = useCallback(async (slug) => {
    try {
      const response = await fetch(`${API_URL}/api/products/getproduct/${slug}`, {
        credentials: 'include'
      });

      if (!response.ok) {
        throw new Error('Failed to fetch product');
      }

      const data = await response.json();
      return data;
    } catch (err) {
      console.error('Error fetching product:', err);
      throw err;
    }
  }, []);

  useEffect(() => {
    fetchProducts();
  }, [fetchProducts]);

  return {
    products,
    loading,
    error,
    getProductBySlug,
    fetchProducts
  };
}; 