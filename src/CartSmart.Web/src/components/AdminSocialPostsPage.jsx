import React, { useCallback, useEffect, useRef, useState } from 'react';
import { useAuth } from '../context/AuthContext';

const API_URL = process.env.REACT_APP_API_URL || 'http://localhost:5000';

const STATUS_LABELS = {
  pending_approval: 'Pending Approval',
  approved:         'Approved',
  posted:           'Posted',
  rejected:         'Rejected',
};

const STATUS_COLORS = {
  pending_approval: 'bg-yellow-100 text-yellow-800',
  approved:         'bg-blue-100 text-blue-800',
  posted:           'bg-green-100 text-green-800',
  rejected:         'bg-red-100 text-red-800',
};

const DEAL_TYPE_META = {
  1: { label: 'Direct', badge: 'bg-slate-100 text-slate-700 border-slate-200' },
  2: { label: 'Coupon', badge: 'bg-emerald-100 text-emerald-700 border-emerald-200' },
  3: { label: 'Stacked', badge: 'bg-amber-100 text-amber-700 border-amber-200' },
  4: { label: 'External', badge: 'bg-indigo-100 text-indigo-700 border-indigo-200' },
};

function toBool(v) {
  if (typeof v === 'boolean') return v;
  if (typeof v === 'string') return v.toLowerCase() === 'true';
  return Boolean(v);
}

function formatMoney(v) {
  const n = Number(v);
  return Number.isNaN(n) ? '' : `$${n.toFixed(2)}`;
}

function uniqIntList(values) {
  return Array.from(new Set((values || []).filter(v => Number.isInteger(v) && v > 0)));
}

function stripHashtags(text) {
  return (text || '')
    .split(/\s+/)
    .filter(token => token && !token.startsWith('#'))
    .join(' ')
    .trim();
}

function captionHasUrl(text) {
  return /https?:\/\//i.test(text || '');
}

function getManualPostLink(post) {
  const dealTypeId = Number(post?.dealDetails?.dealTypeId);
  if (dealTypeId === 1 || !dealTypeId) return post?.dealUrl || '';
  return post?.cartSmartDealUrl || post?.dealUrl || '';
}

function buildManualPostText(post, captionText, platform = 'generic') {
  const baseCaption = platform === 'facebook' ? stripHashtags(captionText || '') : (captionText || '');
  const link = getManualPostLink(post);
  if (!link || captionHasUrl(baseCaption)) return baseCaption.trim();
  return `${baseCaption}\n${link}`.trim();
}

function SelectionPill({ text, onRemove }) {
  return (
    <span className="inline-flex items-center gap-1 bg-blue-50 text-blue-700 border border-blue-200 text-xs px-2 py-1 rounded-full">
      {text}
      <button type="button" className="font-bold hover:text-blue-900" onClick={onRemove}>x</button>
    </span>
  );
}

function IdPickerModal({
  open,
  title,
  endpoint,
  selectedIds,
  authFetch,
  onClose,
  onApply,
  renderLabel,
  mapId,
  onRowsLoaded,
}) {
  const authFetchRef = useRef(authFetch);
  const [query, setQuery] = useState('');
  const [debouncedQuery, setDebouncedQuery] = useState('');
  const [rows, setRows] = useState([]);
  const [loading, setLoading] = useState(false);
  const [draftSelected, setDraftSelected] = useState([]);

  useEffect(() => {
    authFetchRef.current = authFetch;
  }, [authFetch]);

  useEffect(() => {
    const t = setTimeout(() => setDebouncedQuery(query), 300);
    return () => clearTimeout(t);
  }, [query]);

  useEffect(() => {
    if (!open) return;
    setDraftSelected(uniqIntList(selectedIds));
  }, [open, selectedIds]);

  useEffect(() => {
    if (!open) return;
    let cancelled = false;

    async function load() {
      setLoading(true);
      try {
        const res = await authFetchRef.current(`${API_URL}${endpoint}?query=${encodeURIComponent(debouncedQuery)}&limit=40`);
        if (!res.ok) throw new Error(`Failed to load options (${res.status})`);
        const data = await res.json();
        const list = Array.isArray(data) ? data : [];
        if (!cancelled) {
          setRows(list);
          if (typeof onRowsLoaded === 'function') onRowsLoaded(list);
        }
      } catch {
        if (!cancelled) setRows([]);
      } finally {
        if (!cancelled) setLoading(false);
      }
    }

    load();
    return () => { cancelled = true; };
  }, [open, debouncedQuery, endpoint, onRowsLoaded]);

  if (!open) return null;

  function toggle(id) {
    setDraftSelected(prev => prev.includes(id) ? prev.filter(x => x !== id) : [...prev, id]);
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div className="w-full max-w-2xl bg-white rounded-2xl border border-gray-200 shadow-xl overflow-hidden">
        <div className="px-4 py-3 border-b border-gray-200 flex items-center justify-between">
          <h3 className="text-sm font-semibold text-gray-800">{title}</h3>
          <button type="button" className="text-gray-500 hover:text-gray-800 font-bold" onClick={onClose}>x</button>
        </div>
        <div className="p-4">
          <input
            type="text"
            value={query}
            onChange={e => setQuery(e.target.value)}
            placeholder="Search by name or ID"
            className="w-full mb-3 rounded-lg border border-gray-300 px-3 py-2 text-sm"
          />

          <div className="max-h-[50vh] overflow-auto border border-gray-200 rounded-lg">
            {loading ? (
              <div className="p-4 text-sm text-gray-500">Loading...</div>
            ) : rows.length === 0 ? (
              <div className="p-4 text-sm text-gray-500">No results</div>
            ) : (
              rows.map((row, idx) => {
                const id = mapId(row);
                const checked = draftSelected.includes(id);
                return (
                  <label key={`${id}-${idx}`} className="flex items-start gap-2 px-3 py-2 border-b border-gray-100 last:border-b-0 cursor-pointer hover:bg-gray-50">
                    <input type="checkbox" checked={checked} onChange={() => toggle(id)} className="mt-0.5" />
                    <span className="text-sm text-gray-800">{renderLabel(row)}</span>
                  </label>
                );
              })
            )}
          </div>
        </div>
        <div className="px-4 py-3 border-t border-gray-200 flex items-center justify-between">
          <span className="text-xs text-gray-500">Selected: {draftSelected.length}</span>
          <div className="flex gap-2">
            <button type="button" className="px-3 py-1.5 text-sm rounded-lg border border-gray-300 text-gray-700" onClick={onClose}>Cancel</button>
            <button
              type="button"
              className="px-3 py-1.5 text-sm rounded-lg bg-blue-600 text-white hover:bg-blue-700"
              onClick={() => onApply(uniqIntList(draftSelected))}
            >
              Apply
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}

function normalizeCaption(cap = {}) {
  return {
    id: cap.id,
    captionText: cap.captionText ?? cap.caption_text ?? '',
    platform: cap.platform ?? 'all',
    selected: Boolean(cap.selected),
  };
}

function normalizeDealStep(step = {}, idx = 0) {
  return {
    stepNumber: step.stepNumber ?? step.step_number ?? idx + 1,
    dealId: step.dealId ?? step.deal_id ?? null,
    dealTypeId: step.dealTypeId ?? step.deal_type_id ?? null,
    dealTypeName: step.dealTypeName ?? step.deal_type_name ?? null,
    couponCode: step.couponCode ?? step.coupon_code ?? null,
    additionalDetails: step.additionalDetails ?? step.additional_details ?? null,
    dealUrl: step.dealUrl ?? step.deal_url ?? null,
    externalOfferUrl: step.externalOfferUrl ?? step.external_offer_url ?? null,
    externalStoreName: step.externalStoreName ?? step.external_store_name ?? null,
    externalStoreUrl: step.externalStoreUrl ?? step.external_store_url ?? null,
  };
}

function normalizeDealDetails(details = null) {
  if (!details || typeof details !== 'object') return null;
  const stepsRaw = Array.isArray(details.steps) ? details.steps : [];
  return {
    dealTypeId: details.dealTypeId ?? details.deal_type_id ?? null,
    dealTypeName: details.dealTypeName ?? details.deal_type_name ?? null,
    couponCode: details.couponCode ?? details.coupon_code ?? null,
    additionalDetails: details.additionalDetails ?? details.additional_details ?? null,
    externalOfferUrl: details.externalOfferUrl ?? details.external_offer_url ?? null,
    externalStoreName: details.externalStoreName ?? details.external_store_name ?? null,
    externalStoreUrl: details.externalStoreUrl ?? details.external_store_url ?? null,
    steps: stepsRaw.map((step, idx) => normalizeDealStep(step, idx)),
  };
}

function normalizePost(post = {}) {
  return {
    id: post.id,
    status: post.status,
    isWeekly: Boolean(post.isWeekly ?? post.is_weekly),
    scheduledDate: post.scheduledDate ?? post.scheduled_date ?? null,
    productImage: post.productImage ?? post.product_image ?? null,
    productName: post.productName ?? post.product_name ?? '',
    cartSmartDealUrl: post.cartSmartDealUrl ?? post.cart_smart_deal_url ?? null,
    currentPrice: post.currentPrice ?? post.current_price ?? null,
    originalPrice: post.originalPrice ?? post.original_price ?? null,
    dealUrl: post.dealUrl ?? post.deal_url ?? null,
    dealDetails: normalizeDealDetails(post.dealDetails ?? post.deal_details ?? null),
    adminNotes: post.adminNotes ?? post.admin_notes ?? null,
    cardImageUrl: post.cardImageUrl ?? post.card_image_url ?? null,
    captions: Array.isArray(post.captions) ? post.captions.map(normalizeCaption) : [],
  };
}

function DiscountBadge({ current, original }) {
  if (!original || Number(original) <= 0 || Number(current) >= Number(original)) return null;
  const pct = Math.round((1 - Number(current) / Number(original)) * 100);
  return (
    <span className="ml-2 inline-block bg-green-600 text-white text-xs font-bold px-2 py-0.5 rounded">
      -{pct}%
    </span>
  );
}

function PostCard({ post, onApprove, onReject, onPostNow, onDelete, onCaptionEdit, onCaptionSelect, onCopy, onGenerateCard }) {
  const [editingCaptionId, setEditingCaptionId] = useState(null);
  const [draftText, setDraftText]               = useState('');
  const [saving, setSaving]                     = useState(false);
  const [generatingCard, setGeneratingCard]     = useState(false);
  const [cardImageUrl, setCardImageUrl]          = useState(post.cardImageUrl ?? null);
  const [previewOpen, setPreviewOpen]            = useState(false);

  async function handleGenerateCard() {
    setGeneratingCard(true);
    try {
      const dataUri = await onGenerateCard(post.id);
      if (dataUri) setCardImageUrl(dataUri);
    } finally {
      setGeneratingCard(false);
    }
  }

  const selectedCaption = post.captions?.find(c => c.selected) ?? post.captions?.[0];

  async function saveCaption(captionId) {
    setSaving(true);
    await onCaptionEdit(post.id, captionId, draftText);
    setEditingCaptionId(null);
    setSaving(false);
  }

  const statusLabel = STATUS_LABELS[post.status] ?? post.status;
  const statusColor = STATUS_COLORS[post.status] ?? 'bg-gray-100 text-gray-700';

  const details = post.dealDetails;
  const dealTypeId = Number(details?.dealTypeId);
  const dealTypeMeta = DEAL_TYPE_META[dealTypeId] ?? null;
  const dealTypeName = details?.dealTypeName || dealTypeMeta?.label || null;
  const hasSteps = Array.isArray(details?.steps) && details.steps.length > 0;

  return (
    <div className="bg-white rounded-2xl shadow border border-gray-200 overflow-hidden mb-6">
      {/* Header row */}
      <div className="flex items-center justify-between px-5 py-3 border-b border-gray-100 bg-gray-50">
        <div className="flex items-center gap-3">
          <span className={`text-xs font-semibold px-2.5 py-1 rounded-full ${statusColor}`}>
            {statusLabel}
          </span>
          {post.isWeekly && (
            <span className="text-xs font-semibold px-2 py-0.5 rounded-full bg-purple-100 text-purple-800">
              Weekly Digest
            </span>
          )}
          {dealTypeName && (
            <span className={`text-xs font-semibold px-2 py-0.5 rounded-full border ${dealTypeMeta?.badge ?? 'bg-gray-100 text-gray-700 border-gray-200'}`}>
              {dealTypeName} Deal
            </span>
          )}
          <span className="text-xs text-gray-400">
            {post.scheduledDate ? new Date(post.scheduledDate).toLocaleDateString() : '—'}
          </span>
        </div>
        <span className="text-xs text-gray-400">#{post.id}</span>
      </div>

      {/* Card image preview — full-width banner when generated */}
      {cardImageUrl && (
        <div className="px-5 pt-4 pb-2">
          <p className="text-xs font-medium text-gray-500 uppercase tracking-wide mb-2">Social Card Preview</p>
          <div
            role="button"
            tabIndex={0}
            className="relative rounded-xl overflow-hidden border border-gray-200 bg-gray-900 max-w-sm mx-auto block cursor-zoom-in group focus:outline-none focus:ring-2 focus:ring-blue-500"
            onClick={() => setPreviewOpen(true)}
            onKeyDown={e => {
              if (e.key === 'Enter' || e.key === ' ') {
                e.preventDefault();
                setPreviewOpen(true);
              }
            }}
            aria-label="Open social card preview zoomed in"
          >
            <img
              src={cardImageUrl}
              alt="Social card preview"
              className="w-full h-auto block group-hover:opacity-95"
            />
            <span className="absolute bottom-2 left-2 bg-black/60 text-white text-xs px-2 py-1 rounded-lg backdrop-blur">
              Click to zoom
            </span>
            <a
              href={cardImageUrl}
              download={`card-post-${post.id}.png`}
              className="absolute top-2 right-2 bg-black/60 text-white text-xs px-2 py-1 rounded-lg hover:bg-black/80 backdrop-blur"
              onClick={e => e.stopPropagation()}
            >
              Download
            </a>
          </div>

          {previewOpen && (
            <div
              className="fixed inset-0 z-50 flex items-center justify-center bg-black/80 p-4"
              onClick={() => setPreviewOpen(false)}
            >
              <div className="relative max-w-4xl w-full" onClick={e => e.stopPropagation()}>
                <button
                  type="button"
                  className="absolute -top-3 -right-3 z-10 h-9 w-9 rounded-full bg-white text-gray-800 shadow-lg hover:bg-gray-100 font-bold"
                  onClick={() => setPreviewOpen(false)}
                  aria-label="Close social card preview"
                >
                  ×
                </button>
                <img
                  src={cardImageUrl}
                  alt="Social card preview zoomed"
                  className="max-h-[90vh] w-auto max-w-full mx-auto rounded-2xl shadow-2xl bg-white"
                />
              </div>
            </div>
          )}
        </div>
      )}

      <div className="flex gap-0 sm:gap-4 p-5">
        {/* Product image (shown only when no card has been generated) */}
        {!cardImageUrl && post.productImage && (
          <div className="hidden sm:block flex-shrink-0 w-28 h-28 rounded-xl overflow-hidden bg-gray-100 border">
            <img
              src={post.productImage}
              alt={post.productName}
              className="w-full h-full object-contain p-1"
              onError={e => { e.target.style.display = 'none'; }}
            />
          </div>
        )}

        <div className="flex-1 min-w-0">
          {/* Product name + price */}
          <div className="flex flex-wrap items-baseline gap-2 mb-1">
            <h3 className="text-base font-semibold text-gray-900 truncate max-w-xs">
              {post.productName}
            </h3>
            <span className="text-lg font-bold text-green-700">
              {formatMoney(post.currentPrice)}
            </span>
            {post.originalPrice && (
              <span className="text-sm text-gray-400 line-through">
                {formatMoney(post.originalPrice)}
              </span>
            )}
            <DiscountBadge current={post.currentPrice} original={post.originalPrice} />
          </div>

          {/* Deal URL */}
          {post.dealUrl && (
            <a
              href={post.dealUrl}
              target="_blank"
              rel="noopener noreferrer"
              className="text-xs text-blue-600 hover:underline break-all"
            >
              {post.dealUrl.length > 60 ? post.dealUrl.slice(0, 60) + '…' : post.dealUrl}
            </a>
          )}

          {/* Deal details */}
          {details && (
            <div className="mt-3 rounded-xl border border-gray-200 bg-gray-50 p-3">
              <p className="text-xs font-medium text-gray-500 uppercase tracking-wide mb-2">Deal Details</p>

              {dealTypeId === 2 && (
                <div className="text-sm text-gray-700">
                  {details.couponCode ? (
                    <span>
                      Coupon: <span className="font-mono font-semibold bg-emerald-100 text-emerald-800 px-2 py-0.5 rounded">{details.couponCode}</span>
                    </span>
                  ) : (
                    <span className="text-gray-600">Coupon deal with no code required.</span>
                  )}
                </div>
              )}

              {details.additionalDetails && (
                <p className="mt-2 text-sm text-gray-700 whitespace-pre-line">{details.additionalDetails}</p>
              )}

              {hasSteps && (dealTypeId === 3 || dealTypeId === 4) && (
                <div className="mt-2 space-y-2">
                  {(details.steps || []).map((step, idx) => {
                    const stepTypeId = Number(step.dealTypeId);
                    const stepMeta = DEAL_TYPE_META[stepTypeId] ?? null;
                    const stepTypeName = step.dealTypeName || stepMeta?.label || 'Deal';
                    return (
                      <div key={`${post.id}-step-${step.dealId ?? idx}-${idx}`} className="rounded-lg border border-gray-200 bg-white p-2.5">
                        <div className="flex flex-wrap items-center gap-2 text-xs mb-1">
                          <span className="font-semibold text-gray-700">Step {step.stepNumber ?? idx + 1}</span>
                          <span className={`px-1.5 py-0.5 rounded border ${stepMeta?.badge ?? 'bg-gray-100 text-gray-700 border-gray-200'}`}>
                            {stepTypeName}
                          </span>
                        </div>

                        {step.couponCode && (
                          <p className="text-xs text-gray-700">
                            Coupon: <span className="font-mono font-semibold bg-emerald-100 text-emerald-800 px-1.5 py-0.5 rounded">{step.couponCode}</span>
                          </p>
                        )}
                        {!step.couponCode && stepTypeId === 2 && (
                          <p className="text-xs text-gray-600">Coupon step with no code required.</p>
                        )}
                        {step.additionalDetails && (
                          <p className="text-xs text-gray-700 whitespace-pre-line mt-1">{step.additionalDetails}</p>
                        )}
                        {(step.dealUrl || step.externalOfferUrl) && (
                          <div className="mt-1.5 flex flex-wrap gap-2 text-xs">
                            {step.dealUrl && (
                              <a href={step.dealUrl} target="_blank" rel="noopener noreferrer" className="text-blue-600 hover:underline break-all">
                                Deal Link
                              </a>
                            )}
                            {step.externalOfferUrl && (
                              <a href={step.externalOfferUrl} target="_blank" rel="noopener noreferrer" className="text-indigo-600 hover:underline break-all">
                                {step.externalStoreName ? `${step.externalStoreName} External Offer` : 'External Offer'}
                              </a>
                            )}
                          </div>
                        )}
                      </div>
                    );
                  })}
                </div>
              )}
            </div>
          )}

          {/* Captions */}
          <div className="mt-3 space-y-2">
            <p className="text-xs font-medium text-gray-500 uppercase tracking-wide">Captions</p>
            {(post.captions ?? []).map((cap, idx) => (
              <div
                key={cap.id}
                className={`rounded-xl border p-3 text-sm leading-relaxed cursor-pointer transition-colors ${
                  cap.selected
                    ? 'border-blue-500 bg-blue-50'
                    : 'border-gray-200 bg-gray-50 hover:border-blue-300'
                }`}
                onClick={() => !editingCaptionId && onCaptionSelect(post.id, cap.id)}
              >
                {editingCaptionId === cap.id ? (
                  <div onClick={e => e.stopPropagation()}>
                    <textarea
                      className="w-full text-sm border rounded-lg p-2 resize-none focus:outline-none focus:ring-2 focus:ring-blue-400"
                      rows={4}
                      value={draftText}
                      onChange={e => setDraftText(e.target.value)}
                    />
                    <div className="flex gap-2 mt-2">
                      <button
                        className="text-xs px-3 py-1 rounded-lg bg-blue-600 text-white hover:bg-blue-700 disabled:opacity-50"
                        disabled={saving}
                        onClick={() => saveCaption(cap.id)}
                      >
                        {saving ? 'Saving…' : 'Save'}
                      </button>
                      <button
                        className="text-xs px-3 py-1 rounded-lg border border-gray-300 text-gray-600 hover:bg-gray-100"
                        onClick={() => setEditingCaptionId(null)}
                      >
                        Cancel
                      </button>
                    </div>
                  </div>
                ) : (
                  <div className="flex justify-between items-start gap-2">
                    <span className="whitespace-pre-line flex-1">{cap.captionText}</span>
                    <div className="flex flex-col gap-1 flex-shrink-0">
                      {cap.selected && (
                        <span className="text-xs font-bold text-blue-600 uppercase">Selected</span>
                      )}
                      <button
                        className="text-xs text-gray-400 hover:text-blue-600 underline"
                        onClick={e => {
                          e.stopPropagation();
                          setDraftText(cap.captionText);
                          setEditingCaptionId(cap.id);
                        }}
                      >
                        Edit
                      </button>
                    </div>
                  </div>
                )}
              </div>
            ))}
          </div>

          {/* Admin notes */}
          {post.adminNotes && (
            <p className="mt-2 text-xs italic text-gray-500">Note: {post.adminNotes}</p>
          )}
        </div>
      </div>

      {/* Action buttons */}
      <div className="flex flex-wrap gap-2 px-5 pb-4">
        <button
          className="px-3 py-1.5 text-xs font-medium rounded-lg border border-emerald-400 text-emerald-700 hover:bg-emerald-50 disabled:opacity-50"
          disabled={generatingCard}
          onClick={handleGenerateCard}
        >
          {generatingCard ? 'Generating…' : cardImageUrl ? 'Regen Card' : 'Generate Card'}
        </button>
        <button
          className="px-3 py-1.5 text-xs font-medium rounded-lg border border-gray-300 text-gray-700 hover:bg-gray-50"
          onClick={() => onCopy(buildManualPostText(post, selectedCaption?.captionText, 'generic'))}
        >
          Copy Post
        </button>
        <button
          className="px-3 py-1.5 text-xs font-medium rounded-lg border border-gray-300 text-gray-700 hover:bg-gray-50"
          onClick={() => onCopy(buildManualPostText(post, selectedCaption?.captionText, 'twitter'))}
        >
          Copy X
        </button>
        <button
          className="px-3 py-1.5 text-xs font-medium rounded-lg border border-gray-300 text-gray-700 hover:bg-gray-50"
          onClick={() => onCopy(buildManualPostText(post, selectedCaption?.captionText, 'facebook'))}
        >
          Copy Facebook
        </button>
        <button
          className="px-3 py-1.5 text-xs font-medium rounded-lg border border-gray-300 text-gray-700 hover:bg-gray-50"
          onClick={() => onCopy(buildManualPostText(post, selectedCaption?.captionText, 'instagram'))}
        >
          Copy Instagram
        </button>

        {(post.status === 'pending_approval' || post.status === 'approved') && (
          <>
          {post.status === 'pending_approval' && (
            <button
              className="px-4 py-2 text-sm font-medium rounded-xl bg-blue-600 text-white hover:bg-blue-700"
              onClick={() => onApprove(post.id, selectedCaption?.id ?? null)}
            >
              Approve
            </button>
          )}
          {post.status === 'approved' && (
            <button
              className="px-4 py-2 text-sm font-medium rounded-xl bg-green-600 text-white hover:bg-green-700"
              onClick={() => onPostNow(post.id)}
            >
              Post Now
            </button>
          )}
          {post.status !== 'rejected' && (
            <button
              className="px-4 py-2 text-sm font-medium rounded-xl border border-red-300 text-red-600 hover:bg-red-50"
              onClick={() => onReject(post.id)}
            >
              Reject
            </button>
          )}
          </>
        )}

        <button
          className="px-4 py-2 text-sm font-medium rounded-xl border border-red-500 text-red-700 hover:bg-red-50"
          onClick={() => onDelete(post.id)}
        >
          Delete
        </button>
      </div>
    </div>
  );
}

export default function AdminSocialPostsPage() {
  const { user, isAuthenticated, loading, authFetch } = useAuth();
  const isAdmin = isAuthenticated && toBool(user?.admin);

  const [posts, setPosts]           = useState([]);
  const [activeTab, setActiveTab]   = useState('pending_approval');
  const [pageError, setPageError]   = useState('');
  const [loadingPosts, setLoading]  = useState(false);
  const [generating, setGenerating] = useState(false);
  const [successMsg, setSuccess]    = useState('');
  const [genCount, setGenCount] = useState('2');
  const [maxPerProduct, setMaxPerProduct] = useState('1');
  const [dealIds, setDealIds] = useState([]);
  const [productIds, setProductIds] = useState([]);
  const [priorityDealIds, setPriorityDealIds] = useState([]);
  const [priorityProductIds, setPriorityProductIds] = useState([]);
  const [excludedDealIds, setExcludedDealIds] = useState([]);
  const [excludedProductIds, setExcludedProductIds] = useState([]);

  const [productLookup, setProductLookup] = useState({});
  const [dealLookup, setDealLookup] = useState({});

  const [pickerConfig, setPickerConfig] = useState({ open: false, kind: 'product', target: '' });

  const loadPosts = useCallback(async (status) => {
    setPageError('');
    setLoading(true);
    try {
      const res = await authFetch(
        `${API_URL}/api/admin/social-posts?status=${status}&limit=50`
      );
      if (!res.ok) throw new Error(`Failed to load posts (${res.status})`);
      const data = await res.json();
      setPosts(Array.isArray(data) ? data.map(normalizePost) : []);
    } catch (e) {
      setPageError(e?.message ?? 'Failed to load posts');
      setPosts([]);
    } finally {
      setLoading(false);
    }
  }, [authFetch]);

  useEffect(() => {
    if (!loading && isAdmin) loadPosts(activeTab);
  }, [loading, isAdmin, activeTab, loadPosts]);

  function openPicker(kind, target) {
    setPickerConfig({ open: true, kind, target });
  }

  function closePicker() {
    setPickerConfig({ open: false, kind: 'product', target: '' });
  }

  function getSelectedIdsForTarget(target) {
    switch (target) {
      case 'dealIds': return dealIds;
      case 'productIds': return productIds;
      case 'priorityDealIds': return priorityDealIds;
      case 'priorityProductIds': return priorityProductIds;
      case 'excludedDealIds': return excludedDealIds;
      case 'excludedProductIds': return excludedProductIds;
      default: return [];
    }
  }

  function applySelectedIds(target, ids) {
    switch (target) {
      case 'dealIds': setDealIds(ids); break;
      case 'productIds': setProductIds(ids); break;
      case 'priorityDealIds': setPriorityDealIds(ids); break;
      case 'priorityProductIds': setPriorityProductIds(ids); break;
      case 'excludedDealIds': setExcludedDealIds(ids); break;
      case 'excludedProductIds': setExcludedProductIds(ids); break;
      default: break;
    }
    closePicker();
  }

  const mergeProductLookup = useCallback((rows) => {
    setProductLookup(prev => {
      const next = { ...prev };
      for (const r of rows || []) {
        if (!r || !r.id) continue;
        next[r.id] = r.name || `Product #${r.id}`;
      }
      return next;
    });
  }, []);

  const mergeDealLookup = useCallback((rows) => {
    setDealLookup(prev => {
      const next = { ...prev };
      for (const r of rows || []) {
        if (!r || !r.dealId) continue;
        const product = r.productName || `Product #${r.productId ?? '?'}`;
        const discount = Number(r.discountPercent) > 0 ? ` (${r.discountPercent}% off)` : '';
        next[r.dealId] = `Deal #${r.dealId}: ${product}${discount}`;
      }
      return next;
    });
  }, []);

  const isDealPicker = pickerConfig.kind === 'deal';
  const pickerEndpoint = isDealPicker
    ? '/api/admin/social-posts/options/deals'
    : '/api/admin/social-posts/options/products';
  const handlePickerRowsLoaded = isDealPicker ? mergeDealLookup : mergeProductLookup;

  // ── Actions ─────────────────────────────────────────────────────────────

  async function handleApprove(postId, captionId) {
    setPageError('');
    try {
      const res = await authFetch(`${API_URL}/api/admin/social-posts/${postId}/approve`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ caption_id: captionId, admin_notes: null }),
      });
      if (!res.ok) throw new Error(`Approve failed (${res.status})`);
      setSuccess('Post approved.');
      loadPosts(activeTab);
    } catch (e) {
      setPageError(e?.message ?? 'Approve failed');
    }
  }

  async function handleReject(postId) {
    setPageError('');
    const notes = window.prompt('Optional rejection note:');
    if (notes === null) return; // cancelled
    try {
      const res = await authFetch(`${API_URL}/api/admin/social-posts/${postId}/reject`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ admin_notes: notes || null }),
      });
      if (!res.ok) throw new Error(`Reject failed (${res.status})`);
      setSuccess('Post rejected.');
      loadPosts(activeTab);
    } catch (e) {
      setPageError(e?.message ?? 'Reject failed');
    }
  }

  async function handlePostNow(postId) {
    if (!window.confirm('Post this deal to all configured platforms now?')) return;
    setPageError('');
    try {
      const res = await authFetch(`${API_URL}/api/admin/social-posts/${postId}/post-now`, {
        method: 'POST',
      });
      if (!res.ok) throw new Error(`Post failed (${res.status})`);
      const result = await res.json();
      const platformSummary = (result.platforms ?? [])
        .map(p => `${p.platform}: ${p.skipped ? 'skipped' : p.success ? '✓' : '✗'}`)
        .join(', ');
      setSuccess(`Posted! Platforms: ${platformSummary || 'none configured'}`);
      loadPosts(activeTab);
    } catch (e) {
      setPageError(e?.message ?? 'Post failed');
    }
  }

  async function handleDelete(postId) {
    if (!window.confirm('Delete this social post? This cannot be undone.')) return;
    setPageError('');
    try {
      const res = await authFetch(`${API_URL}/api/admin/social-posts/${postId}`, {
        method: 'DELETE',
      });
      if (!res.ok) throw new Error(`Delete failed (${res.status})`);
      setSuccess('Post deleted.');
      setPosts(prev => prev.filter(p => p.id !== postId));
    } catch (e) {
      setPageError(e?.message ?? 'Delete failed');
    }
  }

  async function handleCopy(text) {
    try {
      await navigator.clipboard.writeText(text || '');
      setSuccess('Copied to clipboard.');
    } catch {
      setPageError('Clipboard copy failed.');
    }
  }

  async function handleCaptionEdit(postId, captionId, newText) {
    setPageError('');
    try {
      const res = await authFetch(
        `${API_URL}/api/admin/social-posts/${postId}/captions/${captionId}`,
        {
          method: 'PUT',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ caption_text: newText }),
        }
      );
      if (!res.ok) throw new Error(`Save caption failed (${res.status})`);
      // Refresh this single post in the list
      const pRes = await authFetch(`${API_URL}/api/admin/social-posts/${postId}`);
      if (pRes.ok) {
        const updated = normalizePost(await pRes.json());
        setPosts(prev => prev.map(p => (p.id === postId ? updated : p)));
      }
    } catch (e) {
      setPageError(e?.message ?? 'Failed to save caption');
      throw e;
    }
  }

  async function handleCaptionSelect(postId, captionId) {
    // Approve with the chosen caption pre-selected
    await handleApprove(postId, captionId);
  }

  async function handleGenerate() {
    setGenerating(true);
    setPageError('');
    try {
      const payload = {
        count: Number(genCount) > 0 ? Number(genCount) : undefined,
        max_per_product_per_day: Number(maxPerProduct) > 0 ? Number(maxPerProduct) : undefined,
        deal_ids: dealIds,
        product_ids: productIds,
        priority_deal_ids: priorityDealIds,
        priority_product_ids: priorityProductIds,
        excluded_deal_ids: excludedDealIds,
        excluded_product_ids: excludedProductIds,
      };

      const res = await authFetch(`${API_URL}/api/admin/social-posts/generate`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload),
      });
      if (!res.ok) throw new Error(`Generate failed (${res.status})`);
      const data = await res.json();
      setSuccess(`Generated ${data.generated ?? 0} new post(s).`);
      if (activeTab === 'pending_approval') loadPosts('pending_approval');
    } catch (e) {
      setPageError(e?.message ?? 'Generate failed');
    } finally {
      setGenerating(false);
    }
  }

  async function handleGenerateCard(postId) {
    setPageError('');
    try {
      const res = await authFetch(`${API_URL}/api/admin/social-posts/${postId}/generate-card`, {
        method: 'POST',
      });
      if (!res.ok) throw new Error(`Card generation failed (${res.status})`);
      const blob = await res.blob();
      return new Promise((resolve) => {
        const reader = new FileReader();
        reader.onloadend = () => {
          // Refresh this post to pick up persisted image_url
          authFetch(`${API_URL}/api/admin/social-posts/${postId}`).then(async pRes => {
            if (pRes.ok) {
              const updated = normalizePost(await pRes.json());
              setPosts(prev => prev.map(p => (p.id === postId ? updated : p)));
            }
          });
          resolve(reader.result);
        };
        reader.readAsDataURL(blob);
      });
    } catch (e) {
      setPageError(e?.message ?? 'Card generation failed');
      return null;
    }
  }

  async function handleGenerateWeekly() {
    setGenerating(true);
    setPageError('');
    try {
      const res = await authFetch(`${API_URL}/api/admin/social-posts/generate-weekly`, {
        method: 'POST',
      });
      if (!res.ok) throw new Error(`Weekly generate failed (${res.status})`);
      setSuccess('Weekly digest post created. Check Pending Approval.');
      if (activeTab === 'pending_approval') loadPosts('pending_approval');
    } catch (e) {
      setPageError(e?.message ?? 'Weekly generate failed');
    } finally {
      setGenerating(false);
    }
  }

  // ── Render ───────────────────────────────────────────────────────────────

  if (!loading && !isAdmin) {
    return (
      <main className="min-h-screen flex items-center justify-center">
        <p className="text-gray-500">Access denied</p>
      </main>
    );
  }

  const tabs = Object.keys(STATUS_LABELS);

  return (
    <main className="max-w-3xl mx-auto px-4 py-8">
      <div className="flex flex-wrap items-center justify-between gap-3 mb-6">
        <h1 className="text-2xl font-bold text-gray-900">Social Posts</h1>
        <div className="flex gap-2">
          <button
            className="px-4 py-2 text-sm font-medium rounded-xl bg-indigo-600 text-white hover:bg-indigo-700 disabled:opacity-50"
            disabled={generating}
            onClick={handleGenerate}
          >
            {generating ? 'Generating…' : 'Generate Daily Posts'}
          </button>
          <button
            className="px-4 py-2 text-sm font-medium rounded-xl border border-indigo-300 text-indigo-700 hover:bg-indigo-50 disabled:opacity-50"
            disabled={generating}
            onClick={handleGenerateWeekly}
          >
            Weekly Digest
          </button>
        </div>
      </div>

      <section className="mb-6 p-4 rounded-2xl border border-gray-200 bg-gray-50">
        <h2 className="text-sm font-semibold text-gray-700 mb-3">Generate Options</h2>
        <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
          <label className="text-xs text-gray-600">
            Post Count
            <input
              type="number"
              min="1"
              max="10"
              value={genCount}
              onChange={e => setGenCount(e.target.value)}
              className="mt-1 w-full rounded-lg border border-gray-300 px-2 py-1.5 text-sm"
            />
          </label>
          <label className="text-xs text-gray-600">
            Max Posts Per Product Per Day
            <input
              type="number"
              min="1"
              max="10"
              value={maxPerProduct}
              onChange={e => setMaxPerProduct(e.target.value)}
              className="mt-1 w-full rounded-lg border border-gray-300 px-2 py-1.5 text-sm"
            />
          </label>
          <div className="text-xs text-gray-600 sm:col-span-2">
            <div className="flex items-center justify-between gap-2">
              <span>Only These Deals</span>
              <button type="button" className="text-xs px-2 py-1 rounded border border-gray-300 bg-white" onClick={() => openPicker('deal', 'dealIds')}>Select Deals</button>
            </div>
            <div className="mt-2 flex flex-wrap gap-1">
              {dealIds.map(id => <SelectionPill key={`deal-${id}`} text={dealLookup[id] ?? `Deal #${id}`} onRemove={() => setDealIds(dealIds.filter(x => x !== id))} />)}
            </div>
          </div>

          <div className="text-xs text-gray-600 sm:col-span-2">
            <div className="flex items-center justify-between gap-2">
              <span>Only These Products</span>
              <button type="button" className="text-xs px-2 py-1 rounded border border-gray-300 bg-white" onClick={() => openPicker('product', 'productIds')}>Select Products</button>
            </div>
            <div className="mt-2 flex flex-wrap gap-1">
              {productIds.map(id => <SelectionPill key={`prod-${id}`} text={productLookup[id] ?? `Product #${id}`} onRemove={() => setProductIds(productIds.filter(x => x !== id))} />)}
            </div>
          </div>

          <div className="text-xs text-gray-600 sm:col-span-2">
            <div className="flex items-center justify-between gap-2">
              <span>Priority Deals</span>
              <button type="button" className="text-xs px-2 py-1 rounded border border-gray-300 bg-white" onClick={() => openPicker('deal', 'priorityDealIds')}>Select Priority Deals</button>
            </div>
            <div className="mt-2 flex flex-wrap gap-1">
              {priorityDealIds.map(id => <SelectionPill key={`pdeal-${id}`} text={dealLookup[id] ?? `Deal #${id}`} onRemove={() => setPriorityDealIds(priorityDealIds.filter(x => x !== id))} />)}
            </div>
          </div>

          <div className="text-xs text-gray-600 sm:col-span-2">
            <div className="flex items-center justify-between gap-2">
              <span>Priority Products</span>
              <button type="button" className="text-xs px-2 py-1 rounded border border-gray-300 bg-white" onClick={() => openPicker('product', 'priorityProductIds')}>Select Priority Products</button>
            </div>
            <div className="mt-2 flex flex-wrap gap-1">
              {priorityProductIds.map(id => <SelectionPill key={`pprod-${id}`} text={productLookup[id] ?? `Product #${id}`} onRemove={() => setPriorityProductIds(priorityProductIds.filter(x => x !== id))} />)}
            </div>
          </div>

          <div className="text-xs text-gray-600 sm:col-span-2">
            <div className="flex items-center justify-between gap-2">
              <span>Excluded Deals</span>
              <button type="button" className="text-xs px-2 py-1 rounded border border-gray-300 bg-white" onClick={() => openPicker('deal', 'excludedDealIds')}>Select Excluded Deals</button>
            </div>
            <div className="mt-2 flex flex-wrap gap-1">
              {excludedDealIds.map(id => <SelectionPill key={`edeal-${id}`} text={dealLookup[id] ?? `Deal #${id}`} onRemove={() => setExcludedDealIds(excludedDealIds.filter(x => x !== id))} />)}
            </div>
          </div>

          <div className="text-xs text-gray-600 sm:col-span-2">
            <div className="flex items-center justify-between gap-2">
              <span>Excluded Products</span>
              <button type="button" className="text-xs px-2 py-1 rounded border border-gray-300 bg-white" onClick={() => openPicker('product', 'excludedProductIds')}>Select Excluded Products</button>
            </div>
            <div className="mt-2 flex flex-wrap gap-1">
              {excludedProductIds.map(id => <SelectionPill key={`eprod-${id}`} text={productLookup[id] ?? `Product #${id}`} onRemove={() => setExcludedProductIds(excludedProductIds.filter(x => x !== id))} />)}
            </div>
          </div>
        </div>
      </section>

      {/* Status tabs */}
      <div className="flex gap-1 mb-6 border-b border-gray-200">
        {tabs.map(tab => (
          <button
            key={tab}
            className={`px-4 py-2 text-sm font-medium rounded-t-lg transition-colors ${
              activeTab === tab
                ? 'bg-white border border-b-white border-gray-200 text-blue-700 -mb-px'
                : 'text-gray-500 hover:text-gray-700'
            }`}
            onClick={() => setActiveTab(tab)}
          >
            {STATUS_LABELS[tab]}
          </button>
        ))}
      </div>

      {/* Alerts */}
      {successMsg && (
        <div className="mb-4 px-4 py-3 rounded-xl bg-green-50 border border-green-200 text-green-800 text-sm flex justify-between">
          {successMsg}
          <button className="ml-3 text-green-600 hover:text-green-800 font-bold" onClick={() => setSuccess('')}>×</button>
        </div>
      )}
      {pageError && (
        <div className="mb-4 px-4 py-3 rounded-xl bg-red-50 border border-red-200 text-red-700 text-sm flex justify-between">
          {pageError}
          <button className="ml-3 text-red-500 hover:text-red-700 font-bold" onClick={() => setPageError('')}>×</button>
        </div>
      )}

      {/* Posts list */}
      {loadingPosts ? (
        <div className="py-16 text-center text-gray-400">Loading…</div>
      ) : posts.length === 0 ? (
        <div className="py-16 text-center text-gray-400">
          No {STATUS_LABELS[activeTab]?.toLowerCase()} posts.
          {activeTab === 'pending_approval' && (
            <span> Click <strong>Generate Daily Posts</strong> to create today's posts.</span>
          )}
        </div>
      ) : (
        posts.map(post => (
          <PostCard
            key={post.id}
            post={post}
            onApprove={handleApprove}
            onReject={handleReject}
            onPostNow={handlePostNow}
            onDelete={handleDelete}
            onCaptionEdit={handleCaptionEdit}
            onCaptionSelect={handleCaptionSelect}
            onCopy={handleCopy}
            onGenerateCard={handleGenerateCard}
          />
        ))
      )}

      <IdPickerModal
        open={pickerConfig.open}
        title={isDealPicker ? 'Select Deals' : 'Select Products'}
        endpoint={pickerEndpoint}
        selectedIds={getSelectedIdsForTarget(pickerConfig.target)}
        authFetch={authFetch}
        onClose={closePicker}
        onApply={(ids) => applySelectedIds(pickerConfig.target, ids)}
        mapId={(row) => isDealPicker ? Number(row.dealId) : Number(row.id)}
        renderLabel={(row) => isDealPicker
          ? `Deal #${row.dealId} • ${row.productName || `Product #${row.productId}`} • $${Number(row.price || 0).toFixed(2)}${Number(row.discountPercent) > 0 ? ` • ${row.discountPercent}% off` : ''}`
          : `Product #${row.id} • ${row.name || '(unnamed)'}${row.slug ? ` • ${row.slug}` : ''}`
        }
        onRowsLoaded={handlePickerRowsLoaded}
      />
    </main>
  );
}
