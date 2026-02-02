import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import { useAuth } from '../context/AuthContext';

const API_URL = process.env.REACT_APP_API_URL || 'http://localhost:5000';

function toBool(v) {
  if (typeof v === 'boolean') return v;
  if (typeof v === 'string') return v.toLowerCase() === 'true';
  return Boolean(v);
}

function formatMoney(value) {
  const n = Number(value);
  if (Number.isNaN(n)) return '';
  return n.toFixed(2);
}

export default function AdminManualPriceTasksPage() {
  const { user, isAuthenticated, loading, authFetch } = useAuth();
  const isAdmin = isAuthenticated && toBool(user?.admin);

  const [tasks, setTasks] = useState([]);
  const [pageError, setPageError] = useState('');
  const [loadingTasks, setLoadingTasks] = useState(false);
  const [draftById, setDraftById] = useState({});
  const [statusById, setStatusById] = useState({}); // { inStock: bool|null, sold: bool|null }
  const [savingById, setSavingById] = useState({});
  const [filter, setFilter] = useState('');

  const firstInputRef = useRef(null);

  const loadTasks = useCallback(async ({ focus = false } = {}) => {
    setPageError('');
    setLoadingTasks(true);
    try {
      const res = await authFetch(`${API_URL}/api/admin/manual-price/tasks?limit=100`);
      if (!res.ok) {
        const t = await res.text();
        throw new Error(t || `Failed to load tasks (${res.status})`);
      }
      const data = await res.json();
      setTasks(Array.isArray(data) ? data : []);

      // seed draft prices from current price for speed
      const nextDraft = {};
      const nextStatus = {};
      for (const row of data || []) {
        const currentPrice = row?.current?.price;
        if (currentPrice != null) nextDraft[row.id] = formatMoney(currentPrice);
        nextStatus[row.id] = { inStock: null, sold: null }; // default: do not change status unless explicitly set
      }
      setDraftById(nextDraft);
      setStatusById(nextStatus);

      if (focus) {
        setTimeout(() => {
          if (firstInputRef.current) firstInputRef.current.focus();
        }, 50);
      }
    } catch (e) {
      setPageError(e?.message || 'Failed to load tasks');
      setTasks([]);
    } finally {
      setLoadingTasks(false);
    }
  }, [authFetch]);

  useEffect(() => {
    if (!loading && isAdmin) loadTasks({ focus: true });
  }, [loading, isAdmin, loadTasks]);

  const filtered = useMemo(() => {
    const q = (filter || '').trim().toLowerCase();
    if (!q) return tasks;
    return tasks.filter(t => {
      const url = (t?.url || '').toLowerCase();
      const name = (t?.product?.name || '').toLowerCase();
      const slug = (t?.product?.slug || '').toLowerCase();
      return url.includes(q) || name.includes(q) || slug.includes(q);
    });
  }, [tasks, filter]);

  async function submitTask(taskId, { forcePrice } = {}) {
    setSavingById(s => ({ ...s, [taskId]: true }));
    setPageError('');
    try {
      const row = tasks.find(t => t.id === taskId);
      if (!row) throw new Error('Task not found in list');

      const rawDraft = draftById[taskId];
      const price = forcePrice != null ? forcePrice : (rawDraft != null && rawDraft !== '' ? Number(rawDraft) : null);
      const flags = statusById[taskId] || { inStock: null, sold: null };

      const payload = {
        price: price != null && !Number.isNaN(price) ? price : null,
        currency: 'USD',
        inStock: flags.inStock,
        sold: flags.sold,
      };

      const res = await authFetch(`${API_URL}/api/admin/manual-price/tasks/${taskId}/submit`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload),
      });

      if (!res.ok) {
        const t = await res.text();
        throw new Error(t || `Submit failed (${res.status})`);
      }

      // remove row for speed
      setTasks(prev => prev.filter(t => t.id !== taskId));
      setDraftById(prev => {
        const next = { ...prev };
        delete next[taskId];
        return next;
      });
      setStatusById(prev => {
        const next = { ...prev };
        delete next[taskId];
        return next;
      });
    } catch (e) {
      setPageError(e?.message || 'Submit failed');
    } finally {
      setSavingById(s => ({ ...s, [taskId]: false }));
    }
  }

  if (loading) return <div className="p-6">Loading…</div>;

  if (!isAuthenticated) {
    return <div className="p-6 text-gray-700">Please log in.</div>;
  }

  if (!isAdmin) {
    return <div className="p-6 text-gray-700">Admin access required.</div>;
  }

  return (
    <div className="container mx-auto px-4 py-6">
      <div className="flex flex-col gap-3 md:flex-row md:items-center md:justify-between">
        <div>
          <h1 className="text-2xl font-bold text-gray-900">Manual Price Tasks</h1>
          <p className="text-sm text-gray-600">Open the URL, then confirm or update the price.</p>
        </div>
        <div className="flex gap-2">
          <button
            type="button"
            onClick={() => loadTasks({ focus: true })}
            className="px-3 py-2 rounded-md bg-gray-900 text-white hover:bg-gray-800 disabled:opacity-60"
            disabled={loadingTasks}
          >
            {loadingTasks ? 'Refreshing…' : 'Refresh'}
          </button>
        </div>
      </div>

      {pageError && (
        <div className="mt-4 rounded-md border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
          {pageError}
        </div>
      )}

      <div className="mt-4 flex flex-col gap-2 md:flex-row md:items-center md:justify-between">
        <div className="text-sm text-gray-600">Pending: <span className="font-semibold text-gray-900">{tasks.length}</span></div>
        <input
          className="w-full md:w-96 rounded-md border border-gray-300 px-3 py-2 text-sm"
          placeholder="Filter by product or URL…"
          value={filter}
          onChange={e => setFilter(e.target.value)}
        />
      </div>

      <div className="mt-4 overflow-x-auto rounded-lg border border-gray-200 bg-white">
        <table className="min-w-full text-sm">
          <thead className="bg-gray-50 text-left text-xs font-semibold uppercase tracking-wide text-gray-600">
            <tr>
              <th className="px-4 py-3">Deal</th>
              <th className="px-4 py-3">Price</th>
              <th className="px-4 py-3">Status</th>
              <th className="px-4 py-3 text-right">Action</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-gray-100">
            {filtered.length === 0 ? (
              <tr>
                <td className="px-4 py-6 text-center text-gray-600" colSpan={4}>
                  {loadingTasks ? 'Loading…' : 'No pending tasks.'}
                </td>
              </tr>
            ) : (
              filtered.map((t, idx) => {
                const currentPrice = t?.current?.price;
                const draft = draftById[t.id] ?? '';
                const flags = statusById[t.id] || { inStock: null, sold: null };
                const saving = Boolean(savingById[t.id]);

                return (
                  <tr key={t.id} className={saving ? 'opacity-60' : ''}>
                    <td className="px-4 py-3">
                      <div className="flex items-start gap-3">
                        <div className="min-w-0">
                          <div className="font-semibold text-gray-900 truncate">
                            {t?.product?.slug ? (
                              <Link to={`/products/${t.product.slug}`} className="hover:underline">
                                {t?.product?.name || 'Product'}
                              </Link>
                            ) : (
                              <span>{t?.product?.name || 'Product'}</span>
                            )}
                          </div>
                          <div className="mt-1 flex flex-wrap items-center gap-2">
                            <a
                              href={t.url}
                              target="_blank"
                              rel="noreferrer"
                              className="text-xs text-[#4CAF50] hover:underline"
                            >
                              Open URL
                            </a>
                            <span className="text-xs text-gray-400">•</span>
                            <span className="text-xs text-gray-600">Task #{t.id}</span>
                            {t?.createdAt && (
                              <>
                                <span className="text-xs text-gray-400">•</span>
                                <span className="text-xs text-gray-600">{new Date(t.createdAt).toLocaleString()}</span>
                              </>
                            )}
                          </div>
                          <div className="mt-2 text-xs text-gray-500 truncate">{t.url}</div>
                        </div>
                      </div>
                    </td>

                    <td className="px-4 py-3">
                      <div className="flex items-center gap-2">
                        <input
                          ref={idx === 0 ? firstInputRef : null}
                          className="w-28 rounded-md border border-gray-300 px-2 py-1"
                          inputMode="decimal"
                          value={draft}
                          onChange={e => setDraftById(d => ({ ...d, [t.id]: e.target.value }))}
                          onKeyDown={e => {
                            if (e.key === 'Enter') submitTask(t.id);
                          }}
                        />
                        <div className="text-xs text-gray-500">
                          {currentPrice != null ? `Current: $${formatMoney(currentPrice)}` : 'Current: —'}
                        </div>
                      </div>
                    </td>

                    <td className="px-4 py-3">
                      <div className="flex gap-2">
                        <button
                          type="button"
                          className={`px-2 py-1 rounded-md border text-xs ${flags.sold ? 'bg-gray-900 text-white border-gray-900' : 'bg-white text-gray-700 border-gray-300 hover:bg-gray-50'}`}
                          onClick={() => setStatusById(s => ({ ...s, [t.id]: { inStock: null, sold: true } }))}
                          disabled={saving}
                        >
                          Sold
                        </button>
                        <button
                          type="button"
                          className={`px-2 py-1 rounded-md border text-xs ${flags.inStock === false ? 'bg-gray-900 text-white border-gray-900' : 'bg-white text-gray-700 border-gray-300 hover:bg-gray-50'}`}
                          onClick={() => setStatusById(s => ({ ...s, [t.id]: { inStock: false, sold: false } }))}
                          disabled={saving}
                        >
                          OOS
                        </button>
                        <button
                          type="button"
                          className={`px-2 py-1 rounded-md border text-xs ${flags.inStock === true && !flags.sold ? 'bg-gray-900 text-white border-gray-900' : 'bg-white text-gray-700 border-gray-300 hover:bg-gray-50'}`}
                          onClick={() => setStatusById(s => ({ ...s, [t.id]: { inStock: true, sold: false } }))}
                          disabled={saving}
                        >
                          In Stock
                        </button>
                      </div>
                    </td>

                    <td className="px-4 py-3 text-right">
                      <div className="flex justify-end gap-2">
                        <button
                          type="button"
                          onClick={() => submitTask(t.id)}
                          disabled={saving}
                          className="px-3 py-2 rounded-md bg-[#4CAF50] text-white hover:bg-[#3d8b40] disabled:opacity-60"
                        >
                          {saving ? 'Saving…' : 'Confirm'}
                        </button>
                      </div>
                    </td>
                  </tr>
                );
              })
            )}
          </tbody>
        </table>
      </div>

      <div className="mt-4 text-xs text-gray-500">
        Tip: Use Tab + Enter to fly through confirmations.
      </div>
    </div>
  );
}
