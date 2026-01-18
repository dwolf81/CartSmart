import React, { useState, useEffect, useRef } from 'react';
import { useAuth } from '../context/AuthContext';
import LoadingSpinner from './LoadingSpinner';
import { FaTag, FaTicketAlt, FaLink, FaLayerGroup } from 'react-icons/fa';

const API_URL = process.env.REACT_APP_API_URL || 'http://localhost:5000';

const SubmitDealModal = ({ isOpen, onClose, productId, msrpPrice, storeId = null, scope = 'product', mode = 'create', deal = null, onSubmitted }) => {
  const { user } = useAuth();

  const isStoreDeal = scope === 'store';
  const effectiveStoreId = storeId ?? (deal?.store_id || deal?.storeId) ?? null;


  const [urlError, setUrlError] = useState('');
  const [externalUrlError, setExternalUrlError] = useState('');
  const [submitError, setSubmitError] = useState('');

  const initialFormData = {
    dealType: 'direct',
    price: '',
    freeShipping: false,
    url: '',
    externalOfferUrl: '',
    additionalDetails: '',
    couponCode: '',
    condition: 'new',
    discountPercent: '',
    expirationDate: '',
  };
  const [formData, setFormData] = useState(initialFormData);
  const [visibleSteps, setVisibleSteps] = useState(2);
  const [loading, setLoading] = useState(false);
  const [userDeals, setUserDeals] = useState([]);
  const [dealDomain, setDealDomain] = useState('');
  const [detailsTouched, setDetailsTouched] = useState(false);
  const [lastChanged, setLastChanged] = useState(null); // 'price' | 'discountPercent' | null

  // --- DYNAMIC PRODUCT ATTRIBUTE FIELDS FOR DEAL SUBMISSION ---
  const [productAttributes, setProductAttributes] = useState([]); // VariantFilterAttributeDTO[]
  const [dealAttributes, setDealAttributes] = useState({}); // { [attributeId]: number[] }
  const dealAttributesTouchedRef = useRef(false);
  const dealAttributesAutoInitializedRef = useRef(false);
  // Baseline override price (current site price) if a direct approved deal exists below MSRP
  const [baselinePrice, setBaselinePrice] = useState(null);

  // Helper to extract domain from URL
  const extractDomain = url => {
    try {
      const u = new URL(url);
      return u.hostname.replace(/^www\./, '');
    } catch {
      return '';
    }
  };

  // Fetch user's deals with matching domain
  useEffect(() => {
    if (!user || !formData.url) return;
    const domain = extractDomain(formData.url);
    setDealDomain(domain);
    if (!domain) return;
    (async () => {
      try {
        const res = await fetch(`${API_URL}/api/deals/user?status=active,pending`, { credentials: 'include' });
        if (!res.ok) return;
        const allDeals = await res.json();
        const filtered = allDeals.filter(d => extractDomain(d.dealUrl || d.url) === domain);
        setUserDeals(filtered);
      } catch {}
    })();
  }, [formData.url, user]);

  useEffect(() => {
    // Fetch product attributes + allowed enum values for this product
    if (!isOpen) return;

    // When editing/creating store-wide deals, ensure product-specific state is cleared
    // so we don't leak UI from a previously edited product deal.
    if (isStoreDeal || !productId)
    {
      setProductAttributes([]);
      setDealAttributes({});
      dealAttributesTouchedRef.current = false;
      dealAttributesAutoInitializedRef.current = false;
      return;
    }
    let aborted = false;

    (async () => {
      try {
        const res = await fetch(`${API_URL}/api/products/${productId}/variant-filters`, { credentials: 'include' });
        if (!res.ok) return;
        const data = await res.json().catch(() => null);
        if (aborted || !data) return;
        const attrs = data.attributes || data.Attributes || [];
        setProductAttributes(Array.isArray(attrs) ? attrs : []);
      } catch {
        if (!aborted) setProductAttributes([]);
      }
    })();

    return () => {
      aborted = true;
    };
  }, [isOpen, isStoreDeal, productId]);

  // Default-select ALL product options unless the listing is eBay.
  // Rationale: most listings allow choosing variants on the store site, so deals should match all filters.
  useEffect(() => {
    if (!isOpen) return;
    if (isStoreDeal) return;
    if (!productId) return;
    if (!Array.isArray(productAttributes) || productAttributes.length === 0) return;

    const domain = extractDomain(formData.url || '');
    const isEbayListing = !!domain && /(^|\.)ebay\./i.test(domain);

    const hasAnySelection = Object.values(dealAttributes || {}).some(v => Array.isArray(v) && v.length > 0);

    // If the URL is eBay and we only auto-initialized selections, clear them.
    if (isEbayListing)
    {
      if (dealAttributesAutoInitializedRef.current && !dealAttributesTouchedRef.current)
      {
        setDealAttributes({});
        dealAttributesAutoInitializedRef.current = false;
      }
      return;
    }

    // Non-eBay: if nothing selected yet and user hasn't made selections, select all.
    if (!hasAnySelection && !dealAttributesTouchedRef.current)
    {
      const next = {};
      productAttributes.forEach((attr) => {
        const attributeId = attr.attributeId ?? attr.AttributeId;
        const options = attr.options ?? attr.Options ?? [];
        if (!attributeId || !Array.isArray(options) || options.length === 0) return;
        const allIds = options
          .map(o => Number(o.id ?? o.Id))
          .filter(n => !Number.isNaN(n) && n > 0);
        if (allIds.length > 0) next[attributeId] = allIds;
      });
      setDealAttributes(next);
      dealAttributesAutoInitializedRef.current = true;
    }
  }, [isOpen, isStoreDeal, productId, productAttributes, formData.url, dealAttributes]);

  // Derive baseline price for coupon/external deals based on existing direct approved deals for this product & domain
  useEffect(() => {
    // Only relevant for creating or editing coupon/external deals
    if (!productId || !formData.url || (formData.dealType !== 'coupon' && formData.dealType !== 'external')) {
      setBaselinePrice(null);
      return;
    }
    const domain = extractDomain(formData.url);
    if (!domain) {
      setBaselinePrice(null);
      return;
    }
    let abort = false;
    (async () => {
      try {
        // Fetch direct NEW deals (conditionId=1, dealTypeId=1)
        const resp = await fetch(`${API_URL}/api/deals/product/${productId}?conditionId=1&dealTypeId=1&pageSize=50`, { credentials: 'include' });
        if (!resp.ok) return;
        const data = await resp.json();
        const arr = data.Deals || data.deals || [];
        // Filter: domain match, approved status (2), price > 0
        const candidatePrices = arr
          .filter(d => {
            if (!d) return false;
            const dDomain = extractDomain(d.url || d.external_offer_url || '');
            if (!dDomain || dDomain !== domain) return false;
            if (Number(d.deal_status_id) !== 2) return false; // Approved
            const p = parseFloat(d.price);
            return !isNaN(p) && p > 0;
          })
          .map(d => parseFloat(d.price));
        if (abort) return;
        if (candidatePrices.length === 0) {
          setBaselinePrice(null);
        } else {
          // Lowest price
            const lowest = candidatePrices.reduce((m, v) => v < m ? v : m, candidatePrices[0]);
          // Only override if below MSRP (avoid inflating discount if data anomaly)
          if (typeof msrpPrice === 'number' && msrpPrice > 0 && lowest < msrpPrice) {
            setBaselinePrice(lowest);
          } else {
            setBaselinePrice(null);
          }
        }
      } catch {
        if (!abort) setBaselinePrice(null);
      }
    })();
    return () => { abort = true; };
  }, [productId, formData.url, formData.dealType, msrpPrice]);

  // Prefill when editing
  useEffect(() => {
    if (!isOpen || mode !== 'edit' || !deal) return;
    const mapDealType = (id) => (id === 1 ? 'direct' : id === 2 ? 'coupon' : id === 4 ? 'external' : 'direct');
    const mapCondition = (id) => (id === 2 ? 'used' : id === 3 ? 'refurbished' : 'new');

    if (isStoreDeal) {
      setUrlError('');
      setFormData({
        ...(deal?.deal_id ? { id: deal.deal_id } : {}),
        dealType: mapDealType(deal.deal_type_id || deal.dealTypeId),
        // store-wide edits: only store-wide fields
        price: '',
        freeShipping: false,
        url: '',
        externalOfferUrl: deal.external_offer_url || deal.externalOfferUrl || '',
        additionalDetails: deal.additional_details || '',
        couponCode: deal.coupon_code || deal.couponCode || '',
        condition: 'new',
        discountPercent: deal.discount_percent != null
          ? Math.round(deal.discount_percent)
          : (deal.discountPercent != null ? Math.round(deal.discountPercent) : ''),
        expirationDate: deal.expiration_date
          ? new Date(deal.expiration_date).toISOString().slice(0, 16)
          : (deal.expirationDate ? new Date(deal.expirationDate).toISOString().slice(0, 16) : ''),
      });
      return;
    }

    setFormData({
      ...(deal?.deal_id ? { id: deal.deal_id } : {}),
      dealType: mapDealType(deal.deal_type_id || deal.dealTypeId),
      price: deal.price ?? '',
      freeShipping: !!(deal.free_shipping ?? deal.freeShipping),
      url: deal.url || '',
      externalOfferUrl: deal.external_offer_url || deal.externalOfferUrl || '',
      additionalDetails: deal.additional_details || '',
      couponCode: deal.coupon_code || deal.couponCode || '',
      condition: mapCondition(deal.condition_id || deal.conditionId),
      discountPercent: deal.discount_percent != null
        ? Math.round(deal.discount_percent)
        : (deal.discountPercent != null ? Math.round(deal.discountPercent) : ''),
      expirationDate: deal.expiration_date
        ? new Date(deal.expiration_date).toISOString().slice(0, 16)
        : (deal.expirationDate ? new Date(deal.expirationDate).toISOString().slice(0, 16) : ''),
    });
  }, [isOpen, mode, deal, isStoreDeal]);

  const round2 = v => (v == null || v === '' || isNaN(v)) ? '' : (Math.round(v * 100) / 100).toFixed(2);
  const clampPct = v => Math.min(Math.max(v, 0), 100);

  // PURE onChange: store raw value ONLY
  const handleChange = (e) => {
    const { name, value, checked } = e.target;
    if (name === 'url') {
      setFormData(p => ({ ...p, url: value }));
      if (urlError) setUrlError('');
      return;
    }
    if (name === 'externalOfferUrl') {
      setFormData(p => ({ ...p, externalOfferUrl: value }));
      if (externalUrlError) setExternalUrlError('');
      return;
    }
    if (name === 'freeShipping') {
      setFormData(p => ({ ...p, freeShipping: checked }));
      return;
    }
    if (name === 'discountPercent') {
      // Allow only digits, clamp 0-100
      let cleaned = value.replace(/[^0-9]/g, '');
      if (cleaned === '') {
        setFormData(p => ({ ...p, discountPercent: '' }));
        return;
      }
      let intVal = parseInt(cleaned, 10);
      if (isNaN(intVal)) intVal = '';
      else if (intVal > 100) intVal = 100;
      setFormData(p => ({ ...p, discountPercent: intVal }));
      return;
    }
    if (name === 'price') {
      setFormData(p => ({ ...p, price: value }));
      return;
    }
    setFormData(p => ({ ...p, [name]: value }));
  };

  // Compute only when discount % loses focus
  const handleDiscountBlur = (e) => {
    const base = (baselinePrice != null ? baselinePrice : msrpPrice);
    if (!base || base <= 0) return;
    const val = e.target.value;
    if (val === '') return;
    setFormData(p => {
      if (p.price !== '' && p.price != null) return p; // do not overwrite existing price
      const num = parseInt(val, 10);
      if (isNaN(num)) return p;
      const pct = Math.min(Math.max(num, 0), 100);
      const newPrice = base * (1 - pct / 100);
      return { ...p, discountPercent: pct, price: (Math.round(newPrice * 100) / 100).toFixed(2) };
    });
  };

  const handlePriceBlur = (e) => {
    const base = (baselinePrice != null ? baselinePrice : msrpPrice);
    if (!base || base <= 0) return;
    const val = e.target.value;
    if (val === '') return;
    setFormData(p => {
      if (p.discountPercent !== '' && p.discountPercent != null) return p; // do not overwrite existing discount
      const num = parseFloat(val);
      if (isNaN(num) || num < 0) return p;
      let pct = base === 0 ? 0 : ((base - num) / base) * 100;
      pct = Math.round(pct); // integer rounding
      pct = Math.min(Math.max(pct, 0), 100);
      return { ...p, price: val, discountPercent: pct };
    });
  };

  const handleAddStep = (e) => {
    e.preventDefault();
    setVisibleSteps((prev) => (prev < 5 ? prev + 1 : prev));
  };

  const isValidUrl = (val) => {
    if (!val) return false;
    try {
      const u = new URL(val);
      return !!u.protocol && (u.protocol === 'http:' || u.protocol === 'https:') && !!u.hostname;
    } catch {
      return false;
    }
  };

  const validateUrls = () => {
    let ok = true;
    // Product URL required for product-scoped deals only
    if (!isStoreDeal) {
      if (!isValidUrl(formData.url)) {
        setUrlError(formData.url ? 'Invalid product URL.' : 'Product URL is required.');
        ok = false;
      } else {
        setUrlError('');
      }
    } else {
      setUrlError('');
    }
    // External offer (only when external)
    if (formData.dealType === 'external') {
      if (!isValidUrl(formData.externalOfferUrl)) {
        setExternalUrlError(formData.externalOfferUrl ? 'Invalid external offer URL.' : 'External offer URL is required.');
        ok = false;
      } else {
        setExternalUrlError('');
      }
    } else {
      setExternalUrlError('');
    }
    return ok;
  };

  const resetForm = () => {
    setFormData(initialFormData);
    setVisibleSteps(2);
    setUrlError('');
    setExternalUrlError('');
    setSubmitError('');
    setDetailsTouched(false);
    setDealDomain('');
    setUserDeals([]);
    setDealAttributes({});
    dealAttributesTouchedRef.current = false;
    dealAttributesAutoInitializedRef.current = false;
  };

  const handleSubmit = async (e) => {
    e.preventDefault();
    setSubmitError('');
    if (!validateUrls()) return;

    try {
      setLoading(true);
      let conditionId = '';
      switch (formData.condition) {
        case 'new': conditionId = 1; break;
        case 'used': conditionId = 2; break;
        case 'refurbished': conditionId = 3; break;
      }
      let dealTypeId = '';
      switch (formData.dealType) {
        case 'direct': dealTypeId = 1; break;
        case 'coupon': dealTypeId = 2; break;
        case 'external': dealTypeId = 4; break;
      }
      // Use baselinePrice for discount calculations only client-side; submission still sends entered price & discount.
      const variantAttributes = Object.entries(dealAttributes)
        .map(([attributeId, enumValueIds]) => ({
          attributeId: Number(attributeId),
          enumValueIds: (Array.isArray(enumValueIds) ? enumValueIds : []).map(Number).filter(n => !Number.isNaN(n) && n > 0)
        }))
        .filter(x => x.attributeId > 0 && x.enumValueIds.length > 0);

      if (isStoreDeal) {
        // StoreId is required for creating store-wide deals.
        // For editing an existing store-wide deal, the backend already knows the store and
        // ignores StoreId when it's <= 0, so we don't hard-fail the UI if the list payload
        // didn't include store_id.
        const isEditingExistingStoreWide = mode === 'edit' && !!deal?.deal_id;
        if (!isEditingExistingStoreWide && !effectiveStoreId) throw new Error('Missing store');

        const body = {
          storeId: effectiveStoreId ?? 0,
          dealTypeId,
          additionalDetails: formData.additionalDetails || '',
          couponCode: formData.dealType === 'coupon' ? formData.couponCode : null,
          discountPercent: formData.discountPercent === '' ? null : parseInt(formData.discountPercent, 10),
          expirationDate: formData.expirationDate || null,
          externalOfferUrl: formData.dealType === 'external' ? formData.externalOfferUrl : null,
        };

        if (mode === 'edit' && deal?.deal_id) {
          const response = await fetch(`${API_URL}/api/deals/store-wide/${deal.deal_id}` , {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            credentials: 'include',
            body: JSON.stringify(body),
          });
          if (!response.ok) {
            if (response.status === 409) {
              const conflict = await response.json().catch(() => ({}));
              throw new Error(conflict.message || 'This deal already exists.');
            }
            const err = await response.json().catch(() => ({}));
            throw new Error(err.message || 'Failed to update deal');
          }
          const updated = await response.json().catch(() => null);
          alert('Deal updated. It will be reviewed if required.');
          resetForm();
          if (typeof onSubmitted === 'function') {
            await onSubmitted(updated);
          } else {
            onClose?.();
          }
          return;
        }

        const response = await fetch(`${API_URL}/api/deals/store-wide`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          credentials: 'include',
          body: JSON.stringify(body),
        });
        if (!response.ok) {
          if (response.status === 409) {
            const conflict = await response.json().catch(() => ({}));
            throw new Error(conflict.message || 'This deal already exists.');
          }
          if (response.status === 429) {
            const rate = await response.json().catch(() => ({}));
            throw new Error(rate.message || 'You have reached your daily submission limit.');
          }
          const error = await response.json().catch(() => ({}));
          throw new Error(error.message || 'Failed to submit deal');
        }
        const created = await response.json().catch(() => null);
        alert('Deal submitted successfully and will be reviewed.');
        resetForm();
        if (typeof onSubmitted === 'function') {
          await onSubmitted(created);
        } else {
          onClose?.();
        }
        return;
      }

      const body = {
        // only send id on edit when it exists
        ...(mode === 'edit' && deal?.deal_id ? { dealId: deal.deal_id } : {}),
        ...(mode === 'edit' && deal?.deal_product_id ? { dealProductId: deal.deal_product_id } : {}),
        productId,
        dealTypeId,
        price: formData.price === '' ? null : parseFloat(formData.price),
        freeShipping: formData.freeShipping,
        url: formData.url,
        externalOfferUrl: formData.dealType === 'external' ? formData.externalOfferUrl : undefined,
        additionalDetails: formData.additionalDetails || '',
        couponCode: formData.dealType === 'coupon' ? formData.couponCode : null,
        conditionId,
        discountPercent: formData.discountPercent === '' ? null : parseFloat(formData.discountPercent),
        expirationDate: formData.expirationDate || null,
        ...(variantAttributes.length > 0 ? { variantAttributes } : {}),
      };

      if (mode === 'edit' && deal?.deal_product_id) {
        const response = await fetch(`${API_URL}/api/deals/${deal.deal_product_id}`, {
          method: 'PUT',
          headers: { 'Content-Type': 'application/json' },
          credentials: 'include',
          body: JSON.stringify(body),
        });
        if (!response.ok) {
          if (response.status === 409) {
            const conflict = await response.json().catch(() => ({}));
            throw new Error(conflict.message || 'This deal already exists.');
          }
          const err = await response.json().catch(() => ({}));
          throw new Error(err.message || 'Failed to update deal');
        }
        const updated = await response.json().catch(() => null);
        alert('Deal updated. It will be reviewed if required.');
        resetForm(); // reset after edit success
        if (typeof onSubmitted === 'function') {
          await onSubmitted(updated);
        } else {
          onClose?.();
        }
        return;
      }

      const response = await fetch(`${API_URL}/api/deals`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify(body),
      });
      if (!response.ok) {
        if (response.status === 409) {
          const conflict = await response.json().catch(() => ({}));
          throw new Error(conflict.message || 'This deal already exists.');
        }
        if (response.status === 429) {
          const rate = await response.json().catch(() => ({}));
          throw new Error(rate.message || 'You have reached your daily submission limit.');
        }
        const error = await response.json().catch(() => ({}));
        throw new Error(error.message || 'Failed to submit deal');
      }
      const created = await response.json().catch(() => null);
      alert('Deal submitted successfully and will be reviewed.');
      resetForm(); // reset after create success
      if (typeof onSubmitted === 'function') {
        await onSubmitted(created);
      } else {
        onClose?.();
      }
    } catch (err) {
      console.error('Error submitting deal:', err);
      setSubmitError(err.message || 'Failed to submit deal. Please try again.');
    } finally {
      setLoading(false);
    }
  };

  const handleClose = () => {
    resetForm();
    onClose();
  };

  if (!isOpen) return null;

  if (loading) return <LoadingSpinner />;

  // Deal type visual mapping
  const DEAL_TYPE_META = {
    direct: {
      label: 'Direct Deal',
      icon: <FaTag className="inline mr-1 text-slate-600" title="Direct Deal" />,
      color: 'text-slate-700',
      desc: 'Buy directly at the listed price.'
    },
    coupon: {
      label: 'Coupon Deal',
      icon: <FaTicketAlt className="inline mr-1 text-emerald-600" title="Coupon Deal" />,
      color: 'text-emerald-700',
      desc: 'Use a coupon code for a discount.'
    },
    external: {
      label: 'External Offer',
      icon: <FaLink className="inline mr-1 text-indigo-600" title="External Offer" />,
      color: 'text-indigo-700',
      desc: 'Redeem at an external site.'
    }
  };

  const productDomain = extractDomain(formData.url);
  const isEbay = !!productDomain && /(^|\.)ebay\./i.test(productDomain);
  const isUsedOrRefurb = formData.condition === 'used' || formData.condition === 'refurbished';

  // REQUIRED ONLY for eBay listings OR used/refurbished items
  const isAdditionalDetailsRequired = isEbay || isUsedOrRefurb;

  const isAdditionalDetailsInvalid =
    isAdditionalDetailsRequired &&
    !formData.additionalDetails.trim() &&
    detailsTouched;

  return (
    <div className="fixed inset-0 bg-black bg-opacity-50 z-50 flex items-center justify-center p-4">
      <div className="bg-white rounded-lg max-w-2xl w-full max-h-[90vh] overflow-y-auto">
        <div className="p-6">
          <div className="flex justify-between items-center mb-4">
            <h2 className="text-2xl font-bold">{mode === 'edit' ? 'Edit Deal' : 'Submit a Deal'}</h2>
            <button onClick={handleClose} className="text-gray-500 hover:text-gray-700">
              <svg className="w-6 h-6" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M6 18L18 6M6 6l12 12" />
              </svg>
            </button>
          </div>
          <form onSubmit={handleSubmit} className="space-y-4">
            {!!submitError && (
              <div className="p-3 rounded-md bg-red-50 border border-red-200 text-sm text-red-700">
                {submitError}
              </div>
            )}

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Deal Type</label>
              <select
                name="dealType"
                value={formData.dealType}
                onChange={handleChange}
                className="w-full px-3 py-2 border border-gray-300 rounded-md"
              >
                {Object.entries(DEAL_TYPE_META).map(([value, meta]) => (
                  <option key={value} value={value} title={meta.desc}>
                    {meta.label}
                  </option>
                ))}
              </select>
              <div className="mt-1 flex flex-wrap gap-2">
                {DEAL_TYPE_META[formData.dealType]?.icon}
                <span className={`text-xs ${DEAL_TYPE_META[formData.dealType]?.color}`}>{DEAL_TYPE_META[formData.dealType]?.desc}</span>
              </div>
            </div>

            {!isStoreDeal && (
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">Product URL</label>
                <input
                  type="url"
                  name="url"
                  value={formData.url}
                  onChange={handleChange}
                  onBlur={() => {
                    if (formData.url) validateUrls();
                  }}
                  className={`w-full px-3 py-2 border rounded-md ${urlError ? 'border-red-400' : 'border-gray-300'}`}
                  required
                  placeholder="https://"
                />
                {urlError && <p className="mt-1 text-xs text-red-600">{urlError}</p>}
              </div>
            )}
      
            {/* Coupon Code (conditional) */}
            {formData.dealType === 'coupon' && (
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">
                  Coupon Code
                </label>
                <input
                  type="text"
                  name="couponCode"
                  value={formData.couponCode}
                  onChange={handleChange}
                  className="w-full px-3 py-2 border border-gray-300 rounded-md"
                  required
                />
              </div>
            )}
            {/* External Offer URL only for external offer */}
            {formData.dealType === 'external' && (
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">
                  External Offer URL
                </label>
                <input
                  type="url"
                  name="externalOfferUrl"
                  value={formData.externalOfferUrl}
                  onChange={handleChange}
                  onBlur={() => { if (formData.externalOfferUrl) validateUrls(); }}
                  className={`w-full px-3 py-2 border rounded-md ${externalUrlError ? 'border-red-400' : 'border-gray-300'}`}
                  required
                  placeholder="https://"
                />
                {externalUrlError && <p className="mt-1 text-xs text-red-600">{externalUrlError}</p>}
              </div>
            )}

      
          
        


          {/* Pricing Row: Discount % (when applicable) + Deal Price */}
          {!isStoreDeal && (
            <div className="flex flex-col md:flex-row md:items-start gap-4">
                        <div className={`w-full ${ (formData.dealType === 'external' || formData.dealType === 'coupon') ? 'md:w-1/2' : 'md:w-full'}`}>
              <label className="block text-sm font-medium text-gray-700 mb-1">
                Deal Price ($)
              </label>
              <input
                type="number"
                name="price"
                value={formData.price || ''}
                onChange={handleChange}
                onBlur={handlePriceBlur}
                step="0.01"
                min="0"
                className="w-full px-3 py-2 border border-gray-300 rounded-md"
                placeholder="0.00"
                required
              />
            </div>
           
              <div className="w-full md:w-1/2">
                <label className="block text-sm font-medium text-gray-700 mb-1">
                  Discount Percentage (%)
                </label>
                <input
                  type="number"
                  name="discountPercent"
                  value={formData.discountPercent === '' ? '' : formData.discountPercent}
                  onChange={handleChange}
                  onBlur={handleDiscountBlur}
                  min="0"
                  max="100"
                  step="1"
                  inputMode="numeric"
                  className="w-full px-3 py-2 border border-gray-300 rounded-md"
                  placeholder="e.g. 15"
                  required={formData.dealType === 'external' || formData.dealType === 'coupon'}
                />
              </div>
          

            </div>
          )}

          {isStoreDeal && (
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">
                Discount Percentage (%)
              </label>
              <input
                type="number"
                name="discountPercent"
                value={formData.discountPercent === '' ? '' : formData.discountPercent}
                onChange={handleChange}
                onBlur={handleDiscountBlur}
                min="0"
                max="100"
                step="1"
                inputMode="numeric"
                className="w-full px-3 py-2 border border-gray-300 rounded-md"
                placeholder="e.g. 15"
                required={formData.dealType === 'external' || formData.dealType === 'coupon'}
              />
            </div>
          )}

            {/* Condition + Free Shipping Row */}
            {!isStoreDeal && (
              <div className="flex flex-col md:flex-row items-start md:items-end gap-6">
              <div className="w-full md:w-1/2">
                <label className="block text-sm font-medium text-gray-700 mb-1">
                  Condition
                </label>
                <select
                  name="condition"
                  value={formData.condition}
                  onChange={handleChange}
                  className="w-full px-3 py-2 border border-gray-300 rounded-md"
                >
                  <option value="new">New</option>
                  <option value="used">Used</option>
                  <option value="refurbished">Refurbished</option>
                </select>
              </div>

              <div className="mb-2">
                <label className="block text-sm font-medium text-gray-700 mb-1">
                  Free Shipping
                </label>
                <input
                  type="checkbox"
                  name="freeShipping"
                  checked={formData.freeShipping}
                  onChange={handleChange}
                  className="h-6 w-6 border border-gray-300 rounded-md"
                />
              </div>
              </div>
            )}
        



        

            




            {/* Additional Details / External Offer Details */}
            <div className="mt-2">
              <label className="block text-sm font-medium text-gray-700 mb-2">
                {formData.dealType === 'external'
                  ? (isAdditionalDetailsRequired ? 'External Offer Details *' : 'External Offer Details')
                  : (isAdditionalDetailsRequired ? 'Additional Details *' : 'Additional Details')}
              </label>
              <textarea
                name="additionalDetails"
                value={formData.additionalDetails}
                onChange={e => {
                  if (!detailsTouched) setDetailsTouched(true);
                  handleChange(e);
                }}
                onBlur={() => setDetailsTouched(true)}
                rows="4"
                className={`w-full px-3 py-2 border rounded-md ${
                  isAdditionalDetailsInvalid ? 'border-red-400' : 'border-gray-300'
                }`}
                placeholder={
                  isEbay
                    ? 'eBay listing: include condition, specs, defects, seller notes...'
                    : 'Add context (optional details, limitations, variant, store notes)...'
                }
                required={isAdditionalDetailsRequired}
              />
              {isAdditionalDetailsRequired && (
                <p className={`mt-1 text-xs ${isAdditionalDetailsInvalid ? 'text-red-600' : 'text-gray-500'}`}>
                  Required only for eBay listings or used/refurbished items.
                </p>
              )}
              {isEbay && (
                <p className="mt-1 text-xs text-amber-600">
                  eBay listings must describe condition & specifics.
                </p>
              )}
            </div>

            {/* Product Attributes (Optional) */}
            {Array.isArray(productAttributes) && productAttributes.length > 0 && (
              <div className="mt-2 space-y-3">
                <div className="flex items-center gap-2">
                  <FaLayerGroup className="text-slate-600" />
                  <h3 className="text-sm font-semibold text-slate-800">Product Options (Optional)</h3>
                </div>
                <p className="text-xs text-slate-500">
                  Select one or more options per attribute if the listing applies to multiple variants.
                </p>

                {productAttributes.map((attr) => {
                  const attributeId = attr.attributeId ?? attr.AttributeId;
                  const label = (
                    attr.description ??
                    attr.Description ??
                    attr.label ??
                    attr.Label ??
                    attr.attributeKey ??
                    attr.AttributeKey ??
                    'Attribute'
                  );
                  const options = attr.options ?? attr.Options ?? [];
                  const selectedIds = (dealAttributes[attributeId] || [])
                    .map((v) => Number(v))
                    .filter((n) => !Number.isNaN(n) && n > 0);
                  const selectedSet = new Set(selectedIds);

                  if (!attributeId || !Array.isArray(options) || options.length === 0) return null;

                  return (
                    <div key={attributeId}>
                      <div className="flex items-center justify-between gap-3 mb-2">
                        <label className="block text-sm font-medium text-gray-700">{label}</label>
                        <div className="flex items-center gap-2">
                          <span className="text-xs text-slate-500">{selectedIds.length} selected</span>
                          <button
                            type="button"
                            className="text-xs text-slate-600 hover:text-slate-800 underline"
                            onClick={() => {
                              dealAttributesTouchedRef.current = true;
                              dealAttributesAutoInitializedRef.current = false;
                              const allIds = options
                                .map(o => Number(o.id ?? o.Id))
                                .filter(n => !Number.isNaN(n) && n > 0);
                              setDealAttributes(prev => ({ ...prev, [attributeId]: allIds }));
                            }}
                          >
                            Select all
                          </button>
                          <button
                            type="button"
                            className="text-xs text-slate-600 hover:text-slate-800 underline"
                            onClick={() => {
                              dealAttributesTouchedRef.current = true;
                              dealAttributesAutoInitializedRef.current = false;
                              setDealAttributes(prev => ({ ...prev, [attributeId]: [] }));
                            }}
                          >
                            Clear
                          </button>
                        </div>
                      </div>
                      <div className="w-full px-3 py-2 border border-gray-300 rounded-md max-h-40 overflow-auto space-y-2">
                        {options.map((opt) => {
                          const id = opt.id ?? opt.Id;
                          const idNum = Number(id);
                          const name = opt.displayName ?? opt.DisplayName ?? opt.enumKey ?? opt.EnumKey ?? String(id);
                          if (!id || Number.isNaN(idNum) || idNum <= 0) return null;
                          const checked = selectedSet.has(idNum);

                          return (
                            <label key={idNum} className="flex items-start gap-2 text-sm text-slate-700">
                              <input
                                type="checkbox"
                                className="mt-1"
                                checked={checked}
                                onChange={() => {
                                  dealAttributesTouchedRef.current = true;
                                  dealAttributesAutoInitializedRef.current = false;
                                  setDealAttributes((prev) => {
                                    const current = Array.isArray(prev?.[attributeId]) ? prev[attributeId] : [];
                                    const next = current.includes(idNum)
                                      ? current.filter((x) => Number(x) !== idNum)
                                      : [...current, idNum];
                                    return { ...prev, [attributeId]: next };
                                  });
                                }}
                              />
                              <span className="leading-5">{name}</span>
                            </label>
                          );
                        })}
                      </div>
                    </div>
                  );
                })}
              </div>
            )}

            {/* Expiration Date (Optional) */}
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">
                Expiration Date (Optional)
              </label>
              <input
                type="datetime-local"
                name="expirationDate"
                value={formData.expirationDate}
                onChange={handleChange}
                className="w-full px-3 py-2 border border-gray-300 rounded-md"
                min={new Date().toISOString().slice(0, 16)} // Prevents selecting past dates
              />
              <p className="mt-1 text-sm text-gray-500">
                Leave blank if the deal has no expiration date
              </p>
            </div>

            {/* Submit Button */}
            <div className="flex justify-end space-x-4">
              <button
                type="button"
                onClick={handleClose}
                className="px-4 py-2 border border-gray-300 rounded-md text-gray-700 hover:bg-gray-50"
              >
                Cancel
              </button>
              <button
                type="submit"
                className="px-4 py-2 bg-[#4CAF50] text-white rounded-md hover:bg-[#3d8b40]"
              >
                {mode === 'edit' ? 'Save Changes' : 'Submit Deal'}
              </button>
            </div>
          </form>
        </div>
      </div>
    </div>
  );
};

export default SubmitDealModal;