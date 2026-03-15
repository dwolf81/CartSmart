import React, { useCallback, useEffect, useState } from 'react';
import { useAuth } from '../context/AuthContext';

const API_URL = process.env.REACT_APP_API_URL || 'http://localhost:5000';

function toBool(v) {
  if (typeof v === 'boolean') return v;
  if (typeof v === 'string') return v.toLowerCase() === 'true';
  return Boolean(v);
}

const MODE_LABELS = { 0: 'None', 1: 'All', 2: 'Browser Only' };

function MethodBadge({ label, summary, enabled }) {
  if (summary.totalCount === 0 && enabled !== false) return (
    <span className="inline-flex items-center gap-1 text-[11px] text-gray-400 bg-gray-50 border border-gray-200 rounded px-1.5 py-0.5">{label} —</span>
  );
  if (enabled === false) return (
    <span className="inline-flex items-center gap-1 text-[11px] text-gray-400 bg-gray-100 border border-gray-200 rounded px-1.5 py-0.5 line-through">{label} disabled</span>
  );
  const rate = summary.totalCount > 0 ? Math.round((summary.successCount / summary.totalCount) * 100) : 0;
  const color = rate >= 80 ? 'text-emerald-700 bg-emerald-50 border-emerald-200'
    : rate >= 40 ? 'text-amber-700 bg-amber-50 border-amber-200'
    : 'text-red-700 bg-red-50 border-red-200';
  return (
    <span className={`inline-flex items-center gap-1 text-[11px] border rounded px-1.5 py-0.5 ${color}`}>
      {label} {summary.successCount}/{summary.totalCount} ({rate}%)
    </span>
  );
}

function DetailRow({ log }) {
  return (
    <tr className="text-xs border-t border-gray-100">
      <td className="py-1.5 px-2 font-mono text-gray-500 whitespace-nowrap">{new Date(log.createdAt).toLocaleString()}</td>
      <td className="py-1.5 px-2">
        <span className={`inline-block px-1.5 py-0.5 rounded text-[10px] font-medium ${
          log.method === 'http' ? 'bg-emerald-100 text-emerald-700'
            : log.method === 'playwright' ? 'bg-indigo-100 text-indigo-700'
            : 'bg-sky-100 text-sky-700'
        }`}>{log.method}</span>
      </td>
      <td className="py-1.5 px-2">
        {log.success
          ? <span className="text-emerald-600 font-medium">${log.price?.toFixed(2)} {log.currency}</span>
          : <span className="text-red-600">{log.errorMessage || 'Failed'}</span>
        }
      </td>
      <td className="py-1.5 px-2 text-gray-500 max-w-xs truncate" title={log.url}>{log.url}</td>
    </tr>
  );
}

export default function AdminScrapeReportPage() {
  const { user, isAuthenticated, loading: authLoading, authFetch } = useAuth();
  const isAdmin = isAuthenticated && toBool(user?.admin);

  const [stores, setStores] = useState([]);
  const [loadingStores, setLoadingStores] = useState(false);
  const [error, setError] = useState('');
  const [days, setDays] = useState(7);

  // Expanded store detail
  const [expandedStoreId, setExpandedStoreId] = useState(null);
  const [detailLogs, setDetailLogs] = useState([]);
  const [detailLoading, setDetailLoading] = useState(false);
  const [detailFilter, setDetailFilter] = useState('all'); // 'all', 'success', 'fail', 'http', 'playwright', 'extension'

  // Toggling loading state
  const [togglingStore, setTogglingStore] = useState(null);

  const loadReport = useCallback(async () => {
    setError('');
    setLoadingStores(true);
    try {
      const res = await authFetch(`${API_URL}/api/stores/admin/scrape-report?days=${days}`);
      if (!res.ok) throw new Error(`Failed to load report (${res.status})`);
      const data = await res.json();
      setStores(Array.isArray(data) ? data : []);
    } catch (e) {
      setError(e.message);
    } finally {
      setLoadingStores(false);
    }
  }, [authFetch, days]);

  useEffect(() => {
    if (isAdmin) loadReport();
  }, [isAdmin, loadReport]);

  const loadDetail = useCallback(async (storeId) => {
    setDetailLoading(true);
    setDetailLogs([]);
    try {
      const res = await authFetch(`${API_URL}/api/stores/admin/scrape-report/${storeId}?days=${days}&limit=300`);
      if (!res.ok) throw new Error(`Failed to load details (${res.status})`);
      const data = await res.json();
      setDetailLogs(Array.isArray(data) ? data : []);
    } catch {
      setDetailLogs([]);
    } finally {
      setDetailLoading(false);
    }
  }, [authFetch, days]);

  const toggleExpand = (storeId) => {
    if (expandedStoreId === storeId) {
      setExpandedStoreId(null);
      setDetailLogs([]);
      setDetailFilter('all');
    } else {
      setExpandedStoreId(storeId);
      setDetailFilter('all');
      loadDetail(storeId);
    }
  };

  const toggleMethod = async (storeId, field, currentValue) => {
    setTogglingStore(storeId);
    try {
      const res = await authFetch(`${API_URL}/api/stores/admin/${storeId}/scrape-methods`, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ [field]: !currentValue })
      });
      if (!res.ok) throw new Error('Toggle failed');
      const updated = await res.json();
      setStores(prev => prev.map(s =>
        s.storeId === storeId
          ? { ...s, scrapeHttpEnabled: updated.scrapeHttpEnabled, scrapePlaywrightEnabled: updated.scrapePlaywrightEnabled }
          : s
      ));
    } catch {
      // Silently fail — user can retry
    } finally {
      setTogglingStore(null);
    }
  };

  const filteredLogs = detailLogs.filter(l => {
    if (detailFilter === 'all') return true;
    if (detailFilter === 'success') return l.success;
    if (detailFilter === 'fail') return !l.success;
    return l.method === detailFilter;
  });

  if (authLoading) return <div className="p-8 text-center text-gray-500">Loading…</div>;
  if (!isAdmin) return <div className="p-8 text-center text-red-600">Admin access required.</div>;

  return (
    <div className="max-w-6xl mx-auto px-4 py-6">
      <div className="flex items-center justify-between mb-4">
        <h1 className="text-xl font-bold text-gray-900">Scrape Report</h1>
        <div className="flex items-center gap-3">
          <select
            value={days}
            onChange={(e) => setDays(Number(e.target.value))}
            className="text-sm border rounded px-2 py-1"
          >
            <option value={1}>Last 24h</option>
            <option value={3}>Last 3 days</option>
            <option value={7}>Last 7 days</option>
            <option value={14}>Last 14 days</option>
            <option value={30}>Last 30 days</option>
          </select>
          <button
            onClick={loadReport}
            disabled={loadingStores}
            className="px-3 py-1.5 text-sm bg-blue-600 text-white rounded hover:bg-blue-700 disabled:opacity-50"
          >
            {loadingStores ? 'Loading…' : 'Refresh'}
          </button>
        </div>
      </div>

      {error && <div className="mb-4 text-sm text-red-700 bg-red-50 border border-red-200 rounded p-3">{error}</div>}

      {stores.length === 0 && !loadingStores && !error && (
        <div className="text-sm text-gray-500 text-center py-8">No scrape-enabled stores found.</div>
      )}

      <div className="space-y-2">
        {stores.map(store => {
          const totalAll = store.http.totalCount + store.playwright.totalCount + store.extension.totalCount;
          const successAll = store.http.successCount + store.playwright.successCount + store.extension.successCount;
          const overallRate = totalAll > 0 ? Math.round((successAll / totalAll) * 100) : null;
          const isExpanded = expandedStoreId === store.storeId;

          return (
            <div key={store.storeId} className="border border-gray-200 rounded-lg bg-white">
              {/* Summary row */}
              <button
                onClick={() => toggleExpand(store.storeId)}
                className="w-full flex items-center justify-between px-4 py-3 hover:bg-gray-50 transition-colors text-left"
              >
                <div className="flex items-center gap-3 min-w-0">
                  <div className="font-medium text-sm text-gray-900 truncate">{store.storeName}</div>
                  <span className="text-[10px] text-gray-400 bg-gray-100 rounded px-1.5 py-0.5 whitespace-nowrap">
                    {MODE_LABELS[store.scrapeModeId] || 'Mode ' + store.scrapeModeId}
                  </span>
                </div>
                <div className="flex items-center gap-2 flex-shrink-0">
                  {store.scrapeModeId === 1 && (
                    <>
                      <MethodBadge label="HTTP" summary={store.http} enabled={store.scrapeHttpEnabled} />
                      <MethodBadge label="PW" summary={store.playwright} enabled={store.scrapePlaywrightEnabled} />
                    </>
                  )}
                  <MethodBadge label="Ext" summary={store.extension} />
                  {overallRate !== null && (
                    <span className={`text-xs font-mono font-medium ml-1 ${
                      overallRate >= 80 ? 'text-emerald-600' : overallRate >= 40 ? 'text-amber-600' : 'text-red-600'
                    }`}>{overallRate}%</span>
                  )}
                  {totalAll === 0 && (
                    <span className="text-xs text-gray-400">no logs</span>
                  )}
                  <svg className={`w-4 h-4 text-gray-400 transition-transform ${isExpanded ? 'rotate-180' : ''}`} fill="none" viewBox="0 0 24 24" stroke="currentColor">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 9l-7 7-7-7" />
                  </svg>
                </div>
              </button>

              {/* Expanded detail */}
              {isExpanded && (
                <div className="border-t border-gray-200 px-4 py-3">
                  {/* Method toggles */}
                  {store.scrapeModeId === 1 && (
                    <div className="flex items-center gap-4 mb-3 pb-3 border-b border-gray-100">
                      <span className="text-xs font-medium text-gray-600">Enable Methods:</span>
                      <label className="inline-flex items-center gap-1.5 text-xs cursor-pointer">
                        <input
                          type="checkbox"
                          checked={store.scrapeHttpEnabled}
                          disabled={togglingStore === store.storeId}
                          onChange={() => toggleMethod(store.storeId, 'scrapeHttpEnabled', store.scrapeHttpEnabled)}
                          className="rounded"
                        />
                        Simple GET (HTTP)
                      </label>
                      <label className="inline-flex items-center gap-1.5 text-xs cursor-pointer">
                        <input
                          type="checkbox"
                          checked={store.scrapePlaywrightEnabled}
                          disabled={togglingStore === store.storeId}
                          onChange={() => toggleMethod(store.storeId, 'scrapePlaywrightEnabled', store.scrapePlaywrightEnabled)}
                          className="rounded"
                        />
                        Playwright
                      </label>
                    </div>
                  )}

                  {/* Detail filter tabs */}
                  <div className="flex items-center gap-1.5 mb-3">
                    {['all', 'success', 'fail', 'http', 'playwright', 'extension'].map(f => (
                      <button
                        key={f}
                        onClick={() => setDetailFilter(f)}
                        className={`px-2 py-1 text-[11px] rounded border ${
                          detailFilter === f
                            ? 'bg-blue-600 text-white border-blue-600'
                            : 'bg-white text-gray-600 border-gray-200 hover:bg-gray-50'
                        }`}
                      >
                        {f === 'all' ? 'All' : f === 'success' ? 'Success' : f === 'fail' ? 'Failed' : f.charAt(0).toUpperCase() + f.slice(1)}
                      </button>
                    ))}
                    <span className="text-[11px] text-gray-400 ml-2">{filteredLogs.length} log(s)</span>
                  </div>

                  {detailLoading ? (
                    <div className="text-xs text-gray-500 py-4 text-center">Loading logs…</div>
                  ) : filteredLogs.length === 0 ? (
                    <div className="text-xs text-gray-400 py-4 text-center">No logs match the current filter.</div>
                  ) : (
                    <div className="max-h-80 overflow-y-auto border rounded">
                      <table className="w-full text-left">
                        <thead className="sticky top-0 bg-gray-50">
                          <tr className="text-[10px] text-gray-500 uppercase">
                            <th className="py-1.5 px-2 font-medium">Time</th>
                            <th className="py-1.5 px-2 font-medium">Method</th>
                            <th className="py-1.5 px-2 font-medium">Result</th>
                            <th className="py-1.5 px-2 font-medium">URL</th>
                          </tr>
                        </thead>
                        <tbody>
                          {filteredLogs.map(log => (
                            <DetailRow key={log.id} log={log} />
                          ))}
                        </tbody>
                      </table>
                    </div>
                  )}
                </div>
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
}
