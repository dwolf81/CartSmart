import React, { useEffect, useMemo, useState } from 'react';
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

export default function AdminProductModal({
  isOpen,
  onClose,
  mode = 'edit',
  productId = null,
  productTypeId = null,
  closeOnCreated = false,
  onCreated,
  onUpdated
}) {
  const { user, isAuthenticated, authFetch } = useAuth();

  const initialMode = mode === 'add' ? 'add' : 'edit';
  const [internalMode, setInternalMode] = useState(initialMode);
  const [currentProductId, setCurrentProductId] = useState(productId);

  const [adminEditLoading, setAdminEditLoading] = useState(false);
  const [adminEditSaving, setAdminEditSaving] = useState(false);
  const [adminEditError, setAdminEditError] = useState('');

  const [adminProductDraft, setAdminProductDraft] = useState({ name: '', msrp: '', description: '' });
  const [adminProductImageUrl, setAdminProductImageUrl] = useState('');
  const [adminProductSelectedFile, setAdminProductSelectedFile] = useState(null);
  const [adminProductPreviewUrl, setAdminProductPreviewUrl] = useState('');

  const [adminBrands, setAdminBrands] = useState([]);
  const [adminBrandId, setAdminBrandId] = useState('');
  const [adminCreateBrandExpanded, setAdminCreateBrandExpanded] = useState(false);
  const [adminNewBrandDraft, setAdminNewBrandDraft] = useState({ name: '', url: '' });
  const [adminAttributes, setAdminAttributes] = useState([]);
  const [adminAvailableAttributes, setAdminAvailableAttributes] = useState([]);
  const [adminAddAttributeId, setAdminAddAttributeId] = useState('');
  const [adminNewAttributeDraft, setAdminNewAttributeDraft] = useState({ name: '', dataType: 'enum', description: '', isRequired: false });
  const [adminCreateAttrExpanded, setAdminCreateAttrExpanded] = useState(false);
  const [adminEnumDrafts, setAdminEnumDrafts] = useState({});
  const [adminNewEnumDrafts, setAdminNewEnumDrafts] = useState({});
  const [adminAttrExpanded, setAdminAttrExpanded] = useState({});

  const canEdit = isAuthenticated && !!user?.admin;

  const title = internalMode === 'add' ? 'Add Product' : 'Edit Product';
  const saveLabel = internalMode === 'add' ? 'Create Product' : 'Save Product';

  const resetToAddDraft = () => {
    if ((adminProductPreviewUrl || '').startsWith('blob:')) {
      try {
        URL.revokeObjectURL(adminProductPreviewUrl);
      } catch {}
    }
    setAdminEditError('');
    setAdminEditLoading(false);
    setAdminEditSaving(false);
    setAdminProductDraft({ name: '', msrp: '', description: '' });
    setAdminProductImageUrl('');
    setAdminProductSelectedFile(null);
    setAdminProductPreviewUrl('');

    setAdminBrands([]);
    setAdminBrandId('');
    setAdminCreateBrandExpanded(false);
    setAdminNewBrandDraft({ name: '', url: '' });
    setAdminAttributes([]);
    setAdminAvailableAttributes([]);
    setAdminAddAttributeId('');
    setAdminNewAttributeDraft({ name: '', dataType: 'enum', description: '', isRequired: false });
    setAdminCreateAttrExpanded(false);
    setAdminEnumDrafts({});
    setAdminNewEnumDrafts({});
    setAdminAttrExpanded({});
  };

  const seedFromEditResponse = (data) => {
    setAdminProductDraft({
      name: data?.product?.name ?? '',
      msrp: data?.product?.msrp ?? '',
      description: data?.product?.description ?? ''
    });

    setAdminProductImageUrl(data?.product?.imageUrl ?? '');
    const bid = data?.product?.brandId;
    setAdminBrandId(bid != null ? String(bid) : '');
    setAdminProductSelectedFile(null);
    if ((adminProductPreviewUrl || '').startsWith('blob:')) {
      try {
        URL.revokeObjectURL(adminProductPreviewUrl);
      } catch {}
    }
    setAdminProductPreviewUrl('');

    const attrs = Array.isArray(data?.attributes) ? data.attributes : [];
    const available = Array.isArray(data?.availableAttributes) ? data.availableAttributes : [];
    setAdminAttributes(attrs);
    setAdminAvailableAttributes(available);
    setAdminAddAttributeId(available?.[0]?.attributeId ? String(available[0].attributeId) : '');

    const expanded = {};
    attrs.forEach((a) => {
      expanded[String(a.attributeId)] = false;
    });
    setAdminAttrExpanded(expanded);

    const drafts = {};
    attrs.forEach((a) => {
      (a.options || []).forEach((o) => {
        drafts[String(o.id)] = {
          displayName: o.displayName ?? '',
          sortOrder: o.sortOrder ?? 0,
          isActive: !!o.isActive,
          isEnabled: (o.isEnabled !== false) && !!o.isActive
        };
      });
    });
    setAdminEnumDrafts(drafts);

    const newDrafts = {};
    attrs.forEach((a) => {
      newDrafts[String(a.attributeId)] = {
        enumKey: '',
        displayName: '',
        sortOrder: 0,
        isActive: true
      };
    });
    setAdminNewEnumDrafts(newDrafts);
  };

  const loadEditData = async (id) => {
    setAdminEditError('');
    setAdminEditLoading(true);
    try {
      const res = await authFetch(`${API_URL}/api/products/${id}/admin/edit`);
      if (!res.ok) {
        const msg = await res.text().catch(() => '');
        throw new Error(msg || 'Failed to load admin edit data');
      }
      const data = await res.json();
      seedFromEditResponse(data);
    } catch (e) {
      console.error(e);
      setAdminEditError('Failed to load edit data.');
    } finally {
      setAdminEditLoading(false);
    }
  };

  const loadBrands = async () => {
    setAdminEditError('');
    try {
      const res = await authFetch(`${API_URL}/api/brands`);
      if (!res.ok) throw new Error('Failed to load brands');
      const data = await res.json();
      setAdminBrands(Array.isArray(data) ? data : []);
    } catch (e) {
      console.error(e);
      // Keep non-fatal; modal can still function.
      setAdminBrands([]);
    }
  };

  const refreshAdminEditData = async () => {
    if (!currentProductId) return;
    const res = await authFetch(`${API_URL}/api/products/${currentProductId}/admin/edit`);
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

    setAdminProductImageUrl(data?.product?.imageUrl ?? '');

    setAdminEnumDrafts((prev) => {
      const next = { ...prev };
      attrs.forEach((a) => {
        (a.options || []).forEach((o) => {
          const key = String(o.id);
          const existing = next[key] || {};
          next[key] = {
            ...existing,
            displayName: o.displayName ?? existing.displayName ?? '',
            sortOrder: o.sortOrder ?? existing.sortOrder ?? 0,
            isActive: o.isActive != null ? !!o.isActive : !!existing.isActive,
            isEnabled: (o.isEnabled !== false) && (o.isActive != null ? !!o.isActive : !!existing.isActive)
          };
        });
      });
      return next;
    });

    setAdminNewEnumDrafts((prev) => {
      const next = { ...prev };
      attrs.forEach((a) => {
        const key = String(a.attributeId);
        if (!next[key]) {
          next[key] = { enumKey: '', displayName: '', sortOrder: 0, isActive: true };
        }
      });
      return next;
    });

    setAdminAttrExpanded((prev) => {
      const next = {};
      attrs.forEach((a) => {
        const key = String(a.attributeId);
        next[key] = prev[key] ?? false;
      });
      return next;
    });
  };

  useEffect(() => {
    if (!isOpen) return;

    const nextInternalMode = mode === 'add' ? 'add' : 'edit';
    setInternalMode(nextInternalMode);
    setCurrentProductId(productId);

    if (!canEdit) {
      resetToAddDraft();
      return;
    }

    // Load brand catalog for both add and edit modes.
    loadBrands();

    if (nextInternalMode === 'add') {
      resetToAddDraft();
      return;
    }

    if (productId) {
      loadEditData(productId);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isOpen, mode, productId, canEdit]);

  const handleAdminCreateBrand = async () => {
    setAdminEditError('');
    const name = (adminNewBrandDraft.name || '').trim();
    if (!name) {
      setAdminEditError('Brand name is required.');
      return;
    }

    setAdminEditSaving(true);
    try {
      const res = await authFetch(`${API_URL}/api/brands`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          name,
          url: (adminNewBrandDraft.url || '').trim() || null
        })
      });
      if (!res.ok) {
        const msg = await res.text().catch(() => '');
        throw new Error(msg || 'Failed to create brand');
      }

      const created = await res.json();
      const id = created?.id;
      await loadBrands();

      if (id != null) setAdminBrandId(String(id));
      setAdminNewBrandDraft({ name: '', url: '' });
      setAdminCreateBrandExpanded(false);
    } catch (e) {
      console.error(e);
      setAdminEditError('Failed to create brand.');
    } finally {
      setAdminEditSaving(false);
    }
  };

  const handleSaveProduct = async () => {
    setAdminEditError('');
    setAdminEditSaving(true);
    try {
      const msrpValue = adminProductDraft.msrp === '' ? null : Number(adminProductDraft.msrp);

      if (internalMode === 'add') {
        if (!productTypeId) throw new Error('Missing product type');
        const res = await authFetch(`${API_URL}/api/products/admin`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            name: adminProductDraft.name,
            msrp: msrpValue,
            description: adminProductDraft.description,
            productTypeId: Number(productTypeId),
            brandId: adminBrandId ? Number(adminBrandId) : null
          })
        });
        if (!res.ok) {
          const msg = await res.text().catch(() => '');
          throw new Error(msg || 'Failed to create product');
        }
        const created = await res.json();
        const createdId = created?.id;
        if (!createdId) throw new Error('Create succeeded but returned no id');

        if (typeof onCreated === 'function') onCreated(created);
        if (closeOnCreated) {
          onClose();
          return;
        }

        setCurrentProductId(createdId);
        setInternalMode('edit');
        await loadEditData(createdId);
        return;
      }

      if (!currentProductId) throw new Error('Missing product id');

      // Upload photo first (optional)
      if (adminProductSelectedFile) {
        const formData = new FormData();
        formData.append('file', adminProductSelectedFile);
        const uploadRes = await authFetch(`${API_URL}/api/products/${currentProductId}/admin/image`, {
          method: 'POST',
          body: formData
        });
        if (!uploadRes.ok) {
          const msg = await uploadRes.text().catch(() => '');
          throw new Error(msg || 'Failed to upload product image');
        }
        const payload = await uploadRes.json().catch(() => ({}));
        const newUrl = payload?.imageUrl;
        if (newUrl) setAdminProductImageUrl(newUrl);

        setAdminProductSelectedFile(null);
        if ((adminProductPreviewUrl || '').startsWith('blob:')) {
          try {
            URL.revokeObjectURL(adminProductPreviewUrl);
          } catch {}
        }
        setAdminProductPreviewUrl('');
      }

      const res = await authFetch(`${API_URL}/api/products/${currentProductId}/admin`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          name: adminProductDraft.name,
          msrp: msrpValue,
          description: adminProductDraft.description,
          brandId: adminBrandId ? Number(adminBrandId) : null
        })
      });

      if (!res.ok) {
        const msg = await res.text().catch(() => '');
        throw new Error(msg || 'Failed to save product');
      }

      const updated = await res.json();
      if (typeof onUpdated === 'function') onUpdated(updated);
    } catch (e) {
      console.error(e);
      setAdminEditError(internalMode === 'add' ? 'Failed to create product.' : 'Failed to save product.');
    } finally {
      setAdminEditSaving(false);
    }
  };

  const handleAdminAddAttribute = async () => {
    setAdminEditError('');
    if (!currentProductId) return;
    if (!adminAddAttributeId) return;
    setAdminEditSaving(true);
    try {
      const res = await authFetch(`${API_URL}/api/products/${currentProductId}/admin/product-attributes`, {
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
    if (!currentProductId) return;
    const name = (adminNewAttributeDraft.name || '').trim();
    if (!name) {
      setAdminEditError('Attribute name is required.');
      return;
    }

    setAdminEditSaving(true);
    try {
      const res = await authFetch(`${API_URL}/api/products/${currentProductId}/admin/attributes`, {
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
    if (!currentProductId) return;
    setAdminEditSaving(true);
    try {
      const res = await authFetch(`${API_URL}/api/products/${currentProductId}/admin/product-attributes/${attributeId}`, {
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
    if (!currentProductId) return;
    setAdminEditSaving(true);
    try {
      const res = await authFetch(`${API_URL}/api/products/${currentProductId}/admin/product-attributes/${attributeId}`, {
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
    if (!currentProductId) return;
    setAdminEditSaving(true);
    try {
      const d = adminEnumDrafts[String(enumValueId)] || {};
      const res = await authFetch(
        `${API_URL}/api/products/${currentProductId}/admin/attributes/${attributeId}/enum-values/${enumValueId}`,
        {
          method: 'PUT',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            displayName: d.displayName,
            sortOrder: Number(d.sortOrder) || 0,
            isActive: !!d.isActive
          })
        }
      );
      if (!res.ok) throw new Error('Failed to update enum value');
      await refreshAdminEditData();
    } catch (e) {
      console.error(e);
      setAdminEditError('Failed to save value.');
    } finally {
      setAdminEditSaving(false);
    }
  };

  const handleAdminToggleEnumEnabled = async (attributeId, enumValueId, isEnabled) => {
    setAdminEditError('');
    if (!currentProductId) return;

    const key = String(enumValueId);
    const prior = adminEnumDrafts[key];
    const priorEnabled = prior?.isEnabled !== false;

    // Optimistic UI update
    setAdminEnumDrafts((prev) => ({
      ...prev,
      [key]: { ...(prev[key] || {}), isEnabled: !!isEnabled }
    }));

    try {
      const res = await authFetch(
        `${API_URL}/api/products/${currentProductId}/admin/product-attributes/${attributeId}/enum-values/${enumValueId}/enabled`,
        {
          method: 'PUT',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ isEnabled: !!isEnabled })
        }
      );
      if (!res.ok) throw new Error('Failed to update enum availability');
    } catch (e) {
      console.error(e);
      setAdminEditError('Failed to update enum availability.');
      // Revert on failure
      setAdminEnumDrafts((prev) => ({
        ...prev,
        [key]: { ...(prev[key] || {}), isEnabled: priorEnabled }
      }));
    }
  };

  const handleAdminAddEnumValue = async (attributeId) => {
    setAdminEditError('');
    if (!currentProductId) return;
    setAdminEditSaving(true);
    try {
      const d = adminNewEnumDrafts[String(attributeId)] || {};
      const res = await authFetch(`${API_URL}/api/products/${currentProductId}/admin/attributes/${attributeId}/enum-values`, {
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
      setAdminNewEnumDrafts((prev) => ({
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

  const canShowAttributes = internalMode === 'edit' && !!currentProductId;

  const close = () => {
    if (adminEditSaving) return;
    onClose();
  };

  if (!isOpen) return null;

  const imageSrc = adminProductPreviewUrl || adminProductImageUrl || 'https://placehold.co/128x128';

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
              disabled={adminEditSaving}
            >
              <svg className="w-6 h-6" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M6 18L18 6M6 6l12 12" />
              </svg>
            </button>
          </div>

          {!canEdit ? (
            <div className="py-6 text-center text-gray-600">Admin access required.</div>
          ) : adminEditLoading ? (
            <div className="py-8 text-center text-gray-600">Loading…</div>
          ) : (
            <>
              {adminEditError && <div className="mb-4 text-sm text-red-600">{adminEditError}</div>}

              <div className="mb-4">
                <div className="text-sm font-medium text-gray-700 mb-2">Product Photo</div>
                {!canShowAttributes ? (
                  <div className="text-sm text-gray-600">Create the product to upload a photo.</div>
                ) : (
                  <div className="flex items-center gap-4">
                    <div className="relative w-24 h-24">
                      <img
                        src={imageSrc}
                        alt="Product"
                        className="w-full h-full rounded-lg object-cover border cursor-pointer"
                        onClick={() => document.getElementById('productImageInput')?.click()}
                      />
                      <div
                        className="absolute inset-0 bg-black bg-opacity-50 rounded-lg flex items-center justify-center opacity-0 hover:opacity-100 transition-opacity cursor-pointer"
                        onClick={() => document.getElementById('productImageInput')?.click()}
                      >
                        <span className="text-white text-sm font-medium">Change</span>
                      </div>
                      <input
                        id="productImageInput"
                        type="file"
                        accept="image/*"
                        className="hidden"
                        onChange={(e) => {
                          const file = e.target.files?.[0];
                          if (!file) return;
                          setAdminProductSelectedFile(file);
                          const previewUrl = URL.createObjectURL(file);
                          if ((adminProductPreviewUrl || '').startsWith('blob:')) {
                            try {
                              URL.revokeObjectURL(adminProductPreviewUrl);
                            } catch {}
                          }
                          setAdminProductPreviewUrl(previewUrl);
                        }}
                        disabled={adminEditSaving}
                      />
                    </div>
                    <div className="text-sm text-gray-600">
                      Upload an image; it will be stored as WebP.
                    </div>
                  </div>
                )}
              </div>

              <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-1">Name</label>
                  <input
                    value={adminProductDraft.name}
                    onChange={(e) => setAdminProductDraft((p) => ({ ...p, name: e.target.value }))}
                    className="w-full px-3 py-2 border rounded-md text-sm"
                    disabled={adminEditSaving}
                  />
                </div>
                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-1">MSRP</label>
                  <input
                    value={adminProductDraft.msrp}
                    onChange={(e) => setAdminProductDraft((p) => ({ ...p, msrp: e.target.value }))}
                    className="w-full px-3 py-2 border rounded-md text-sm"
                    inputMode="decimal"
                    placeholder="e.g. 19.99"
                    disabled={adminEditSaving}
                  />
                </div>

                <div className="md:col-span-2">
                  <label className="block text-sm font-medium text-gray-700 mb-1">Brand</label>
                  <select
                    value={adminBrandId}
                    onChange={(e) => setAdminBrandId(e.target.value)}
                    className="w-full px-3 py-2 border rounded-md text-sm"
                    disabled={adminEditSaving}
                  >
                    <option value="">(No brand)</option>
                    {adminBrands.map((b) => (
                      <option key={b.id} value={String(b.id)}>
                        {b.name}
                      </option>
                    ))}
                  </select>

                  <div className="mt-2 border rounded-lg p-4">
                    <button
                      type="button"
                      onClick={() => setAdminCreateBrandExpanded((v) => !v)}
                      className="w-full flex items-center justify-between text-left"
                      disabled={adminEditSaving}
                    >
                      <div className="text-sm font-semibold">Add new brand</div>
                      <div className="text-sm text-blue-600 hover:text-blue-700">
                        {adminCreateBrandExpanded ? 'Hide' : 'Show'}
                      </div>
                    </button>

                    {adminCreateBrandExpanded && (
                      <div className="grid grid-cols-1 md:grid-cols-12 gap-3 mt-3">
                        <div className="md:col-span-6">
                          <label className="block text-sm font-medium text-gray-700 mb-1">Brand name</label>
                          <input
                            value={adminNewBrandDraft.name}
                            onChange={(e) => setAdminNewBrandDraft((p) => ({ ...p, name: e.target.value }))}
                            className="w-full px-3 py-2 border rounded-md text-sm"
                            placeholder="e.g. Acme"
                            disabled={adminEditSaving}
                          />
                        </div>
                        <div className="md:col-span-6">
                          <label className="block text-sm font-medium text-gray-700 mb-1">Brand URL (optional)</label>
                          <input
                            value={adminNewBrandDraft.url}
                            onChange={(e) => setAdminNewBrandDraft((p) => ({ ...p, url: e.target.value }))}
                            className="w-full px-3 py-2 border rounded-md text-sm"
                            placeholder="https://..."
                            disabled={adminEditSaving}
                          />
                        </div>
                        <div className="md:col-span-12 flex justify-end">
                          <button
                            type="button"
                            onClick={handleAdminCreateBrand}
                            disabled={adminEditSaving}
                            className="h-10 px-4 rounded-lg bg-blue-600 text-white text-sm hover:bg-blue-700 transition-colors disabled:opacity-60"
                          >
                            Create Brand
                          </button>
                        </div>
                      </div>
                    )}
                  </div>
                </div>

                <div className="md:col-span-2">
                  <label className="block text-sm font-medium text-gray-700 mb-1">Description</label>
                  <textarea
                    rows={4}
                    value={adminProductDraft.description}
                    onChange={(e) => setAdminProductDraft((p) => ({ ...p, description: e.target.value }))}
                    className="w-full px-3 py-2 border rounded-md text-sm"
                    disabled={adminEditSaving}
                  />
                </div>
              </div>

              <div className="flex justify-end mt-4">
                <button
                  type="button"
                  onClick={handleSaveProduct}
                  disabled={adminEditSaving || (internalMode === 'add' && !productTypeId)}
                  className="h-10 px-4 rounded-lg bg-blue-600 text-white text-sm hover:bg-blue-700 transition-colors disabled:opacity-60"
                >
                  {adminEditSaving ? 'Saving…' : saveLabel}
                </button>
              </div>

              <hr className="my-6" />

              <h4 className="text-md font-semibold mb-3">Attributes</h4>

              {!canShowAttributes ? (
                <div className="text-sm text-gray-600">Create the product to manage attributes.</div>
              ) : (
                <>
                  {adminAvailableAttributes?.length > 0 && (
                    <div className="flex flex-col md:flex-row gap-3 items-start md:items-end mb-4">
                      <div className="flex-1">
                        <label className="block text-sm font-medium text-gray-700 mb-1">Add attribute</label>
                        <select
                          value={adminAddAttributeId}
                          onChange={(e) => setAdminAddAttributeId(e.target.value)}
                          className="w-full px-3 py-2 border rounded-md text-sm"
                          disabled={adminEditSaving}
                        >
                          {adminAvailableAttributes.map((a) => (
                            <option key={a.attributeId} value={String(a.attributeId)}>
                              {(a.description || '').trim() ? a.description : 'No description'} ({a.dataType})
                            </option>
                          ))}
                        </select>
                      </div>
                      <button
                        type="button"
                        onClick={handleAdminAddAttribute}
                        disabled={adminEditSaving || !adminAddAttributeId}
                        className="h-10 px-4 rounded-lg bg-blue-600 text-white text-sm hover:bg-blue-700 transition-colors disabled:opacity-60"
                      >
                        Add
                      </button>
                    </div>
                  )}

                  <div className="border rounded-lg p-4 mb-4">
                    <button
                      type="button"
                      onClick={() => setAdminCreateAttrExpanded((v) => !v)}
                      className="w-full flex items-center justify-between text-left"
                      disabled={adminEditSaving}
                    >
                      <div className="text-sm font-semibold">Create new attribute</div>
                      <div className="text-sm text-blue-600 hover:text-blue-700">{adminCreateAttrExpanded ? 'Hide' : 'Show'}</div>
                    </button>

                    {adminCreateAttrExpanded && (
                      <div className="grid grid-cols-1 md:grid-cols-12 gap-3 mt-3">
                        <div className="md:col-span-4">
                          <label className="block text-sm font-medium text-gray-700 mb-1">Name</label>
                          <input
                            value={adminNewAttributeDraft.name}
                            onChange={(e) => setAdminNewAttributeDraft((p) => ({ ...p, name: e.target.value }))}
                            className="w-full px-3 py-2 border rounded-md text-sm"
                            placeholder="e.g. Pack Size"
                            disabled={adminEditSaving}
                          />
                          <div className="mt-1 text-xs text-gray-500">Key will be auto-generated.</div>
                        </div>
                        <div className="md:col-span-3">
                          <label className="block text-sm font-medium text-gray-700 mb-1">Type</label>
                          <select
                            value={adminNewAttributeDraft.dataType}
                            onChange={(e) => setAdminNewAttributeDraft((p) => ({ ...p, dataType: e.target.value }))}
                            className="w-full px-3 py-2 border rounded-md text-sm"
                            disabled={adminEditSaving}
                          >
                            <option value="enum">enum</option>
                            <option value="text">text</option>
                            <option value="number">number</option>
                            <option value="bool">bool</option>
                          </select>
                        </div>
                        <div className="md:col-span-5">
                          <label className="block text-sm font-medium text-gray-700 mb-1">Description (optional)</label>
                          <input
                            value={adminNewAttributeDraft.description}
                            onChange={(e) => setAdminNewAttributeDraft((p) => ({ ...p, description: e.target.value }))}
                            className="w-full px-3 py-2 border rounded-md text-sm"
                            placeholder="Shown to admins"
                            disabled={adminEditSaving}
                          />
                        </div>
                        <div className="md:col-span-7 flex items-center gap-2">
                          <label className="inline-flex items-center gap-2 text-sm">
                            <input
                              type="checkbox"
                              checked={!!adminNewAttributeDraft.isRequired}
                              onChange={(e) => setAdminNewAttributeDraft((p) => ({ ...p, isRequired: e.target.checked }))}
                              disabled={adminEditSaving}
                            />
                            Required for this product
                          </label>
                        </div>
                        <div className="md:col-span-5 flex justify-end">
                          <button
                            type="button"
                            onClick={handleAdminCreateAttribute}
                            disabled={adminEditSaving}
                            className="h-10 px-4 rounded-lg bg-blue-600 text-white text-sm hover:bg-blue-700 transition-colors disabled:opacity-60"
                          >
                            Create & Add
                          </button>
                        </div>
                      </div>
                    )}
                  </div>

                  {adminAttributes.length === 0 ? (
                    <div className="text-sm text-gray-600">No product attributes configured yet.</div>
                  ) : (
                    <div className="space-y-6">
                      {adminAttributes.map((attr) => (
                        <div key={attr.attributeId} className="border rounded-lg p-4">
                          <div className="flex flex-col md:flex-row md:items-center md:justify-between gap-3 mb-3">
                            <div>
                              <div className="font-semibold">{attr.attributeKey}</div>
                              {attr.description && <div className="text-xs text-gray-600">{attr.description}</div>}
                            </div>
                            <div className="flex items-center gap-4">
                              <button
                                type="button"
                                onClick={() =>
                                  setAdminAttrExpanded((prev) => ({
                                    ...prev,
                                    [String(attr.attributeId)]: !prev[String(attr.attributeId)]
                                  }))
                                }
                                className="text-sm text-blue-600 hover:text-blue-700"
                                disabled={adminEditSaving}
                              >
                                {adminAttrExpanded[String(attr.attributeId)]
                                  ? 'Hide values'
                                  : `Show values (${(attr.options || []).length})`}
                              </button>
                              <label className="inline-flex items-center gap-2 text-sm">
                                <input
                                  type="checkbox"
                                  checked={!!attr.isRequired}
                                  onChange={(e) => handleAdminToggleAttributeRequired(attr.attributeId, e.target.checked)}
                                  disabled={adminEditSaving}
                                />
                                Required
                              </label>
                              <button
                                type="button"
                                onClick={() => handleAdminRemoveAttribute(attr.attributeId)}
                                disabled={adminEditSaving}
                                className="text-sm text-red-600 hover:text-red-700"
                              >
                                Remove
                              </button>
                            </div>
                          </div>

                          {adminAttrExpanded[String(attr.attributeId)] && (
                            <div className="space-y-2">
                              {(attr.options || []).map((opt) => {
                                const draft = adminEnumDrafts[String(opt.id)] || {
                                  displayName: '',
                                  sortOrder: 0,
                                  isActive: opt.isActive != null ? !!opt.isActive : true,
                                  isEnabled: (opt.isEnabled !== false) && (opt.isActive != null ? !!opt.isActive : true)
                                };

                                const isEnumActive = !!draft.isActive;
                                const isEnumEnabled = (draft.isEnabled !== false) && isEnumActive;
                                return (
                                  <div key={opt.id} className="grid grid-cols-1 md:grid-cols-12 gap-2 items-center">
                                    <div className="md:col-span-3 text-sm text-gray-700">
                                      <span className="font-mono text-xs">{opt.enumKey}</span>
                                    </div>
                                    <div className="md:col-span-5">
                                      <input
                                        value={draft.displayName}
                                        onChange={(e) =>
                                          setAdminEnumDrafts((prev) => ({
                                            ...prev,
                                            [String(opt.id)]: { ...draft, displayName: e.target.value }
                                          }))
                                        }
                                        className="w-full px-3 py-2 border rounded-md text-sm"
                                        placeholder="Display name"
                                      />
                                    </div>
                                    <div className="md:col-span-2">
                                      <input
                                        value={draft.sortOrder}
                                        onChange={(e) =>
                                          setAdminEnumDrafts((prev) => ({
                                            ...prev,
                                            [String(opt.id)]: { ...draft, sortOrder: e.target.value }
                                          }))
                                        }
                                        className="w-full px-3 py-2 border rounded-md text-sm"
                                        inputMode="numeric"
                                        placeholder="Sort"
                                      />
                                    </div>
                                    <div className="md:col-span-2">
                                      <div className="flex flex-wrap items-center gap-x-4 gap-y-2">
                                        <label className="inline-flex items-center gap-2 text-sm whitespace-nowrap">
                                          <input
                                            type="checkbox"
                                            checked={isEnumActive}
                                            disabled={adminEditSaving}
                                            onChange={(e) => {
                                              const nextActive = e.target.checked;

                                              // If turning inactive, it cannot remain enabled for the product.
                                              if (!nextActive && isEnumEnabled) {
                                                handleAdminToggleEnumEnabled(attr.attributeId, opt.id, false);
                                              }

                                              setAdminEnumDrafts((prev) => ({
                                                ...prev,
                                                [String(opt.id)]: {
                                                  ...draft,
                                                  isActive: nextActive,
                                                  ...(nextActive ? {} : { isEnabled: false })
                                                }
                                              }));
                                            }}
                                          />
                                          Active
                                        </label>

                                        <label
                                          className={`inline-flex items-center gap-2 text-sm whitespace-nowrap ${
                                            !isEnumActive ? 'text-gray-400' : ''
                                          }`}
                                          title={!isEnumActive ? 'Enable is only available for active values.' : undefined}
                                        >
                                          <input
                                            type="checkbox"
                                            checked={isEnumEnabled}
                                            onChange={(e) => handleAdminToggleEnumEnabled(attr.attributeId, opt.id, e.target.checked)}
                                            disabled={adminEditSaving || !isEnumActive}
                                          />
                                          Enabled
                                        </label>
                                      </div>
                                    </div>
                                    <div className="md:col-span-1 flex justify-end">
                                      <button
                                        type="button"
                                        onClick={() => handleAdminSaveEnumValue(attr.attributeId, opt.id)}
                                        disabled={adminEditSaving}
                                        className="text-sm text-blue-600 hover:text-blue-700"
                                      >
                                        Save
                                      </button>
                                    </div>
                                  </div>
                                );
                              })}

                              <div className="grid grid-cols-1 md:grid-cols-12 gap-2 items-center pt-2 border-t">
                                <div className="md:col-span-3">
                                  <input
                                    value={adminNewEnumDrafts[String(attr.attributeId)]?.enumKey ?? ''}
                                    onChange={(e) =>
                                      setAdminNewEnumDrafts((prev) => ({
                                        ...prev,
                                        [String(attr.attributeId)]: {
                                          ...(prev[String(attr.attributeId)] || {
                                            displayName: '',
                                            sortOrder: 0,
                                            isActive: true
                                          }),
                                          enumKey: e.target.value
                                        }
                                      }))
                                    }
                                    className="w-full px-3 py-2 border rounded-md text-sm font-mono"
                                    placeholder="enum_key"
                                  />
                                </div>
                                <div className="md:col-span-5">
                                  <input
                                    value={adminNewEnumDrafts[String(attr.attributeId)]?.displayName ?? ''}
                                    onChange={(e) =>
                                      setAdminNewEnumDrafts((prev) => ({
                                        ...prev,
                                        [String(attr.attributeId)]: {
                                          ...(prev[String(attr.attributeId)] || {
                                            enumKey: '',
                                            sortOrder: 0,
                                            isActive: true
                                          }),
                                          displayName: e.target.value
                                        }
                                      }))
                                    }
                                    className="w-full px-3 py-2 border rounded-md text-sm"
                                    placeholder="Display name"
                                  />
                                </div>
                                <div className="md:col-span-2">
                                  <input
                                    value={adminNewEnumDrafts[String(attr.attributeId)]?.sortOrder ?? 0}
                                    onChange={(e) =>
                                      setAdminNewEnumDrafts((prev) => ({
                                        ...prev,
                                        [String(attr.attributeId)]: {
                                          ...(prev[String(attr.attributeId)] || {
                                            enumKey: '',
                                            displayName: '',
                                            isActive: true
                                          }),
                                          sortOrder: e.target.value
                                        }
                                      }))
                                    }
                                    className="w-full px-3 py-2 border rounded-md text-sm"
                                    inputMode="numeric"
                                    placeholder="Sort"
                                  />
                                </div>
                                <div className="md:col-span-1">
                                  <label className="inline-flex items-center gap-2 text-sm">
                                    <input
                                      type="checkbox"
                                      checked={adminNewEnumDrafts[String(attr.attributeId)]?.isActive !== false}
                                      onChange={(e) =>
                                        setAdminNewEnumDrafts((prev) => ({
                                          ...prev,
                                          [String(attr.attributeId)]: {
                                            ...(prev[String(attr.attributeId)] || {
                                              enumKey: '',
                                              displayName: '',
                                              sortOrder: 0
                                            }),
                                            isActive: e.target.checked
                                          }
                                        }))
                                      }
                                    />
                                    Active
                                  </label>
                                </div>
                                <div className="md:col-span-1 flex justify-end">
                                  <button
                                    type="button"
                                    onClick={() => handleAdminAddEnumValue(attr.attributeId)}
                                    disabled={adminEditSaving}
                                    className="text-sm text-blue-600 hover:text-blue-700"
                                  >
                                    Add
                                  </button>
                                </div>
                              </div>
                            </div>
                          )}
                        </div>
                      ))}
                    </div>
                  )}
                </>
              )}
            </>
          )}
        </div>
      </div>
    </div>
  );
}
