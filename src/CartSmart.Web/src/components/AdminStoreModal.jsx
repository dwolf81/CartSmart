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
    scrapeEnabled: false,
    scrapeConfig: '',
    requiredQueryVars: ''
  });

  const [storeImageUrl, setStoreImageUrl] = useState('');
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
      scrapeEnabled: false,
      scrapeConfig: '',
      requiredQueryVars: ''
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
      scrapeEnabled: !!s?.scrapeEnabled,
      scrapeConfig: s?.scrapeConfig ?? '',
      requiredQueryVars: s?.requiredQueryVars ?? ''
    });

    setStoreImageUrl(s?.imageUrl ?? '');
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
    if (!selectedFile) return null;

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
    return data?.imageUrl || null;
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
      const body = {
        name,
        url: (draft.url || '').trim() || null,
        slug: (draft.slug || '').trim() || null,
        approved: !!draft.approved,
        description: draft.description || null,

        affiliateCode: (draft.affiliateCode || '').trim() || null,
        affiliateCodeVar: (draft.affiliateCodeVar || '').trim() || null,
        brandId: draft.brandId ? Number(draft.brandId) : null,
        upfrontCost: draft.upfrontCost === '' ? null : Number(draft.upfrontCost),
        upfrontCostTermId: draft.upfrontCostTermId ? Number(draft.upfrontCostTermId) : null,
        apiEnabled: !!draft.apiEnabled,
        scrapeEnabled: !!draft.scrapeEnabled,
        scrapeConfig: (draft.scrapeConfig || '').trim() || null,
        requiredQueryVars: (draft.requiredQueryVars || '').trim() || null
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
        if (id && selectedFile) {
          imageUrl = await uploadImageIfNeeded(id);
          if (imageUrl) setStoreImageUrl(imageUrl);
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
      if (selectedFile) {
        imageUrl = await uploadImageIfNeeded(storeId);
        if (imageUrl) setStoreImageUrl(imageUrl);
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

  const imageSrc = previewUrl || storeImageUrl || 'https://placehold.co/128x128';

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
                <div className="flex items-center gap-4">
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
                  <div className="text-sm text-gray-600">
                    Upload an image; it will be stored as WebP.
                  </div>
                </div>
              </div>

              <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-1">Name</label>
                  <input
                    value={draft.name}
                    onChange={(e) => setDraft((p) => ({ ...p, name: e.target.value }))}
                    className="w-full px-3 py-2 border rounded-md text-sm"
                    disabled={saving}
                  />
                </div>

                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-1">Slug</label>
                  <input
                    value={draft.slug}
                    onChange={(e) => setDraft((p) => ({ ...p, slug: e.target.value }))}
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

                <div className="flex items-center gap-2">
                  <input
                    id="scrapeEnabled"
                    type="checkbox"
                    checked={!!draft.scrapeEnabled}
                    onChange={(e) => setDraft((p) => ({ ...p, scrapeEnabled: e.target.checked }))}
                    disabled={saving}
                  />
                  <label htmlFor="scrapeEnabled" className="text-sm text-gray-700">Scrape Enabled</label>
                </div>

                <div className="md:col-span-2">
                  <label className="block text-sm font-medium text-gray-700 mb-1">Scrape Config (optional JSON)</label>
                  <textarea
                    rows={3}
                    value={draft.scrapeConfig}
                    onChange={(e) => setDraft((p) => ({ ...p, scrapeConfig: e.target.value }))}
                    className="w-full px-3 py-2 border rounded-md text-sm font-mono"
                    placeholder='{"price_selectors":["#price",".offer-price"]}'
                    disabled={saving}
                  />
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
