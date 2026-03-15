import React, { useEffect, useState } from 'react';
import { useAuth } from '../context/AuthContext';

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

const slugify = (value) => {
  const input = (value || '').trim();
  if (!input) return '';

  // Normalize unicode (remove diacritics) and keep only [a-z0-9-]
  const normalized = input
    .toLowerCase()
    .normalize('NFKD')
    .replace(/[\u0300-\u036f]/g, '');

  return normalized
    .replace(/[^a-z0-9\s-]/g, '')
    .trim()
    .replace(/\s+/g, '-')
    .replace(/-+/g, '-')
    .replace(/^-|-$/g, '');
};

/* ─── Reusable result panel for test-scrape methods ─── */
function TestResultPanel({ result }) {
  if (!result) return null;
  const ok = result.success;
  return (
    <div className={`text-xs rounded-md p-2 border ${
      ok ? 'bg-emerald-50 border-emerald-200 text-emerald-800'
         : 'bg-red-50 border-red-200 text-red-800'
    }`}>
      {ok ? (
        <>
          <div className="font-semibold text-sm mb-1">
            Price: {result.currency === 'USD' ? '$' : result.currency === 'EUR' ? '\u20AC' : result.currency === 'GBP' ? '\u00A3' : ''}
            {result.price?.toFixed(2)}
            {result.currency && <span className="text-xs font-normal ml-1">{result.currency}</span>}
          </div>
          {result.inStock != null && (
            <div>{result.inStock ? '\u2705 In Stock' : '\u274C Out of Stock'}</div>
          )}
          {result.htmlLength != null && (
            <div className="text-gray-500">HTML size: {(result.htmlLength / 1024).toFixed(1)} KB</div>
          )}
          {result.candidates?.length > 0 && (
            <details className="mt-1">
              <summary className="cursor-pointer text-emerald-700 hover:text-emerald-900">
                {result.candidates.length} candidate{result.candidates.length !== 1 ? 's' : ''} found
              </summary>
              <ul className="mt-1 space-y-0.5 text-emerald-800">
                {result.candidates.map((c, i) => (
                  <li key={i} className="font-mono">
                    {c.currency === 'USD' ? '$' : ''}{c.amount?.toFixed(2)}
                    {c.struck && <span className="ml-1 text-orange-600">(struck)</span>}
                    {c.promo && <span className="ml-1 text-orange-600">(promo)</span>}
                    <span className="ml-1 text-gray-500">via {c.selector}</span>
                  </li>
                ))}
              </ul>
            </details>
          )}
        </>
      ) : (
        <div>
          {result.blockedByBotProtection && <span className="font-semibold">{'\uD83D\uDEE1\uFE0F'} Bot Protection: </span>}
          {result.error || 'Unknown error'}
          {result.htmlLength != null && (
            <span className="ml-2 text-gray-500">(HTML: {(result.htmlLength / 1024).toFixed(1)} KB)</span>
          )}
        </div>
      )}
    </div>
  );
}

export default function AdminStoreModal({
  isOpen,
  onClose,
  mode = 'edit',
  storeId = null,
  onCreated,
  onUpdated
}) {
  const { user, isAuthenticated, authFetch } = useAuth();
  const canEdit = isAuthenticated && !!user?.admin;

  const internalMode = mode === 'add' ? 'add' : 'edit';
  const title = internalMode === 'add' ? 'Add Store' : 'Edit Store';
  const saveLabel = internalMode === 'add' ? 'Create Store' : 'Save Store';

  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');

  const [brands, setBrands] = useState([]);
  const [addNewBrand, setAddNewBrand] = useState(false);
  const [newBrandName, setNewBrandName] = useState('');
  const [newBrandUrl, setNewBrandUrl] = useState('');

  const [testScrapeUrl, setTestScrapeUrl] = useState('');
  const [testHttpLoading, setTestHttpLoading] = useState(false);
  const [testHttpResult, setTestHttpResult] = useState(null);
  const [testPlaywrightLoading, setTestPlaywrightLoading] = useState(false);
  const [testPlaywrightResult, setTestPlaywrightResult] = useState(null);
  const [testBrowserLoading, setTestBrowserLoading] = useState(false);
  const [testBrowserResult, setTestBrowserResult] = useState(null);

  const [slugEdited, setSlugEdited] = useState(false);

  const [draft, setDraft] = useState({
    name: '',
    url: '',
    slug: '',
    approved: true,
    description: '',

    affiliateCode: '',
    affiliateCodeVar: '',
    brandId: '',
    upfrontCost: '',
    upfrontCostTermId: '',
    apiEnabled: false,
    scrapeModeId: '0',
    scrapeConfig: '',
    requiredQueryVars: '',
    scrapeHttpEnabled: true,
    scrapePlaywrightEnabled: true
  });

  const [storeImageUrl, setStoreImageUrl] = useState('');
  const [storeImageUrlInput, setStoreImageUrlInput] = useState('');
  const [selectedFile, setSelectedFile] = useState(null);
  const [previewUrl, setPreviewUrl] = useState('');

  const close = () => {
    if (saving) return;
    onClose();
  };

  const resetDraft = () => {
    if ((previewUrl || '').startsWith('blob:')) {
      try {
        URL.revokeObjectURL(previewUrl);
      } catch {}
    }

    setError('');
    setLoading(false);
    setSaving(false);
    setSelectedFile(null);
    setPreviewUrl('');
    setStoreImageUrl('');
    setStoreImageUrlInput('');
    setSlugEdited(false);
    setTestScrapeUrl('');
    setTestHttpResult(null);
    setTestPlaywrightResult(null);
    setTestBrowserResult(null);

    setDraft({
      name: '',
      url: '',
      slug: '',
      approved: true,
      description: '',

      affiliateCode: '',
      affiliateCodeVar: '',
      brandId: '',
      upfrontCost: '',
      upfrontCostTermId: '',
      apiEnabled: false,
      scrapeModeId: '0',
      scrapeConfig: '',
      requiredQueryVars: '',
      scrapeHttpEnabled: true,
      scrapePlaywrightEnabled: true
    });
  };

  const seedFromEditResponse = (data) => {
    const s = data?.store || {};
    setDraft({
      name: s?.name ?? '',
      url: s?.url ?? '',
      slug: s?.slug ?? '',
      approved: s?.approved !== false,
      description: s?.description ?? '',

      affiliateCode: s?.affiliateCode ?? '',
      affiliateCodeVar: s?.affiliateCodeVar ?? '',
      brandId: s?.brandId != null ? String(s.brandId) : '',
      upfrontCost: s?.upfrontCost != null ? String(s.upfrontCost) : '',
      upfrontCostTermId: s?.upfrontCostTermId != null ? String(s.upfrontCostTermId) : '',
      apiEnabled: !!s?.apiEnabled,
      scrapeModeId: s?.scrapeModeId != null ? String(s.scrapeModeId) : '0',
      scrapeConfig: s?.scrapeConfig ?? '',
      requiredQueryVars: s?.requiredQueryVars ?? '',
      scrapeHttpEnabled: s?.scrapeHttpEnabled !== false,
      scrapePlaywrightEnabled: s?.scrapePlaywrightEnabled !== false
    });

    setStoreImageUrl(s?.imageUrl ?? '');
    setStoreImageUrlInput('');
    setSlugEdited(true);
    setSelectedFile(null);
    if ((previewUrl || '').startsWith('blob:')) {
      try {
        URL.revokeObjectURL(previewUrl);
      } catch {}
    }
    setPreviewUrl('');
  };

  const loadEditData = async (id) => {
    setError('');
    setLoading(true);
    try {
      const res = await authFetch(`${API_URL}/api/stores/${id}/admin/edit`);
      if (!res.ok) {
        const msg = await res.text().catch(() => '');
        throw new Error(msg || 'Failed to load store');
      }
      const data = await res.json();
      seedFromEditResponse(data);
    } catch (e) {
      console.error(e);
      setError('Failed to load edit data.');
    } finally {
      setLoading(false);
    }
  };

  const loadBrands = async () => {
    try {
      const res = await authFetch(`${API_URL}/api/brands`);
      if (!res.ok) throw new Error('Failed to load brands');
      const data = await res.json();
      setBrands(Array.isArray(data) ? data : []);
    } catch (e) {
      console.error(e);
      // Non-fatal; allow modal to function without brand dropdown options.
      setBrands([]);
    }
  };

  const uploadImageIfNeeded = async (id) => {
    if (!selectedFile && !(storeImageUrlInput || '').trim()) return null;

    if ((storeImageUrlInput || '').trim() && !selectedFile) {
      const res = await authFetch(`${API_URL}/api/stores/${id}/admin/image-from-url`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ imageUrl: (storeImageUrlInput || '').trim() })
      });
      if (!res.ok) {
        const msg = await res.text().catch(() => '');
        throw new Error(msg || 'Failed to import image');
      }
      const data = await res.json().catch(() => ({}));
      const url = data?.imageUrl || null;
      if (url) setStoreImageUrl(url);
      setStoreImageUrlInput('');
      return url;
    }

    const fd = new FormData();
    fd.append('file', selectedFile);

    const res = await authFetch(`${API_URL}/api/stores/${id}/admin/image`, {
      method: 'POST',
      body: fd
    });
    if (!res.ok) {
      const msg = await res.text().catch(() => '');
      throw new Error(msg || 'Failed to upload image');
    }
    const data = await res.json();
    const url = data?.imageUrl || null;
    if (url) setStoreImageUrl(url);

    setSelectedFile(null);
    if ((previewUrl || '').startsWith('blob:')) {
      try {
        URL.revokeObjectURL(previewUrl);
      } catch {}
    }
    setPreviewUrl('');
    return url;
  };

  const handleSave = async () => {
    setError('');

    const name = (draft.name || '').trim();
    if (!name) {
      setError('Name is required.');
      return;
    }

    setSaving(true);
    try {
      // If "Add as new Brand" is checked, create the brand first
      let resolvedBrandId = draft.brandId ? Number(draft.brandId) : null;
      if (addNewBrand && newBrandName.trim()) {
        const brandRes = await authFetch(`${API_URL}/api/brands`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ name: newBrandName.trim(), url: newBrandUrl.trim() || null })
        });
        if (!brandRes.ok) throw new Error('Failed to create brand');
        const created = await brandRes.json();
        resolvedBrandId = created.id;
        setBrands((prev) => {
          if (prev.some((b) => b.id === created.id)) return prev;
          return [...prev, { id: created.id, name: created.name }].sort((a, b) => (a.name || '').localeCompare(b.name || ''));
        });
        setDraft((p) => ({ ...p, brandId: String(created.id) }));
        setAddNewBrand(false);
        setNewBrandName('');
        setNewBrandUrl('');
      }

      const body = {
        name,
        url: (draft.url || '').trim() || null,
        slug: (draft.slug || '').trim() || null,
        approved: !!draft.approved,
        description: draft.description || null,

        affiliateCode: (draft.affiliateCode || '').trim() || null,
        affiliateCodeVar: (draft.affiliateCodeVar || '').trim() || null,
        brandId: resolvedBrandId,
        upfrontCost: draft.upfrontCost === '' ? null : Number(draft.upfrontCost),
        upfrontCostTermId: draft.upfrontCostTermId ? Number(draft.upfrontCostTermId) : null,
        apiEnabled: !!draft.apiEnabled,
        scrapeModeId: draft.scrapeModeId ? Number(draft.scrapeModeId) : 0,
        scrapeConfig: (draft.scrapeConfig || '').trim() || null,
        requiredQueryVars: (draft.requiredQueryVars || '').trim() || null,
        scrapeHttpEnabled: !!draft.scrapeHttpEnabled,
        scrapePlaywrightEnabled: !!draft.scrapePlaywrightEnabled
      };

      if (internalMode === 'add') {
        const res = await authFetch(`${API_URL}/api/stores/admin`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(body)
        });
        if (!res.ok) {
          const msg = await res.text().catch(() => '');
          throw new Error(msg || 'Failed to create store');
        }
        const created = await res.json();
        const id = created?.id;

        let imageUrl = null;
        if (id && (selectedFile || (storeImageUrlInput || '').trim())) {
          imageUrl = await uploadImageIfNeeded(id);
        }

        onCreated?.({ ...created, imageUrl: imageUrl || created?.imageUrl });
        close();
        return;
      }

      // edit mode
      if (!storeId) throw new Error('Missing storeId');

      const res = await authFetch(`${API_URL}/api/stores/${storeId}/admin`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body)
      });
      if (!res.ok) {
        const msg = await res.text().catch(() => '');
        throw new Error(msg || 'Failed to save store');
      }
      const updated = await res.json();

      let imageUrl = null;
      if (selectedFile || (storeImageUrlInput || '').trim()) {
        imageUrl = await uploadImageIfNeeded(storeId);
      }

      onUpdated?.({ ...updated, imageUrl: imageUrl || updated?.imageUrl });
      close();
    } catch (e) {
      console.error(e);
      setError('Failed to save store.');
    } finally {
      setSaving(false);
    }
  };

  useEffect(() => {
    if (!isOpen) return;

    if (!canEdit) {
      resetDraft();
      return;
    }

    loadBrands();

    if (internalMode === 'add') {
      resetDraft();
      return;
    }

    if (storeId) {
      loadEditData(storeId);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isOpen, mode, storeId, canEdit]);

  if (!isOpen) return null;

  const imageSrc = previewUrl || storeImageUrlInput || storeImageUrl || 'https://placehold.co/128x128';

  return (
    <div className="fixed inset-0 bg-black bg-opacity-50 z-50 flex items-center justify-center p-4">
      <div className="bg-white rounded-lg max-w-2xl w-full max-h-[90vh] overflow-y-auto shadow-lg">
        <div className="p-6">
          <div className="flex justify-between items-center mb-4">
            <h2 className="text-2xl font-bold">{title}</h2>
            <button
              type="button"
              onClick={close}
              className="text-gray-500 hover:text-gray-700"
              disabled={saving}
            >
              <svg className="w-6 h-6" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M6 18L18 6M6 6l12 12" />
              </svg>
            </button>
          </div>

          {!canEdit ? (
            <div className="py-6 text-center text-gray-600">Admin access required.</div>
          ) : loading ? (
            <div className="py-8 text-center text-gray-600">Loading…</div>
          ) : (
            <>
              {error && <div className="mb-4 text-sm text-red-600">{error}</div>}

              <div className="mb-4">
                <div className="text-sm font-medium text-gray-700 mb-2">Store Image</div>
                <div className="flex items-start gap-4">
                  <div className="relative w-24 h-24">
                    <img
                      src={imageSrc}
                      alt="Store"
                      className="w-full h-full rounded-lg object-cover border cursor-pointer"
                      onClick={() => document.getElementById('storeImageInput')?.click()}
                    />
                    <div
                      className="absolute inset-0 bg-black bg-opacity-50 rounded-lg flex items-center justify-center opacity-0 hover:opacity-100 transition-opacity cursor-pointer"
                      onClick={() => document.getElementById('storeImageInput')?.click()}
                    >
                      <span className="text-white text-sm font-medium">Change</span>
                    </div>
                    <input
                      id="storeImageInput"
                      type="file"
                      accept="image/*"
                      className="hidden"
                      onChange={(e) => {
                        const file = e.target.files?.[0];
                        if (!file) return;
                        setStoreImageUrlInput('');
                        setSelectedFile(file);
                        const nextPreview = URL.createObjectURL(file);
                        if ((previewUrl || '').startsWith('blob:')) {
                          try {
                            URL.revokeObjectURL(previewUrl);
                          } catch {}
                        }
                        setPreviewUrl(nextPreview);
                      }}
                      disabled={saving}
                    />
                  </div>
                  <div className="flex-1 text-sm text-gray-600">
                    <div>Upload an image; it will be stored as WebP.</div>

                    <div className="mt-2">
                      <label className="block text-xs font-medium text-gray-700 mb-1">Or paste image URL</label>
                      <div className="flex items-center gap-2">
                        <input
                          value={storeImageUrlInput}
                          onChange={(e) => {
                            const next = e.target.value;
                            setStoreImageUrlInput(next);
                            if ((next || '').trim()) {
                              setSelectedFile(null);
                              if ((previewUrl || '').startsWith('blob:')) {
                                try {
                                  URL.revokeObjectURL(previewUrl);
                                } catch {}
                              }
                              setPreviewUrl('');
                            }
                          }}
                          className="w-full px-3 py-2 border rounded-md text-sm"
                          placeholder="https://..."
                          disabled={saving}
                        />
                        {storeImageUrlInput && (
                          <button
                            type="button"
                            className="px-2 py-2 border rounded-md text-xs text-gray-700 hover:bg-gray-50"
                            onClick={() => setStoreImageUrlInput('')}
                            disabled={saving}
                          >
                            Clear
                          </button>
                        )}
                      </div>
                    </div>
                  </div>
                </div>
              </div>

              <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-1">Name</label>
                  <input
                    value={draft.name}
                    onChange={(e) => setDraft((p) => ({ ...p, name: e.target.value }))}
                    onBlur={() => {
                      if (internalMode !== 'add') return;
                      if (slugEdited) return;
                      if ((draft.slug || '').trim()) return;
                      const next = slugify(draft.name);
                      if (next) setDraft((p) => ({ ...p, slug: next }));
                    }}
                    className="w-full px-3 py-2 border rounded-md text-sm"
                    disabled={saving}
                  />
                </div>

                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-1">Slug</label>
                  <input
                    value={draft.slug}
                    onChange={(e) => {
                      setSlugEdited(true);
                      setDraft((p) => ({ ...p, slug: e.target.value }));
                    }}
                    className="w-full px-3 py-2 border rounded-md text-sm"
                    placeholder="e.g. best-buy"
                    disabled={saving}
                  />
                </div>

                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-1">URL</label>
                  <input
                    value={draft.url}
                    onChange={(e) => setDraft((p) => ({ ...p, url: e.target.value }))}
                    className="w-full px-3 py-2 border rounded-md text-sm"
                    placeholder="e.g. bestbuy.com"
                    disabled={saving}
                  />
                </div>

                <div className="flex items-center gap-2 pt-6">
                  <input
                    id="approved"
                    type="checkbox"
                    checked={!!draft.approved}
                    onChange={(e) => setDraft((p) => ({ ...p, approved: e.target.checked }))}
                    disabled={saving}
                  />
                  <label htmlFor="approved" className="text-sm text-gray-700">Approved</label>
                </div>

                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-1">Affiliate Code (optional)</label>
                  <input
                    value={draft.affiliateCode}
                    onChange={(e) => setDraft((p) => ({ ...p, affiliateCode: e.target.value }))}
                    className="w-full px-3 py-2 border rounded-md text-sm"
                    disabled={saving}
                  />
                </div>

                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-1">Affiliate Code Var (optional)</label>
                  <input
                    value={draft.affiliateCodeVar}
                    onChange={(e) => setDraft((p) => ({ ...p, affiliateCodeVar: e.target.value }))}
                    className="w-full px-3 py-2 border rounded-md text-sm"
                    disabled={saving}
                  />
                </div>

                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-1">Brand</label>
                  {!addNewBrand && (
                    <select
                      value={draft.brandId}
                      onChange={(e) => setDraft((p) => ({ ...p, brandId: e.target.value }))}
                      className="w-full px-3 py-2 border rounded-md text-sm"
                      disabled={saving}
                    >
                      <option value="">(No brand)</option>
                      {brands.map((b) => (
                        <option key={b.id} value={String(b.id)}>
                          {b.name}
                        </option>
                      ))}
                    </select>
                  )}
                  {addNewBrand && (
                    <div className="space-y-2">
                      <input
                        value={newBrandName}
                        onChange={(e) => setNewBrandName(e.target.value)}
                        className="w-full px-3 py-2 border rounded-md text-sm"
                        placeholder="Brand name"
                        disabled={saving}
                      />
                      <input
                        value={newBrandUrl}
                        onChange={(e) => setNewBrandUrl(e.target.value)}
                        className="w-full px-3 py-2 border rounded-md text-sm"
                        placeholder="Brand URL (optional)"
                        disabled={saving}
                      />
                    </div>
                  )}
                  <label className="mt-1.5 flex items-center gap-2 text-xs text-gray-600 cursor-pointer">
                    <input
                      type="checkbox"
                      checked={addNewBrand}
                      onChange={(e) => {
                        setAddNewBrand(e.target.checked);
                        if (!e.target.checked) {
                          setNewBrandName('');
                          setNewBrandUrl('');
                        }
                      }}
                      className="h-3.5 w-3.5"
                      disabled={saving}
                    />
                    Add as new Brand
                  </label>
                </div>

                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-1">Required Query Vars (optional)</label>
                  <input
                    value={draft.requiredQueryVars}
                    onChange={(e) => setDraft((p) => ({ ...p, requiredQueryVars: e.target.value }))}
                    className="w-full px-3 py-2 border rounded-md text-sm"
                    placeholder="comma,separated,list"
                    disabled={saving}
                  />
                </div>

                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-1">Upfront Cost (optional)</label>
                  <input
                    value={draft.upfrontCost}
                    onChange={(e) => setDraft((p) => ({ ...p, upfrontCost: e.target.value }))}
                    className="w-full px-3 py-2 border rounded-md text-sm"
                    inputMode="decimal"
                    disabled={saving}
                  />
                </div>

                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-1">Upfront Cost Term Id (optional)</label>
                  <input
                    value={draft.upfrontCostTermId}
                    onChange={(e) => setDraft((p) => ({ ...p, upfrontCostTermId: e.target.value }))}
                    className="w-full px-3 py-2 border rounded-md text-sm"
                    inputMode="numeric"
                    disabled={saving}
                  />
                </div>

                <div className="flex items-center gap-2">
                  <input
                    id="apiEnabled"
                    type="checkbox"
                    checked={!!draft.apiEnabled}
                    onChange={(e) => setDraft((p) => ({ ...p, apiEnabled: e.target.checked }))}
                    disabled={saving}
                  />
                  <label htmlFor="apiEnabled" className="text-sm text-gray-700">API Enabled</label>
                </div>

                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-1">Scrape Mode</label>
                  <select
                    value={draft.scrapeModeId}
                    onChange={(e) => setDraft((p) => ({ ...p, scrapeModeId: e.target.value }))}
                    className="w-full px-3 py-2 border rounded-md text-sm"
                    disabled={saving}
                  >
                    <option value="0">None</option>
                    <option value="1">All (Service + Extension)</option>
                    <option value="2">Browser Extension Only</option>
                  </select>
                </div>

                <div className="md:col-span-2">
                  <div className="flex items-center justify-between mb-1">
                    <label className="text-sm font-medium text-gray-700">Scrape Config (optional JSON)</label>
                    {!draft.scrapeConfig && (
                      <button
                        type="button"
                        onClick={() => setDraft((p) => ({ ...p, scrapeConfig: '{"price_selectors":["#price",".offer-price"]}' }))}
                        className="text-xs text-blue-600 hover:text-blue-800 underline"
                        disabled={saving}
                      >
                        Use Template
                      </button>
                    )}
                  </div>
                  <textarea
                    rows={3}
                    value={draft.scrapeConfig}
                    onChange={(e) => setDraft((p) => ({ ...p, scrapeConfig: e.target.value }))}
                    className="w-full px-3 py-2 border rounded-md text-sm font-mono"
                    placeholder='{"price_selectors":["#price",".offer-price"]}'
                    disabled={saving}
                  />

                  {/* Test Scrape Config — multi-method diagnostic */}
                  {draft.scrapeConfig && draft.scrapeModeId !== '0' && (
                    <div className="mt-2 p-3 bg-slate-50 border border-slate-200 rounded-md space-y-3">
                      <div className="text-xs font-medium text-gray-700">Test Scrape Config</div>

                      {/* Shared URL input */}
                      <input
                        value={testScrapeUrl}
                        onChange={(e) => { setTestScrapeUrl(e.target.value); setTestBrowserResult(null); }}
                        className="w-full px-2 py-1.5 border rounded-md text-xs font-mono"
                        placeholder="https://store.com/product-page"
                        disabled={saving}
                      />

                      {/* ── Test 1: Simple GET (only for "All" mode) ── */}
                      {draft.scrapeModeId === '1' && (
                        <div className="border border-slate-200 rounded-md p-2.5 bg-white">
                          <div className="flex items-center justify-between mb-1.5">
                            <div className="text-xs font-semibold text-gray-700">1. Simple GET <span className="font-normal text-gray-500">(HttpClient)</span></div>
                            <button
                              type="button"
                              disabled={saving || testHttpLoading || !testScrapeUrl.trim() || !draft.scrapeConfig.trim()}
                              onClick={async () => {
                                setTestHttpLoading(true);
                                setTestHttpResult(null);
                                try {
                                  const res = await authFetch(`${API_URL}/api/stores/admin/test-scrape`, {
                                    method: 'POST',
                                    headers: { 'Content-Type': 'application/json' },
                                    body: JSON.stringify({ url: testScrapeUrl.trim(), scrapeConfig: draft.scrapeConfig.trim(), method: 'http' })
                                  });
                                  if (!res.ok) throw new Error(`Server returned ${res.status}`);
                                  const data = await res.json();
                                  setTestHttpResult(data);
                                } catch (e) {
                                  setTestHttpResult({ success: false, error: e.message || 'Request failed' });
                                } finally {
                                  setTestHttpLoading(false);
                                }
                              }}
                              className="px-2.5 py-1 text-xs bg-emerald-600 text-white rounded hover:bg-emerald-700 disabled:opacity-50 whitespace-nowrap"
                            >
                              {testHttpLoading ? 'Testing\u2026' : 'Run'}
                            </button>
                          </div>
                          <p className="text-[10px] text-gray-500 mb-1.5">Tests a raw HTTP fetch — no JavaScript execution. Many sites require JS so this may fail.</p>
                          {testHttpResult && <TestResultPanel result={testHttpResult} />}
                        </div>
                      )}

                      {/* ── Test 2: Playwright (only for "All" mode) ── */}
                      {draft.scrapeModeId === '1' && (
                        <div className="border border-slate-200 rounded-md p-2.5 bg-white">
                          <div className="flex items-center justify-between mb-1.5">
                            <div className="text-xs font-semibold text-gray-700">2. Playwright <span className="font-normal text-gray-500">(Headless Browser)</span></div>
                            <button
                              type="button"
                              disabled={saving || testPlaywrightLoading || !testScrapeUrl.trim() || !draft.scrapeConfig.trim()}
                              onClick={async () => {
                                setTestPlaywrightLoading(true);
                                setTestPlaywrightResult(null);
                                try {
                                  const res = await authFetch(`${API_URL}/api/stores/admin/test-scrape`, {
                                    method: 'POST',
                                    headers: { 'Content-Type': 'application/json' },
                                    body: JSON.stringify({ url: testScrapeUrl.trim(), scrapeConfig: draft.scrapeConfig.trim(), method: 'playwright' })
                                  });
                                  if (!res.ok) throw new Error(`Server returned ${res.status}`);
                                  const data = await res.json();
                                  setTestPlaywrightResult(data);
                                } catch (e) {
                                  setTestPlaywrightResult({ success: false, error: e.message || 'Request failed' });
                                } finally {
                                  setTestPlaywrightLoading(false);
                                }
                              }}
                              className="px-2.5 py-1 text-xs bg-indigo-600 text-white rounded hover:bg-indigo-700 disabled:opacity-50 whitespace-nowrap"
                            >
                              {testPlaywrightLoading ? 'Testing\u2026' : 'Run'}
                            </button>
                          </div>
                          <p className="text-[10px] text-gray-500 mb-1.5">Tests with a headless Chromium browser — executes JavaScript. Simulates what the scraping service does.</p>
                          {testPlaywrightResult && <TestResultPanel result={testPlaywrightResult} />}
                        </div>
                      )}

                      {/* ── Test 3: Browser Extension (for "All" or "Browser Extension Only") ── */}
                      <div className="border border-slate-200 rounded-md p-2.5 bg-white">
                        <div className="flex items-center justify-between mb-1.5">
                          <div className="text-xs font-semibold text-gray-700">
                            {draft.scrapeModeId === '1' ? '3. ' : ''}Browser Extension <span className="font-normal text-gray-500">(Real Browser)</span>
                          </div>
                          <div className="flex items-center gap-1.5">
                            <button
                              type="button"
                              disabled={!testScrapeUrl.trim() || !draft.scrapeConfig.trim() || testBrowserLoading}
                              onClick={() => {
                                const hasExtension = document.documentElement.dataset.cartsmartExtension === '1';
                                if (!hasExtension) {
                                  setTestBrowserResult({ success: false, error: 'CartSmart browser extension not detected. Install the extension and reload the page.' });
                                  return;
                                }

                                let selectors;
                                try {
                                  const cfg = JSON.parse(draft.scrapeConfig.trim());
                                  selectors = cfg.price_selectors;
                                  if (!selectors || !selectors.length) throw new Error('No price_selectors found');
                                } catch (e) {
                                  setTestBrowserResult({ success: false, error: 'Invalid scrape config: ' + e.message });
                                  return;
                                }

                                setTestBrowserLoading(true);
                                setTestBrowserResult(null);
                                const requestId = Date.now().toString();

                                const timeout = setTimeout(() => {
                                  window.removeEventListener('cartsmart-test-scrape-result', onResult);
                                  setTestBrowserResult({ success: false, error: 'Timed out — the extension did not respond in time.' });
                                  setTestBrowserLoading(false);
                                }, 35000);

                                function onResult(evt) {
                                  const d = evt.detail;
                                  if (d?.requestId !== requestId) return;
                                  window.removeEventListener('cartsmart-test-scrape-result', onResult);
                                  clearTimeout(timeout);

                                  if (d.success && d.price !== null && d.price !== undefined) {
                                    setTestBrowserResult({
                                      success: true,
                                      price: d.price,
                                      currency: d.currency || 'USD',
                                      inStock: d.inStock,
                                      candidates: (d.candidates || []).map(c => ({
                                        amount: c.amount,
                                        currency: c.currency,
                                        struck: c.struck,
                                        promo: c.promo,
                                      })),
                                    });
                                  } else {
                                    setTestBrowserResult({ success: false, error: d.error || 'No price found on page.' });
                                  }
                                  setTestBrowserLoading(false);
                                }

                                window.addEventListener('cartsmart-test-scrape-result', onResult);
                                window.dispatchEvent(new CustomEvent('cartsmart-test-scrape', {
                                  detail: { url: testScrapeUrl.trim(), selectors, requestId }
                                }));
                              }}
                              className="px-2.5 py-1 text-xs bg-sky-600 text-white rounded hover:bg-sky-700 disabled:opacity-50 whitespace-nowrap"
                            >
                              {testBrowserLoading ? 'Testing\u2026' : 'Run'}
                            </button>
                          </div>
                        </div>
                        <p className="text-[10px] text-gray-500 mb-1.5">
                          Opens the URL in a real browser tab via the CartSmart extension and runs the CSS selectors against the live DOM.
                          {draft.scrapeModeId === '2' && ' Since scrape mode is "Browser Extension Only", this is the primary test.'}
                        </p>
                        {testBrowserResult && <TestResultPanel result={testBrowserResult} />}
                      </div>
                    </div>
                  )}
                </div>

                <div className="md:col-span-2">
                  <label className="block text-sm font-medium text-gray-700 mb-1">Description</label>
                  <textarea
                    rows={4}
                    value={draft.description}
                    onChange={(e) => setDraft((p) => ({ ...p, description: e.target.value }))}
                    className="w-full px-3 py-2 border rounded-md text-sm"
                    disabled={saving}
                  />
                </div>
              </div>

              <div className="flex justify-end mt-4">
                <button
                  type="button"
                  onClick={handleSave}
                  disabled={saving}
                  className="h-10 px-4 rounded-lg bg-blue-600 text-white text-sm hover:bg-blue-700 transition-colors disabled:opacity-60"
                >
                  {saving ? 'Saving…' : saveLabel}
                </button>
              </div>
            </>
          )}
        </div>
      </div>
    </div>
  );
}
