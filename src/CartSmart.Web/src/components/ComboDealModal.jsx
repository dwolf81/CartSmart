import React, { useState, useEffect, useRef } from 'react';
import LoadingSpinner from './LoadingSpinner';
import { useAuth } from '../context/AuthContext';
import SubmitDealModal from './SubmitDealModal';
import { FaTag, FaTicketAlt, FaLayerGroup, FaLink } from 'react-icons/fa';

const API_URL = process.env.REACT_APP_API_URL || 'http://localhost:5000';

const ComboDealModal = ({ isOpen, onClose, productId, msrpPrice, onComboCreated, mode = 'create', deal = null }) => {
  const { user } = useAuth();
  const [deals, setDeals] = useState([]);
  const [selectedDeals, setSelectedDeals] = useState([]);
  const [filterStoreId, setFilterStoreId] = useState(null); // store_id to constrain available deals
  // --- NEW PAGING STATE FOR POSSIBLE DEALS ---
  const [dealsPage, setDealsPage] = useState(1);
  const [hasMoreDeals, setHasMoreDeals] = useState(true);
  const [loadingMoreDeals, setLoadingMoreDeals] = useState(false);
  const pageSize = 10; // increased from 5 to 10
  // --- EXPANSION STATE FOR COMPACT CARDS ---
  const [expandedSelected, setExpandedSelected] = useState({});
  const [expandedAvailable, setExpandedAvailable] = useState({});
  const possibleRef = useRef(null);
  const [loading, setLoading] = useState(false);
  const [description, setDescription] = useState('');
  const [price, setPrice] = useState('');
  const [discountPercent, setDiscountPercent] = useState('');
  const [isSubmitDealModalOpen, setIsSubmitDealModalOpen] = useState(false);
  const [manualPriceOverride, setManualPriceOverride] = useState(false);
  const [isMobile, setIsMobile] = useState(false); // NEW: detect mobile

  // NEW: Deal type meta (mirrors ProductPage styling icons/colors kept minimal here)
  const DEAL_TYPE_META = {
    1: {
      label: 'Direct',
      icon: <FaTag className="w-3.5 h-3.5 text-slate-600" />,
      badge:
        'inline-flex items-center gap-1.5 bg-white border border-slate-200 text-slate-700 px-1.5 py-0.5 rounded text-xs font-medium'
    },
    2: {
      label: 'Coupon',
      icon: <FaTicketAlt className="w-3.5 h-3.5 text-emerald-600" />,
      badge:
        'inline-flex items-center gap-1.5 bg-white border border-emerald-200 text-emerald-700 px-1.5 py-0.5 rounded text-xs font-medium'
    },
    3: {
      label: 'Stacked',
      icon: <FaLayerGroup className="w-3.5 h-3.5 text-amber-600" />,
      badge:
        'inline-flex items-center gap-1.5 bg-white border border-amber-200 text-amber-700 px-1.5 py-0.5 rounded text-xs font-medium'
    },
    4: {
      label: 'External',
      icon: <FaLink className="w-3.5 h-3.5 text-indigo-600" />,
      badge:
        'inline-flex items-center gap-1.5 bg-white border border-indigo-200 text-indigo-700 px-1.5 py-0.5 rounded text-xs font-medium'
    }
  };

  // helper: normalize API shapes to an array
  const normalizeDeals = (data) => {
    if (!data) return [];
    if (Array.isArray(data)) return data;
    if (Array.isArray(data.deals)) return data.deals;
    if (typeof data === 'object') return Object.values(data);
    return [];
  };

  const handleChange = (e) => {
    const { name, value } = e.target;
    if (name === 'price') {
      setPrice(value);
      setManualPriceOverride(true); // User manually edited price
    }
    if (name === 'discountPercent') {
      setDiscountPercent(value);
    }
  };

  // Helper: normalize store_id safely to a number or null
  const getStoreId = (deal) => {
    const sid = deal?.store_id ?? deal?.storeId ?? null;
    const n = sid === null ? null : Number(sid);
    return Number.isFinite(n) ? n : null;
  };

  // Derived: whether a Direct deal (deal_type_id === 1) is already selected
  const hasSelectedDirect = selectedDeals
    .map(id => deals.find(d => d.deal_id === id))
    .filter(Boolean)
    .some(d => d.deal_type_id === 1);

  // RESET when modal opens
  useEffect(() => {
    if (!isOpen) return;
    if (!productId) return;
    setDeals([]);
    setDealsPage(1);
    setHasMoreDeals(true);
    setExpandedAvailable({});
    setManualPriceOverride(false);
    fetchDealsPage(1, true);
  }, [isOpen, productId]);

  // Prefill edit for stacked deal
  useEffect(() => {
    if (!isOpen || mode !== 'edit' || !deal) return;

    setDescription(deal.description || deal.additional_details || '');
    setPrice(deal.price || '');
    setDiscountPercent(deal.discount_percent || '');
    setManualPriceOverride(true);

    if (Array.isArray(deal.steps) && deal.steps.length) {
      const ids = deal.steps
        .map(s => s.deal_id || s.source_deal_id || s.child_deal_id)
        .filter(Boolean);
      setSelectedDeals(ids);
      return;
    }

    if (deal.deal_type_id === 3 && !deal.steps) {
      (async () => {
        try {
          const res = await fetch(`${API_URL}/api/deals/${deal.deal_id}`, { credentials: 'include' });
          if (!res.ok) return;
          const full = await res.json();
          const ids = (full.steps || [])
            .map(s => s.deal_id || s.source_deal_id || s.child_deal_id)
            .filter(Boolean);
          setSelectedDeals(ids);
        } catch {
          // silent
        }
      })();
    }
  }, [isOpen, mode, deal]);

  // Auto-calculate price and discount when selection changes
  useEffect(() => {
    if (!isOpen) return;
    if (!selectedDeals?.length) {
      if (!manualPriceOverride) {
        setPrice('');
        setDiscountPercent('');
      }
      return;
    }

    const selected = selectedDeals
      .map(id => deals.find(d => d.deal_id === id))
      .filter(Boolean);

    if (selected.length === 0) return;

    // Start with the first deal's price
    let finalPrice = Number(selected[0].price) || 0;

    // Apply subsequent deals' discounts sequentially
    for (let i = 1; i < selected.length; i++) {
      const deal = selected[i];
      const dealDiscount = Number(deal.discount_percent) || 0;
      
      if (dealDiscount > 0) {
        finalPrice = finalPrice * (1 - dealDiscount / 100);
      }
    }

    // Sum all discount percentages (capped at 100)
    const totalDiscountPercent = selected.reduce((sum, d) => 
      sum + (Number(d.discount_percent) || 0), 0
    );

    // Only update price if user hasn't manually overridden
    if (!manualPriceOverride) {
      setPrice(finalPrice.toFixed(2));
    }
    setDiscountPercent(Math.min(totalDiscountPercent, 100).toFixed(2));

  }, [isOpen, selectedDeals, deals, manualPriceOverride]);

  // PAGED FETCH
  const fetchDealsPage = async (page, initial = false) => {
    if (!productId) return;
    if (!initial && !hasMoreDeals) return;
    if (loading || loadingMoreDeals) return; // guard against parallel calls
    try {
      if (initial) setLoading(true); else setLoadingMoreDeals(true);
      const qs = new URLSearchParams();
      qs.append('page', page.toString());
      qs.append('pageSize', pageSize.toString());
      qs.append('userId', user?.id || 0);
      [1, 2, 4].forEach(id => qs.append('dealTypeId', id));
      const res = await fetch(`${API_URL}/api/deals/product/${productId}?${qs.toString()}`, { credentials: 'include' });
      if (!res.ok) throw new Error('Failed to load deals');
      const data = await res.json();
      const batch = normalizeDeals(data);
      setDeals(prev => {
        const existingIds = new Set(prev.map(d => d.deal_id));
        const merged = [...prev];
        batch.forEach(b => { if (!existingIds.has(b.deal_id)) merged.push(b); });
        return merged;
      });
      setHasMoreDeals(batch.length === pageSize);
      if (batch.length === pageSize) setDealsPage(page);
    } catch {
      if (initial) setDeals([]);
      setHasMoreDeals(false);
    } finally {
      setLoading(false);
      setLoadingMoreDeals(false);
    }
  };

  // INFINITE SCROLL HANDLER
  useEffect(() => {
    const el = possibleRef.current;
    if (!el) return;
    const onScroll = () => {
      if (loadingMoreDeals || !hasMoreDeals) return;
      if (el.scrollTop + el.clientHeight >= el.scrollHeight - 48) {
        fetchDealsPage(dealsPage + 1);
      }
    };
    el.addEventListener('scroll', onScroll);
    return () => el.removeEventListener('scroll', onScroll);
  }, [loadingMoreDeals, hasMoreDeals, dealsPage]);

  // Auto-load more if content doesn't overflow yet (no scrollbar) but more pages exist
  useEffect(() => {
    if (!isOpen) return;
    const el = possibleRef.current;
    if (!el) return;
    if (hasMoreDeals && !loading && !loadingMoreDeals && el.scrollHeight <= el.clientHeight) {
      fetchDealsPage(dealsPage + 1);
    }
  }, [deals, loading, loadingMoreDeals, hasMoreDeals, dealsPage, isOpen]);

  const toggleSelected = (id) =>
    setExpandedSelected(s => ({ ...s, [id]: !s[id] }));
  const toggleAvailable = (id) =>
    setExpandedAvailable(s => ({ ...s, [id]: !s[id] }));

  const handleSelect = (dealId) => {
    const d = deals.find(x => x.deal_id === dealId);
    if (!d) return;

    const candidateStoreId = getStoreId(d);

    // First selection must have a valid store_id to enable filtering
    if (selectedDeals.length === 0) {
      if (candidateStoreId === null) {
        alert('This deal is missing a store and cannot be stacked.');
        return;
      }
      // Accept first selection and set filter to that store
      setSelectedDeals([dealId]);
      setFilterStoreId(candidateStoreId);
      return;
    }

    // If removing selection
    if (selectedDeals.includes(dealId)) {
      const next = selectedDeals.filter(id => id !== dealId);
      setSelectedDeals(next);
      // If none selected, clear filter
      if (next.length === 0) setFilterStoreId(null);
      return;
    }

    // Enforce same store
    if (filterStoreId !== null && candidateStoreId !== filterStoreId) {
      alert('All stacked deals must be from the same store.');
      return;
    }
    if (candidateStoreId === null) {
      alert('This deal is missing a store and cannot be stacked.');
      return;
    }

    // Enforce only one Direct deal
    if (d.deal_type_id === 1 && hasSelectedDirect) {
      alert('Only one Direct deal can be included in a stacked deal.');
      return;
    }

    setSelectedDeals(prev => [...prev, dealId]);
  };

  const handleOrderChange = (fromIdx, toIdx) => {
    const updated = [...selectedDeals];
    const [moved] = updated.splice(fromIdx, 1);
    updated.splice(toIdx, 0, moved);
    setSelectedDeals(updated);
  };

  const handleClose = () => {
    setDescription('');
    setSelectedDeals([]);
    setFilterStoreId(null);
    onClose();
  };

  const handleSubmit = async (e) => {
    e.preventDefault(); // CHANGED: handle form submit
    if (!user) { alert('Please log in to submit a deal'); return; }
    if (selectedDeals.length < 2) { alert('Select at least two deals to create a stacked deal.'); return; }
    if (!description || description.trim().length === 0) {  // NEW: manual validation
      alert('Please enter a description for the stacked deal.');
      return;
    }
    setLoading(true);
    try {
      const dealData = {
        ...(mode === 'edit' && deal?.deal_id ? { dealId: deal.deal_id } : {}),
        ...(mode === 'edit' && deal?.deal_product_id ? { dealProductId: deal.deal_product_id } : {}),
        productId,
        dealUrl: '',
        price: Number(price) || 0,
        discountPercent: Number(discountPercent) || 0,
        dealTypeId: 3,
        conditionId: 1,
        additionalDetails: description.trim(), // CHANGED: trim
        dealIds: selectedDeals,
      };

      const url = mode === 'edit' && deal?.deal_product_id
        ? `${API_URL}/api/deals/${deal.deal_product_id}`
        : `${API_URL}/api/deals`;
      const method = mode === 'edit' && deal?.deal_product_id ? 'PUT' : 'POST';

      const res = await fetch(url, {
        method,
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify(dealData),
      });
      if (!res.ok) {
        const err = await res.json().catch(() => ({}));
        throw new Error(err.message || (mode === 'edit' ? 'Failed to update stacked deal' : 'Failed to create stacked deal'));
      }

      // Success alert similar to SubmitDealModal
      alert(mode === 'edit'
        ? 'Stacked deal updated. It will be reviewed if required.'
        : 'Stacked deal submitted successfully and will be reviewed.');

      onComboCreated && onComboCreated();
      onClose();
    } catch (err) {
      alert(err.message || 'Error saving stacked deal.');
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    const check = () => setIsMobile(window.matchMedia('(max-width: 768px)').matches);
    check();
    window.addEventListener('resize', check);
    return () => window.removeEventListener('resize', check);
  }, []);

  if (!isOpen) return null;

  const dealsArray = Array.isArray(deals) ? deals : [];

  // Derived: available deals excluding selected ones,
  // filtered by store AND by direct-once rule
  const availableDeals = (Array.isArray(deals) ? deals : [])
    .filter(d => !selectedDeals.includes(d.deal_id))
    .filter(d => {
      const sid = getStoreId(d);
      return filterStoreId === null || sid === filterStoreId;
    })
    .filter(d => !(hasSelectedDirect && d.deal_type_id === 1));

  return (
    <div className="fixed inset-0 bg-black bg-opacity-50 z-50 flex items-center justify-center p-4">
      <div className="bg-white rounded-lg max-w-2xl w-full max-h-[90vh] overflow-y-auto">
        <form className="p-6" onSubmit={handleSubmit}>
          <div className="flex justify-between items-center mb-6">
            <h2 className="text-2xl font-bold">{mode === 'edit' ? 'Edit Stacked Deal' : 'Create Stacked Deal'}</h2>
            <button type="button" onClick={handleClose} className="text-gray-500 hover:text-gray-700">
              <svg className="w-6 h-6" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M6 18L18 6M6 6l12 12" />
              </svg>
            </button>
          </div>
          <div className="mb-4 text-gray-700 text-sm bg-blue-50 border border-blue-200 rounded p-3">
            <b>{mode === 'edit' ? 'Edit your Stacked Deal' : 'What is a Stacked Deal?'}</b><br />
            {mode === 'edit'
              ? 'Update the description, steps, and order, then save your changes.'
              : 'A Stacked Deal lets you combine two or more existing deals for this product into a single, step-by-step offer.'}
          </div>
          <div className="space-y-6">

  {/* Pricing Row: Discount % (when applicable) + Deal Price */}
          <div className="flex flex-col md:flex-row md:items-start gap-4">
            <div className={`w-full md:w-1/2`}>
              <label className="block text-sm font-medium text-gray-700 mb-1">
                Deal Price ($)
              </label>
              <input
                type="number"
                name="price"
                value={price}
                onChange={handleChange}
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
                  value={discountPercent === undefined ? '' : discountPercent}
                  onChange={e => {
                    let val = e.target.value;
                    if (val === '') { handleChange(e); return; }
                    if (!/^\d*\.?\d*$/.test(val)) return;
                    handleChange(e);
                  }}
                  onBlur={e => {
                    let val = e.target.value;
                    if (val === '') return;
                    let num = Number(val);
                    if (isNaN(num)) num = '';
                    else if (num < 0) num = 0;
                    else if (num > 100) num = 100;
                    handleChange({ ...e, target: { ...e.target, value: num } });
                  }}
                  min="0"
                  max="100"
                  step="any"
                  className="w-full px-3 py-2 border border-gray-300 rounded-md"
                  placeholder="e.g. 15"
                  required
                />
              </div>
            </div>
            {/* Selected deals */}
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-2">Stacked Deal Description</label>
              <textarea
                value={description}
                onChange={e => setDescription(e.target.value)}
                rows="3"
                className="w-full px-3 py-2 border border-gray-300 rounded-md"
                required
              />
            </div>
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-2">Selected deals (click to expand):</label>
                            <div className="space-y-2">
                {selectedDeals.length === 0 && (
                  <div className="text-gray-400 text-sm">No deals selected yet.</div>
                )}
                {selectedDeals.map((dealId, idx) => {
                  const d = dealsArray.find(x => x.deal_id === dealId);
                  if (!d) return null;
                  const open = !!expandedSelected[d.deal_id];
                  return (
                    <div key={d.deal_id} className="border rounded-md overflow-hidden ">
                      <div className="flex">
                        <button
                          type="button"
                          onClick={() => toggleSelected(d.deal_id)}
                          className="flex-1 flex items-center gap-3 px-3 py-2 text-left hover:bg-gray-50 transition"
                        >
                          <span className="inline-flex h-6 w-6 items-center justify-center rounded-full bg-white border text-xs font-semibold text-slate-700">
                            {idx + 1}
                          </span>
                          <span className={DEAL_TYPE_META[d.deal_type_id]?.badge}>
                            {DEAL_TYPE_META[d.deal_type_id]?.icon}
                            <span>{DEAL_TYPE_META[d.deal_type_id]?.label} Deal</span>
                          </span>
                          <span className="text-sm font-semibold text-slate-700">${d.price}</span>
                          {d.discount_percent > 0 && (
                            <span className="bg-green-100 text-green-700 text-xs font-semibold px-2 py-0.5 rounded-full">
                              {d.discount_percent}% off
                            </span>
                          )}
                          {!isMobile && d.coupon_code && ( // NEW: show coupon only on desktop
                            <code className="bg-white border px-2 py-0.5 rounded text-xs">
                              {d.coupon_code}
                            </code>
                          )}
                          <div className="ml-auto flex items-center gap-2">
                            <div className="flex flex-col -my-2">
                              <button
                                type="button"
                                disabled={idx === 0}
                                onClick={(e) => { e.stopPropagation(); handleOrderChange(idx, idx - 1); }}
                                className="text-[10px] disabled:opacity-30"
                                title="Move up"
                              >▲</button>
                              <button
                                type="button"
                                disabled={idx === selectedDeals.length - 1}
                                onClick={(e) => { e.stopPropagation(); handleOrderChange(idx, idx + 1); }}
                                className="text-[10px] disabled:opacity-30"
                                title="Move down"
                              >▼</button>
                            </div>
                            <svg className={`w-4 h-4 text-slate-600 transition-transform ${open ? 'rotate-180' : ''}`} viewBox="0 0 24 24" stroke="currentColor" fill="none" strokeWidth="2">
                              <path strokeLinecap="round" strokeLinejoin="round" d="M19 9l-7 7-7-7" />
                            </svg>
                          </div>
                        </button>
                        <button
                          type="button"
                          onClick={() => {
                            setSelectedDeals(prev => {
                              const next = prev.filter(id => id !== d.deal_id);
                              if (next.length === 0) setFilterStoreId(null); // CLEAR FILTER WHEN NO SELECTIONS
                              return next;
                            });
                          }}
                          className="px-3 py-2 text-sm bg-red-500 text-white hover:bg-red-600"
                          title="Remove from stack"
                        >
                          Remove
                        </button>
                      </div>
                      {open && (
                        <div className="px-4 pt-2 pb-4 text-sm text-slate-700 space-y-1">
                          <div>
                            <span className="text-gray-600 font-medium">Source:</span> {d.store_url || 'N/A'}
                          </div>
                          {d.coupon_code && (
                            <div>
                              <span className="text-gray-600 font-medium">Coupon Code:</span>{' '}
                              <code className="bg-white border px-2 py-0.5 rounded">{d.coupon_code}</code>
                            </div>
                          )}
                          {d.additional_details && (
                            <div>
                              <span className="text-gray-600 font-medium">Additional Details:</span> {d.additional_details}
                            </div>
                          )}
                        </div>
                      )}
                    </div>
                  );
                })}
              </div>
            </div>

            {/* Possible deals to stack */}
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-2 flex items-center justify-between">
                <span>Possible deals to stack:</span>
                <div className="flex items-center gap-2">
                  <button
                    type="button"
                    onClick={() => setIsSubmitDealModalOpen(true)}
                    className="px-3 py-1 bg-[#4CAF50] text-white rounded hover:bg-[#3d8b40] text-sm"
                  >
                    + Add New Deal
                  </button>
                </div>
              </label>
              <div
                ref={possibleRef}
                className="max-h-[50vh] md:max-h-[60vh] overflow-y-auto border rounded p-2 bg-gray-50 space-y-2"
              >
                {availableDeals.map(d => {
                  const open = !!expandedAvailable[d.deal_id];
                  return (
                    <div key={d.deal_id} className="border rounded-md overflow-hidden">
                      <div className="flex">
                        <button
                          type="button"
                          onClick={() => toggleAvailable(d.deal_id)}
                          className="flex-1 flex items-center gap-3 px-3 py-1 text-left hover:bg-gray-50 transition"
                        >
                          <span className={DEAL_TYPE_META[d.deal_type_id]?.badge}>
                            {DEAL_TYPE_META[d.deal_type_id]?.icon}
                            <span>{DEAL_TYPE_META[d.deal_type_id]?.label} Deal</span>
                          </span>
                          <span className="text-sm font-semibold text-slate-700">${d.price}</span>
                          {d.discount_percent > 0 && (
                            <span className="bg-green-100 text-green-700 text-xs font-semibold px-2 py-0.5 rounded-full">
                              {d.discount_percent}%
                            </span>
                          )}
                          {!isMobile && d.coupon_code && ( // NEW: show coupon only on desktop
                            <code className="bg-white border px-2 py-0.5 rounded text-xs">
                              {d.coupon_code}
                            </code>
                          )}
                          {(() => {
                            const sid = getStoreId(d);
                            return sid !== null ? (
                              <span className="bg-slate-100 text-slate-700 text-xs font-semibold px-2 py-0.5 rounded-full">
                                {d.store_url}
                              </span>
                            ) : (
                              <span className="bg-red-100 text-red-700 text-xs font-semibold px-2 py-0.5 rounded-full">
                                No store
                              </span>
                            );
                          })()}
                          <svg
                            className={`w-4 h-4 ml-auto text-slate-600 transition-transform ${open ? 'rotate-180' : ''}`}
                            viewBox="0 0 24 24" stroke="currentColor" fill="none" strokeWidth="2"
                          >
                            <path strokeLinecap="round" strokeLinejoin="round" d="M19 9l-7 7-7-7" />
                          </svg>
                        </button>
                        <button
                          type="button"
                          onClick={() => handleSelect(d.deal_id)}
                          className="px-3 py-2 text-sm bg-[#4CAF50] text-white hover:bg-[#3d8b40]"
                          title="Add to stack"
                        >
                          Add
                        </button>
                      </div>
                      {open && (
                        <div className="px-4 pt-2 pb-3 text-sm text-slate-700 space-y-1">
                          <div>
                            <span className="text-gray-600 font-medium">Source:</span> {d.store_url || 'N/A'}
                          </div>
                          {d.coupon_code && (
                            <div>
                              <span className="text-gray-600 font-medium">Coupon Code:</span>{' '}
                              <code className="bg-white border px-2 py-0.5 rounded">{d.coupon_code}</code>
                            </div>
                          )}
                          {d.additional_details && (
                            <div>
                              <span className="text-gray-600 font-medium">Additional Details:</span> {d.additional_details}
                            </div>
                          )}
                        </div>
                      )}
                    </div>
                  );
                })}
                {!loading && dealsArray.filter(d => !selectedDeals.includes(d.deal_id)).length === 0 && (
                  <div className="text-gray-400 text-sm">No more deals to select.</div>
                )}
                {(loadingMoreDeals || hasMoreDeals) && (
                  <div className="text-center py-1 text-xs text-gray-500">
                    {loadingMoreDeals
                      ? 'Loading…'
                      : (hasMoreDeals ? 'Scroll to load more…' : 'All deals loaded')}
                  </div>
                )}
              </div>
            </div>

            <div className="flex justify-end space-x-4">
              <button
                type="button"
                onClick={handleClose}
                className="px-4 py-2 border border-gray-300 rounded-md text-gray-700 hover:bg-gray-50"
              >
                Cancel
              </button>
              <button
                type="submit"  // CHANGED: submit to trigger required validation
                disabled={loading || selectedDeals.length < 2}
                className="px-4 py-2 bg-[#4CAF50] text-white rounded-md hover:bg-[#3d8b40] disabled:opacity-60 disabled:cursor-not-allowed"
              >
                {loading
                  ? (mode === 'edit' ? 'Saving…' : 'Creating…')
                  : (mode === 'edit' ? 'Save Changes' : 'Create Stacked Deal')}
              </button>
            </div>

            {/* Inline SubmitDealModal refresh (unchanged except positioning) */}
            <SubmitDealModal
              isOpen={isSubmitDealModalOpen}
              onClose={() => setIsSubmitDealModalOpen(false)}
              onSubmitted={async () => {
                setIsSubmitDealModalOpen(false);
                setDeals([]); setDealsPage(1); setHasMoreDeals(true);
                await fetchDealsPage(1, true);
              }}
              productId={productId}
              msrpPrice={msrpPrice}
            />
          </div>
        </form>
      </div>
    </div>
  );
};

export default ComboDealModal;
