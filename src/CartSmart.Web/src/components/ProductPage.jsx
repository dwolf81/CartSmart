import React, { useMemo, useState, useEffect, useRef } from 'react';
import { Helmet } from 'react-helmet-async';
import { useParams, Link, useNavigate, useSearchParams } from 'react-router-dom';
import SubmitDealModal from './SubmitDealModal';
import ComboDealModal from './ComboDealModal';
import { useProducts } from '../hooks/useProducts';
import { useAuth } from '../context/AuthContext';
// import { useTermsConsent } from '../context/TermsConsentContext';
import LoadingSpinner from './LoadingSpinner';
import RatingSourcesModal from './RatingSourcesModal';
import { FaTag, FaTicketAlt, FaLink, FaLayerGroup, FaFlag, FaPlus, FaQuestionCircle } from 'react-icons/fa';
import { Flag } from "lucide-react";
import { useScrollLock } from '../hooks/useScrollLock';
import AdminProductModal from './AdminProductModal';
import { appendAffiliateParam, getAffiliateFields } from '../utils/affiliateUrl';
import RewardsTooltipPill from './RewardsTooltipPill';

const API_URL = process.env.REACT_APP_API_URL || 'http://localhost:5000';
const SITE_URL = process.env.REACT_APP_SITE_URL || (typeof window !== 'undefined' ? window.location.origin : '');

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

const BUTTON_STYLES = {
  base: 'inline-flex items-center justify-center gap-2 h-10 px-4 rounded-lg text-white text-sm shadow-sm transition-colors',
  green: 'bg-[#4CAF50] hover:bg-[#3d8b40]',
  blue: 'bg-blue-600 hover:bg-blue-700',
  blueActive: 'bg-blue-500 hover:bg-blue-600',
  disabled: 'bg-gray-300 cursor-not-allowed',
  flagLink: 'text-sm text-slate-500 hover:text-slate-700 underline underline-offset-2'
};

// Replace DEAL_FLAG_REASONS with numeric ids matching DB rows
const DEAL_FLAG_REASONS = [
  { id: 1, label: 'Inaccurate' },
  { id: 2, label: 'Out of Stock' },
  { id: 3, label: 'Coupon Invalid' },
  { id: 4, label: 'Expired' },
  { id: 5, label: 'Spam' },
  { id: 6, label: 'Other (Describe)' }
];

const useIsMobile = (bp = 768) => {
  const [mobile, setMobile] = useState(typeof window !== 'undefined' && window.innerWidth < bp);
  useEffect(() => {
    const handler = () => setMobile(window.innerWidth < bp);
    window.addEventListener('resize', handler);
    return () => window.removeEventListener('resize', handler);
  }, [bp]);
  return mobile;
};

const ProductPage = () => {
  const { productSlug } = useParams();
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const { getProductBySlug } = useProducts();
  const { isAuthenticated, user, authFetch } = useAuth();
  const isAdmin = isAuthenticated && !!user?.admin;
  // Terms consent gating removed; actions proceed directly

  const storeIdFromQuery = useMemo(() => {
    const raw = searchParams.get('storeId');
    if (!raw) return null;
    const n = Number(raw);
    if (!Number.isFinite(n) || n <= 0) return null;
    return n;
  }, [searchParams]);

  const [product, setProduct] = useState(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);
  const [dealFilters, setDealFilters] = useState({
    storeId: null,
    dealTypeId: null,
    conditionId: null
  });

  // If navigated with `?storeId=123`, initialize the store filter once.
  useEffect(() => {
    if (!storeIdFromQuery) return;
    setDealFilters((prev) => {
      if (prev.storeId != null) return prev;
      return { ...prev, storeId: storeIdFromQuery };
    });
  }, [storeIdFromQuery]);

  const [collapsedStoreDeals, setCollapsedStoreDeals] = useState([]); // 1 row per store (primary)
  const [availableStores, setAvailableStores] = useState([]); // [{ store_id, store_name, store_image_url }]
  const [expandedStoreIds, setExpandedStoreIds] = useState([]); // number[]
  const [expandedStoreDealsById, setExpandedStoreDealsById] = useState({}); // { [storeId]: rows }
  const expandedStoreCacheRef = useRef(new Map()); // key: `${productId}:${storeId}:${dealTypeId}:${conditionId}` -> rows

  const [initialLoading, setInitialLoading] = useState(true);
  const [openDropdown, setOpenDropdown] = useState(null);
  const [isModalOpen, setIsModalOpen] = useState(false);
  const [isComboModalOpen, setIsComboModalOpen] = useState(false);
  const [dealsLoading, setDealsLoading] = useState(true);
  const [isRatingModalOpen, setIsRatingModalOpen] = useState(false);
  const [ratingSources, setRatingSources] = useState([]);
  const [isImageOpen, setIsImageOpen] = useState(false);
  const [galleryIndex, setGalleryIndex] = useState(0);
  const [isFlagModalOpen, setIsFlagModalOpen] = useState(false);
  const [dealToFlag, setDealToFlag] = useState(null);
  const [activatedExternal, setActivatedExternal] = useState({}); // deal_id -> bool
  const [expandedStackedSteps, setExpandedStackedSteps] = useState({}); // { `${parentId}:${stepId}`: true }

  // --- ADMIN EDIT (Product + Variants) ---
  const [isAdminEditOpen, setIsAdminEditOpen] = useState(false);
  const [adminEditLoading, setAdminEditLoading] = useState(false);
  const [adminEditSaving, setAdminEditSaving] = useState(false);
  const [adminEditError, setAdminEditError] = useState('');
  const [adminProductDraft, setAdminProductDraft] = useState({ name: '', msrp: '', description: '' });
  const [adminAttributes, setAdminAttributes] = useState([]);
  const [adminAvailableAttributes, setAdminAvailableAttributes] = useState([]);
  const [adminAddAttributeId, setAdminAddAttributeId] = useState('');
  const [adminNewAttributeDraft, setAdminNewAttributeDraft] = useState({ name: '', dataType: 'enum', description: '', isRequired: false });
  const [adminCreateAttrExpanded, setAdminCreateAttrExpanded] = useState(false);
  // drafts for existing enum values: { [enumValueId]: { displayName, sortOrder, isActive } }
  const [adminEnumDrafts, setAdminEnumDrafts] = useState({});
  // drafts for adding enum values: { [attributeId]: { enumKey, displayName, sortOrder, isActive } }
  const [adminNewEnumDrafts, setAdminNewEnumDrafts] = useState({});
  // per-attribute UI collapse state (expanded=false means collapsed)
  const [adminAttrExpanded, setAdminAttrExpanded] = useState({});
  // derive image gallery (fall back to single image / placeholder)
  const galleryImages = (product?.images && product.images.length)
    ? product.images
    : product?.imageUrl
      ? [product.imageUrl]
      : ['https://placehold.co/500x500'];

  // track locally-flagged deals so UI updates immediately
  const [flaggedDeals, setFlaggedDeals] = useState({});
  const [flagReasonId, setFlagReasonId] = useState(null);
  const [flagComment, setFlagComment] = useState('');
  const [flagSubmitting, setFlagSubmitting] = useState(false);
  const [adminDeleteProductDeal, setAdminDeleteProductDeal] = useState(false);
  const [adminDeleteDeal, setAdminDeleteDeal] = useState(false);

  // --- "MORE" VARIANT FILTERS (ProductVariant) ---
  const [isMoreOpen, setIsMoreOpen] = useState(false);
  const [variantFilterOptions, setVariantFilterOptions] = useState(null);
  const [variantFilterLoading, setVariantFilterLoading] = useState(false);
  const [variantFilterError, setVariantFilterError] = useState(null);
  // { [attributeId: string]: number[] }
  const [variantFilterSelections, setVariantFilterSelections] = useState({});
  // Applied selections used for actual deal filtering
  // { [attributeId: string]: number[] }
  const [appliedVariantFilterSelections, setAppliedVariantFilterSelections] = useState({});

  const UPFRONT_COST_TERMS = {
    1: { id: 1, label: 'One Time' },
    2: { id: 2, label: 'Monthly' },
    3: { id: 3, label: 'Annually' }
  };

  const isAdminDeleteMode = isAdmin && (adminDeleteProductDeal || adminDeleteDeal);

  const isDealFlagged = (d) => !!(flaggedDeals[d?.deal_product_id] || d?.user_flagged || d?.userFlagged || d?.userflagged);

  const dealTypeLabel = (dealTypeId) => {
    if (!dealTypeId) return 'All';
    return `${DEAL_TYPE_META[dealTypeId]?.label ?? 'Unknown'} Deal`;
  };

  const conditionLabel = (conditionId) => {
    if (!conditionId) return 'All';
    switch (conditionId) {
      case 1: return 'New';
      case 2: return 'Used';
      case 3: return 'Refurbished';
      default: return 'All';
    }
  };

  const storeLabel = (storeId) => {
    if (!storeId) return 'All';
    const found = availableStores.find(s => Number(s.store_id) === Number(storeId));
    return found?.store_name || 'Store';
  };

  const seedFlaggedDealsFromBatch = (batch) => {
    setFlaggedDeals(prev => {
      const next = { ...prev };
      batch.forEach(d => {
        if (d.user_flagged && d.deal_product_id) next[d.deal_product_id] = true;
      });
      return next;
    });
  };

  // Backend expects jsonb payload with keys: attribute_id, enum_value_ids
  // - OR within each attribute (any enum_value_id matches)
  // - AND across attributes (every attribute must match)
  const buildAttributeFiltersPayload = (selections) => {
    const s = selections || {};
    return Object.keys(s)
      .map(k => Number(k))
      .filter(attributeId => !Number.isNaN(attributeId) && attributeId > 0)
      .sort((a, b) => a - b)
      .map(attributeId => {
        const raw = Array.isArray(s?.[attributeId.toString()]) ? s[attributeId.toString()] : [];
        const enumValueIds = raw
          .map(Number)
          .filter(v => !Number.isNaN(v) && v > 0)
          .sort((a, b) => a - b);
        return { attribute_id: attributeId, enum_value_ids: enumValueIds };
      })
      .filter(x => Array.isArray(x.enum_value_ids) && x.enum_value_ids.length > 0);
  };

  const appliedAttributeFiltersKey = useMemo(() => {
    const payload = buildAttributeFiltersPayload(appliedVariantFilterSelections);
    return payload.length ? JSON.stringify(payload) : 'none';
  }, [appliedVariantFilterSelections]);

  const cacheKeyFor = ({ storeId }) => {
    const pid = product?.id ?? 0;
    const dt = dealFilters.dealTypeId ?? 'all';
    const c = dealFilters.conditionId ?? 'all';
    const s = storeId ?? 'all';
    const vf = appliedAttributeFiltersKey;
    return `${pid}:${s}:${dt}:${c}:${vf}`;
  };

  const fetchDeals2 = async ({ storeId }) => {
    const body = {
      storeId: storeId ?? null,
      dealTypeId: dealFilters.dealTypeId ?? null,
      conditionId: dealFilters.conditionId ?? null,
      attributeFilters: buildAttributeFiltersPayload(appliedVariantFilterSelections)
    };

    const resp = await fetch(`${API_URL}/api/deals/product2/${product.id}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      credentials: 'include',
      body: JSON.stringify(body)
    });
    if (!resp.ok) throw new Error('Failed to fetch deals');
    const data = await resp.json();
    return Array.isArray(data) ? data : [];
  };

  const ensureExpandedStoreDeals = async (storeId) => {
    const key = cacheKeyFor({ storeId });
    if (expandedStoreCacheRef.current.has(key)) {
      return expandedStoreCacheRef.current.get(key);
    }
    const rows = await fetchDeals2({ storeId });
    expandedStoreCacheRef.current.set(key, rows);
    return rows;
  };

  const clearExpandedCacheAndCollapse = (nextExpandedStoreId = null) => {
    expandedStoreCacheRef.current.clear();
    setExpandedStoreDealsById({});
    setExpandedStoreIds(nextExpandedStoreId ? [nextExpandedStoreId] : []);
  };

  const clearVariantFilters = () => {
    setVariantFilterSelections({});
  };

  const clearAppliedVariantFilters = () => {
    setVariantFilterSelections({});
    setAppliedVariantFilterSelections({});
    expandedStoreCacheRef.current.clear();
  };

  const applyVariantFilters = () => {
    setAppliedVariantFilterSelections(variantFilterSelections || {});
    expandedStoreCacheRef.current.clear();
  };

  const closeMorePanel = () => {
    setVariantFilterSelections(appliedVariantFilterSelections || {});
    setIsMoreOpen(false);
  };

  const toggleVariantFilterValue = (attributeId, enumValueId) => {
    setVariantFilterSelections(prev => {
      const k = attributeId.toString();
      const prevList = Array.isArray(prev?.[k]) ? prev[k] : [];
      const exists = prevList.some(v => Number(v) === Number(enumValueId));
      const nextList = exists
        ? prevList.filter(v => Number(v) !== Number(enumValueId))
        : [...prevList, Number(enumValueId)];
      const next = { ...prev, [k]: nextList };
      if (nextList.length === 0) delete next[k];
      return next;
    });
  };

  const loadVariantFilterOptions = async () => {
    if (!product?.id) return;
    if (variantFilterLoading) return;
    if (variantFilterOptions) return;

    setVariantFilterLoading(true);
    setVariantFilterError(null);
    try {
      const resp = await fetch(`${API_URL}/api/products/${product.id}/variant-filters`, {
        credentials: 'include'
      });
      if (!resp.ok) throw new Error('Failed to load variant filters');
      const data = await resp.json();
      setVariantFilterOptions(data);
    } catch (e) {
      console.error(e);
      setVariantFilterOptions({ variants: [], attributes: [] });
      setVariantFilterError('Failed to load filters');
    } finally {
      setVariantFilterLoading(false);
    }
  };

  // Reset variant filters when product changes
  useEffect(() => {
    setIsMoreOpen(false);
    setVariantFilterOptions(null);
    setVariantFilterSelections({});
    setAppliedVariantFilterSelections({});
    setVariantFilterLoading(false);
    setVariantFilterError(null);
  }, [product?.id]);

  // Preload variant filters so we can hide the "More" button when none exist
  useEffect(() => {
    if (!product?.id) return;
    if (variantFilterOptions !== null) return;
    loadVariantFilterOptions();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [product?.id, variantFilterOptions]);


  useEffect(() => {
    const loadProduct = async () => {
      try {
        setLoading(true);
        const productData = await getProductBySlug(productSlug, { cacheBust: true });
        if (!productData) {
          setError('Product not found');
          return;
        }
        setProduct(productData);

        // Removed initial deal fetch; deals are fetched by fetchData effect when product is set
        // setDealsLoading(true);
        // const response = await fetch(`${API_URL}/api/deals/product/${productData.id}`, { credentials: 'include' });
        // if (!response.ok) throw new Error('Failed to fetch deals');
        // const dealsData = await response.json();
        // setDeals(dealsData.deals || dealsData || []);
        // setTotalCount(dealsData.totalCount || 10);
      } catch (err) {
        console.error('Error loading product:', err);
        setError(err.message);
      } finally {
        setLoading(false);
        setDealsLoading(false);
      }
    };

    loadProduct();
  }, [productSlug]);

  const refreshProduct = async () => {
    try {
      const fresh = await getProductBySlug(productSlug, { cacheBust: true });
      if (fresh) setProduct(fresh);
    } catch (e) {
      console.error('Failed to refresh product after admin update:', e);
    }
  };

  // Load deals (collapsed or store-filtered) whenever product or filters change
  useEffect(() => {
    if (!product) return;

    let cancelled = false;
    const run = async () => {
      setInitialLoading(true);
      setDealsLoading(true);
      try {
        // If store filter is set, show that store's deals (up to 5) and ignore global expanded state
        if (dealFilters.storeId) {
          // When deep-linking with `?storeId=...`, availableStores may be empty because we never ran the
          // collapsed fetch. Populate it so the Store dropdown/labels work correctly.
          if (!availableStores || availableStores.length === 0) {
            const collapsedAll = await fetchDeals2({ storeId: null });
            if (cancelled) return;
            setAvailableStores(
              collapsedAll
                .filter(d => d.store_id)
                .map(d => ({ store_id: Number(d.store_id), store_name: d.store_name, store_image_url: d.store_image_url }))
                .filter((s, idx, arr) => arr.findIndex(x => x.store_id === s.store_id) === idx)
            );

            const anchor = collapsedAll.find(d => Number(d.store_id) === Number(dealFilters.storeId));
            if (anchor) {
              setCollapsedStoreDeals([anchor]);
              seedFlaggedDealsFromBatch([anchor]);
            }
          }

          const expanded = await ensureExpandedStoreDeals(dealFilters.storeId);
          if (cancelled) return;
          setExpandedStoreDealsById({ [dealFilters.storeId]: expanded });
          seedFlaggedDealsFromBatch(expanded);

          // Keep a 1-item collapsed list to anchor store header
          setCollapsedStoreDeals(expanded.length ? [expanded[0]] : []);
        } else {
          // Collapsed view: one row per store
          const collapsed = await fetchDeals2({ storeId: null });
          if (cancelled) return;
          setCollapsedStoreDeals(collapsed);
          seedFlaggedDealsFromBatch(collapsed);

          // Update store dropdown options from collapsed results
          setAvailableStores(
            collapsed
              .filter(d => d.store_id)
              .map(d => ({ store_id: d.store_id, store_name: d.store_name, store_image_url: d.store_image_url }))
              .filter((s, idx, arr) => arr.findIndex(x => x.store_id === s.store_id) === idx)
          );
        }
      } catch (e) {
        console.error(e);
        if (cancelled) return;
        setCollapsedStoreDeals([]);
        setExpandedStoreDealsById({});
      } finally {
        if (cancelled) return;
        setInitialLoading(false);
        setDealsLoading(false);
      }
    };

    run();
    return () => {
      cancelled = true;
    };
  }, [product?.id, dealFilters.storeId, dealFilters.dealTypeId, dealFilters.conditionId, appliedAttributeFiltersKey]);

  // Refresh expanded store results when non-store filters change (but only in collapsed view)
  useEffect(() => {
    if (!product) return;
    if (dealFilters.storeId) return;
    if (!expandedStoreIds || expandedStoreIds.length === 0) return;

    let cancelled = false;
    const run = async () => {
      try {
        setDealsLoading(true);
        const nextById = {};
        for (const storeId of expandedStoreIds) {
          const expanded = await ensureExpandedStoreDeals(storeId);
          if (cancelled) return;
          nextById[storeId] = expanded;
          seedFlaggedDealsFromBatch(expanded);
        }
        if (cancelled) return;
        setExpandedStoreDealsById(nextById);
      } catch (e) {
        console.error(e);
      } finally {
        if (cancelled) return;
        setDealsLoading(false);
      }
    };

    run();
    return () => {
      cancelled = true;
    };
  }, [product?.id, dealFilters.storeId, dealFilters.dealTypeId, dealFilters.conditionId, appliedAttributeFiltersKey, expandedStoreIds.join(',')]);

  useEffect(() => {
    if (!product) return;
    // Fetch rating sources from the backend
    const fetchRatings = async () => {
      try {
        const response = await fetch(`${API_URL}/api/products/${product.id}/ratings`, {
          credentials: 'include'
        });
        if (!response.ok) throw new Error('Failed to fetch ratings');
        const ratings = await response.json();
        setRatingSources(ratings || []);
      } catch (err) {
        console.error('Error fetching ratings:', err);
        setRatingSources([]);
      }
    };
    fetchRatings();
  }, [product]);

  // (Removed old placeholder "dynamic product attribute filters" test data in favor of variant-based More panel.)

  const handleClickOutside = () => {
    setOpenDropdown(null);
  };

  // Close dropdown when clicking outside
  useEffect(() => {
    if (!openDropdown) return;

    const onPointerDown = (event) => {
      const target = event.target;
      if (!(target instanceof Element)) return;

      // Any click inside a dropdown root should not close it.
      if (target.closest('[data-dropdown-root="true"]')) return;

      setOpenDropdown(null);
    };

    document.addEventListener('mousedown', onPointerDown);
    document.addEventListener('touchstart', onPointerDown);
    return () => {
      document.removeEventListener('mousedown', onPointerDown);
      document.removeEventListener('touchstart', onPointerDown);
    };
  }, [openDropdown]);

  /*
  const filteredDeals = deals.filter(deal => {
    if (filters.condition !== 'all' && deal.condition !== filters.condition) return false;
    if (filters.dealType !== 'all' && deal.type.toLowerCase().replace(' ', '') !== filters.dealType) return false;
    return true;
  });
  */

  const handleFlagDeal = async () => {
    if (!isAuthenticated) {
      alert('Please log in to flag a deal.');
      return;
    }
    const dealId = dealToFlag?.dealId;
    const dealProductId = dealToFlag?.dealProductId ?? null;
    if (!dealId) {
      alert('Missing deal id.');
      return;
    }
    if (dealProductId && flaggedDeals[dealProductId]) return;
    const adminDeleteMode = isAdmin && (adminDeleteProductDeal || adminDeleteDeal);
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
            dealProductId: adminDeleteDeal ? null : dealProductId,
            deleteDeal: !!adminDeleteDeal
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
      if (dealProductId) {
        setFlaggedDeals(p => ({ ...p, [dealProductId]: true }));
      }
      setIsFlagModalOpen(false);
      setDealToFlag(null);
      setFlagComment('');
      setAdminDeleteProductDeal(false);
      setAdminDeleteDeal(false);
      if (adminDeleteMode) {
        refreshProduct();
      }
      alert(
        adminDeleteMode
          ? (adminDeleteDeal ? 'Deal deleted (all product deals removed).' : 'Product deal deleted.')
          : 'Deal flagged. Thank you!'
      );
    } catch (e) {
      console.error(e);
      const adminDeleteMode = isAdmin && (adminDeleteProductDeal || adminDeleteDeal);
      alert(adminDeleteMode ? 'Failed to delete deal.' : 'Failed to flag deal.');
    } finally {
      setFlagSubmitting(false);
    }
  };

  // Add this function to handle opening the flag modal
  const openFlagModal = ({ dealId, dealProductId = null }) => {
    if (!isAuthenticated) {
      navigate(`/login?redirect=${encodeURIComponent(window.location.pathname)}`);
      return;
    }
    setDealToFlag({ dealId, dealProductId });
    setFlagReasonId(null);
    setFlagComment('');
    setAdminDeleteProductDeal(false);
    setAdminDeleteDeal(false);
    setIsFlagModalOpen(true);
  };

  // Lock scroll when any modal is open
  useScrollLock(isModalOpen || isComboModalOpen || isImageOpen || isRatingModalOpen || isFlagModalOpen || isAdminEditOpen);

  // Add this utility function at the top of your component, after the imports
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

  const handleActivateExternal = (deal) => {
    if (!deal?.external_offer_url) return;
    const { affiliateCodeVar, affiliateCode } = getAffiliateFields(deal, 'external');
    window.open(appendAffiliateParam(deal.external_offer_url, affiliateCodeVar, affiliateCode), '_blank', 'noopener,noreferrer');
    setActivatedExternal(prev => ({ ...prev, [deal.deal_id]: true }));
  };

  const openAdminEdit = async () => {
    if (!isAuthenticated) {
      navigate(`/login?redirect=${encodeURIComponent(window.location.pathname)}`);
      return;
    }
    if (!user?.admin) return;

    setIsAdminEditOpen(true);
  };

  const closeAdminEdit = () => {
    setIsAdminEditOpen(false);
    setAdminEditError('');
    setAdminEditSaving(false);
    setAdminAttrExpanded({});
  };

  const refreshAdminEditData = async () => {
    const res = await authFetch(`${API_URL}/api/products/${product.id}/admin/edit`);
    if (!res.ok) throw new Error('Failed to reload admin edit data');
    const data = await res.json();
    const attrs = Array.isArray(data?.attributes) ? data.attributes : [];
    const available = Array.isArray(data?.availableAttributes) ? data.availableAttributes : [];
    setAdminAttributes(attrs);
    setAdminAvailableAttributes(available);
    setAdminProductDraft({
      name: data?.product?.name ?? '',
      msrp: data?.product?.msrp ?? '',
      description: data?.product?.description ?? ''
    });

    // Re-seed enum drafts but preserve any in-progress edits where possible
    setAdminEnumDrafts(prev => {
      const next = { ...prev };
      attrs.forEach(a => {
        (a.options || []).forEach(o => {
          const key = String(o.id);
          if (!next[key]) {
            next[key] = {
              displayName: o.displayName ?? '',
              sortOrder: o.sortOrder ?? 0,
              isActive: !!o.isActive
            };
          }
        });
      });
      return next;
    });

    setAdminNewEnumDrafts(prev => {
      const next = { ...prev };
      attrs.forEach(a => {
        const key = String(a.attributeId);
        if (!next[key]) {
          next[key] = { enumKey: '', displayName: '', sortOrder: 0, isActive: true };
        }
      });
      return next;
    });

    // Preserve any expanded state during refresh; default collapsed for new attributes.
    setAdminAttrExpanded(prev => {
      const next = {};
      attrs.forEach(a => {
        const key = String(a.attributeId);
        next[key] = prev[key] ?? false;
      });
      return next;
    });
  };

  const handleAdminSaveProduct = async () => {
    setAdminEditError('');
    setAdminEditSaving(true);
    try {
      const msrpValue = adminProductDraft.msrp === '' ? null : Number(adminProductDraft.msrp);
      const res = await authFetch(`${API_URL}/api/products/${product.id}/admin`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          name: adminProductDraft.name,
          msrp: msrpValue,
          description: adminProductDraft.description
        })
      });
      if (!res.ok) {
        const msg = await res.text().catch(() => '');
        throw new Error(msg || 'Failed to save product');
      }
      const updated = await res.json();
      setProduct(p => ({
        ...p,
        name: updated?.name ?? p.name,
        msrp: updated?.msrp ?? p.msrp,
        description: updated?.description ?? p.description
      }));
      closeAdminEdit();
    } catch (e) {
      console.error(e);
      setAdminEditError('Failed to save product.');
    } finally {
      setAdminEditSaving(false);
    }
  };

  const handleAdminAddAttribute = async () => {
    setAdminEditError('');
    if (!adminAddAttributeId) return;
    setAdminEditSaving(true);
    try {
      const res = await authFetch(`${API_URL}/api/products/${product.id}/admin/product-attributes`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ attributeId: Number(adminAddAttributeId), isRequired: false })
      });
      if (!res.ok) throw new Error('Failed to add attribute');
      await refreshAdminEditData();
    } catch (e) {
      console.error(e);
      setAdminEditError('Failed to add attribute.');
    } finally {
      setAdminEditSaving(false);
    }
  };

  const handleAdminCreateAttribute = async () => {
    setAdminEditError('');
    const name = (adminNewAttributeDraft.name || '').trim();
    if (!name) {
      setAdminEditError('Attribute name is required.');
      return;
    }

    setAdminEditSaving(true);
    try {
      const res = await authFetch(`${API_URL}/api/products/${product.id}/admin/attributes`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          name,
          dataType: adminNewAttributeDraft.dataType,
          description: adminNewAttributeDraft.description,
          isRequired: !!adminNewAttributeDraft.isRequired
        })
      });
      if (!res.ok) {
        const msg = await res.text().catch(() => '');
        throw new Error(msg || 'Failed to create attribute');
      }

      setAdminNewAttributeDraft({ name: '', dataType: 'enum', description: '', isRequired: false });
      await refreshAdminEditData();
    } catch (e) {
      console.error(e);
      setAdminEditError('Failed to create attribute.');
    } finally {
      setAdminEditSaving(false);
    }
  };

  const handleAdminRemoveAttribute = async (attributeId) => {
    setAdminEditError('');
    setAdminEditSaving(true);
    try {
      const res = await authFetch(`${API_URL}/api/products/${product.id}/admin/product-attributes/${attributeId}`, {
        method: 'DELETE'
      });
      if (!res.ok) throw new Error('Failed to remove attribute');
      await refreshAdminEditData();
    } catch (e) {
      console.error(e);
      setAdminEditError('Failed to remove attribute.');
    } finally {
      setAdminEditSaving(false);
    }
  };

  const handleAdminToggleAttributeRequired = async (attributeId, isRequired) => {
    setAdminEditError('');
    setAdminEditSaving(true);
    try {
      const res = await authFetch(`${API_URL}/api/products/${product.id}/admin/product-attributes/${attributeId}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ attributeId, isRequired: !!isRequired })
      });
      if (!res.ok) throw new Error('Failed to update required flag');
      await refreshAdminEditData();
    } catch (e) {
      console.error(e);
      setAdminEditError('Failed to update attribute.');
    } finally {
      setAdminEditSaving(false);
    }
  };

  const handleAdminSaveEnumValue = async (attributeId, enumValueId) => {
    setAdminEditError('');
    setAdminEditSaving(true);
    try {
      const d = adminEnumDrafts[String(enumValueId)] || {};
      const res = await authFetch(`${API_URL}/api/products/${product.id}/admin/attributes/${attributeId}/enum-values/${enumValueId}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          displayName: d.displayName,
          sortOrder: Number(d.sortOrder) || 0,
          isActive: !!d.isActive
        })
      });
      if (!res.ok) throw new Error('Failed to update enum value');
      await refreshAdminEditData();
    } catch (e) {
      console.error(e);
      setAdminEditError('Failed to save value.');
    } finally {
      setAdminEditSaving(false);
    }
  };

  const handleAdminAddEnumValue = async (attributeId) => {
    setAdminEditError('');
    setAdminEditSaving(true);
    try {
      const d = adminNewEnumDrafts[String(attributeId)] || {};
      const res = await authFetch(`${API_URL}/api/products/${product.id}/admin/attributes/${attributeId}/enum-values`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          enumKey: d.enumKey,
          displayName: d.displayName,
          sortOrder: d.sortOrder === '' ? 0 : Number(d.sortOrder) || 0,
          isActive: d.isActive !== false
        })
      });
      if (!res.ok) {
        const msg = await res.text().catch(() => '');
        throw new Error(msg || 'Failed to create enum value');
      }
      await refreshAdminEditData();
      setAdminNewEnumDrafts(prev => ({
        ...prev,
        [String(attributeId)]: { enumKey: '', displayName: '', sortOrder: 0, isActive: true }
      }));
    } catch (e) {
      console.error(e);
      setAdminEditError('Failed to add value.');
    } finally {
      setAdminEditSaving(false);
    }
  };

  const toggleStackedStep = (parentId, stepId) => {
    const key = `${parentId}:${stepId}`;
    setExpandedStackedSteps(s => ({ ...s, [key]: !s[key] }));
  };

  // Add these helper functions inside the component:
  const logDealClick = (dealId, external) => {
    if (!dealId || !product?.id) return;
    fetch(`${API_URL}/api/deals/${dealId}/click?productId=${product.id}&external=${external}`, {
      method: 'POST',
      credentials: 'include',
      headers: { 'Content-Type': 'application/json' }
    }).catch(() => { });
  };

  const findDealRowById = (dealId) => {
    const id = Number(dealId);
    if (!Number.isFinite(id) || id <= 0) return null;

    const fromCollapsed = (collapsedStoreDeals || []).find(d => Number(d?.deal_id) === id);
    if (fromCollapsed) return fromCollapsed;

    const expanded = expandedStoreDealsById || {};
    for (const rows of Object.values(expanded)) {
      const found = (rows || []).find(d => Number(d?.deal_id) === id);
      if (found) return found;
    }

    return null;
  };

  const handleGetDealClick = (e, dealId, external, url) => {
    e.preventDefault();
    if (!url) return;
    const dealRow = findDealRowById(dealId);
    const { affiliateCodeVar, affiliateCode } = getAffiliateFields(dealRow, external ? 'external' : 'normal');
    // Open first to avoid popup blockers, then log
    window.open(appendAffiliateParam(url, affiliateCodeVar, affiliateCode), '_blank', 'noopener,noreferrer');
    logDealClick(dealId, external);
  };

  const [descExpanded, setDescExpanded] = useState(false);
  const isMobile = useIsMobile();
  const renderUpfrontCost = (cost, termId) => {
    if (cost == null) return null;
    const termLabel = UPFRONT_COST_TERMS[termId]?.label;
    if (isMobile) {
      // Separate line on mobile
      return (
        <div className="mt-1">
          <span className="text-gray-600 font-medium text-sm">Upfront Cost:</span>{' '}
          <span className="text-sm">
            {formatPrice(cost)}{termLabel ? ` / ${termLabel}` : ''}
          </span>
        </div>
      );
    }
    // Inline (desktop)
    return (
      <span>
        <span className="text-gray-600 font-medium text-sm"> — Upfront Cost:</span>{' '}
        <span className="text-sm">
          {formatPrice(cost)}{termLabel ? ` / ${termLabel}` : ''}
        </span>
      </span>
    );
  };

  const rawDesc = product?.description || '';
  const shouldCollapse = isMobile && rawDesc.length > 0;
  const visibleDesc = shouldCollapse && !descExpanded
    ? rawDesc.split('\n').slice(0, 2).join('\n')  // first couple lines
    : rawDesc;

  if (loading) return <LoadingSpinner />;
  if (error) return <div className="container mx-auto px-4 py-8">Error: {error}</div>;
  if (!product) return <div className="container mx-auto px-4 py-8">Product not found</div>;

  const siteUrl = SITE_URL;
  const canonical = `${siteUrl}/products/${product.slug}`;
  const title = `${product.name} — Shop Smarter | CartSmart`;
  const desc = (product.description || '').replace(/\s+/g, ' ').slice(0, 155);
  const firstImage = (product?.imageUrl || (Array.isArray(galleryImages) && galleryImages[0])) || '';
  const imageAbs = firstImage && firstImage.startsWith('http') ? firstImage : (firstImage ? `${siteUrl}${firstImage.startsWith('/') ? '' : '/'}${firstImage}` : '');

  const variantFilterAttrs = (variantFilterOptions?.attributes || variantFilterOptions?.Attributes || []);
  const hasVariantFilters = Array.isArray(variantFilterAttrs) && variantFilterAttrs.length > 0;

  return (
        <div className="container mx-auto px-4 py-8">
      <Helmet>
        <title>{title}</title>
        <meta name="description" content={desc} />
        <link rel="canonical" href={canonical} />
        <meta name="robots" content="index,follow" />
        <meta property="og:type" content="product" />
        <meta property="og:site_name" content="CartSmart" />
        <meta property="og:title" content={title} />
        <meta property="og:description" content={desc} />
        <meta property="og:url" content={canonical} />
        {imageAbs ? <meta property="og:image" content={imageAbs} /> : null}
        <meta name="twitter:card" content="summary_large_image" />
        <meta name="twitter:title" content={title} />
        <meta name="twitter:description" content={desc} />
        {imageAbs ? <meta name="twitter:image" content={imageAbs} /> : null}
        <script type="application/ld+json">{JSON.stringify({
          '@context': 'https://schema.org',
          '@type': 'Product',
          name: product.name,
          description: desc,
          image: imageAbs ? [imageAbs] : undefined,
          brand: product.brandName ? { '@type': 'Brand', name: product.brandName } : undefined,
          url: canonical
        })}</script>
      </Helmet>
  
      <div className="flex flex-col md:flex-row gap-8">
        {/* Left Pane - Product Information */}
        <div className="md:w-1/2">
          <div className="bg-white rounded-lg shadow-lg p-6">
            {/* Gallery main image (fixed height) */}
            <div className="mb-4">
              <button
                type="button"
                onClick={() => setIsImageOpen(true)}
                className="block w-full rounded-lg mb-3 overflow-hidden focus:outline-none"
                aria-label={`Open gallery for ${product.name}`}
              >
                <img
                  src={product?.imageUrl || 'https://placehold.co/600x600'}
                  alt={product?.name}
                  className={`w-full object-cover rounded-lg border transition-all ${
                    isMobile ? 'h-32' : 'h-[500px]'
                  }`}
                />
              </button>

              {/* Thumbnails (only show if more than one image) */}
              {galleryImages.length > 1 && (
                <div className="flex gap-2 overflow-x-auto py-1 px-2">
                  {galleryImages.map((src, idx) => (
                    <button
                      key={idx}
                      onClick={() => { setGalleryIndex(idx); }}
                      className={`flex-shrink-0 rounded-md overflow-hidden focus:outline-none transition-all ${idx === galleryIndex ? 'ring-2 ring-[#4CAF50]' : 'ring-0'
                        }`}
                      style={{ width: 84, height: 64 }}
                      aria-label={`View image ${idx + 1}`}
                    >
                      <img
                        src={src}
                        alt={`${product.name} ${idx + 1}`}
                        className="w-full h-full object-cover"
                      />
                    </button>
                  ))}
                </div>
              )}
            </div>

            {/* Lightbox / slideshow modal */}
            {isImageOpen && (
              <div className="fixed inset-0 z-50 overflow-auto p-4">
                <div
                  className="absolute inset-0 bg-black bg-opacity-70"
                  onClick={() => setIsImageOpen(false)}
                  aria-hidden="true"
                />
                <div className="relative max-w-5xl w-full bg-transparent mx-auto my-8">
                  <button
                    type="button"
                    onClick={() => setIsImageOpen(false)}
                    className="absolute top-2 right-2 z-20 rounded-full bg-white bg-opacity-90 p-2 hover:bg-opacity-100 focus:outline-none"
                    aria-label="Close image"
                  >
                    ✕
                  </button>

                  {/* Prev */}
                  {galleryImages.length > 1 && (
                    <button
                      onClick={(e) => { e.stopPropagation(); setGalleryIndex(i => (i - 1 + galleryImages.length) % galleryImages.length); }}
                      className="absolute left-2 top-1/2 z-20 -translate-y-1/2 rounded-full bg-white bg-opacity-90 p-2 focus:outline-none"
                      aria-label="Previous image"
                    >
                      <svg className="w-5 h-5" viewBox="0 0 24 24" fill="none" stroke="currentColor"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M15 19l-7-7 7-7" /></svg>
                    </button>
                  )}

                  {/* Image */}
                  <div className="mx-auto max-h-[85vh] overflow-auto rounded shadow-lg bg-white">
                    <img
                      src={galleryImages[galleryIndex]}
                      alt={`${product.name} large`}
                      className="w-full h-auto max-h-[85vh] object-contain bg-white"
                    />
                  </div>

                  {/* Next */}
                  {galleryImages.length > 1 && (
                    <button
                      onClick={(e) => { e.stopPropagation(); setGalleryIndex(i => (i + 1) % galleryImages.length); }}
                      className="absolute right-2 top-1/2 z-20 -translate-y-1/2 rounded-full bg-white bg-opacity-90 p-2 focus:outline-none"
                      aria-label="Next image"
                    >
                      <svg className="w-5 h-5" viewBox="0 0 24 24" fill="none" stroke="currentColor"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M9 5l7 7-7 7" /></svg>
                    </button>
                  )}

                  {/* Thumbnail strip inside modal */}
                  {galleryImages.length > 1 && (
                    <div className="mt-3 flex gap-2 justify-center overflow-x-auto px-2">
                      {galleryImages.map((src, idx) => (
                        <button
                          key={idx}
                          onClick={(e) => { e.stopPropagation(); setGalleryIndex(idx); }}
                          className={`flex-shrink-0 rounded-md overflow-hidden transition-all ${idx === galleryIndex ? 'ring-2 ring-[#4CAF50]' : 'ring-0'}`}
                          style={{ width: 84, height: 64 }}
                          aria-label={`Select image ${idx + 1}`}
                        >
                          <img src={src} alt={`${product.name} thumb ${idx + 1}`} className="w-full h-full object-cover" />
                        </button>
                      ))}
                    </div>
                  )}
                </div>
              </div>
            )}
            <div className="flex items-start justify-between gap-3">
              <h1 className="text-2xl font-bold mb-4">{product.name}</h1>
              {isAuthenticated && user?.admin && (
                <button
                  type="button"
                  onClick={openAdminEdit}
                  className="h-10 px-4 rounded-lg bg-blue-600 text-white text-sm hover:bg-blue-700 transition-colors"
                >
                  Edit Product
                </button>
              )}
            </div>
            <div className="mb-4">
              <span className="text-gray-600">Brand: </span>
              <span className="font-semibold">{product.brandName}</span>
            </div>
            <div className="mb-4">
              <span className="text-gray-600">Regular Price: </span>
              <span className="font-semibold">{formatPrice(product.msrp)}</span>
            </div>
            {/*
            <div className="mb-6">
              <span className="text-gray-600">Rating: </span>
              <button
                className="font-semibold text-blue-600 underline hover:text-blue-800 focus:outline-none"
                onClick={() => setIsRatingModalOpen(true)}
                title="Show rating sources"
                type="button"
              >
                {product.rating}/10
              </button>
            </div>
            */}
            <div className="mt-4">
              <p className="text-sm md:text-base whitespace-pre-line">
                {visibleDesc}
                {shouldCollapse && !descExpanded && rawDesc !== visibleDesc && ' …'}
              </p>
              {shouldCollapse && (
                <button
                  type="button"
                  onClick={() => setDescExpanded(v => !v)}
                  className="mt-2 text-xs font-medium text-blue-600 hover:underline"
                  aria-expanded={descExpanded}
                >
                  {descExpanded ? 'Show Less' : 'Show More'}
                </button>
              )}
            </div>
          </div>
        </div>

        {/* Right Pane - Deals Information */}
        <div className="md:w-1/2">
          <div className="bg-white rounded-lg shadow-lg p-6">
            {/* Price Challenge CTA */}
            <div className="mb-6 flex flex-col gap-2">
              <div className="text-sm text-gray-600">
                <span>Found a lower price? Prove it - we reward the best deals.</span>
                <RewardsTooltipPill
                  label={<FaQuestionCircle className="inline ml-1 text-gray-400 hover:text-gray-600" />}
                  pillClassName="inline-flex items-center"
                />
              </div>

              <div className="flex flex-row gap-4">
              <button
                onClick={() => {
                  if (!isAuthenticated) {
                    navigate(`/login?redirect=${encodeURIComponent(window.location.pathname)}`);
                    return;
                  }
                  setIsModalOpen(true);
                }}
                className="flex-1 bg-[#4CAF50] text-white px-4 py-3 rounded-lg hover:bg-[#3d8b40] transition-colors flex items-center justify-center space-x-2"
                title="Submit a lower price to earn rewards"
              >
                <FaPlus className="w-4 h-4" />
                <span>Beat the Price</span>
                <span className="text-xs bg-white/15 px-2 py-0.5 rounded-full">Earn rewards</span>
              </button>
              <button
                onClick={() => {
                  if (!isAuthenticated) {
                    navigate(`/login?redirect=${encodeURIComponent(window.location.pathname)}`);
                    return;
                  }
                  setIsComboModalOpen(true);
                }}
                className="flex-1 bg-blue-600 text-white px-4 py-3 rounded-lg hover:bg-blue-700 transition-colors flex items-center justify-center space-x-2"
                title="Stack multiple deals (coupon + sale + external, etc.) to unlock an even lower final price and earn points."
              >
                <FaPlus className="w-4 h-4" />
                <span>Stack Deals</span>
                <span className="text-xs bg-white/15 px-2 py-0.5 rounded-full">Earn rewards</span>
              </button>
              </div>
            </div>

            {/* Filters */}
            <div className="mb-6 flex flex-col sm:flex-row sm:flex-wrap items-stretch sm:items-center gap-3">


                <div className={`relative w-full sm:w-auto ${openDropdown === 'store' ? 'z-50' : 'z-30'}`} data-dropdown-root="true">
                  <button
                    onClick={() => setOpenDropdown(openDropdown === 'store' ? null : 'store')}
                    className="w-full sm:w-auto px-4 py-2 border rounded-lg bg-white hover:bg-gray-50 flex items-center justify-between gap-2"
                  >
                    <span>Store: {storeLabel(dealFilters.storeId)}</span>
                    <svg className="w-4 h-4 text-[#4CAF50]" fill="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M19 9l-7 7-7-7" />
                    </svg>
                  </button>
                  {openDropdown === 'store' && (
                    <div className="absolute z-50 mt-1 w-full sm:w-64 bg-white rounded-lg shadow-lg border">
                      <button
                        key="all"
                        onClick={() => {
                          clearExpandedCacheAndCollapse(null);
                          setDealFilters(prev => ({ ...prev, storeId: null }));
                          setOpenDropdown(null);
                        }}
                        className={`block w-full text-left px-4 py-2 hover:bg-gray-100 ${!dealFilters.storeId ? 'bg-[#e8f5e9] text-[#4CAF50]' : ''}`}
                      >
                        All
                      </button>
                      {availableStores.map(store => (
                        <button
                          key={store.store_id}
                          onClick={async () => {
                            clearExpandedCacheAndCollapse(store.store_id);
                            setDealFilters(prev => ({ ...prev, storeId: store.store_id }));
                            setOpenDropdown(null);
                          }}
                          className={`block w-full text-left px-4 py-2 hover:bg-gray-100 ${dealFilters.storeId === store.store_id ? 'bg-[#e8f5e9] text-[#4CAF50]' : ''}`}
                        >
                          {store.store_name || 'Store'}
                        </button>
                      ))}
                    </div>
                  )}
                </div>


                <div className={`relative w-full sm:w-auto ${openDropdown === 'dealType' ? 'z-50' : 'z-30'}`} data-dropdown-root="true">
                  <button
                    onClick={() => setOpenDropdown(openDropdown === 'dealType' ? null : 'dealType')}
                    className="w-full sm:w-auto px-4 py-2 border rounded-lg bg-white hover:bg-gray-50 flex items-center justify-between gap-2"
                  >
                    <span>Deal Type: {dealTypeLabel(dealFilters.dealTypeId)}</span>
                    <svg className="w-4 h-4 text-[#4CAF50]" fill="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M19 9l-7 7-7-7" />
                    </svg>
                  </button>
                  {openDropdown === 'dealType' && (
                    <div className="absolute z-50 mt-1 w-full sm:w-64 bg-white rounded-lg shadow-lg border">
                      <button
                        key="all"
                        onClick={() => {
                          clearExpandedCacheAndCollapse(dealFilters.storeId ?? null);
                          setDealFilters(prev => ({ ...prev, dealTypeId: null }));
                          setOpenDropdown(null);
                        }}
                        className={`block w-full text-left px-4 py-2 hover:bg-gray-100 ${!dealFilters.dealTypeId ? 'bg-[#e8f5e9] text-[#4CAF50]' : ''}`}
                      >
                        All
                      </button>
                      {[2, 1, 4, 3].map(typeId => (
                        <button
                          key={typeId}
                          onClick={() => {
                            clearExpandedCacheAndCollapse(dealFilters.storeId ?? null);
                            setDealFilters(prev => ({ ...prev, dealTypeId: typeId }));
                            setOpenDropdown(null);
                          }}
                          className={`block w-full text-left px-4 py-2 hover:bg-gray-100 ${dealFilters.dealTypeId === typeId ? 'bg-[#e8f5e9] text-[#4CAF50]' : ''}`}
                        >
                          <span>{DEAL_TYPE_META[typeId]?.label} Deal</span>
                          <span className="block text-xs text-gray-500 ml-6">{DEAL_TYPE_META[typeId]?.desc}</span>
                        </button>
                      ))}
                    </div>
                  )}
                </div>

                <div className={`relative w-full sm:w-auto ${openDropdown === 'condition' ? 'z-50' : 'z-30'}`} data-dropdown-root="true">
                  <button
                    onClick={() => setOpenDropdown(openDropdown === 'condition' ? null : 'condition')}
                    className="w-full sm:w-auto px-4 py-2 border rounded-lg bg-white hover:bg-gray-50 flex items-center justify-between gap-2"
                  >
                    <span>Condition: {conditionLabel(dealFilters.conditionId)}</span>
                    <svg className="w-4 h-4 text-[#4CAF50]" fill="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M19 9l-7 7-7-7" />
                    </svg>
                  </button>
                  {openDropdown === 'condition' && (
                    <div className="absolute z-50 mt-1 w-full sm:w-48 bg-white rounded-lg shadow-lg border">
                      {[
                        { id: null, label: 'All' },
                        { id: 1, label: 'New' },
                        { id: 2, label: 'Used' },
                        { id: 3, label: 'Refurbished' }
                      ].map((condition) => (
                        <button
                          key={condition.label}
                          onClick={() => {
                            clearExpandedCacheAndCollapse(dealFilters.storeId ?? null);
                            setDealFilters(prev => ({ ...prev, conditionId: condition.id }));
                            setOpenDropdown(null);
                          }}
                          className={`block w-full text-left px-4 py-2 hover:bg-gray-100 ${dealFilters.conditionId === condition.id ? 'bg-[#e8f5e9] text-[#4CAF50]' : ''}`}
                        >
                          {condition.label}
                        </button>
                      ))}
                    </div>
                  )}
                </div>
               {hasVariantFilters && (
                 <div className={`relative w-full sm:w-auto ${isMoreOpen ? 'z-50' : 'z-30'}`} data-dropdown-root="true">
                    <button
                      onClick={async () => {
                        setOpenDropdown(null);
                        const next = !isMoreOpen;
                        setIsMoreOpen(next);
                        if (next) {
                          setVariantFilterSelections(appliedVariantFilterSelections || {});
                          await loadVariantFilterOptions();
                        }
                      }}
                      className="w-full sm:w-auto px-4 py-2 border rounded-lg bg-white hover:bg-gray-50 flex items-center justify-between gap-2"
                      type="button"
                    >
                      {(() => {
                        const totalSelected = Object.values(appliedVariantFilterSelections || {}).reduce((sum, arr) => {
                          return sum + (Array.isArray(arr) ? arr.length : 0);
                        }, 0);

                        return (
                          <>
                            <span>More</span>
                            {totalSelected > 0 && (
                              <span className="inline-flex items-center justify-center min-w-5 h-5 px-1.5 rounded-full bg-[#e8f5e9] text-[#4CAF50] text-xs font-semibold">
                                {totalSelected}
                              </span>
                            )}
                          </>
                        );
                      })()}
                      <svg className="w-4 h-4 text-[#4CAF50]" fill="currentColor" viewBox="0 0 24 24">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M19 9l-7 7-7-7" />
                      </svg>
                    </button>
                  </div>
               )}


                {/* Product Attribute Filters - Consistent Dropdowns */}
                {/*productAttributes.map(attr => (
                <div key={attr.name} className="relative z-20">
                  <button
                    onClick={() => setOpenDropdown(openDropdown === attr.name ? null : attr.name)}
                    className="px-4 py-2 border rounded-lg bg-white hover:bg-gray-50 flex items-center space-x-2"
                  >
                    <span>{attr.name}: {attributeFilters[attr.name] || 'All'}</span>
                    <svg className="w-4 h-4 text-[#4CAF50]" fill="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M19 9l-7 7-7-7" />
                    </svg>
                  </button>
                  {openDropdown === attr.name && (
                    <div className="absolute z-30 mt-1 w-48 bg-white rounded-lg shadow-lg border">
                      <button
                        key="all"
                        onClick={() => {
                          setAttributeFilters(f => ({ ...f, [attr.name]: '' }));
                          setOpenDropdown(null);
                        }}
                        className={`block w-full text-left px-4 py-2 hover:bg-gray-100 ${!attributeFilters[attr.name] ? 'bg-[#e8f5e9] text-[#4CAF50]' : ''}`}
                      >
                        All
                      </button>
                      {attr.values.map(val => (
                        <button
                          key={val}
                          onClick={() => {
                            setAttributeFilters(f => ({ ...f, [attr.name]: val }));
                            setOpenDropdown(null);
                          }}
                          className={`block w-full text-left px-4 py-2 hover:bg-gray-100 ${attributeFilters[attr.name] === val ? 'bg-[#e8f5e9] text-[#4CAF50]' : ''}`}
                        >
                          {val}
                        </button>
                      ))}
                    </div>
                  )}
                </div>
              ))*/}
            </div>

            {isMoreOpen && (
              <div className="mb-6 border rounded-lg shadow-lg bg-white" data-dropdown-root="true">
                <div className="flex items-center justify-between px-4 py-3 border-b">
                  <div className="font-semibold">More</div>
                  <div className="flex items-center gap-2">
                    <button
                      type="button"
                      onClick={closeMorePanel}
                      className="text-sm text-slate-600 hover:text-slate-800 underline underline-offset-2"
                    >
                      Close
                    </button>
                  </div>
                </div>

                <div className="p-4 max-h-[60vh] overflow-y-auto">
                  {variantFilterLoading ? (
                    <div className="text-sm text-gray-500">Loading…</div>
                  ) : (
                    <>
                      {variantFilterError && (
                        <div className="text-sm text-red-600 mb-3">{variantFilterError}</div>
                      )}

                      {(() => {
                        const attrs = (variantFilterOptions?.attributes || variantFilterOptions?.Attributes || []);
                        if (!Array.isArray(attrs) || attrs.length === 0) {
                          return <div className="text-sm text-gray-500">No filters available.</div>;
                        }

                        return (
                          <div className="grid grid-cols-1 md:grid-cols-3 gap-6">
                            {attrs.map((attr) => {
                              const attributeId = Number(attr.attributeId ?? attr.AttributeId);
                              if (Number.isNaN(attributeId) || attributeId <= 0) return null;

                              const label = (
                                attr.description ??
                                attr.Description ??
                                attr.label ??
                                attr.Label ??
                                attr.attributeKey ??
                                attr.AttributeKey ??
                                `Attribute ${attributeId}`
                              ).toString();
                              const options = (attr.options ?? attr.Options ?? [])
                                .map(o => ({
                                  id: Number(o.id ?? o.Id),
                                  label: (o.displayName ?? o.DisplayName ?? o.enumKey ?? o.EnumKey ?? '').toString(),
                                  sortOrder: Number(o.sortOrder ?? o.SortOrder ?? 0)
                                }))
                                .filter(o => !Number.isNaN(o.id) && o.id > 0 && o.label);

                              if (options.length === 0) return null;

                              const key = attributeId.toString();
                              const selected = Array.isArray(variantFilterSelections?.[key]) ? variantFilterSelections[key] : [];
                              const selectedCount = selected.length;

                              const selectionLabel = selectedCount > 0
                                ? `${selectedCount} selected`
                                : 'Any';

                              return (
                                <div key={key} className="min-w-0">
                                  <div className="text-sm font-medium text-slate-700 mb-2 truncate" title={label}>{label}</div>

                                  <details className="group border rounded-lg bg-white">
                                    <summary className="px-3 py-2 cursor-pointer select-none flex items-center justify-between text-sm text-slate-700 [&::-webkit-details-marker]:hidden">
                                      <span className="truncate">{selectionLabel}</span>
                                      <svg className="w-4 h-4 text-[#4CAF50] transition-transform group-open:rotate-180" fill="currentColor" viewBox="0 0 24 24" aria-hidden="true">
                                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M19 9l-7 7-7-7" />
                                      </svg>
                                    </summary>

                                    <div className="px-3 pb-3 pt-1 max-h-56 overflow-y-auto flex flex-col gap-2">
                                      {options
                                        .sort((a, b) => a.sortOrder - b.sortOrder || a.label.localeCompare(b.label))
                                        .map((opt) => (
                                          <label key={opt.id} className="flex items-center gap-2 text-sm text-slate-700">
                                            <input
                                              type="checkbox"
                                              checked={selected.map(Number).includes(opt.id)}
                                              onChange={() => toggleVariantFilterValue(attributeId, opt.id)}
                                              className="h-4 w-4"
                                            />
                                            <span className="truncate" title={opt.label}>{opt.label}</span>
                                          </label>
                                        ))}
                                    </div>
                                  </details>
                                </div>
                              );
                            })}
                          </div>
                        );
                      })()}
                    </>
                  )}
                </div>

                <div className="px-4 py-3 border-t bg-white flex items-center justify-end gap-3">
                  <button
                    type="button"
                    onClick={clearAppliedVariantFilters}
                    className="px-4 py-2 border rounded-lg bg-white hover:bg-gray-50 text-slate-700"
                  >
                    Clear
                  </button>
                  <button
                    type="button"
                    onClick={() => {
                      applyVariantFilters();
                      setIsMoreOpen(false);
                    }}
                    className="px-4 py-2 rounded-lg bg-[#4CAF50] hover:bg-[#3d8b40] text-white"
                  >
                    Apply
                  </button>
                </div>
              </div>
            )}

            {/* Deals List */}
            <h2 className="text-xl font-bold mb-6">Lowest Price Possible</h2>
            {initialLoading ? (
              <LoadingSpinner />
            ) : (
              <>
                <div className="space-y-6">
                  {collapsedStoreDeals.length > 0 ? (
                    collapsedStoreDeals.map(primaryDeal => {
                      const storeId = primaryDeal.store_id;
                      const storeName = primaryDeal.store_name || getDomain(primaryDeal.store_url || primaryDeal.url);
                      const additionalCount = Number(primaryDeal.additional_deal_count || 0);
                      const isStoreFiltered = !!dealFilters.storeId && storeId === dealFilters.storeId;
                      const isExpanded = !!(storeId && (isStoreFiltered || expandedStoreIds.includes(storeId)));
                      const rawDealsForStore = isExpanded
                        ? (expandedStoreDealsById[storeId] || [primaryDeal])
                        : [primaryDeal];

                      const visibleDealsForStore = rawDealsForStore
                        .map((deal, rawIndex) => ({ deal, rawIndex }));

                      if (visibleDealsForStore.length === 0) return null;

                      const onExpand = async () => {
                        if (!storeId) return;
                        setExpandedStoreIds(prev => (prev.includes(storeId) ? prev : [...prev, storeId]));
                        try {
                          setDealsLoading(true);
                          const rows = await ensureExpandedStoreDeals(storeId);
                          setExpandedStoreDealsById(prev => ({ ...prev, [storeId]: rows }));
                          seedFlaggedDealsFromBatch(rows);
                        } catch (e) {
                          console.error(e);
                        } finally {
                          setDealsLoading(false);
                        }
                      };

                      const onCollapse = () => {
                        setExpandedStoreIds(prev => prev.filter(id => id !== storeId));
                        setExpandedStoreDealsById(prev => {
                          const next = { ...prev };
                          delete next[storeId];
                          return next;
                        });
                      };

                      return (
                        <div key={storeId ?? primaryDeal.deal_id} className="border rounded-lg p-4">
                          <div className="flex items-center justify-between mb-4">
                            <div className="flex items-center gap-3">
                              {primaryDeal.store_image_url && (
                                <img
                                  src={primaryDeal.store_image_url}
                                  alt={storeName}
                                  className="w-8 h-8 rounded"
                                />
                              )}
                              <div className="font-semibold text-base">{storeName}</div>
                            </div>
                          </div>

                          <div className="space-y-4">
                            {visibleDealsForStore.map(({ deal, rawIndex }, idx) => {
                              // Anonymous obfuscation toggle: keep logic here for possible future re-enable.
                              const ENABLE_ANON_OBFUSCATION = false;
                              const shouldObfuscate = ENABLE_ANON_OBFUSCATION && !isAuthenticated && isExpanded && rawIndex > 0;
                              return (
                              <div
                                key={deal.deal_id}
                                className="border rounded-lg p-4 hover:shadow-md transition-shadow relative flex flex-col text-base overflow-hidden"
                              >
                        {/* Price & badges */}
                        <span className="absolute top-4 right-4 flex flex-col items-end">
                          <div className="flex items-center gap-2">
                            {deal.discount_percent > 0 && (
                              <span className="bg-green-100 text-green-700 text-xs font-semibold px-2 py-1 rounded-full">
                                {deal.discount_percent}% Off
                              </span>
                            )}                            
                            <span className="font-bold text-green-600 text-xl">
                              {formatPrice(deal.price)}
                            </span>

                          </div>
                          {deal.msrp != null && deal.msrp > deal.price && (
                            <span className="text-xs text-red-600 font-semibold">
                              Save {formatPrice(deal.msrp - deal.price)}
                            </span>
                          )}
                          {deal.free_shipping && (
                            <span className="flex items-center gap-1 text-xs text-gray-600 mt-1">
                              <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2"
                                  d="M5 8h14M5 8a2 2 0 110-4h14a2 2 0 110 4M5 8v10a2 2 0 002 2h10a2 2 0 002-2V8m-9 4h4" />
                              </svg>
                              Free Shipping
                            </span>
                          )}
                        </span>

                        {/* Login overlay (only for expanded secondary deals) */}
                        {shouldObfuscate && (
                          <div className="absolute inset-0 flex items-center justify-center z-10">
                            <div className="bg-gray-200 bg-opacity-50 text-gray-700 px-4 py-2 rounded shadow text-center font-semibold text-base">
                              <span>
                                <Link
                                  to={`/login?redirect=${encodeURIComponent(window.location.pathname)}`}
                                  className="text-[#4CAF50] underline hover:text-[#3d8b40]"
                                >
                                  Login
                                </Link>
                                {" or "}
                                <Link
                                  to={`/signup`}
                                  className="text-[#4CAF50] underline hover:text-[#3d8b40]"
                                >
                                  Create an Account
                                </Link>
                                {` to see how to buy it for ${formatPrice(deal.price)}`}
                              </span>
                            </div>
                          </div>
                        )}

                        <div className={shouldObfuscate ? 'blur-sm select-none flex-1' : 'flex-1'}>
                          {/* Stacked (combo) deal */}
                          {deal.deal_type_id === 3 && (
                            <div>
                              {/* Stacked Deal Header (always visible) */}
                              <div className="flex flex-wrap items-center gap-2 mb-2">
                                <span
                                  className={`flex items-center px-2 py-1 rounded font-semibold text-sm ${DEAL_TYPE_META[3].badge}`}
                                  title={DEAL_TYPE_META[3].desc}
                                >
                                  {DEAL_TYPE_META[3].icon} Stacked Deal
                                </span>
                                <span className="text-sm text-amber-700 font-medium">
                                  {deal.steps?.length || 0} deals
                                </span>


                              </div>

                              <div className="mb-1">
                                <span className="text-gray-600 font-medium text-sm">Condition:</span>{' '}
                                <span className="text-sm">{deal.condition_name}</span>
                              </div>

                              {deal.additional_details && (
                                <div className="mb-1">
                                  <span className="text-gray-600 font-medium text-sm">Additional Details:</span>{' '}
                                  <span className="text-sm">{deal.additional_details}</span>
                                </div>
                              )}

                              {/* Steps list (each step has its own accordion header) */}
                              {deal.steps && (
                                <div className="mt-2 flex flex-col divide-y border rounded-md overflow-hidden">
                                  {deal.steps.map((step, idx) => {
                                    const key = step.deal_id || `${deal.deal_id}-${idx}`;
                                    const open = !!expandedStackedSteps[`${deal.deal_id}:${key}`];
                                    const meta = DEAL_TYPE_META[step.deal_type_id] || {};
                                    return (
                                      <div key={key}>
                                        {/* Step summary row */}
                                        <button
                                          type="button"
                                          onClick={() => toggleStackedStep(deal.deal_id, key)}
                                          className="w-full flex items-center gap-3 px-2 py-2 text-left hover:bg-gray-50 transition"
                                        >
                                          <span className="inline-flex h-6 w-6 items-center justify-center rounded-full bg-white border text-xs font-semibold text-slate-700">
                                            {idx + 1}
                                          </span>
                                          <span className="text-sm font-medium text-blue-800 flex items-center gap-1">
                                            {meta.icon}
                                            {meta.label} Deal
                                          </span>
                                          {step.discount_percent > 0 && (
                                            <span className="bg-green-100 text-green-700 text-xs font-semibold px-2 py-0.5 rounded-full">
                                              {step.discount_percent}% off
                                            </span>
                                          )}
                                          {!isMobile && step.coupon_code && (
                                            <code className="bg-white border px-2 py-0.5 rounded text-sm">
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

                                        {/* Step details */}
                                        {open && (
                                          <div className="px-2 pb-2 pt-1 text-sm text-slate-700">
                                            {step.external_offer_url != null && (
                                              <div className="mt-2">
                                                <span className="text-gray-600 font-medium">Activate at:</span>{' '}
                                                {step.external_store_url || 'N/A'}
                                                {step.external_upfront_cost != null && (
                                                  renderUpfrontCost(step.external_upfront_cost, step.external_upfront_cost_term_id)
                                                )}

                                              </div>
                                            )}

                                            <div className="mt-2">
                                              <span className="text-gray-600 font-medium">Buy at:</span>{' '}
                                              {step.store_url || 'N/A'}
                                              {step.upfront_cost != null && (
                                                renderUpfrontCost(step.upfront_cost, step.upfront_cost_term_id)
                                              )}
                                            </div>
                                            {step.coupon_code && (
                                              <div className="mt-1">
                                                <span className="text-gray-600 font-medium">Coupon Code:</span>{' '}
                                                <code
                                                  onClick={(event) => {
                                                    navigator.clipboard.writeText(deal.coupon_code);
                                                    const el = document.createElement('span');
                                                    el.textContent = 'Copied!';
                                                    el.className = 'text-green-600 text-xs ml-2';
                                                    event.target.parentNode.appendChild(el);
                                                    setTimeout(() => el.remove(), 1500);
                                                  }}
                                                  className="bg-gray-100 px-2 py-1 rounded cursor-pointer hover:bg-gray-200 transition-colors text-sm"
                                                  title="Click to copy"
                                                >
                                                  {isAuthenticated ? step.coupon_code : ''}
                                                </code>
                                              </div>
                                            )}
                                            {step.additional_details && (
                                              <div className="mt-2 text-sm text-gray-600">
                                                <span className="text-gray-600 font-medium">Additional Details:</span> {step.additional_details}
                                              </div>
                                            )}
                                          
                                              {step.deal_type_id !== 3 && (
                                                  <div className="mt-2 w-full flex flex-col gap-2">
                                                  {step.deal_type_id === 4 ? (
                                                    <div
                                                      role="group"
                                                      aria-label="External offer steps"
                                                      className="flex w-full gap-2 justify-end flex-nowrap"
                                                    >
                                                      <a
                                                        href={appendAffiliateParam(
                                                          step.external_offer_url,
                                                          (step?.affiliate_code_var ?? step?.affiliateCodeVar ?? deal?.affiliate_code_var ?? deal?.affiliateCodeVar),
                                                          (step?.affiliate_code ?? step?.affiliateCode ?? deal?.affiliate_code ?? deal?.affiliateCode)
                                                        )}
                                                        target="_blank"
                                                        rel="noopener noreferrer"
                                                        onClick={(e) => handleGetDealClick(e, step.deal_id, true, step.external_offer_url)}
                                                        className={`${BUTTON_STYLES.base} ${BUTTON_STYLES.blue} whitespace-nowrap`}
                                                      >
                                                        Activate Offer
                                                      </a>
                                                      <a
                                                        href={appendAffiliateParam(
                                                          step.url,
                                                          (step?.affiliate_code_var ?? step?.affiliateCodeVar ?? deal?.affiliate_code_var ?? deal?.affiliateCodeVar),
                                                          (step?.affiliate_code ?? step?.affiliateCode ?? deal?.affiliate_code ?? deal?.affiliateCode)
                                                        )}
                                                        target="_blank"
                                                        rel="noopener noreferrer"
                                                        onClick={(e) => handleGetDealClick(e, step.deal_id, false, step.url)}
                                                        className={`${BUTTON_STYLES.base} ${BUTTON_STYLES.green} whitespace-nowrap`}
                                                      >
                                                        Buy at {getDomain(step.store_url || step.url)}
                                                      </a>
                                                    </div>
                                                  ) : (
                                                                            <div
                                   role="group"
                                   aria-label="External offer steps"
                                   className="flex w-full gap-2 justify-end flex-nowrap"
                                 >
                                                    <a
                                                      href={appendAffiliateParam(
                                                        step.url,
                                                        (step?.affiliate_code_var ?? step?.affiliateCodeVar ?? deal?.affiliate_code_var ?? deal?.affiliateCodeVar),
                                                        (step?.affiliate_code ?? step?.affiliateCode ?? deal?.affiliate_code ?? deal?.affiliateCode)
                                                      )}
                                                      target="_blank"
                                                      rel="noopener noreferrer"
                                                      onClick={(e) => handleGetDealClick(e, step.deal_id, false, step.url)}
                                                      className={`${BUTTON_STYLES.base} ${BUTTON_STYLES.green} whitespace-nowrap`}
                                                    >
                                                      View
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
                            </div>
                          )}

                          {/* Non-stacked deals */}
                          {deal.deal_type_id !== 3 && (
                            <>
                              <div className="mb-2 flex items-center">
                                <span
                                  className={`flex items-center px-2 py-1 rounded font-semibold mr-2 text-sm ${DEAL_TYPE_META[deal.deal_type_id]?.badge}`}
                                  title={DEAL_TYPE_META[deal.deal_type_id]?.desc}
                                >
                                  {DEAL_TYPE_META[deal.deal_type_id]?.icon}
                                  {DEAL_TYPE_META[deal.deal_type_id]?.label} Deal
                                </span>
                              </div>


                              {deal.external_offer_url != null && (
                                <div className="mb-1">
                                  <span className="text-gray-600 font-medium text-sm">Activate at:</span>{' '}
                                  <span className="text-sm ">{shouldObfuscate ? '' : deal.external_store_url}</span>
                                  {deal.external_upfront_cost != null && (
                                    renderUpfrontCost(deal.external_upfront_cost, deal.external_upfront_cost_term_id)
                                  )}
                                </div>
                              )}
                              <div className="mb-1">
                                <span className="text-gray-600 font-medium text-sm">Buy at:</span>{' '}
                                <span className="text-sm">{shouldObfuscate ? '' : deal.store_url}</span>
                                {deal.upfront_cost != null && (
                                  renderUpfrontCost(deal.upfront_cost, deal.upfront_cost_term_id)
                                )}
                              </div>
                              {deal.coupon_code && (
                                <div className="mb-1 text-sm">
                                  <span className="text-gray-600 font-medium">Coupon Code:</span>{' '}
                                  <code
                                    onClick={(event) => {
                                      navigator.clipboard.writeText(deal.coupon_code);
                                      const el = document.createElement('span');
                                      el.textContent = 'Copied!';
                                      el.className = 'text-green-600 text-xs ml-2';
                                      event.target.parentNode.appendChild(el);
                                      setTimeout(() => el.remove(), 1500);
                                    }}
                                    className="bg-gray-100 px-2 py-1 rounded cursor-pointer hover:bg-gray-200 transition-colors text-sm"
                                    title="Click to copy"
                                  >
                                    {shouldObfuscate ? '' : deal.coupon_code}
                                  </code>
                                </div>
                              )}
                              <div className="mb-1">
                                <span className="text-gray-600 font-medium text-sm">Condition:</span>{' '}
                                <span className="text-sm">{deal.condition_name}</span>
                              </div>
                              {deal.additional_details && (
                                <div className="mb-1 text-sm">
                                  <span className="text-gray-600 font-medium">Additional Details:</span>{' '}
                                  {deal.additional_details}
                                </div>
                              )}
                            </>
                          )}
                        </div>



                        {/* Action buttons show for authenticated users and for non-obfuscated primary deal */}
                        {!shouldObfuscate && (
                          <div className="mt-2 w-full flex flex-col gap-2">
                            {deal.deal_type_id === 4 && (
                              <div
                                role="group"
                                aria-label="External offer steps"
                                className="flex w-full gap-2 justify-end flex-nowrap"
                              >
                                <a
                                  href={appendAffiliateParam(
                                    deal.external_offer_url,
                                    (deal?.external_affiliate_code_var ?? deal?.externalAffiliateCodeVar ?? deal?.affiliate_code_var ?? deal?.affiliateCodeVar),
                                    (deal?.external_affiliate_code ?? deal?.externalAffiliateCode ?? deal?.affiliate_code ?? deal?.affiliateCode)
                                  )}
                                  target="_blank"
                                  rel="noopener noreferrer"
                                  onClick={(e) => handleGetDealClick(e, deal.deal_id, true, deal.external_offer_url)}
                                  className={`${BUTTON_STYLES.base} ${BUTTON_STYLES.blue} whitespace-nowrap`}
                                >
                                  Activate Offer
                                </a>
                                <a
                                  href={appendAffiliateParam(
                                    deal.url,
                                    (deal?.affiliate_code_var ?? deal?.affiliateCodeVar),
                                    (deal?.affiliate_code ?? deal?.affiliateCode)
                                  )}
                                  target="_blank"
                                  rel="noopener noreferrer"
                                  onClick={(e) => handleGetDealClick(e, deal.deal_id, false, deal.url)}
                                  className={`${BUTTON_STYLES.base} ${BUTTON_STYLES.green} whitespace-nowrap`}
                                >
                                  Buy at {getDomain(deal.store_url || deal.url)}
                                </a>
                              </div>
                            )}

                            {deal.deal_type_id !== 3 && deal.deal_type_id !== 4 && (
                              <div
                                role="group"
                                aria-label="External offer steps"
                                className="flex w-full gap-2 justify-end flex-nowrap"
                              >
                                <a
                                  href={appendAffiliateParam(
                                    deal.url,
                                    (deal?.affiliate_code_var ?? deal?.affiliateCodeVar),
                                    (deal?.affiliate_code ?? deal?.affiliateCode)
                                  )}
                                  target="_blank"
                                  rel="noopener noreferrer"
                                  onClick={(e) => handleGetDealClick(e, deal.deal_id, false, deal.url)}
                                  className={`${BUTTON_STYLES.base} ${BUTTON_STYLES.green} whitespace-nowrap`}
                                >
                                  View
                                </a>
                              </div>
                            )}

                            {/* Footer actions inside card */}
                            {(
                              <div className={`flex items-center justify-between text-xs text-gray-400 ${deal.deal_type_id === 3 ? 'pt-2 ' : ''}`}>
                                <div className="flex items-center gap-2 text-xs text-gray-400">
                                  <Link to={`/profile/${deal.user_name}`} className="flex items-center gap-2 flex-shrink-0">
                                    <img
                                      src={deal.user_image_url}
                                      alt={deal.user_name}
                                      className="w-6 h-6 rounded-full"
                                    />
                                    <span className="truncate">@{deal.user_name} ({deal.level}%)</span>
                                  </Link>
                                </div>

                                <button
                                  onClick={() => openFlagModal({ dealId: deal.deal_id, dealProductId: deal.deal_product_id })}
                                  disabled={isDealFlagged(deal)}
                                  title={isDealFlagged(deal) ? 'Deal flagged' : 'Flag this deal'}
                                  aria-label={isDealFlagged(deal) ? 'Deal flagged' : 'Flag this deal'}
                                  className={
                                    isDealFlagged(deal)
                                      ? 'text-sm text-red-600 cursor-default select-none no-underline'
                                      : BUTTON_STYLES.flagLink
                                  }
                                >
                                  {isDealFlagged(deal) ? 'Deal Flagged' : 'Not working?'}
                                </button>
                              </div>
                            )}
                          </div>
                        )}

                              </div>
                            );
                          })}
                          </div>

                          {/* Expand/Collapse CTA */}
                          {storeId && !isExpanded && additionalCount > 0 && (
                            <button
                              type="button"
                              onClick={onExpand}
                              className="mt-4 text-xs text-[#4CAF50] hover:underline"
                              disabled={dealsLoading}
                            >
                              View {Math.min(additionalCount, 5)} more from {storeName}
                            </button>
                          )}

                          {storeId && isExpanded && (
                            <button
                              type="button"
                              onClick={onCollapse}
                              className="mt-4 text-xs text-[#4CAF50] hover:underline"
                            >
                              Hide {storeName} deals
                            </button>
                          )}
                        </div>
                      );
                    })
                  ) : (
                    <div className="text-center py-8 text-gray-500 text-base">
                      No deals found matching your filters
                    </div>
                  )}
                </div>
              </>
            )}
          </div>
        </div>
      </div>

      {/* Modals */}
      <SubmitDealModal
        isOpen={isModalOpen}
        onClose={() => setIsModalOpen(false)}
        productId={product.id}
        msrpPrice={product.msrp}   // ADDED
      />
      <ComboDealModal
        isOpen={isComboModalOpen}
        onClose={() => setIsComboModalOpen(false)}
        productId={product.id}
        msrpPrice={product.msrp} // PASS MSRP DOWN
        onComboCreated={() => {
          expandedStoreCacheRef.current.clear();
          setExpandedStoreDealsById({});
          setExpandedStoreIds(dealFilters.storeId ? [dealFilters.storeId] : []);
          setInitialLoading(true);
        }}
      />
      <RatingSourcesModal
        isOpen={isRatingModalOpen}
        onClose={() => setIsRatingModalOpen(false)}
        sources={ratingSources}
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
                      checked={adminDeleteProductDeal}
                      onChange={(e) => {
                        const checked = !!e.target.checked;
                        setAdminDeleteProductDeal(checked);
                        if (checked) setAdminDeleteDeal(false);
                      }}
                      className="h-4 w-4"
                      disabled={!dealToFlag?.dealProductId}
                    />
                    <span>Admin: delete this product deal only</span>
                  </label>
                  <label className="flex items-center gap-2 text-sm text-slate-700">
                    <input
                      type="checkbox"
                      checked={adminDeleteDeal}
                      onChange={(e) => {
                        const checked = !!e.target.checked;
                        setAdminDeleteDeal(checked);
                        if (checked) setAdminDeleteProductDeal(false);
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
                  onChange={e => setFlagReasonId(e.target.value ? Number(e.target.value) : null)}
                  className="w-full mb-4 px-3 py-2 border rounded-md text-sm"
                >
                  <option value="">Select a reason...</option>
                  {DEAL_FLAG_REASONS.map(r => (
                    <option key={r.id} value={r.id}>{r.label}</option>
                  ))}
                </select>
                <label className="block text-sm font-medium text-gray-700 mb-1">
                  Comments {flagReasonId === 6 ? '*' : '(Optional)'}
                </label>
                <textarea
                  rows={3}
                  value={flagComment}
                  onChange={e => setFlagComment(e.target.value)}
                  className={`w-full px-3 py-2 border rounded-md text-sm ${flagReasonId === 6 && !flagComment.trim() ? 'border-red-400' : 'border-gray-300'
                    }`}
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
                  setAdminDeleteProductDeal(false);
                  setAdminDeleteDeal(false);
                }}
                className="px-4 py-2 text-gray-600 hover:text-gray-800 transition-colors"
                disabled={flagSubmitting}
              >
                Cancel
              </button>
              <button
                type="button"
                onClick={() => handleFlagDeal()}
                disabled={
                  flagSubmitting ||
                  (!((isAdmin && (adminDeleteProductDeal || adminDeleteDeal))) && !flagReasonId) ||
                  (isAdmin && adminDeleteProductDeal && !dealToFlag?.dealProductId) ||
                  (!((isAdmin && (adminDeleteProductDeal || adminDeleteDeal))) && flagReasonId === 6 && !flagComment.trim())
                }
                className="px-4 py-2 bg-red-600 text-white rounded-lg hover:bg-red-700 transition-colors disabled:opacity-60"
              >
                {flagSubmitting
                  ? 'Submitting...'
                  : ((isAdmin && adminDeleteDeal)
                    ? 'Delete Entire Deal'
                    : ((isAdmin && adminDeleteProductDeal)
                      ? 'Delete Product Deal'
                      : 'Submit Flag'))}
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Admin Edit Modal */}
      <AdminProductModal
        isOpen={isAdminEditOpen}
        onClose={closeAdminEdit}
        mode="edit"
        productId={product?.id}
        onUpdated={() => {
          refreshProduct();
          closeAdminEdit();
        }}
      />
    </div>
  );
};

export default ProductPage;