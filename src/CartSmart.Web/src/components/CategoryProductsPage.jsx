import React, { useEffect, useMemo, useState } from 'react';
import { Helmet } from 'react-helmet-async';
import { Link, useParams } from 'react-router-dom';
import LoadingSpinner from './LoadingSpinner';
import AdminProductModal from './AdminProductModal';
import { useAuth } from '../context/AuthContext';

const resolveApiBaseUrl = () => {
  const configured = process.env.REACT_APP_API_URL;
  if (configured) return configured.replace(/\/+$/, '');

  if (typeof window !== 'undefined' && window.location)
  {
    const { hostname, port, origin } = window.location;
    const isLocalhost = hostname === 'localhost' || hostname === '127.0.0.1';
    if (isLocalhost && port && port !== '5000') return `http://${hostname}:5000`;
    if (origin) return origin.replace(/\/+$/, '');
  }

  return 'http://localhost:5000';
};

const API_URL = resolveApiBaseUrl();
const SITE_URL = process.env.REACT_APP_SITE_URL || (typeof window !== 'undefined' ? window.location.origin : '');

const CategoryProductsPage = () => {
  const { productType } = useParams();
  const { user } = useAuth();
  const decodedProductTypeSlug = useMemo(() => {
    try {
      return decodeURIComponent(productType || '');
    } catch {
      return productType || '';
    }
  }, [productType]);

  const [categoryName, setCategoryName] = useState('');
  const [categoryTypeId, setCategoryTypeId] = useState(null);
  const [categoryTypeSlug, setCategoryTypeSlug] = useState('');

  const [isAdminModalOpen, setIsAdminModalOpen] = useState(false);
  const [reloadTick, setReloadTick] = useState(0);

  const [displayCount, setDisplayCount] = useState(6);
  const [deals, setDeals] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);

  // Filters (match StorePage dropdown formatting)
  const [openDropdown, setOpenDropdown] = useState(null);
  const [brandId, setBrandId] = useState(null);
  const [brands, setBrands] = useState([]);
  const [brandsLoading, setBrandsLoading] = useState(false);

  useEffect(() => {
    if (!openDropdown) return;
    const onMouseDown = (e) => {
      const target = e.target;
      if (target?.closest?.('[data-dropdown-root="true"]')) return;
      setOpenDropdown(null);
    };
    window.addEventListener('mousedown', onMouseDown);
    return () => window.removeEventListener('mousedown', onMouseDown);
  }, [openDropdown]);

  useEffect(() => {
    const load = async () => {
      try {
        setLoading(true);
        setError(null);
        setDisplayCount(6);

        let resolvedName = '';

        const typesResp = await fetch(`${API_URL}/api/producttypes?_=${Date.now()}`, { credentials: 'include', cache: 'no-store' });
        if (typesResp.ok) {
          const types = await typesResp.json();
          const list = Array.isArray(types) ? types : [];
          const needle = (decodedProductTypeSlug || '').toString().trim().toLowerCase();
          const matched = list.find((pt) => ((pt?.slug ?? pt?.Slug) || '').toString().trim().toLowerCase() === needle);

          resolvedName = matched?.name ?? matched?.Name ?? '';
          setCategoryName(resolvedName);
          setCategoryTypeId(matched?.id ?? matched?.Id ?? null);
          setCategoryTypeSlug(matched?.slug ?? matched?.Slug ?? decodedProductTypeSlug);
        }

        // Load available brands for this category (public endpoint).
        try {
          setBrandsLoading(true);
          const brandsResp = await fetch(
            `${API_URL}/api/products/brands/byproducttype?productType=${encodeURIComponent(decodedProductTypeSlug || '')}`,
            { credentials: 'include' }
          );
          if (brandsResp.ok) {
            const data = await brandsResp.json();
            setBrands(Array.isArray(data) ? data : []);
          } else {
            setBrands([]);
          }
        } catch {
          setBrands([]);
        } finally {
          setBrandsLoading(false);
        }

        // Backend accepts product type slug OR name.
        const qp = new URLSearchParams();
        qp.set('productType', decodedProductTypeSlug || '');
        if (brandId) qp.set('brandId', String(brandId));
        const resp = await fetch(`${API_URL}/api/products/byproducttype?${qp.toString()}`, {
          credentials: 'include'
        });

        if (!resp.ok) throw new Error('Failed to fetch deals');

        const data = await resp.json();
        setDeals(Array.isArray(data) ? data : []);
      } catch (e) {
        console.error('Error loading category deals:', e);
        setError(e.message || 'Failed to load deals');
      } finally {
        setLoading(false);
      }
    };

    if (decodedProductTypeSlug) load();
    else {
      setDeals([]);
      setLoading(false);
      setError('Missing category');
    }
  }, [decodedProductTypeSlug, reloadTick, brandId]);

  // Reset brand filter when category changes
  useEffect(() => {
    setBrandId(null);
    setOpenDropdown(null);
  }, [decodedProductTypeSlug]);

  const brandOptions = useMemo(() => {
    const list = Array.isArray(brands) ? brands : [];
    return list
      .map(b => ({ id: Number(b?.id ?? b?.Id), name: b?.name ?? b?.Name }))
      .filter(b => Number.isFinite(b.id) && b.id > 0)
      .sort((a, b) => (a.name || '').localeCompare(b.name || ''));
  }, [brands]);

  const brandLabel = (id) => {
    if (!id) return 'All';
    const found = brandOptions.find(x => Number(x.id) === Number(id));
    return found?.name || 'Brand';
  };

  const formatPrice = (price) => {
    if (price === null || price === undefined) return '$0.00';
    return new Intl.NumberFormat('en-US', {
      style: 'currency',
      currency: 'USD',
      minimumFractionDigits: 2,
      maximumFractionDigits: 2
    }).format(parseFloat(price));
  };

  const visibleDeals = deals.slice(0, displayCount);
  const hasMoreDeals = displayCount < deals.length;

  if (loading) return <LoadingSpinner />;

  const pageTitle = categoryName ? `${categoryName} Deals — CartSmart` : 'Category Deals — CartSmart';
  const pageDescription = categoryName
    ? `Browse today’s best ${categoryName} deals on CartSmart.`
    : 'Browse today’s best deals on CartSmart.';

  return (
    <div className="min-h-screen">
      <Helmet>
        <title>{pageTitle}</title>
        <meta name="description" content={pageDescription} />
        <link rel="canonical" href={`${SITE_URL}/categories/${encodeURIComponent(categoryTypeSlug || decodedProductTypeSlug)}`} />
        <meta name="robots" content="index,follow" />
      </Helmet>

      <div className="container mx-auto px-4 py-12">
        <div className="max-w-3xl mx-auto text-center mb-12">
          <h1 className="text-3xl font-bold text-gray-900 mb-3">{categoryName || 'Category'}</h1>
          <p className="text-gray-600">Today’s best deals in this category.</p>
        </div>
        {error && (
          <div className="bg-red-50 border border-red-200 text-red-700 rounded-lg p-4 mb-6">
            {error}
          </div>
        )}

        {!error && (
          <>
            <div className="mb-6 flex flex-col sm:flex-row sm:flex-wrap items-stretch sm:items-center gap-3">
              <div className={`relative w-full sm:w-auto ${openDropdown === 'brand' ? 'z-50' : 'z-30'}`} data-dropdown-root="true">
                <button
                  type="button"
                  onClick={() => setOpenDropdown(openDropdown === 'brand' ? null : 'brand')}
                  className="w-full sm:w-auto px-4 py-2 border rounded-lg bg-white hover:bg-gray-50 flex items-center justify-between gap-2"
                >
                  <span>Brand: {brandLabel(brandId)}</span>
                  <svg className="w-4 h-4 text-[#4CAF50]" fill="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M19 9l-7 7-7-7" />
                  </svg>
                </button>

                {openDropdown === 'brand' && (
                  <div className="absolute z-50 mt-1 w-full sm:w-64 bg-white rounded-lg shadow-lg border">
                    <button
                      type="button"
                      onClick={() => {
                        setBrandId(null);
                        setOpenDropdown(null);
                        setDisplayCount(6);
                      }}
                      className={`block w-full text-left px-4 py-2 hover:bg-gray-100 ${!brandId ? 'bg-[#e8f5e9] text-[#4CAF50]' : ''}`}
                    >
                      All
                    </button>

                    {brandOptions.map((b) => (
                      <button
                        key={b.id}
                        type="button"
                        onClick={() => {
                          setBrandId(b.id);
                          setOpenDropdown(null);
                          setDisplayCount(6);
                        }}
                        className={`block w-full text-left px-4 py-2 hover:bg-gray-100 ${Number(brandId) === Number(b.id) ? 'bg-[#e8f5e9] text-[#4CAF50]' : ''}`}
                      >
                        {b.name || 'Brand'}
                      </button>
                    ))}

                    {!brandsLoading && brandOptions.length === 0 && (
                      <div className="px-4 py-2 text-sm text-gray-500">No brands available</div>
                    )}
                    {brandsLoading && (
                      <div className="px-4 py-2 text-sm text-gray-500">Loading…</div>
                    )}
                  </div>
                )}
              </div>

              {user?.admin && (
                <div className="w-full sm:w-auto sm:ml-auto flex justify-end">
                  <button
                    type="button"
                    onClick={() => setIsAdminModalOpen(true)}
                    className="inline-block bg-blue-600 text-white px-6 py-2 rounded-lg font-semibold hover:bg-blue-700 transition-colors"
                  >
                    Add Product
                  </button>
                </div>
              )}
            </div>

            {deals.length === 0 ? (
              <div className="text-center text-gray-600">No deals found for this category.</div>
            ) : (
              <>
                <div className="grid md:grid-cols-2 lg:grid-cols-3 gap-8">
                  {visibleDeals.map((bestDeal) => (
                    <div key={bestDeal.product_id ?? bestDeal.slug} className="bg-white rounded-lg shadow-lg overflow-hidden">
                      <Link to={`/products/${bestDeal.slug}`} className="block hover:opacity-90 transition-opacity">
                        <img
                          src={bestDeal.product_image_url}
                          alt={bestDeal.product_name}
                          className="w-full h-48 object-cover"
                        />
                      </Link>
                      <div className="p-6">
                        <Link
                          to={`/products/${bestDeal.slug}`}
                          className="text-xl font-semibold text-gray-900 hover:text-[#4CAF50] mb-2 block"
                        >
                          {bestDeal.product_name}
                        </Link>
                        
                        <div className="flex items-center justify-between mb-4">
                          <div>
                            <span className="text-sm text-gray-500">Regular price</span>
                            <div className="text-lg text-gray-900 line-through">{formatPrice(bestDeal.msrp)}</div>
                          </div>
                          <div className="text-right">
                            <span className="text-sm text-gray-500">Lowest price</span>
                            <div className="text-2xl font-bold text-green-600">{formatPrice(bestDeal.price)}</div>
                            <div>
                              {bestDeal.discount_amt != null && (
                                <span className="text-sm text-red-600 font-semibold">Save {formatPrice(bestDeal.discount_amt)}</span>
                              )}
                            </div>
                          </div>
                        </div>
                        <div className="flex items-end justify-between">
                          {bestDeal.user_name ? (
                            <Link to={`/profile/${bestDeal.user_name}`} className="flex items-end gap-2">
                              {bestDeal.user_image_url && (
                                <img
                                  src={bestDeal.user_image_url}
                                  alt={bestDeal.user_name}
                                  className="w-6 h-6 rounded-full"
                                />
                              )}
                              <div className="flex items-center gap-1">
                                <span className="text-sm text-gray-600">@{bestDeal.user_name}</span>
                                {bestDeal.level != null && (
                                  <span className="text-xs text-gray-500">({bestDeal.level}%)</span>
                                )}
                              </div>
                            </Link>
                          ) : (
                            <div />
                          )}
                          <Link
                            to={`/products/${bestDeal.slug}`}
                            className="bg-[#4CAF50] text-white px-6 py-2 rounded-lg hover:bg-[#3d8b40] transition-colors"
                          >
                            See Deals
                          </Link>
                        </div>
                      </div>
                    </div>
                  ))}
                </div>

                {hasMoreDeals && (
                  <div className="text-center mt-12">
                    <button
                      onClick={() => setDisplayCount((c) => Math.min(c + 6, deals.length))}
                      className="inline-block bg-gray-900 text-white px-8 py-3 rounded-lg font-semibold hover:bg-gray-800 transition-colors"
                    >
                      View More Deals
                    </button>
                  </div>
                )}
              </>
            )}
          </>
        )}
      </div>

      {user?.admin && (
        <AdminProductModal
          isOpen={isAdminModalOpen}
          onClose={() => setIsAdminModalOpen(false)}
          mode="add"
          productTypeId={categoryTypeId}
          closeOnCreated
          onCreated={() => {
            setIsAdminModalOpen(false);
            setReloadTick((t) => t + 1);
          }}
        />
      )}
    </div>
  );
};

export default CategoryProductsPage;
