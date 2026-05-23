import React, { useCallback, useEffect, useRef, useState } from 'react';
import { useAuth } from '../context/AuthContext';

const API_URL = process.env.REACT_APP_API_URL || 'http://localhost:5000';

const STATUSES = ['pending_review', 'approved', 'rejected', 'merged'];

const STATUS_BADGE = {
  pending_review: 'bg-yellow-100 text-yellow-800',
  approved:       'bg-blue-100 text-blue-800',
  rejected:       'bg-red-100 text-red-800',
  merged:         'bg-purple-100 text-purple-800',
  duplicate:      'bg-gray-100 text-gray-700',
};

function formatMoney(v) {
  const n = Number(v);
  return Number.isFinite(n) && n > 0 ? `$${n.toFixed(2)}` : '—';
}

function parseSubmitters(jsonish) {
  if (!jsonish) return [];
  if (Array.isArray(jsonish)) return jsonish;
  try { return JSON.parse(jsonish); } catch { return []; }
}

export default function AdminProductCandidatesPage() {
  const { user, isAuthenticated, loading: authLoading, authFetch } = useAuth();
  const [status, setStatus] = useState('pending_review');
  const [candidates, setCandidates] = useState([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState(null);
  const [activeId, setActiveId] = useState(null);
  const [activeDetail, setActiveDetail] = useState(null);
  const [actionBusy, setActionBusy] = useState(false);
  const [brands, setBrands] = useState([]);
  const [productTypes, setProductTypes] = useState([]);

  useEffect(() => {
    if (!isAuthenticated) return;
    (async () => {
      try {
        const [bRes, tRes] = await Promise.all([
          authFetch(`${API_URL}/api/brands`),
          authFetch(`${API_URL}/api/producttypes`),
        ]);
        if (bRes.ok) setBrands(await bRes.json());
        if (tRes.ok) setProductTypes(await tRes.json());
      } catch {
        // non-fatal; the drawer falls back to a plain id input when empty
      }
    })();
  }, [isAuthenticated, authFetch]);

  const reload = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const res = await authFetch(
        `${API_URL}/api/admin/product-candidates?status=${encodeURIComponent(status)}&limit=50`
      );
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      setCandidates(await res.json());
    } catch (err) {
      setError(err.message || 'Failed to load');
      setCandidates([]);
    } finally {
      setLoading(false);
    }
  }, [authFetch, status]);

  useEffect(() => { if (isAuthenticated) reload(); }, [reload, isAuthenticated]);

  const openDetail = useCallback(async (id) => {
    setActiveId(id);
    setActiveDetail(null);
    try {
      const res = await authFetch(`${API_URL}/api/admin/product-candidates/${id}`);
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      setActiveDetail(await res.json());
    } catch (err) {
      setError(err.message || 'Failed to load candidate');
    }
  }, [authFetch]);

  const closeDetail = () => { setActiveId(null); setActiveDetail(null); };

  const approve = async (id, overrides) => {
    if (!window.confirm('Approve this candidate and create the product?')) return;
    setActionBusy(true);
    try {
      const res = await authFetch(`${API_URL}/api/admin/product-candidates/${id}/approve`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(overrides || {}),
      });
      if (!res.ok) {
        const t = await res.text();
        alert('Approve failed: ' + t);
      } else {
        closeDetail();
        reload();
      }
    } finally { setActionBusy(false); }
  };

  const reject = async (id) => {
    const notes = window.prompt('Optional rejection note:');
    if (notes === null) return;
    setActionBusy(true);
    try {
      const res = await authFetch(`${API_URL}/api/admin/product-candidates/${id}/reject`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ adminNotes: notes }),
      });
      if (!res.ok) {
        alert('Reject failed.');
      } else {
        closeDetail();
        reload();
      }
    } finally { setActionBusy(false); }
  };

  const mergeInto = async (id, productId, conditionCategoryId) => {
    if (!Number.isFinite(productId) || productId <= 0) return;
    setActionBusy(true);
    try {
      const res = await authFetch(`${API_URL}/api/admin/product-candidates/${id}/merge-into`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ productId, conditionCategoryId }),
      });
      if (!res.ok) {
        const t = await res.text();
        alert('Merge failed: ' + t);
      } else {
        closeDetail();
        reload();
      }
    } finally { setActionBusy(false); }
  };

  if (authLoading) return <div className="p-6">Loading…</div>;
  if (!isAuthenticated || !user?.admin) return <div className="p-6 text-red-600">Admin access required.</div>;

  return (
    <div className="max-w-6xl mx-auto p-4 md:p-6">
      <div className="flex flex-wrap items-center gap-3 mb-4">
        <h1 className="text-2xl font-semibold flex-1">Product Candidates</h1>
        <select
          value={status}
          onChange={(e) => setStatus(e.target.value)}
          className="border rounded px-2 py-1 text-sm"
        >
          {STATUSES.map((s) => <option key={s} value={s}>{s.replace('_', ' ')}</option>)}
        </select>
        <button
          onClick={reload}
          className="px-3 py-1 text-sm rounded bg-slate-100 hover:bg-slate-200"
        >
          Refresh
        </button>
      </div>

      {error && <div className="mb-3 text-red-600 text-sm">{error}</div>}

      <div className="bg-white border rounded overflow-x-auto">
        <table className="w-full text-sm">
          <thead className="bg-slate-50 text-left">
            <tr>
              <th className="p-2 w-16">Image</th>
              <th className="p-2">Name / Brand</th>
              <th className="p-2">Store</th>
              <th className="p-2 text-right">MSRP</th>
              <th className="p-2 text-center">#</th>
              <th className="p-2">Status</th>
              <th className="p-2 w-32"></th>
            </tr>
          </thead>
          <tbody>
            {loading && <tr><td colSpan={7} className="p-4 text-center text-slate-500">Loading…</td></tr>}
            {!loading && candidates.length === 0 && (
              <tr><td colSpan={7} className="p-4 text-center text-slate-500">No candidates.</td></tr>
            )}
            {!loading && candidates.map((c) => (
              <tr key={c.id} className="border-t hover:bg-slate-50">
                <td className="p-2">
                  {c.imageUrl ? (
                    <img src={c.imageUrl} alt="" className="w-12 h-12 object-cover rounded border" />
                  ) : (
                    <div className="w-12 h-12 bg-slate-100 rounded border flex items-center justify-center text-slate-400 text-xs">none</div>
                  )}
                </td>
                <td className="p-2">
                  <div className="font-medium">{c.name}</div>
                  <div className="text-xs text-slate-500">{c.brandText || '—'}</div>
                </td>
                <td className="p-2">{c.sourceStoreName || `#${c.sourceStoreId}`}</td>
                <td className="p-2 text-right">{formatMoney(c.msrp)}</td>
                <td className="p-2 text-center">{c.submissionCount}</td>
                <td className="p-2">
                  <span className={`px-2 py-0.5 rounded text-xs ${STATUS_BADGE[c.status] || 'bg-gray-100'}`}>
                    {c.status}
                  </span>
                </td>
                <td className="p-2">
                  <button
                    className="text-blue-600 hover:underline text-sm"
                    onClick={() => openDetail(c.id)}
                  >Review</button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {activeId && activeDetail && (
        <DetailDrawer
          detail={activeDetail}
          busy={actionBusy}
          brands={brands}
          productTypes={productTypes}
          onClose={closeDetail}
          onApprove={approve}
          onReject={reject}
          onMerge={mergeInto}
        />
      )}
    </div>
  );
}

function DetailDrawer({ detail, busy, brands, productTypes, onClose, onApprove, onReject, onMerge }) {
  const { authFetch } = useAuth();
  const c = detail.candidate;
  const dc = (detail.dealCandidates || [])[0];
  const [name, setName] = useState(c.name || '');
  const [msrp, setMsrp] = useState(c.msrp ?? '');
  const [productTypeId, setProductTypeId] = useState(c.productTypeId || '');
  const [brandId, setBrandId] = useState(c.brandId || '');
  // Default to New (1). We don't seed from the linked deal candidate because
  // the extension's body-text scan false-positives on "used"/"pre-owned"
  // retailer nav links and would silently push the deal_product to Used.
  const [conditionCategoryId, setConditionCategoryId] = useState(1);
  const [description, setDescription] = useState(c.description || '');
  const [imageUrl, setImageUrl] = useState(c.imageUrl || '');
  const [imageUrlInput, setImageUrlInput] = useState('');
  const [imageBusy, setImageBusy] = useState(false);
  const [imageError, setImageError] = useState('');
  const [descBusy, setDescBusy] = useState(false);
  const [descError, setDescError] = useState('');
  const fileInputRef = useRef(null);

  // Merge-into-existing picker state. Pre-seeded with the server's
  // suggested merge product when present so admins just confirm.
  const [mergeQuery, setMergeQuery] = useState('');
  const [mergeResults, setMergeResults] = useState([]);
  const [mergeSearching, setMergeSearching] = useState(false);
  const [selectedMergeProduct, setSelectedMergeProduct] = useState(
    c.suggestedMergeProductId
      ? { id: c.suggestedMergeProductId, name: `Product #${c.suggestedMergeProductId}`, brandName: null }
      : null
  );

  useEffect(() => {
    const q = mergeQuery.trim();
    if (q.length < 2) {
      setMergeResults([]);
      return;
    }
    let cancelled = false;
    setMergeSearching(true);
    const handle = setTimeout(async () => {
      try {
        const params = new URLSearchParams({ q, limit: '15' });
        if (productTypeId) params.set('productTypeId', String(productTypeId));
        const res = await authFetch(`${API_URL}/api/admin/product-candidates/search-products?${params}`);
        if (!res.ok) throw new Error(await res.text());
        const data = await res.json();
        if (!cancelled) setMergeResults(Array.isArray(data) ? data : []);
      } catch {
        if (!cancelled) setMergeResults([]);
      } finally {
        if (!cancelled) setMergeSearching(false);
      }
    }, 250);
    return () => { cancelled = true; clearTimeout(handle); };
  }, [mergeQuery, productTypeId, authFetch]);

  const uploadImageFile = async (file) => {
    if (!file) return;
    setImageBusy(true);
    setImageError('');
    try {
      const form = new FormData();
      form.append('file', file);
      const res = await authFetch(`${API_URL}/api/admin/product-candidates/${c.id}/image`, {
        method: 'POST',
        body: form,
      });
      if (!res.ok) throw new Error(await res.text());
      const data = await res.json();
      if (data?.imageUrl) setImageUrl(data.imageUrl);
    } catch (err) {
      setImageError(err.message || 'Upload failed');
    } finally {
      setImageBusy(false);
      if (fileInputRef.current) fileInputRef.current.value = '';
    }
  };

  const importImageFromUrl = async () => {
    const trimmed = (imageUrlInput || '').trim();
    if (!trimmed) return;
    setImageBusy(true);
    setImageError('');
    try {
      const res = await authFetch(`${API_URL}/api/admin/product-candidates/${c.id}/image-from-url`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ imageUrl: trimmed }),
      });
      if (!res.ok) throw new Error(await res.text());
      const data = await res.json();
      if (data?.imageUrl) setImageUrl(data.imageUrl);
      setImageUrlInput('');
    } catch (err) {
      setImageError(err.message || 'Import failed');
    } finally {
      setImageBusy(false);
    }
  };

  const generateDescription = async () => {
    setDescBusy(true);
    setDescError('');
    try {
      const res = await authFetch(`${API_URL}/api/admin/product-candidates/${c.id}/generate-description`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({}),
      });
      if (!res.ok) throw new Error(await res.text());
      const data = await res.json();
      if (data?.description) setDescription(data.description);
    } catch (err) {
      setDescError(err.message || 'Generation failed');
    } finally {
      setDescBusy(false);
    }
  };

  const submitters = parseSubmitters(c.submittersJsonb);

  return (
    <div className="fixed inset-0 z-50 bg-black/40 flex items-end md:items-center justify-center p-2 md:p-6">
      <div className="bg-white rounded shadow-xl w-full md:max-w-2xl max-h-full overflow-y-auto">
        <div className="flex items-center justify-between p-4 border-b">
          <h2 className="font-semibold">Candidate #{c.id}</h2>
          <button onClick={onClose} className="text-slate-500">✕</button>
        </div>

        <div className="p-4 grid md:grid-cols-[160px,1fr] gap-4">
          <div className="space-y-2">
            {imageUrl ? (
              <img src={imageUrl} alt="" className="w-36 h-36 object-cover rounded border" />
            ) : (
              <div className="w-36 h-36 bg-slate-100 rounded border" />
            )}
            <input
              ref={fileInputRef}
              type="file"
              accept="image/*"
              disabled={imageBusy}
              onChange={(e) => uploadImageFile(e.target.files?.[0])}
              className="text-xs w-36"
            />
            <div className="flex gap-1">
              <input
                value={imageUrlInput}
                onChange={(e) => setImageUrlInput(e.target.value)}
                placeholder="Paste image URL"
                disabled={imageBusy}
                className="border rounded px-1.5 py-0.5 text-xs w-24"
              />
              <button
                type="button"
                disabled={imageBusy || !imageUrlInput.trim()}
                onClick={importImageFromUrl}
                className="px-2 py-0.5 text-xs rounded bg-slate-100 hover:bg-slate-200 disabled:opacity-50"
              >Import</button>
            </div>
            {imageBusy && <div className="text-xs text-slate-500">Uploading…</div>}
            {imageError && <div className="text-xs text-red-600 break-words">{imageError}</div>}
            {c.sourceUrlCanonical && (
              <a
                href={c.sourceUrlCanonical}
                target="_blank"
                rel="noopener noreferrer"
                className="text-xs text-blue-600 hover:underline inline-block break-all"
              >Open source page</a>
            )}
          </div>

          <div className="space-y-2">
            <div>
              <label className="text-xs text-slate-500">Name</label>
              <input
                value={name}
                onChange={(e) => setName(e.target.value)}
                className="border rounded px-2 py-1 w-full text-sm"
              />
            </div>
            <div className="grid grid-cols-2 gap-2">
              <div>
                <label className="text-xs text-slate-500">MSRP</label>
                <input
                  value={msrp}
                  onChange={(e) => setMsrp(e.target.value)}
                  className="border rounded px-2 py-1 w-full text-sm"
                />
              </div>
              <div>
                <label className="text-xs text-slate-500">Brand</label>
                <select
                  value={brandId}
                  onChange={(e) => setBrandId(e.target.value)}
                  className="border rounded px-2 py-1 w-full text-sm bg-white"
                >
                  <option value="">— none —</option>
                  {(brands || []).map((b) => (
                    <option key={b.id} value={b.id}>{b.name}</option>
                  ))}
                </select>
              </div>
            </div>
            <div className="grid grid-cols-[1fr,140px] gap-2">
              <div>
                <label className="text-xs text-slate-500">Product type (required for approval)</label>
                <select
                  value={productTypeId}
                  onChange={(e) => setProductTypeId(e.target.value)}
                  className="border rounded px-2 py-1 w-full text-sm bg-white"
                >
                  <option value="">— select —</option>
                  {(productTypes || []).map((t) => (
                    <option key={t.id} value={t.id}>{t.name}</option>
                  ))}
                </select>
              </div>
              <div>
                <label className="text-xs text-slate-500">Condition</label>
                <select
                  value={conditionCategoryId}
                  onChange={(e) => setConditionCategoryId(Number(e.target.value))}
                  className="border rounded px-2 py-1 w-full text-sm bg-white"
                >
                  <option value={1}>New</option>
                  <option value={2}>Used</option>
                  <option value={3}>Refurbished</option>
                </select>
              </div>
            </div>
            <div>
              <div className="flex items-center justify-between">
                <label className="text-xs text-slate-500">Description</label>
                <button
                  type="button"
                  disabled={descBusy}
                  onClick={generateDescription}
                  className="text-xs text-blue-600 hover:underline disabled:opacity-50"
                >{descBusy ? 'Generating…' : 'Generate with AI'}</button>
              </div>
              <textarea
                value={description}
                onChange={(e) => setDescription(e.target.value)}
                rows={5}
                className="border rounded px-2 py-1 w-full text-sm"
              />
              {descError && <div className="text-xs text-red-600 mt-1 break-words">{descError}</div>}
            </div>
            <div className="text-xs text-slate-500 pt-1">
              <div>Brand text scraped: <span className="font-mono">{c.brandText || '—'}</span></div>
              <div>Submissions: {c.submissionCount} · Suggested merge: {c.suggestedMergeProductId || '—'}</div>
            </div>
          </div>
        </div>

        <div className="px-4 pb-4">
          <h3 className="font-semibold text-sm mb-1">Merge into existing product</h3>
          <div className="text-xs text-slate-500 mb-2">
            Search for the product this candidate's deal should attach to. Required for the Merge action.
          </div>
          {selectedMergeProduct ? (
            <div className="flex items-center gap-2 border rounded bg-blue-50 px-2 py-1.5 text-sm">
              {selectedMergeProduct.imageUrl && (
                <img src={selectedMergeProduct.imageUrl} alt="" className="w-8 h-8 object-cover rounded border" />
              )}
              <div className="flex-1 min-w-0">
                <div className="truncate font-medium">{selectedMergeProduct.name}</div>
                <div className="text-xs text-slate-500 truncate">
                  #{selectedMergeProduct.id}
                  {selectedMergeProduct.brandName ? ` · ${selectedMergeProduct.brandName}` : ''}
                </div>
              </div>
              <button
                type="button"
                onClick={() => { setSelectedMergeProduct(null); setMergeQuery(''); }}
                className="text-xs text-slate-500 hover:text-slate-700"
              >Clear</button>
            </div>
          ) : (
            <div className="relative">
              <input
                value={mergeQuery}
                onChange={(e) => setMergeQuery(e.target.value)}
                placeholder="Type to search products…"
                className="border rounded px-2 py-1 w-full text-sm"
              />
              {(mergeSearching || mergeResults.length > 0) && mergeQuery.trim().length >= 2 && (
                <div className="absolute z-10 left-0 right-0 mt-1 bg-white border rounded shadow max-h-60 overflow-y-auto">
                  {mergeSearching && (
                    <div className="px-2 py-1 text-xs text-slate-500">Searching…</div>
                  )}
                  {!mergeSearching && mergeResults.length === 0 && (
                    <div className="px-2 py-1 text-xs text-slate-500">No matches.</div>
                  )}
                  {mergeResults.map((p) => (
                    <button
                      key={p.id}
                      type="button"
                      onClick={() => { setSelectedMergeProduct(p); setMergeResults([]); setMergeQuery(''); }}
                      className="w-full text-left flex items-center gap-2 px-2 py-1.5 hover:bg-slate-50 text-sm"
                    >
                      {p.imageUrl ? (
                        <img src={p.imageUrl} alt="" className="w-8 h-8 object-cover rounded border" />
                      ) : (
                        <div className="w-8 h-8 bg-slate-100 rounded border" />
                      )}
                      <div className="flex-1 min-w-0">
                        <div className="truncate">{p.name}</div>
                        <div className="text-xs text-slate-500 truncate">
                          #{p.id}{p.brandName ? ` · ${p.brandName}` : ''}
                        </div>
                      </div>
                    </button>
                  ))}
                </div>
              )}
            </div>
          )}
        </div>

        {dc && (
          <div className="px-4 pb-4">
            <h3 className="font-semibold text-sm mb-1">Linked deal candidate</h3>
            <div className="text-sm bg-slate-50 border rounded p-3 space-y-1">
              <div>Price: {formatMoney(dc.listingPrice)} {dc.listingCurrency}</div>
              <div>In stock: {String(dc.inStock)}</div>
              <div className="break-all text-xs text-slate-500">{dc.dealUrlCanonical}</div>
            </div>
          </div>
        )}

        {submitters.length > 0 && (
          <div className="px-4 pb-4 text-xs text-slate-500">
            Submitters: {submitters.map((s, i) => (
              <span key={i} className="mr-2">#{s.user_id} ({new Date(s.at).toLocaleDateString()})</span>
            ))}
          </div>
        )}

        <div className="flex items-center justify-end gap-2 p-4 border-t bg-slate-50">
          <button
            disabled={busy || !selectedMergeProduct}
            onClick={() => onMerge(c.id, selectedMergeProduct?.id, conditionCategoryId)}
            title={selectedMergeProduct ? '' : 'Select a product first'}
            className="px-3 py-1 rounded border bg-white hover:bg-slate-100 text-sm disabled:opacity-50 disabled:cursor-not-allowed"
          >Merge into existing</button>
          <button
            disabled={busy}
            onClick={() => onReject(c.id)}
            className="px-3 py-1 rounded border text-red-600 bg-white hover:bg-red-50 text-sm"
          >Reject</button>
          <button
            disabled={busy}
            onClick={() => onApprove(c.id, {
              name,
              msrp: msrp === '' ? null : Number(msrp),
              productTypeId: productTypeId === '' ? null : Number(productTypeId),
              brandId: brandId === '' ? null : Number(brandId),
              conditionCategoryId,
              description,
            })}
            className="px-3 py-1 rounded bg-blue-600 text-white hover:bg-blue-700 text-sm disabled:opacity-50"
          >Approve & create</button>
        </div>
      </div>
    </div>
  );
}
