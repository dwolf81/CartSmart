import React, { useCallback, useEffect, useState } from 'react';
import { useAuth } from '../context/AuthContext';

const API_URL = process.env.REACT_APP_API_URL || 'http://localhost:5000';

const STATUSES = ['pending_review', 'approved', 'rejected', 'promoted'];
const SOURCES = ['', 'crawler', 'ai'];

const STATUS_BADGE = {
  pending_review: 'bg-yellow-100 text-yellow-800',
  approved:       'bg-blue-100 text-blue-800',
  rejected:       'bg-red-100 text-red-800',
  promoted:       'bg-green-100 text-green-800',
};

function formatMoney(v) {
  const n = Number(v);
  return Number.isFinite(n) && n > 0 ? `$${n.toFixed(2)}` : '—';
}

export default function AdminDealCandidatesPage() {
  const { user, isAuthenticated, loading: authLoading, authFetch } = useAuth();
  const [status, setStatus] = useState('pending_review');
  const [source, setSource] = useState('');
  const [rows, setRows] = useState([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState(null);
  const [busy, setBusy] = useState(false);

  const reload = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const params = new URLSearchParams({ status, limit: '50' });
      if (source) params.set('source', source);
      const res = await authFetch(`${API_URL}/api/admin/deal-candidates?${params}`);
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      setRows(await res.json());
    } catch (err) {
      setError(err.message || 'Failed to load');
      setRows([]);
    } finally { setLoading(false); }
  }, [authFetch, status, source]);

  useEffect(() => { if (isAuthenticated) reload(); }, [reload, isAuthenticated]);

  const approve = async (row) => {
    const productIdInput = window.prompt(
      `Confirm product id for this deal (matched id was ${row.productId}):`,
      row.productId
    );
    if (productIdInput === null) return;
    const productId = parseInt(productIdInput, 10);
    if (!Number.isFinite(productId) || productId <= 0) {
      alert('Invalid product id.');
      return;
    }
    const priceInput = window.prompt('Price:', row.listingPrice);
    if (priceInput === null) return;
    const price = Number(priceInput);
    if (!Number.isFinite(price) || price <= 0) {
      alert('Invalid price.');
      return;
    }
    setBusy(true);
    try {
      const res = await authFetch(`${API_URL}/api/admin/deal-candidates/${row.id}/approve`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ productId, price }),
      });
      if (!res.ok) alert('Approve failed: ' + (await res.text()));
      reload();
    } finally { setBusy(false); }
  };

  const reject = async (row) => {
    const notes = window.prompt('Optional rejection note:');
    if (notes === null) return;
    setBusy(true);
    try {
      const res = await authFetch(`${API_URL}/api/admin/deal-candidates/${row.id}/reject`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ adminNotes: notes }),
      });
      if (!res.ok) alert('Reject failed.');
      reload();
    } finally { setBusy(false); }
  };

  if (authLoading) return <div className="p-6">Loading…</div>;
  if (!isAuthenticated || !user?.admin) return <div className="p-6 text-red-600">Admin access required.</div>;

  return (
    <div className="max-w-6xl mx-auto p-4 md:p-6">
      <div className="flex flex-wrap items-center gap-3 mb-4">
        <h1 className="text-2xl font-semibold flex-1">Deal Candidates</h1>
        <select
          value={status}
          onChange={(e) => setStatus(e.target.value)}
          className="border rounded px-2 py-1 text-sm"
        >
          {STATUSES.map((s) => <option key={s} value={s}>{s.replace('_', ' ')}</option>)}
        </select>
        <select
          value={source}
          onChange={(e) => setSource(e.target.value)}
          className="border rounded px-2 py-1 text-sm"
        >
          {SOURCES.map((s) => <option key={s} value={s}>{s || 'all sources'}</option>)}
        </select>
        <button onClick={reload} className="px-3 py-1 text-sm rounded bg-slate-100 hover:bg-slate-200">Refresh</button>
      </div>

      {error && <div className="mb-3 text-red-600 text-sm">{error}</div>}

      <div className="bg-white border rounded overflow-x-auto">
        <table className="w-full text-sm">
          <thead className="bg-slate-50 text-left">
            <tr>
              <th className="p-2">Title</th>
              <th className="p-2">Product</th>
              <th className="p-2 min-w-28">Store</th>
              <th className="p-2 text-right">Price</th>
              <th className="p-2">Source</th>
              <th className="p-2">Conf.</th>
              <th className="p-2">Status</th>
              <th className="p-2 w-44"></th>
            </tr>
          </thead>
          <tbody>
            {loading && <tr><td colSpan={8} className="p-4 text-center text-slate-500">Loading…</td></tr>}
            {!loading && rows.length === 0 && (
              <tr><td colSpan={8} className="p-4 text-center text-slate-500">No candidates.</td></tr>
            )}
            {!loading && rows.map((r) => (
              <tr key={r.id} className="border-t hover:bg-slate-50">
                <td className="p-2">
                  <div className="font-medium truncate max-w-xs" title={r.rawTitle}>{r.rawTitle || '—'}</div>
                  <a
                    href={r.dealUrlCanonical}
                    target="_blank"
                    rel="noopener noreferrer"
                    className="text-xs text-blue-600 hover:underline break-all"
                  >{r.dealUrlCanonical}</a>
                </td>
                <td className="p-2">{r.productName ?? `#${r.productId}`}</td>
                <td className="p-2">{r.storeName ?? `#${r.storeId}`}</td>
                <td className="p-2 text-right">{formatMoney(r.listingPrice)}</td>
                <td className="p-2">{r.source}</td>
                <td className="p-2">{r.aiConfidence ?? '—'}</td>
                <td className="p-2">
                  <span className={`px-2 py-0.5 rounded text-xs ${STATUS_BADGE[r.status] || 'bg-gray-100'}`}>
                    {r.status}
                  </span>
                </td>
                <td className="p-2 space-x-2">
                  {r.status === 'pending_review' && (
                    <>
                      <button
                        disabled={busy}
                        className="text-blue-600 hover:underline text-sm"
                        onClick={() => approve(r)}
                      >Approve</button>
                      <button
                        disabled={busy}
                        className="text-red-600 hover:underline text-sm"
                        onClick={() => reject(r)}
                      >Reject</button>
                    </>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
