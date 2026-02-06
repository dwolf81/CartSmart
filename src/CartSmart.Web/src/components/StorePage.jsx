import React, { useEffect, useMemo, useRef, useState } from 'react';
import { Helmet } from 'react-helmet-async';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { FaTag, FaTicketAlt, FaLink, FaLayerGroup, FaPlus, FaQuestionCircle } from 'react-icons/fa';
import LoadingSpinner from './LoadingSpinner';
import SubmitDealModal from './SubmitDealModal';
import { useAuth } from '../context/AuthContext';
import AdminStoreModal from './AdminStoreModal';
import { appendAffiliateParam, getAffiliateFields } from '../utils/affiliateUrl';
import RewardsTooltipPill from './RewardsTooltipPill';

const resolveApiBaseUrl = () => {
  const configured = process.env.REACT_APP_API_URL;
  if (configured) return configured.replace(/\/+$/, '');

  if (typeof window !== 'undefined' && window.location)
  {
    const { hostname, port, protocol, origin } = window.location;
    const isLocalhost = hostname === 'localhost' || hostname === '127.0.0.1';
    // In local dev, the API is typically on :5000 while the web app may be on :3000, :5173, etc.
    // Always use http for the local API unless you explicitly configured REACT_APP_API_URL.
    if (isLocalhost && port && port !== '5000') return `http://${hostname}:5000`;
    if (origin) return origin.replace(/\/+$/, '');
  }

  return 'http://localhost:5000';
};

const API_URL = resolveApiBaseUrl();
const SITE_URL = process.env.REACT_APP_SITE_URL || (typeof window !== 'undefined' ? window.location.origin : '');

const BUTTON_STYLES = {
  base: 'inline-flex items-center justify-center gap-2 h-10 px-4 rounded-lg text-white text-sm shadow-sm transition-colors',
  green: 'bg-[#4CAF50] hover:bg-[#3d8b40]',
  blue: 'bg-blue-600 hover:bg-blue-700',
  disabled: 'bg-gray-300 cursor-not-allowed',
  flagLink: 'text-sm text-slate-500 hover:text-slate-700 underline underline-offset-2'
};

const DEAL_FLAG_REASONS = [
  { id: 1, label: 'Inaccurate' },
  { id: 2, label: 'Out of Stock' },
  { id: 3, label: 'Coupon Invalid' },
  { id: 4, label: 'Expired' },
  { id: 5, label: 'Spam' },
  { id: 6, label: 'Other (Describe)' }
];

const DEAL_TYPE_META = {
  1: {
    label: 'Direct',
    icon: <FaTag className="inline mr-1 text-slate-600" title="Direct Deal" />,
    badge: 'inline-flex items-center gap-2 bg-white border border-slate-200 text-slate-700 px-2 py-1 rounded-md shadow-sm'
  },
  2: {
    label: 'Coupon',
    icon: <FaTicketAlt className="inline mr-1 text-emerald-600" title="Coupon Deal" />,
    badge: 'inline-flex items-center gap-2 bg-white border border-emerald-200 text-emerald-700 px-2 py-1 rounded-md shadow-sm'
  },
  3: {
    label: 'Stacked',
    icon: <FaLayerGroup className="inline mr-1 text-amber-600" title="Stacked Deal" />,
    badge: 'inline-flex items-center gap-2 bg-white border border-amber-200 text-amber-700 px-2 py-1 rounded-md shadow-sm'
  },
  4: {
    label: 'External',
    icon: <FaLink className="inline mr-1 text-indigo-600" title="External Offer" />,
    badge: 'inline-flex items-center gap-2 bg-white border border-indigo-200 text-indigo-700 px-2 py-1 rounded-md shadow-sm'
  }
};

const StorePage = () => {
  const { slug } = useParams();
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const { user, isAuthenticated } = useAuth();
  const isAdmin = isAuthenticated && !!user?.admin;

  // Product tab filters (ProductPage-style dropdown)
  const [openDropdown, setOpenDropdown] = useState(null);
  const [productTypes, setProductTypes] = useState([]);
  const [productTypesLoading, setProductTypesLoading] = useState(false);
  const productTypesFetchStartedRef = useRef(false);

  const decodedSlug = useMemo(() => {
    try {
      return decodeURIComponent(slug || '');
    } catch {
      return slug || '';
    }
  }, [slug]);

  const [store, setStore] = useState(null);
  const [storeDeals, setStoreDeals] = useState([]);
  const [products, setProducts] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);
  const [displayCount, setDisplayCount] = useState(6);

  // Flagging (mirrors ProductPage behavior)
  const [isFlagModalOpen, setIsFlagModalOpen] = useState(false);
  const [dealToFlag, setDealToFlag] = useState(null);
  const [flaggedDeals, setFlaggedDeals] = useState({});
  const [flagReasonId, setFlagReasonId] = useState(null);
  const [flagComment, setFlagComment] = useState('');
  const [flagSubmitting, setFlagSubmitting] = useState(false);
  const [adminDeleteDeal, setAdminDeleteDeal] = useState(false);

  const isAdminDeleteMode = isAdmin && adminDeleteDeal;

  // Stacked deal steps accordion state
  const [expandedStackedSteps, setExpandedStackedSteps] = useState({});

  const [isSubmitDealOpen, setIsSubmitDealOpen] = useState(false);

  // Store Deals tab filter (match ProductPage formatting)
  const [storeDealTypeId, setStoreDealTypeId] = useState(null);

  const resolvedTab = useMemo(() => {
    const tab = (searchParams.get('tab') || '').toLowerCase();
    return tab === 'products' ? 'products' : 'storeDeals';
  }, [searchParams]);

  const [activeTab, setActiveTab] = useState(resolvedTab);

  useEffect(() => {
    setActiveTab(resolvedTab);
  }, [resolvedTab]);

  // Close dropdown when clicking outside (mirrors ProductPage)
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

  const [isAdminEditOpen, setIsAdminEditOpen] = useState(false);

  const productTypeIdFromQuery = useMemo(() => {
    const raw = searchParams.get('productTypeId');
    if (!raw) return null;
    const n = Number(raw);
    if (!Number.isFinite(n) || n <= 0) return null;
    return n;
  }, [searchParams]);

  const loadStore = async ({ cacheBust = false } = {}) => {
    try {
      setLoading(true);
      setError(null);
      setDisplayCount(6);

      const qp = new URLSearchParams();
      if (cacheBust) qp.set('_', String(Date.now()));
      if (productTypeIdFromQuery) qp.set('productTypeId', String(productTypeIdFromQuery));
      const qs = qp.toString();
      const url = `${API_URL}/api/stores/${encodeURIComponent(decodedSlug)}${qs ? `?${qs}` : ''}`;
      const resp = await fetch(url, { credentials: 'include', cache: 'no-store' });
      if (!resp.ok) throw new Error(`Failed to fetch store (${resp.status})`);

      const data = await resp.json();
      setStore(data?.store ?? null);
      setStoreDeals(Array.isArray(data?.storeDeals) ? data.storeDeals : []);
      setProducts(Array.isArray(data?.products) ? data.products : []);
    } catch (e) {
      console.error('Error loading store page:', e);
      setError(e.message || 'Failed to load store');
    } finally {
      setLoading(false);
    }
  };

  const setTab = (nextTab) => {
    const nextParams = new URLSearchParams(searchParams);
    if (nextTab === 'products') nextParams.set('tab', 'products');
    else nextParams.delete('tab');

    // Push history so Back/Forward cycles tabs naturally.
    setSearchParams(nextParams, { replace: false });
    setActiveTab(nextTab);
  };

  useEffect(() => {
    if (decodedSlug) loadStore();
    else {
      setStore(null);
      setStoreDeals([]);
      setProducts([]);
      setLoading(false);
      setError('Missing store');
    }
  }, [decodedSlug, productTypeIdFromQuery]);

  // Reset store-scoped category list when store changes
  useEffect(() => {
    productTypesFetchStartedRef.current = false;
    setProductTypes([]);
  }, [store?.id]);

  // Load product types for Category dropdown (once, on demand)
  useEffect(() => {
    if (activeTab !== 'products') return;
    const storeId = store?.id || store?.Id;
    if (!storeId) return;
    if (productTypesFetchStartedRef.current) return;
    if (productTypes && productTypes.length > 0) return;

    productTypesFetchStartedRef.current = true;

    const controller = new AbortController();
    const timeoutId = window.setTimeout(() => controller.abort(), 10000);

    const run = async () => {
      try {
        setProductTypesLoading(true);
        const resp = await fetch(`${API_URL}/api/producttypes/by-store/${encodeURIComponent(String(storeId))}`, { credentials: 'include', signal: controller.signal });
        if (!resp.ok) {
          // Allow retry (e.g. when user opens dropdown) if the request fails.
          productTypesFetchStartedRef.current = false;
          return;
        }
        const data = await resp.json();
        setProductTypes(Array.isArray(data) ? data : []);
      } catch (e) {
        // ignore; category filter is optional
        console.warn('Failed to load product types:', e);
        // Allow retry on next dropdown open / tab switch.
        productTypesFetchStartedRef.current = false;
      } finally {
        window.clearTimeout(timeoutId);
        setProductTypesLoading(false);
      }
    };

    run();
    return () => {
      controller.abort();
      window.clearTimeout(timeoutId);
    };
  }, [activeTab, openDropdown, productTypes, store]);

  const setProductTypeFilter = (nextId) => {
    const nextParams = new URLSearchParams(searchParams);
    if (nextId) nextParams.set('productTypeId', String(nextId));
    else nextParams.delete('productTypeId');
    setSearchParams(nextParams, { replace: false });
    setDisplayCount(6);
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

  const getDomain = (url) => {
    if (!url) return '';
    try {
      const u = new URL(url.startsWith('http') ? url : `https://${url}`);
      return u.hostname.replace(/^www\./, '');
    } catch {
      return '';
    }
  };

  const toAbsoluteUrl = (value) => {
    if (value == null) return null;
    const s = String(value).trim();
    if (!s) return null;
    if (s.startsWith('http://') || s.startsWith('https://')) return s;
    return `https://${s}`;
  };

  const logStoreDealClick = (dealId, external) => {
    const id = Number(dealId);
    if (!Number.isFinite(id) || id <= 0) return;
    fetch(`${API_URL}/api/deals/${id}/click?external=${external ? 'true' : 'false'}`, {
      method: 'POST',
      credentials: 'include',
      headers: { 'Content-Type': 'application/json' },
      keepalive: true
    }).catch(() => { });
  };

  const getDealProductId = (d) => {
    const raw = d?.deal_product_id ?? d?.dealProductId ?? d?.DealProductId;
    if (raw == null) return null;
    const n = Number(raw);
    if (!Number.isFinite(n) || n <= 0) return null;
    return n;
  };

  const getDealId = (d) => {
    const raw = d?.deal_id ?? d?.dealId ?? d?.DealId;
    if (raw == null) return null;
    const n = Number(raw);
    if (!Number.isFinite(n) || n <= 0) return null;
    return n;
  };

  const isDealFlagged = (d) => {
    const dId = getDealId(d);
    if (dId) return !!flaggedDeals[`deal:${dId}`];
    const dpId = getDealProductId(d);
    if (dpId) return !!flaggedDeals[`dp:${dpId}`];
    return false;
  };

  const openFlagModal = ({ dealId, dealProductId = null }) => {
    if (!isAuthenticated) {
      navigate(`/login?redirect=${encodeURIComponent(window.location.pathname + window.location.search)}`);
      return;
    }
    const dId = Number(dealId);
    if (!Number.isFinite(dId) || dId <= 0) return;
    const dpId = dealProductId != null ? Number(dealProductId) : null;
    setDealToFlag({ dealId: dId, dealProductId: (Number.isFinite(dpId) && dpId > 0) ? dpId : null });
    setFlagReasonId(null);
    setFlagComment('');
    setAdminDeleteDeal(false);
    setIsFlagModalOpen(true);
  };

  const handleFlagDeal = async ({ dealId, dealProductId = null }) => {
    if (!isAuthenticated) {
      alert('Please log in to flag a deal.');
      return;
    }
    if (!dealId) return;
    if (flaggedDeals[`deal:${dealId}`]) return;
    const adminDeleteMode = isAdmin && adminDeleteDeal;
    if (!adminDeleteMode && !flagReasonId) {
      alert('Please select a reason.');
      return;
    }
    if (!adminDeleteMode && flagReasonId === 6 && !flagComment.trim()) {
      alert('Please describe the issue for "Other".');
      return;
    }

    try {
      setFlagSubmitting(true);

      const resp = adminDeleteMode
        ? await fetch(`${API_URL}/api/deals/admin-delete`, {
          method: 'POST',
          credentials: 'include',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            dealId,
            dealProductId: null,
            deleteDeal: true
          })
        })
        : await fetch(`${API_URL}/api/deals/flag`, {
          method: 'POST',
          credentials: 'include',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            dealId,
            dealProductId,
            dealIssueTypeId: flagReasonId,
            comment: flagComment.trim() || null
          })
        });

      if (!resp.ok) throw new Error('Flag request failed');

      setFlaggedDeals((p) => ({ ...p, [`deal:${dealId}`]: true }));
      setIsFlagModalOpen(false);
      setDealToFlag(null);
      setFlagComment('');
      setAdminDeleteDeal(false);
      if (adminDeleteMode) {
        loadStore({ cacheBust: true });
      }
      alert(
        adminDeleteMode
          ? 'Deal deleted (all product deals removed).'
          : 'Deal flagged. Thank you!'
      );
    } catch (e) {
      console.error(e);
      const adminDeleteMode = isAdmin && adminDeleteDeal;
      alert(adminDeleteMode ? 'Failed to delete deal.' : 'Failed to flag deal.');
    } finally {
      setFlagSubmitting(false);
    }
  };

  const toggleStackedStep = (parentId, stepId) => {
    const key = `${parentId}:${stepId}`;
    setExpandedStackedSteps((s) => ({ ...s, [key]: !s[key] }));
  };

  const renderUpfrontCost = (cost, termId) => {
    if (cost == null) return null;
    const label = termId === 2 ? 'Monthly' : (termId === 3 ? 'Annually' : 'One Time');
    return (
      <span className="ml-2 text-xs text-gray-500">({formatPrice(cost)} {label})</span>
    );
  };

  const categoryOptions = useMemo(() => {
    const types = Array.isArray(productTypes) ? productTypes : [];
    return types
      .map(t => ({ id: Number(t?.id), name: t?.name }))
      .filter(t => Number.isFinite(t.id) && t.id > 0)
      .sort((a, b) => (a.name || '').localeCompare(b.name || ''));
  }, [productTypes]);

  const categoryLabel = (id) => {
    if (!id) return 'All';
    const found = categoryOptions.find(x => Number(x.id) === Number(id));
    return found?.name || 'Category';
  };

  const dealTypeLabel = (dealTypeId) => {
    if (!dealTypeId) return 'All';
    return `${DEAL_TYPE_META[dealTypeId]?.label ?? 'Unknown'} Deal`;
  };

  const filteredStoreDeals = useMemo(() => {
    const rows = Array.isArray(storeDeals) ? storeDeals : [];
    if (!storeDealTypeId) return rows;
    return rows.filter(d => Number(d?.deal_type_id) === Number(storeDealTypeId));
  }, [storeDeals, storeDealTypeId]);

  const visible = products.slice(0, displayCount);
  const hasMore = displayCount < products.length;

  if (loading) return <LoadingSpinner />;

  const storeName = store?.name || store?.Name || 'Store';
  const storeUrl = store?.url || store?.URL;
  const storeImageUrl = store?.imageUrl ?? store?.image_url ?? store?.ImageUrl;

  const hasAnyStoreDeals = Array.isArray(storeDeals) && storeDeals.length > 0;
  const storeDealsEmpty = !hasAnyStoreDeals;
  const filteredStoreDealsEmpty = !filteredStoreDeals || filteredStoreDeals.length === 0;
  const productsEmpty = !products || products.length === 0;

  const pageTitle = `${storeName} — Stores — CartSmart`;
  const pageDescription = `Browse today’s best deals from ${storeName} on CartSmart.`;

  return (
    <div className="min-h-screen">
      <Helmet>
        <title>{pageTitle}</title>
        <meta name="description" content={pageDescription} />
        <link rel="canonical" href={`${SITE_URL}/stores/${encodeURIComponent(decodedSlug)}`} />
        <meta name="robots" content="index,follow" />
      </Helmet>

      <div className="container mx-auto px-4 py-12">
        <div className="mb-10">
          <div className="bg-white rounded-lg shadow-lg p-6">
            <div className="flex flex-col md:flex-row md:items-center md:justify-between gap-4">
              <div className="flex items-center gap-4">
                {storeImageUrl ? (
                  <div className="w-24 h-24 bg-white rounded-lg overflow-hidden flex items-center justify-center shrink-0">
                    <img
                      src={storeImageUrl}
                      alt={storeName ? `${storeName} logo` : 'Store'}
                      className="w-full h-full object-contain"
                      loading="lazy"
                    />
                  </div>
                ) : null}

                <div>
                  <h1 className="text-3xl font-bold text-gray-900">{storeName}</h1>
                  {storeUrl && (
                    <a
                      href={`https://${storeUrl}`}
                      target="_blank"
                      rel="noopener noreferrer"
                      className="text-gray-600 hover:text-[#4CAF50] break-words"
                    >
                      {storeUrl}
                    </a>
                  )}
                </div>
              </div>

              <div className="flex items-center gap-3">
                {isAdmin && store?.id && (
                  <button
                    type="button"
                    onClick={() => setIsAdminEditOpen(true)}
                    className="h-10 px-4 rounded-lg bg-blue-600 text-white text-sm hover:bg-blue-700 transition-colors"
                  >
                    Edit Store
                  </button>
                )}
              </div>
            </div>
          </div>
        </div>

        <div className="mb-8">
          <div className="bg-white rounded-lg shadow-lg">
            <div className="flex items-center gap-6 px-4 border-b border-gray-200">
              <button
                type="button"
                onClick={() => setTab('storeDeals')}
                className={`-mb-px px-2 py-3 text-sm font-semibold whitespace-nowrap ${activeTab === 'storeDeals'
                  ? 'text-gray-900 border-b-2 border-[#4CAF50]'
                  : 'text-gray-600 hover:text-gray-900 border-b-2 border-transparent'}`}
              >
                Store Deals
                <span className="ml-2 inline-flex items-center rounded-full bg-gray-100 px-2 py-0.5 text-xs font-semibold text-gray-700">
                  {storeDeals.length}
                </span>
              </button>
              <button
                type="button"
                onClick={() => setTab('products')}
                className={`-mb-px px-2 py-3 text-sm font-semibold whitespace-nowrap ${activeTab === 'products'
                  ? 'text-gray-900 border-b-2 border-[#4CAF50]'
                  : 'text-gray-600 hover:text-gray-900 border-b-2 border-transparent'}`}
              >
                Products
                <span className="ml-2 inline-flex items-center rounded-full bg-gray-100 px-2 py-0.5 text-xs font-semibold text-gray-700">
                  {products.length}
                </span>
              </button>
            </div>
          </div>
        </div>

        {error && (
          <div className="bg-red-50 border border-red-200 text-red-700 rounded-lg p-4 mb-6">
            {error}
          </div>
        )}

        {!error && activeTab === 'storeDeals' && (
          <>
            {/* Filters (always visible, even when no results) */}
            <div className="mb-6 flex flex-col sm:flex-row sm:flex-wrap items-stretch sm:items-center gap-3">
              <div className={`relative w-full sm:w-auto ${openDropdown === 'dealType' ? 'z-50' : 'z-30'}`} data-dropdown-root="true">
                <button
                  type="button"
                  onClick={() => setOpenDropdown(openDropdown === 'dealType' ? null : 'dealType')}
                  className="w-full sm:w-auto px-4 py-2 border rounded-lg bg-white hover:bg-gray-50 flex items-center justify-between gap-2"
                >
                  <span>Deal Type: {dealTypeLabel(storeDealTypeId)}</span>
                  <svg className="w-4 h-4 text-[#4CAF50]" fill="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M19 9l-7 7-7-7" />
                  </svg>
                </button>
                {openDropdown === 'dealType' && (
                  <div className="absolute z-50 mt-1 w-full sm:w-64 bg-white rounded-lg shadow-lg border">
                    <button
                      type="button"
                      onClick={() => {
                        setStoreDealTypeId(null);
                        setOpenDropdown(null);
                      }}
                      className={`block w-full text-left px-4 py-2 hover:bg-gray-100 ${!storeDealTypeId ? 'bg-[#e8f5e9] text-[#4CAF50]' : ''}`}
                    >
                      All
                    </button>
                    {[2, 1, 4, 3].map(typeId => (
                      <button
                        key={typeId}
                        type="button"
                        onClick={() => {
                          setStoreDealTypeId(typeId);
                          setOpenDropdown(null);
                        }}
                        className={`block w-full text-left px-4 py-2 hover:bg-gray-100 ${Number(storeDealTypeId) === Number(typeId) ? 'bg-[#e8f5e9] text-[#4CAF50]' : ''}`}
                      >
                        <span>{DEAL_TYPE_META[typeId]?.label} Deal</span>
                        <span className="block text-xs text-gray-500 ml-6">{DEAL_TYPE_META[typeId]?.desc}</span>
                      </button>
                    ))}
                  </div>
                )}
              </div>

              <div className="w-full sm:w-auto sm:ml-auto flex justify-end">
                <div className="flex flex-col items-end gap-1">
                  <div className="text-xs text-gray-500">
                    <span>Found a better deal? Prove it - we reward the best deals.</span>
                    <RewardsTooltipPill
                      label={<FaQuestionCircle className="inline ml-1 text-gray-400 hover:text-gray-600" />}
                      pillClassName="inline-flex items-center"
                    />
                  </div>
                  <button
                    type="button"
                    onClick={() => {
                      setOpenDropdown(null);
                      if (!isAuthenticated) {
                        navigate(`/login?redirect=${encodeURIComponent(window.location.pathname)}`);
                        return;
                      }
                      setIsSubmitDealOpen(true);
                    }}
                    className={`${BUTTON_STYLES.base} ${BUTTON_STYLES.green} whitespace-nowrap`}
                    title="Submit a better deal to earn rewards"
                  >
                    <FaPlus className="-ml-0.5" aria-hidden="true" />
                    Beat the Deal
                    <span className="text-xs bg-white/15 px-2 py-0.5 rounded-full">Earn rewards</span>
                  </button>
                </div>
              </div>
            </div>

            {storeDealsEmpty ? (
              <div className="text-center text-gray-600">No store deals found for this store.</div>
            ) : filteredStoreDealsEmpty ? (
              <div className="text-center text-gray-600">No store deals match your filters.</div>
            ) : (
              <div className="grid grid-cols-1 gap-6 justify-items-center items-start">
                {filteredStoreDeals.map((deal) => {
                    const userName = deal?.user_name || 'User';
                    const userImageUrl = deal?.user_image_url;
                    const level = deal?.level;
                    const upfrontCost = deal?.upfront_cost;
                    const upfrontCostTermId = deal?.upfront_cost_term_id;

                    const dealTypeId = deal?.deal_type_id != null ? Number(deal.deal_type_id) : null;
                    const meta = DEAL_TYPE_META[dealTypeId] || {};

                    const storeHost = deal?.store_url || storeUrl;
                    const { affiliateCodeVar, affiliateCode } = getAffiliateFields(deal, 'normal');
                    const { affiliateCodeVar: externalAffiliateCodeVar, affiliateCode: externalAffiliateCode } = getAffiliateFields(deal, 'external');
                    const viewUrl = appendAffiliateParam(
                      toAbsoluteUrl(deal?.url) || toAbsoluteUrl(storeHost),
                      affiliateCodeVar,
                      affiliateCode
                    );
                    const externalOfferUrl = appendAffiliateParam(
                      toAbsoluteUrl(deal?.external_offer_url),
                      externalAffiliateCodeVar,
                      externalAffiliateCode
                    );

                    const steps = Array.isArray(deal?.steps) ? deal.steps : [];
                    const dealId = getDealId(deal) || (dealTypeId === 3 ? getDealId(steps?.[0]) : null);
                    const dealProductId = getDealProductId(deal) || (dealTypeId === 3 ? getDealProductId(steps?.[0]) : null);
                    const canFlag = !!dealId;

                    return (
                      <div key={deal.deal_id} className="bg-white rounded-lg shadow-lg p-6 w-full max-w-3xl">
                        <div className="flex items-start justify-between gap-4 mb-3">
                          <div className="flex flex-wrap items-center gap-2 min-w-0">
                            <span
                              className={`flex items-center px-2 py-1 rounded font-semibold text-sm ${meta.badge || 'bg-gray-100 text-gray-800'}`}
                              title={meta.label ? `${meta.label} Deal` : 'Deal'}
                            >
                              {meta.icon}
                              {meta.label ? `${meta.label} Deal` : 'Deal'}
                            </span>

                            {dealTypeId === 3 && (
                              <span className="text-sm text-amber-700 font-medium">
                                {steps.length} deals
                              </span>
                            )}
                          </div>

                          {deal.discount_percent ? (
                            <span className="bg-green-100 text-green-700 text-xs font-semibold px-2 py-0.5 rounded-full">
                              {deal.discount_percent}% off
                            </span>
                          ) : null}
                        </div>

                        {/* Non-stacked details */}
                        {dealTypeId !== 3 && (
                          <>
                            {(deal.external_offer_url != null || deal.external_offer_store_url) && (
                              <div className="mb-1">
                                <span className="text-gray-600 font-medium text-sm">Activate at:</span>{' '}
                                <span className="text-sm">{deal.external_offer_url || deal.external_offer_store_url || 'N/A'}</span>
                                {deal.external_upfront_cost != null && (
                                  renderUpfrontCost(deal.external_upfront_cost, deal.external_upfront_cost_term_id)
                                )}
                              </div>
                            )}

                            <div className="mb-1">
                              <span className="text-gray-600 font-medium text-sm">Buy at:</span>{' '}
                              <span className="text-sm">{deal.store_url || storeUrl || ''}</span>
                              {upfrontCost != null && renderUpfrontCost(upfrontCost, upfrontCostTermId)}
                            </div>

                            {deal.coupon_code && (
                              <div className="mb-1 text-sm">
                                <span className="text-gray-600 font-medium">Coupon Code:</span>{' '}
                                <code
                                  onClick={() => navigator.clipboard.writeText(deal.coupon_code)}
                                  className="bg-gray-100 px-2 py-1 rounded cursor-pointer hover:bg-gray-200 transition-colors text-sm"
                                  title="Click to copy"
                                >
                                  {deal.coupon_code}
                                </code>
                              </div>
                            )}

                            {deal.additional_details && (
                              <div className="mb-1 text-sm">
                                <span className="text-gray-600 font-medium">Additional Details:</span>{' '}
                                {deal.additional_details}
                              </div>
                            )}
                          </>
                        )}

                        {/* Stacked (combo) deal */}
                        {dealTypeId === 3 && (
                          <>
                            {steps && (
                              <div className="mt-2 flex flex-col divide-y border rounded-md overflow-hidden">
                                {steps.map((step, idx) => {
                                  const stepKey = step.deal_id || `${deal.deal_id}-${idx}`;
                                  const open = !!expandedStackedSteps[`${deal.deal_id}:${stepKey}`];
                                  const stepDealTypeId = step?.deal_type_id != null ? Number(step.deal_type_id) : null;
                                  const stepMeta = DEAL_TYPE_META[stepDealTypeId] || {};

                                  return (
                                    <div key={stepKey}>
                                      <button
                                        type="button"
                                        onClick={() => toggleStackedStep(deal.deal_id, stepKey)}
                                        className="w-full flex items-center gap-3 px-2 py-2 text-left hover:bg-gray-50 transition"
                                      >
                                        <span className="inline-flex h-6 w-6 items-center justify-center rounded-full bg-white border text-xs font-semibold text-slate-700">
                                          {idx + 1}
                                        </span>
                                        <span className="text-sm font-medium text-blue-800 flex items-center gap-1">
                                          {stepMeta.icon}
                                          {stepMeta.label ? `${stepMeta.label} Deal` : 'Deal'}
                                        </span>

                                        {step.discount_percent > 0 && (
                                          <span className="bg-green-100 text-green-700 text-xs font-semibold px-2 py-0.5 rounded-full">
                                            {step.discount_percent}% off
                                          </span>
                                        )}

                                        {step.coupon_code && (
                                          <code className="hidden sm:inline bg-white border px-2 py-0.5 rounded text-sm">
                                            {step.coupon_code}
                                          </code>
                                        )}

                                        <svg
                                          className={`w-4 h-4 ml-auto text-slate-600 transition-transform ${open ? 'rotate-180' : ''}`}
                                          viewBox="0 0 24 24" stroke="currentColor" fill="none" strokeWidth="2"
                                        >
                                          <path strokeLinecap="round" strokeLinejoin="round" d="M19 9l-7 7-7-7" />
                                        </svg>
                                      </button>

                                      {open && (
                                        <div className="px-2 pb-2 pt-1 text-sm text-slate-700">
                                          {(step.external_offer_url != null || step.external_store_url) && (
                                            <div className="mt-2">
                                              <span className="text-gray-600 font-medium">Activate at:</span>{' '}
                                              {step.external_offer_url || step.external_offer_store_url || 'N/A'}
                                              {step.external_upfront_cost != null && (
                                                renderUpfrontCost(step.external_upfront_cost, step.external_upfront_cost_term_id)
                                              )}
                                            </div>
                                          )}

                                          <div className="mt-2">
                                            <span className="text-gray-600 font-medium">Buy at:</span>{' '}
                                            {step.store_url || storeUrl || 'N/A'}
                                            {step.upfront_cost != null && (
                                              renderUpfrontCost(step.upfront_cost, step.upfront_cost_term_id)
                                            )}
                                          </div>

                                          {step.coupon_code && (
                                            <div className="mt-1">
                                              <span className="text-gray-600 font-medium">Coupon Code:</span>{' '}
                                              <code
                                                onClick={() => navigator.clipboard.writeText(step.coupon_code)}
                                                className="bg-gray-100 px-2 py-1 rounded cursor-pointer hover:bg-gray-200 transition-colors text-sm"
                                                title="Click to copy"
                                              >
                                                {step.coupon_code}
                                              </code>
                                            </div>
                                          )}
                                          {step.additional_details && (
                                            <div className="mt-2 text-sm text-gray-600">
                                              <span className="text-gray-600 font-medium">Additional Details:</span> {step.additional_details}
                                            </div>
                                          )}

                                          {/* Step action buttons */}
                                          {stepDealTypeId !== 3 && (
                                            <div className="mt-2 w-full flex flex-col gap-2">
                                              {stepDealTypeId === 4 && toAbsoluteUrl(step.external_offer_url) && (
                                                <div role="group" aria-label="External offer steps" className="flex w-full gap-2 justify-end flex-nowrap">
                                                  <a
                                                    href={appendAffiliateParam(
                                                      toAbsoluteUrl(step.external_offer_url),
                                                      (step?.external_affiliate_code_var ?? step?.externalAffiliateCodeVar ?? externalAffiliateCodeVar),
                                                      (step?.external_affiliate_code ?? step?.externalAffiliateCode ?? externalAffiliateCode)
                                                    )}
                                                    target="_blank"
                                                    rel="noopener noreferrer"
                                                    className={`${BUTTON_STYLES.base} ${BUTTON_STYLES.blue} whitespace-nowrap`}
                                                    onClick={() => logStoreDealClick(step.deal_id, true)}
                                                  >
                                                    Activate Offer
                                                  </a>
                                                  <a
                                                    href={appendAffiliateParam(toAbsoluteUrl(step.url), affiliateCodeVar, affiliateCode)}
                                                    target="_blank"
                                                    rel="noopener noreferrer"
                                                    className={`${BUTTON_STYLES.base} ${BUTTON_STYLES.green} whitespace-nowrap`}
                                                    onClick={() => logStoreDealClick(step.deal_id, false)}
                                                  >
                                                    See {step.store_url ? ` at ${getDomain(step.store_url || step.url)}` : ''}
                                                  </a>
                                                </div>
                                              )}

                                              {stepDealTypeId !== 4 && toAbsoluteUrl(step.url) && (
                                                <div role="group" aria-label="Deal action" className="flex w-full gap-2 justify-end flex-nowrap">
                                                  <a
                                                    href={appendAffiliateParam(toAbsoluteUrl(step.url), affiliateCodeVar, affiliateCode)}
                                                    target="_blank"
                                                    rel="noopener noreferrer"
                                                    className={`${BUTTON_STYLES.base} ${BUTTON_STYLES.green} whitespace-nowrap`}
                                                    onClick={() => logStoreDealClick(step.deal_id, false)}
                                                  >
                                                    See Deal
                                                  </a>
                                                </div>
                                              )}
                                            </div>
                                          )}
                                        </div>
                                      )}
                                    </div>
                                  );
                                })}
                              </div>
                            )}
                          </>
                        )}

                        {/* Footer area (ProductPage-style intent): user bottom-left, actions bottom-right */}
                        <div className="mt-4 flex items-end justify-between gap-3">
                          <div className="flex items-center gap-2 text-xs text-gray-400 min-w-0">
                            <Link to={`/profile/${userName}`} className="flex items-center gap-2 flex-shrink-0 min-w-0">
                              {userImageUrl ? (
                                <img src={userImageUrl} alt={userName} className="w-6 h-6 rounded-full" loading="lazy" />
                              ) : (
                                <div className="w-6 h-6 rounded-full bg-gray-200" />
                              )}
                              <span className="truncate">@{userName}{level != null ? ` (${level}%)` : ''}</span>
                            </Link>
                          </div>

                          <div className="flex flex-col items-end gap-2 shrink-0">
                            {/* Action buttons (match ProductPage behavior by deal type when fields are present) */}
                            {dealTypeId === 4 && externalOfferUrl && viewUrl && (
                              <div role="group" aria-label="External offer steps" className="flex gap-2 justify-end flex-nowrap">
                                <a
                                  href={externalOfferUrl}
                                  target="_blank"
                                  rel="noopener noreferrer"
                                  className={`${BUTTON_STYLES.base} ${BUTTON_STYLES.blue} whitespace-nowrap`}
                                  onClick={() => logStoreDealClick(dealId, true)}
                                >
                                  Activate Offer
                                </a>
                                <a
                                  href={viewUrl}
                                  target="_blank"
                                  rel="noopener noreferrer"
                                  className={`${BUTTON_STYLES.base} ${BUTTON_STYLES.green} whitespace-nowrap`}
                                  onClick={() => logStoreDealClick(dealId, false)}
                                >
                                  See {storeHost ? ` at ${getDomain(storeHost)}` : ''}
                                </a>
                              </div>
                            )}

                            {dealTypeId === 4 && !externalOfferUrl && viewUrl && (
                              <a
                                href={viewUrl}
                                target="_blank"
                                rel="noopener noreferrer"
                                className={`${BUTTON_STYLES.base} ${BUTTON_STYLES.green} whitespace-nowrap`}
                                onClick={() => logStoreDealClick(dealId, false)}
                              >
                                See {storeHost ? ` at ${getDomain(storeHost)}` : ''}
                              </a>
                            )}

                            {dealTypeId !== 3 && dealTypeId !== 4 && viewUrl && (
                              <a
                                href={viewUrl}
                                target="_blank"
                                rel="noopener noreferrer"
                                className={`${BUTTON_STYLES.base} ${BUTTON_STYLES.green} whitespace-nowrap`}
                                onClick={() => logStoreDealClick(dealId, false)}
                              >
                                See Deal
                              </a>
                            )}

                            <button
                              onClick={() => openFlagModal({ dealId, dealProductId })}
                              disabled={!canFlag || isDealFlagged(deal)}
                              title={!canFlag ? 'This store deal cannot be flagged yet' : (isDealFlagged(deal) ? 'Deal flagged' : 'Flag this deal')}
                              aria-label={!canFlag ? 'Flag unavailable' : (isDealFlagged(deal) ? 'Deal flagged' : 'Flag this deal')}
                              className={
                                !canFlag
                                  ? 'text-sm text-gray-400 cursor-not-allowed select-none no-underline'
                                  : (isDealFlagged(deal)
                                    ? 'text-sm text-red-600 cursor-default select-none no-underline'
                                    : BUTTON_STYLES.flagLink)
                              }
                            >
                              {!canFlag ? 'Not working?' : (isDealFlagged(deal) ? 'Deal Flagged' : 'Not working?')}
                            </button>
                          </div>
                        </div>
                      </div>
                    );
                  })}
              </div>
            )}
          </>
        )}

        {!error && activeTab === 'products' && (
          <>
            {/* Filters (always visible, even when no results) */}
            <div className="mb-6 flex flex-col sm:flex-row sm:flex-wrap items-stretch sm:items-center gap-3">
              <div className={`relative w-full sm:w-auto ${openDropdown === 'category' ? 'z-50' : 'z-30'}`} data-dropdown-root="true">
                <button
                  type="button"
                  onClick={() => setOpenDropdown(openDropdown === 'category' ? null : 'category')}
                  className="w-full sm:w-auto px-4 py-2 border rounded-lg bg-white hover:bg-gray-50 flex items-center justify-between gap-2"
                >
                  <span>Category: {categoryLabel(productTypeIdFromQuery)}</span>
                  <svg className="w-4 h-4 text-[#4CAF50]" fill="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M19 9l-7 7-7-7" />
                  </svg>
                </button>

                {openDropdown === 'category' && (
                  <div className="absolute z-50 mt-1 w-full sm:w-64 bg-white rounded-lg shadow-lg border">
                    <button
                      type="button"
                      onClick={() => {
                        setProductTypeFilter(null);
                        setOpenDropdown(null);
                      }}
                      className={`block w-full text-left px-4 py-2 hover:bg-gray-100 ${!productTypeIdFromQuery ? 'bg-[#e8f5e9] text-[#4CAF50]' : ''}`}
                    >
                      All
                    </button>

                    {categoryOptions.map((c) => (
                      <button
                        key={c.id}
                        type="button"
                        onClick={() => {
                          setProductTypeFilter(c.id);
                          setOpenDropdown(null);
                        }}
                        className={`block w-full text-left px-4 py-2 hover:bg-gray-100 ${Number(productTypeIdFromQuery) === Number(c.id) ? 'bg-[#e8f5e9] text-[#4CAF50]' : ''}`}
                      >
                        {c.name || 'Category'}
                      </button>
                    ))}

                    {!productTypesLoading && categoryOptions.length === 0 && (
                      <div className="px-4 py-2 text-sm text-gray-500">No categories available</div>
                    )}
                    {productTypesLoading && (
                      <div className="px-4 py-2 text-sm text-gray-500">Loading…</div>
                    )}
                  </div>
                )}
              </div>
            </div>

            {productsEmpty ? (
              <div className="text-center text-gray-600">No product deals found for this store.</div>
            ) : (
              <>
                <div className="grid md:grid-cols-2 lg:grid-cols-3 gap-6 items-start">
                  {visible.map((bestDeal) => (
                    <div key={bestDeal.product_id ?? bestDeal.slug} className="bg-white rounded-lg shadow-lg overflow-hidden">
                      <Link
                        to={store?.id ? `/products/${bestDeal.slug}?storeId=${store.id}` : `/products/${bestDeal.slug}`}
                        className="block hover:opacity-90 transition-opacity"
                      >
                        <img
                          src={bestDeal.product_image_url}
                          alt={bestDeal.product_name}
                          className="w-full h-48 object-cover"
                        />
                      </Link>
                      <div className="p-6">
                        <Link
                          to={store?.id ? `/products/${bestDeal.slug}?storeId=${store.id}` : `/products/${bestDeal.slug}`}
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
                          <div />
                          <Link
                            to={store?.id ? `/products/${bestDeal.slug}?storeId=${store.id}` : `/products/${bestDeal.slug}`}
                            className="bg-[#4CAF50] text-white px-6 py-2 rounded-lg hover:bg-[#3d8b40] transition-colors"
                          >
                            See Deals
                          </Link>
                        </div>
                      </div>
                    </div>
                  ))}
                </div>

                {hasMore && (
                  <div className="text-center mt-12">
                    <button
                      onClick={() => setDisplayCount((c) => Math.min(c + 6, products.length))}
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

        {activeTab === 'storeDeals' && isAuthenticated && (
          <SubmitDealModal
            isOpen={isSubmitDealOpen}
            onClose={() => setIsSubmitDealOpen(false)}
            scope="store"
            storeId={store?.id ?? store?.Id ?? null}
            mode="create"
            onSubmitted={async () => {
              setIsSubmitDealOpen(false);
              await loadStore({ cacheBust: true });
            }}
          />
        )}
      </div>

      <AdminStoreModal
        isOpen={isAdminEditOpen}
        onClose={() => setIsAdminEditOpen(false)}
        mode="edit"
        storeId={store?.id}
        onUpdated={() => {
          setIsAdminEditOpen(false);
          loadStore({ cacheBust: true });
        }}
      />

      {/* Flag Deal Modal (Reasons) */}
      {isFlagModalOpen && (
        <div className="fixed inset-0 z-50 overflow-auto bg-black/50 flex items-center justify-center">
          <div className="bg-white rounded-lg p-6 max-w-sm w-full mx-4 shadow-lg">
            <h3 className="text-lg font-semibold mb-3">Flag Deal</h3>
            <p className="text-gray-600 text-sm mb-4">
              Select why this deal is incorrect or no longer works.
            </p>

            {isAdmin && (
              <div className="mb-4 p-3 rounded-md border bg-slate-50">
                <div className="space-y-2">
                  <label className="flex items-center gap-2 text-sm text-slate-700">
                    <input
                      type="checkbox"
                      checked={adminDeleteDeal}
                      onChange={(e) => {
                        const checked = !!e.target.checked;
                        setAdminDeleteDeal(checked);
                      }}
                      className="h-4 w-4"
                    />
                    <span>Admin: delete the entire deal (all product deals)</span>
                  </label>
                </div>
                <div className="mt-2 text-xs text-slate-500">
                  Deletes are soft-deletes and will hide deals from users.
                </div>
              </div>
            )}

            {!isAdminDeleteMode ? (
              <>
                <label className="block text-sm font-medium text-gray-700 mb-1">
                  Reason *
                </label>
                <select
                  value={flagReasonId ?? ''}
                  onChange={(e) => setFlagReasonId(e.target.value ? Number(e.target.value) : null)}
                  className="w-full mb-4 px-3 py-2 border rounded-md text-sm"
                >
                  <option value="">Select a reason...</option>
                  {DEAL_FLAG_REASONS.map((r) => (
                    <option key={r.id} value={r.id}>{r.label}</option>
                  ))}
                </select>
                <label className="block text-sm font-medium text-gray-700 mb-1">
                  Comments {flagReasonId === 6 ? '*' : '(Optional)'}
                </label>
                <textarea
                  rows={3}
                  value={flagComment}
                  onChange={(e) => setFlagComment(e.target.value)}
                  className={`w-full px-3 py-2 border rounded-md text-sm ${flagReasonId === 6 && !flagComment.trim() ? 'border-red-400' : 'border-gray-300'}`}
                  placeholder={
                    flagReasonId === 6
                      ? 'Describe the issue...'
                      : 'Add helpful context (optional)...'
                  }
                />
                {flagReasonId === 6 && !flagComment.trim() && (
                  <p className="mt-1 text-xs text-red-600">Required for "Other".</p>
                )}
              </>
            ) : (
              <div className="mb-4 p-3 rounded-md border bg-red-50 text-sm text-red-800">
                This will delete the selected deal. Reason/comments are not required.
              </div>
            )}
            <div className="flex justify-end gap-3 mt-6">
              <button
                type="button"
                onClick={() => {
                  setIsFlagModalOpen(false);
                  setDealToFlag(null);
                  setFlagComment('');
                  setAdminDeleteDeal(false);
                }}
                className="px-4 py-2 text-gray-600 hover:text-gray-800 transition-colors"
                disabled={flagSubmitting}
              >
                Cancel
              </button>
              <button
                type="button"
                onClick={() => handleFlagDeal(dealToFlag)}
                disabled={
                  flagSubmitting ||
                  (!(isAdmin && adminDeleteDeal) && !flagReasonId) ||
                  (!(isAdmin && adminDeleteDeal) && flagReasonId === 6 && !flagComment.trim())
                }
                className="px-4 py-2 bg-red-600 text-white rounded-lg hover:bg-red-700 transition-colors disabled:opacity-60"
              >
                {flagSubmitting
                  ? 'Submitting...'
                  : ((isAdmin && adminDeleteDeal)
                    ? 'Delete Entire Deal'
                    : 'Submit Flag')}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
};

export default StorePage;
