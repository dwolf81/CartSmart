import React, { useCallback, useEffect, useState } from 'react';
import { useAuth } from '../context/AuthContext';

const API_URL = process.env.REACT_APP_API_URL || 'http://localhost:5000';

/**
 * Admin-only editor for store_scan_endpoint rows. Lives inside the store
 * modal but talks to /api/admin/stores/{storeId}/scan-endpoints directly so
 * changes here don't require re-saving the rest of the store.
 *
 * Only renders once a storeId exists — there's nothing to attach endpoints
 * to before the store has been created.
 */
export default function StoreScanEndpointsEditor({ storeId }) {
  const { authFetch } = useAuth();
  const [rows, setRows] = useState([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState(null);
  const [newUrl, setNewUrl] = useState('');
  const [newLabel, setNewLabel] = useState('');
  const [newProductTypeId, setNewProductTypeId] = useState('');
  const [productTypes, setProductTypes] = useState([]);

  const reload = useCallback(async () => {
    if (!storeId) return;
    setLoading(true);
    setError(null);
    try {
      const res = await authFetch(`${API_URL}/api/admin/stores/${storeId}/scan-endpoints`);
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      setRows(await res.json());
    } catch (err) {
      setError(err.message || 'Failed to load');
    } finally { setLoading(false); }
  }, [authFetch, storeId]);

  useEffect(() => { reload(); }, [reload]);

  useEffect(() => {
    fetch(`${API_URL}/api/producttypes`)
      .then(r => r.ok ? r.json() : [])
      .then(data => setProductTypes(Array.isArray(data) ? data : []))
      .catch(() => {});
  }, []);

  const add = async () => {
    if (!newUrl.trim()) return;
    setError(null);
    const res = await authFetch(`${API_URL}/api/admin/stores/${storeId}/scan-endpoints`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        url: newUrl.trim(),
        label: newLabel.trim() || null,
        productTypeId: newProductTypeId === '' ? null : Number(newProductTypeId),
        isActive: true,
      }),
    });
    if (!res.ok) {
      setError(`Add failed: ${await res.text()}`);
      return;
    }
    setNewUrl('');
    setNewLabel('');
    setNewProductTypeId('');
    reload();
  };

  const toggleActive = async (row) => {
    const res = await authFetch(`${API_URL}/api/admin/stores/${storeId}/scan-endpoints/${row.id}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        url: row.url,
        label: row.label,
        productTypeId: row.productTypeId,
        isActive: !row.isActive,
      }),
    });
    if (res.ok) reload();
  };

  const remove = async (row) => {
    if (!window.confirm(`Delete scan endpoint:\n${row.url}?`)) return;
    const res = await authFetch(`${API_URL}/api/admin/stores/${storeId}/scan-endpoints/${row.id}`, {
      method: 'DELETE',
    });
    if (res.ok) reload();
  };

  if (!storeId) {
    return (
      <div className="text-xs text-slate-500 italic">
        Save the store first to add scan endpoints.
      </div>
    );
  }

  return (
    <div className="border rounded p-3 bg-slate-50">
      <div className="flex items-center justify-between mb-2">
        <h3 className="font-semibold text-sm">Scan Endpoints</h3>
        <span className="text-xs text-slate-500">{rows.length} configured</span>
      </div>
      <p className="text-xs text-slate-600 mb-2">
        Listing-index URLs the discovery crawler is allowed to scan for new
        deal candidates. Scope each to a product type to keep the
        fuzzy-match candidate set small.
      </p>

      {error && <div className="text-xs text-red-600 mb-2">{error}</div>}

      {loading && rows.length === 0 ? (
        <div className="text-xs text-slate-500">Loading…</div>
      ) : (
        <table className="w-full text-xs mb-3 bg-white">
          <thead>
            <tr className="text-left bg-slate-100">
              <th className="p-1">URL</th>
              <th className="p-1">Label</th>
              <th className="p-1">Type</th>
              <th className="p-1">Active</th>
              <th className="p-1">Last crawled</th>
              <th className="p-1">Last #</th>
              <th className="p-1"></th>
            </tr>
          </thead>
          <tbody>
            {rows.length === 0 && (
              <tr><td colSpan={7} className="p-2 text-slate-500">No scan endpoints yet.</td></tr>
            )}
            {rows.map((r) => (
              <tr key={r.id} className="border-t">
                <td className="p-1 break-all max-w-xs">{r.url}</td>
                <td className="p-1">{r.label || '—'}</td>
                <td className="p-1">{productTypes.find(pt => pt.id === r.productTypeId)?.name ?? (r.productTypeId != null ? r.productTypeId : '—')}</td>
                <td className="p-1">
                  <input type="checkbox" checked={!!r.isActive} onChange={() => toggleActive(r)} />
                </td>
                <td className="p-1">{r.lastCrawledAt ? new Date(r.lastCrawledAt).toLocaleString() : '—'}</td>
                <td className="p-1">{r.lastResultCount ?? '—'}</td>
                <td className="p-1">
                  <button
                    className="text-red-600 hover:underline"
                    onClick={() => remove(r)}
                  >Delete</button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      <div className="grid grid-cols-1 md:grid-cols-[2fr,1fr,80px,auto] gap-2 items-end">
        <div>
          <label className="block text-xs text-slate-500">URL</label>
          <input
            value={newUrl}
            onChange={(e) => setNewUrl(e.target.value)}
            placeholder="https://store.example.com/clearance"
            className="border rounded px-2 py-1 w-full text-sm"
          />
        </div>
        <div>
          <label className="block text-xs text-slate-500">Label</label>
          <input
            value={newLabel}
            onChange={(e) => setNewLabel(e.target.value)}
            placeholder="Clearance irons"
            className="border rounded px-2 py-1 w-full text-sm"
          />
        </div>
        <div>
          <label className="block text-xs text-slate-500">Product Type</label>
          <select
            value={newProductTypeId}
            onChange={(e) => setNewProductTypeId(e.target.value)}
            className="border rounded px-2 py-1 w-full text-sm"
          >
            <option value="">— Any —</option>
            {productTypes.map(pt => (
              <option key={pt.id} value={pt.id}>{pt.name}</option>
            ))}
          </select>
        </div>
        <button
          type="button"
          onClick={add}
          disabled={!newUrl.trim()}
          className="px-3 py-1 rounded bg-blue-600 text-white text-sm disabled:opacity-50"
        >Add</button>
      </div>
    </div>
  );
}
