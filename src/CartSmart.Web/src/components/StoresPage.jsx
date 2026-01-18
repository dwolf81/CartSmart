import React, { useEffect, useState } from 'react';
import { Helmet } from 'react-helmet-async';
import { Link } from 'react-router-dom';
import LoadingSpinner from './LoadingSpinner';
import { useAuth } from '../context/AuthContext';
import AdminStoreModal from './AdminStoreModal';

const resolveApiBaseUrl = () => {
  const configured = process.env.REACT_APP_API_URL;
  if (configured) return configured;

  if (typeof window !== 'undefined' && window.location?.port === '3000')
    return 'http://localhost:5000';

  if (typeof window !== 'undefined' && window.location?.origin)
    return window.location.origin;

  return 'http://localhost:5000';
};

const API_URL = resolveApiBaseUrl();
const SITE_URL = process.env.REACT_APP_SITE_URL || (typeof window !== 'undefined' ? window.location.origin : '');

const StoresPage = () => {
  const { user, isAuthenticated } = useAuth();
  const isAdmin = isAuthenticated && !!user?.admin;

  const [stores, setStores] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);

  const [isAdminAddOpen, setIsAdminAddOpen] = useState(false);

  const loadStores = async ({ cacheBust = false } = {}) => {
    try {
      setLoading(true);
      setError(null);

      const url = `${API_URL}/api/stores${cacheBust ? `?_=${Date.now()}` : ''}`;
      const resp = await fetch(url, { credentials: 'include', cache: 'no-store' });
      if (!resp.ok) throw new Error(`Failed to fetch stores (${resp.status})`);

      const data = await resp.json();
      setStores(Array.isArray(data) ? data : []);
    } catch (e) {
      console.error('Error loading stores:', e);
      setError(e.message || 'Failed to load stores');
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    loadStores();
  }, []);

  if (loading) return <LoadingSpinner />;

  return (
    <div className="min-h-screen">
      <Helmet>
        <title>Stores — CartSmart</title>
        <meta name="description" content="Browse deals by store on CartSmart." />
        <link rel="canonical" href={`${SITE_URL}/stores`} />
        <meta name="robots" content="index,follow" />
      </Helmet>

      <div className="container mx-auto px-4 py-12">
        <div className="max-w-3xl mx-auto text-center mb-12">
          <h1 className="text-3xl font-bold text-gray-900 mb-3">Stores</h1>
          <p className="text-gray-600">Pick a store to browse its best deals.</p>
        </div>

        {isAdmin && (
          <div className="-mt-6 mb-6 flex justify-end">
            <button
              type="button"
              onClick={() => setIsAdminAddOpen(true)}
              className="h-10 px-4 rounded-lg bg-blue-600 text-white text-sm hover:bg-blue-700 transition-colors"
            >
              Add Store
            </button>
          </div>
        )}

        {error && (
          <div className="bg-red-50 border border-red-200 text-red-700 rounded-lg p-4 mb-6">
            {error}
          </div>
        )}

        {!error && stores.length === 0 ? (
          <div className="text-center text-gray-600">No stores found.</div>
        ) : (
          <div className="grid md:grid-cols-2 lg:grid-cols-3 gap-8">
            {stores
              .filter(s => (s?.name || s?.Name))
              .map(s => {
                const name = s?.name ?? s?.Name;
                const id = s?.id ?? s?.Id;
                const url = s?.url ?? s?.URL;
                const slug = s?.slug ?? s?.Slug;
                const imageUrl = s?.imageUrl ?? s?.image_url ?? s?.ImageUrl;

                return (
                  <div
                    key={id ?? name}
                    className="bg-white rounded-lg shadow-lg overflow-hidden"
                  >
                    <div className="p-6">
                      <div className="flex items-start justify-between gap-4 mb-4">
                        <div className="min-w-0">
                          <div className="text-xl font-semibold text-gray-900 leading-7">{name}</div>
                          {url && (
                            <div className="text-sm text-gray-600 break-words leading-7">
                              <a
                                href={`https://${url}`}
                                target="_blank"
                                rel="noopener noreferrer"
                                className="text-gray-600 hover:text-[#4CAF50] break-words"
                              >
                                {url}
                              </a>
                            </div>
                          )}
                        </div>

                        {imageUrl && (
                          <div className="w-20 h-20 bg-gray-100 rounded-lg overflow-hidden flex items-start justify-center shrink-0">
                            <img
                              src={imageUrl}
                              alt={name ? `${name} logo` : 'Store'}
                              className="w-full h-full object-contain object-top"
                              loading="lazy"
                            />
                          </div>
                        )}
                      </div>
                      {slug ? (
                        <Link
                          to={`/stores/${encodeURIComponent(slug)}`}
                          className="inline-block bg-[#4CAF50] text-white px-6 py-2 rounded-lg hover:bg-[#3d8b40] transition-colors"
                        >
                          View Deals
                        </Link>
                      ) : (
                        <div className="inline-block bg-gray-900 text-white px-6 py-2 rounded-lg opacity-70 cursor-default">
                          Store Page Unavailable
                        </div>
                      )}
                    </div>
                  </div>
                );
              })}
          </div>
        )}
      </div>

      <AdminStoreModal
        isOpen={isAdminAddOpen}
        onClose={() => setIsAdminAddOpen(false)}
        mode="add"
        onCreated={() => {
          setIsAdminAddOpen(false);
          loadStores({ cacheBust: true });
        }}
      />
    </div>
  );
};

export default StoresPage;
