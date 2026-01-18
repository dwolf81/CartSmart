import React, { useEffect, useState } from 'react';
import { Helmet } from 'react-helmet-async';
import { Link } from 'react-router-dom';
import LoadingSpinner from './LoadingSpinner';

const resolveApiBaseUrl = () => {
  const configured = process.env.REACT_APP_API_URL;
  if (configured) return configured;

  // If we're running the CRA dev server (commonly :3000) and no env var is set,
  // assume the API is on :5000.
  if (typeof window !== 'undefined' && window.location?.port === '3000')
    return 'http://localhost:5000';

  // Otherwise, default to same-origin (typical when the API serves the SPA).
  if (typeof window !== 'undefined' && window.location?.origin)
    return window.location.origin;

  return 'http://localhost:5000';
};

const API_URL = resolveApiBaseUrl();
const SITE_URL = process.env.REACT_APP_SITE_URL || (typeof window !== 'undefined' ? window.location.origin : '');

const CategoriesPage = () => {
  const [productTypes, setProductTypes] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);

  useEffect(() => {
    const load = async () => {
      try {
        setLoading(true);
        setError(null);

        const url = `${API_URL}/api/producttypes?_=${Date.now()}`;
        const resp = await fetch(url, {
          credentials: 'include',
          cache: 'no-store'
        });

        if (!resp.ok) throw new Error(`Failed to fetch categories (${resp.status})`);

        const data = await resp.json();
        setProductTypes(Array.isArray(data) ? data : []);
      } catch (e) {
        console.error('Error loading categories:', e);
        setError(e.message || 'Failed to load categories');
      } finally {
        setLoading(false);
      }
    };

    load();
  }, []);

  if (loading) return <LoadingSpinner />;

  return (
    <div className="min-h-screen">
      <Helmet>
        <title>Categories — CartSmart</title>
        <meta name="description" content="Browse deals by category on CartSmart." />
        <link rel="canonical" href={`${SITE_URL}/categories`} />
        <meta name="robots" content="index,follow" />
      </Helmet>

      <div className="container mx-auto px-4 py-12">
        <div className="max-w-3xl mx-auto text-center mb-12">
          <h1 className="text-3xl font-bold text-gray-900 mb-3">Categories</h1>
          <p className="text-gray-600">Pick a category to see today’s best deals.</p>
        </div>
        {error && (
          <div className="bg-red-50 border border-red-200 text-red-700 rounded-lg p-4 mb-6">
            {error}
          </div>
        )}

        {!error && productTypes.length === 0 ? (
          <div className="text-center text-gray-600">No categories found.</div>
        ) : (
          <div className="grid md:grid-cols-2 lg:grid-cols-3 gap-8">
            {productTypes
              .filter(pt => (pt?.name || pt?.Name))
              .map(pt => {
                const name = pt?.name ?? pt?.Name;
                const id = pt?.id ?? pt?.Id;
                const slug = pt?.slug ?? pt?.Slug;
                const href = slug ? `/categories/${encodeURIComponent(slug)}` : `/categories/${encodeURIComponent(name)}`;
                const collage = (pt?.imageUrls ?? pt?.image_urls ?? pt?.ImageUrls ?? [])
                  .filter(Boolean)
                  .slice(0, 4);

                const Collage = () => {
                  if (!collage.length) return null;

                  if (collage.length === 1) {
                    return (
                      <div className="w-full h-40 bg-gray-100">
                        <img
                          src={collage[0]}
                          alt={`${name} collage`}
                          className="w-full h-full object-cover"
                          loading="lazy"
                        />
                      </div>
                    );
                  }

                  if (collage.length === 2) {
                    return (
                      <div className="w-full h-40 bg-gray-100 flex">
                        {collage.map((src, idx) => (
                          <img
                            key={src ?? idx}
                            src={src}
                            alt={`${name} collage ${idx + 1}`}
                            className="w-1/2 h-full object-cover"
                            loading="lazy"
                          />
                        ))}
                      </div>
                    );
                  }

                  if (collage.length === 3) {
                    return (
                      <div className="w-full h-40 bg-gray-100 grid grid-cols-2 grid-rows-2">
                        <img
                          src={collage[0]}
                          alt={`${name} collage 1`}
                          className="col-span-1 row-span-2 w-full h-full object-cover"
                          loading="lazy"
                        />
                        <img
                          src={collage[1]}
                          alt={`${name} collage 2`}
                          className="col-span-1 row-span-1 w-full h-full object-cover"
                          loading="lazy"
                        />
                        <img
                          src={collage[2]}
                          alt={`${name} collage 3`}
                          className="col-span-1 row-span-1 w-full h-full object-cover"
                          loading="lazy"
                        />
                      </div>
                    );
                  }

                  return (
                    <div className="w-full h-40 bg-gray-100 grid grid-cols-2 grid-rows-2">
                      {collage.map((src, idx) => (
                        <img
                          key={src ?? idx}
                          src={src}
                          alt={`${name} collage ${idx + 1}`}
                          className="w-full h-full object-cover"
                          loading="lazy"
                        />
                      ))}
                    </div>
                  );
                };

                return (
                  <Link
                    key={id ?? name}
                    to={href}
                    className="bg-white rounded-lg shadow-lg overflow-hidden hover:opacity-95 transition-opacity"
                  >
                    <Collage />
                    <div className="p-6">
                      <div className="text-xl font-semibold text-gray-900 mb-2">{name}</div>
                      <div className="text-gray-600 mb-4">View deals in this category</div>
                      <div className="inline-block bg-[#4CAF50] text-white px-6 py-2 rounded-lg hover:bg-[#3d8b40] transition-colors">
                        See Products
                      </div>
                    </div>
                  </Link>
                );
              })}
          </div>
        )}
      </div>
    </div>
  );
};

export default CategoriesPage;
