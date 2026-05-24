import React, { useEffect, useState, useRef, useCallback } from 'react';
import { useAuth } from '../context/AuthContext';
import StoreScanEndpointsEditor from './StoreScanEndpointsEditor';

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

/* ─── Source-viewer toggle: collapsible <pre> showing the HTML the server
       actually received, plus a search box and a "find selector" hit count.
       Lets admins distinguish "my selectors are wrong" from "the server got
       a bot-block / SPA shell / wrong page back" without leaving the modal. ─── */
function SourceViewer({ html, htmlTruncated, htmlLength }) {
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');
  if (!html) return null;
  const matches = query.trim() ? (html.match(new RegExp(escapeRegExp(query.trim()), 'gi')) || []).length : null;
  return (
    <div className="mt-2 border-t border-current/10 pt-2">
      <button
        type="button"
        onClick={() => setOpen(o => !o)}
        className="text-[11px] underline opacity-70 hover:opacity-100"
      >
        {open ? 'Hide' : 'View'} page source
        {htmlLength != null && <span className="ml-1 opacity-60">({(htmlLength / 1024).toFixed(1)} KB{htmlTruncated ? ', truncated' : ''})</span>}
      </button>
      {open && (
        <div className="mt-1">
          <div className="flex items-center gap-2 mb-1">
            <input
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              placeholder="Find in source (e.g. class name, tag, selector token)…"
              className="flex-1 px-2 py-1 text-[11px] border rounded font-mono bg-white text-gray-900"
            />
            {matches != null && (
              <span className="text-[11px] opacity-70">{matches} match{matches === 1 ? '' : 'es'}</span>
            )}
          </div>
          <pre className="text-[10px] leading-tight bg-gray-900 text-gray-100 rounded p-2 max-h-72 overflow-auto whitespace-pre-wrap break-all">
            {html}
          </pre>
          {htmlTruncated && (
            <div className="text-[10px] opacity-60 mt-1">Showing the first {(html.length / 1024).toFixed(0)} KB of {(htmlLength / 1024).toFixed(0)} KB.</div>
          )}
        </div>
      )}
    </div>
  );
}

function escapeRegExp(s) {
  return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/* ─── Merge a partial scrape-config (price_selectors OR listing_selectors)
       into the existing JSON string non-destructively. Returns the merged
       JSON string. Falls back to the partial alone if the existing is blank
       or unparseable. ─── */
function mergeScrapeConfig(existingJson, partialJson) {
  let partial;
  try { partial = JSON.parse(partialJson); }
  catch { return partialJson; }

  let existing = {};
  if (existingJson && existingJson.trim()) {
    try { existing = JSON.parse(existingJson); } catch { existing = {}; }
  }

  const merged = { ...existing };
  if (partial && typeof partial === 'object') {
    if (Array.isArray(partial.price_selectors)) merged.price_selectors = partial.price_selectors;
    if (partial.listing_selectors && typeof partial.listing_selectors === 'object') {
      merged.listing_selectors = { ...(existing.listing_selectors || {}), ...partial.listing_selectors };
    }
  }
  return JSON.stringify(merged, null, 2);
}

/* ─── Listing-mode result panel ─── */
function ListingResultPanel({ result }) {
  if (!result) return null;
  const ok = result.success;
  const samples = Array.isArray(result.listings) ? result.listings : [];
  return (
    <div className={`text-xs rounded-md p-2 border ${
      ok ? 'bg-emerald-50 border-emerald-200 text-emerald-800'
         : 'bg-red-50 border-red-200 text-red-800'
    }`}>
      <div className="font-semibold text-sm mb-1">
        {ok ? '✅ ' : '❌ '}
        {result.containerCount != null
          ? `${result.containerCount} container${result.containerCount === 1 ? '' : 's'} matched`
          : (result.error || 'Failed')}
        {result.htmlLength != null && (
          <span className="ml-2 text-xs font-normal text-gray-500">HTML: {(result.htmlLength / 1024).toFixed(1)} KB</span>
        )}
      </div>
      {!ok && result.error && (
        <div className="mb-1">
          {result.blockedByBotProtection && <span className="font-semibold">{'🛡️'} Bot Protection: </span>}
          {result.error}
        </div>
      )}
      {samples.length > 0 && (
        <details className="mt-1" open={ok && samples.length <= 5}>
          <summary className="cursor-pointer">
            Sample of {samples.length} listing{samples.length !== 1 ? 's' : ''}
          </summary>
          <ul className="mt-1 space-y-1">
            {samples.map((s, i) => (
              <li key={i} className="border-t border-current/10 pt-1">
                <div className="font-medium truncate">{s.title || <span className="opacity-50">(no title)</span>}</div>
                <div className="flex items-center gap-2 text-[11px]">
                  {s.price != null ? (
                    <span className="font-mono">
                      {s.currency === 'USD' ? '$' : ''}{s.price.toFixed(2)}
                    </span>
                  ) : (
                    <span className="opacity-50">(no price)</span>
                  )}
                  {s.conditionText && <span className="opacity-70">{s.conditionText}</span>}
                </div>
                {s.url && (
                  <div className="font-mono opacity-60 break-all">{s.url}</div>
                )}
              </li>
            ))}
          </ul>
        </details>
      )}
      <SourceViewer html={result.html} htmlTruncated={result.htmlTruncated} htmlLength={result.htmlLength} />
    </div>
  );
}

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
      {/* Source toggle is rendered outside the ok/error branches so admins can
          inspect what the server actually fetched in either case \u2014 distinguishes
          "my selectors are wrong" from "the server got a different page back". */}
      <SourceViewer html={result.html} htmlTruncated={result.htmlTruncated} htmlLength={result.htmlLength} />
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

  // Two URL slots: one for product-page tests (price selectors), one for
  // listing-page tests (listing selectors). They're separate URLs by nature so
  // we don't share state — switching between them shouldn't wipe results.
  const [testScrapeUrl, setTestScrapeUrl] = useState('');
  const [testListingUrl, setTestListingUrl] = useState('');

  const [testHttpLoading, setTestHttpLoading] = useState(false);
  const [testHttpResult, setTestHttpResult] = useState(null);
  const [testPlaywrightLoading, setTestPlaywrightLoading] = useState(false);
  const [testPlaywrightResult, setTestPlaywrightResult] = useState(null);
  const [testBrowserLoading, setTestBrowserLoading] = useState(false);
  const [testBrowserResult, setTestBrowserResult] = useState(null);

  // Listing-mode test results (HTTP + Playwright only; the extension flow is
  // single-product so it doesn't apply here).
  const [testListingHttpLoading, setTestListingHttpLoading] = useState(false);
  const [testListingHttpResult, setTestListingHttpResult] = useState(null);
  const [testListingPlaywrightLoading, setTestListingPlaywrightLoading] = useState(false);
  const [testListingPlaywrightResult, setTestListingPlaywrightResult] = useState(null);

  const [autoGenLoading, setAutoGenLoading] = useState(false);
  const [autoGenError, setAutoGenError] = useState(null);
  const [autoGenListingLoading, setAutoGenListingLoading] = useState(false);
  const [autoGenListingError, setAutoGenListingError] = useState(null);

  const [slugEdited, setSlugEdited] = useState(false);

  const [draft, setDraft] = useState({
    name: '',
    url: '',
    slug: '',
    approved: true,
    description: '',

    affiliateCode: '',
    affiliateCodeVar: '',
    affiliateUrlTemplate: '',
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
  const [storeImageZoom, setStoreImageZoom] = useState(1);
  const storeCanvasRef = useRef(null);
  const storeImgRef = useRef(null);

  const PREVIEW_SIZE = 128;

  const drawStorePreview = useCallback(() => {
    const canvas = storeCanvasRef.current;
    const img = storeImgRef.current;
    if (!canvas || !img || !img.naturalWidth) return;
    const dpr = window.devicePixelRatio || 1;
    const pxSize = PREVIEW_SIZE * dpr;
    canvas.width = pxSize;
    canvas.height = pxSize;
    const ctx = canvas.getContext('2d');
    ctx.clearRect(0, 0, pxSize, pxSize);
    ctx.imageSmoothingEnabled = true;
    ctx.imageSmoothingQuality = 'high';

    const scale = storeImageZoom;
    const sw = img.naturalWidth / scale;
    const sh = img.naturalHeight / scale;
    const sx = (img.naturalWidth - sw) / 2;
    const sy = (img.naturalHeight - sh) / 2;
    ctx.drawImage(img, sx, sy, sw, sh, 0, 0, pxSize, pxSize);
  }, [storeImageZoom]);

  useEffect(() => { drawStorePreview(); }, [drawStorePreview]);

  const exportStoreZoomedBlob = () =>
    new Promise((resolve) => {
      const img = storeImgRef.current;
      if (!img || !img.naturalWidth) return resolve(null);
      const EXPORT_SIZE = 512;
      const offscreen = document.createElement('canvas');
      offscreen.width = EXPORT_SIZE;
      offscreen.height = EXPORT_SIZE;
      const ctx = offscreen.getContext('2d');
      ctx.clearRect(0, 0, EXPORT_SIZE, EXPORT_SIZE);
      ctx.imageSmoothingEnabled = true;
      ctx.imageSmoothingQuality = 'high';
      const scale = storeImageZoom;
      const sw = img.naturalWidth / scale;
      const sh = img.naturalHeight / scale;
      const sx = (img.naturalWidth - sw) / 2;
      const sy = (img.naturalHeight - sh) / 2;
      ctx.drawImage(img, sx, sy, sw, sh, 0, 0, EXPORT_SIZE, EXPORT_SIZE);
      offscreen.toBlob((blob) => resolve(blob), 'image/png');
    });

  const hasStoreImageEdit = !!(selectedFile || (previewUrl && previewUrl !== storeImageUrl) || (storeImageUrlInput || '').trim());

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
    setStoreImageZoom(1);
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
      affiliateUrlTemplate: s?.affiliateUrlTemplate ?? '',
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
    setStoreImageZoom(1);
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

    const useZoomed = storeImageZoom !== 1 && storeCanvasRef.current && (selectedFile || (storeImageUrlInput || '').trim() || previewUrl);

    if (useZoomed) {
      const zoomedBlob = await exportStoreZoomedBlob();
      if (zoomedBlob) {
        const fd = new FormData();
        fd.append('file', zoomedBlob, 'image.png');
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
          try { URL.revokeObjectURL(previewUrl); } catch {}
        }
        setPreviewUrl('');
        setStoreImageUrlInput('');
        setStoreImageZoom(1);
        return url;
      }
      // Canvas was tainted — fall through
    }

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
      setStoreImageZoom(1);
      return url;
    }

    if (selectedFile) {
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
        try { URL.revokeObjectURL(previewUrl); } catch {}
      }
      setPreviewUrl('');
      setStoreImageZoom(1);
      return url;
    }

    return null;
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
      if (addNewBrand && name.trim()) {
        const brandRes = await authFetch(`${API_URL}/api/brands`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ name: name.trim(), url: (draft.url || '').trim() || null })
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
      }

      const body = {
        name,
        url: (draft.url || '').trim() || null,
        slug: (draft.slug || '').trim() || null,
        approved: !!draft.approved,
        description: draft.description || null,

        affiliateCode: (draft.affiliateCode || '').trim() || null,
        affiliateCodeVar: (draft.affiliateCodeVar || '').trim() || null,
        affiliateUrlTemplate: (draft.affiliateUrlTemplate || '').trim() || null,
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
                  <div className="flex flex-col items-center gap-2">
                    <div className="relative" style={{ width: PREVIEW_SIZE, height: PREVIEW_SIZE }}>
                      <img
                        ref={storeImgRef}
                        src={imageSrc}
                        alt=""
                        crossOrigin="anonymous"
                        className="hidden"
                        onLoad={drawStorePreview}
                      />
                      <canvas
                        ref={storeCanvasRef}
                        width={PREVIEW_SIZE}
                        height={PREVIEW_SIZE}
                        className="w-full h-full rounded-lg border cursor-pointer"
                        style={{ background: 'repeating-conic-gradient(#d1d5db 0% 25%, transparent 0% 50%) 0 0 / 16px 16px' }}
                        onClick={() => document.getElementById('storeImageInput')?.click()}
                      />
                      <div
                        className="absolute inset-0 bg-black bg-opacity-50 rounded-lg flex items-center justify-center opacity-0 hover:opacity-100 transition-opacity cursor-pointer"
                        onClick={() => document.getElementById('storeImageInput')?.click()}
                      >
                        <span className="text-white text-sm font-medium">Change</span>
                      </div>
                    </div>
                    {hasStoreImageEdit && (
                      <div className="flex items-center gap-2 w-full" style={{ maxWidth: PREVIEW_SIZE }}>
                        <span className="text-xs text-gray-500 select-none">−</span>
                        <input
                          type="range"
                          min="0.5"
                          max="3"
                          step="0.05"
                          value={storeImageZoom}
                          onChange={(e) => setStoreImageZoom(parseFloat(e.target.value))}
                          className="flex-1 h-1 accent-blue-600"
                          disabled={saving}
                        />
                        <span className="text-xs text-gray-500 select-none">+</span>
                      </div>
                    )}
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
                        setStoreImageZoom(1);
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
                              setStoreImageZoom(1);
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
                  <label className="block text-sm font-medium text-gray-700 mb-1">Affiliate URL Template (optional)</label>
                  <input
                    value={draft.affiliateUrlTemplate}
                    onChange={(e) => setDraft((p) => ({ ...p, affiliateUrlTemplate: e.target.value }))}
                    className="w-full px-3 py-2 border rounded-md text-sm"
                    placeholder="e.g. ?tag=abc&ref=123  or  https://www.awin1.com/cread.php?awinmid=123&awinaffid=456&ued={url_encoded}"
                    disabled={saving}
                  />
                  <p className="text-xs text-gray-500 mt-1">
                    Wrapper URL: use <code>{'{url}'}</code> / <code>{'{url_encoded}'}</code> placeholders.
                    Extra params: start with <code>?</code> (e.g. <code>?tag=abc&ref=123</code>) to append to the product URL. Overrides Code/Var when set.
                  </p>
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
                    <p className="text-xs text-gray-500 mt-1">Brand will be created using the store name &amp; URL above.</p>
                  )}
                  <label className="mt-1.5 flex items-center gap-2 text-xs text-gray-600 cursor-pointer">
                    <input
                      type="checkbox"
                      checked={addNewBrand}
                      onChange={(e) => setAddNewBrand(e.target.checked)}
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

                  {/* Auto Generate Scrape Config via AI — two independent
                      generators that merge into the existing config without
                      overwriting each other. */}
                  {draft.scrapeModeId !== '0' && (
                    <div className="mt-2 p-3 bg-amber-50 border border-amber-200 rounded-md space-y-3">
                      <div className="text-xs font-medium text-gray-700">Auto Generate Scrape Config (AI)</div>

                      {/* ── Generate price_selectors from a product page ── */}
                      <div className="bg-white/50 rounded p-2 border border-amber-200/60">
                        <div className="text-[11px] font-semibold text-gray-700 mb-1">Price Selectors <span className="font-normal text-gray-500">(from a product page URL)</span></div>
                        <div className="flex items-center gap-2">
                          <input
                            value={testScrapeUrl}
                            onChange={(e) => { setTestScrapeUrl(e.target.value); setTestBrowserResult(null); }}
                            className="flex-1 px-2 py-1.5 border rounded-md text-xs font-mono"
                            placeholder="https://store.com/products/specific-item"
                            disabled={saving || autoGenLoading}
                          />
                          <button
                            type="button"
                            disabled={saving || autoGenLoading || !testScrapeUrl.trim()}
                            onClick={async () => {
                              setAutoGenLoading(true);
                              setAutoGenError(null);
                              try {
                                const fetchMethod = draft.scrapeHttpEnabled ? 'http' : 'playwright';
                                const res = await authFetch(`${API_URL}/api/stores/admin/auto-generate-scrape-config`, {
                                  method: 'POST',
                                  headers: { 'Content-Type': 'application/json' },
                                  body: JSON.stringify({ url: testScrapeUrl.trim(), method: fetchMethod, mode: 'price' })
                                });
                                if (!res.ok) throw new Error(`Server returned ${res.status}`);
                                const data = await res.json();
                                if (data.success && data.scrapeConfig) {
                                  setDraft((p) => ({ ...p, scrapeConfig: mergeScrapeConfig(p.scrapeConfig, data.scrapeConfig) }));
                                } else {
                                  setAutoGenError(data.error || 'Failed to generate price selectors.');
                                }
                              } catch (e) {
                                setAutoGenError(e.message || 'Request failed');
                              } finally {
                                setAutoGenLoading(false);
                              }
                            }}
                            className="px-3 py-1.5 text-xs bg-amber-600 text-white rounded hover:bg-amber-700 disabled:opacity-50 whitespace-nowrap"
                          >
                            {autoGenLoading ? 'Generating…' : 'Generate Price'}
                          </button>
                        </div>
                        {autoGenError && (
                          <div className="mt-1 text-xs text-red-600 bg-red-50 rounded px-2 py-1">{autoGenError}</div>
                        )}
                      </div>

                      {/* ── Generate listing_selectors from a listing/category page ── */}
                      <div className="bg-white/50 rounded p-2 border border-amber-200/60">
                        <div className="text-[11px] font-semibold text-gray-700 mb-1">Listing Selectors <span className="font-normal text-gray-500">(from a category/listing page URL)</span></div>
                        <div className="flex items-center gap-2">
                          <input
                            value={testListingUrl}
                            onChange={(e) => setTestListingUrl(e.target.value)}
                            className="flex-1 px-2 py-1.5 border rounded-md text-xs font-mono"
                            placeholder="https://store.com/category/clearance"
                            disabled={saving || autoGenListingLoading}
                          />
                          <button
                            type="button"
                            disabled={saving || autoGenListingLoading || !testListingUrl.trim()}
                            onClick={async () => {
                              setAutoGenListingLoading(true);
                              setAutoGenListingError(null);
                              try {
                                const fetchMethod = draft.scrapeHttpEnabled ? 'http' : 'playwright';
                                const res = await authFetch(`${API_URL}/api/stores/admin/auto-generate-scrape-config`, {
                                  method: 'POST',
                                  headers: { 'Content-Type': 'application/json' },
                                  body: JSON.stringify({ url: testListingUrl.trim(), method: fetchMethod, mode: 'listing' })
                                });
                                if (!res.ok) throw new Error(`Server returned ${res.status}`);
                                const data = await res.json();
                                if (data.success && data.scrapeConfig) {
                                  setDraft((p) => ({ ...p, scrapeConfig: mergeScrapeConfig(p.scrapeConfig, data.scrapeConfig) }));
                                } else {
                                  setAutoGenListingError(data.error || 'Failed to generate listing selectors.');
                                }
                              } catch (e) {
                                setAutoGenListingError(e.message || 'Request failed');
                              } finally {
                                setAutoGenListingLoading(false);
                              }
                            }}
                            className="px-3 py-1.5 text-xs bg-amber-600 text-white rounded hover:bg-amber-700 disabled:opacity-50 whitespace-nowrap"
                          >
                            {autoGenListingLoading ? 'Generating…' : 'Generate Listing'}
                          </button>
                        </div>
                        {autoGenListingError && (
                          <div className="mt-1 text-xs text-red-600 bg-red-50 rounded px-2 py-1">{autoGenListingError}</div>
                        )}
                      </div>
                    </div>
                  )}

                  {/* Test Price Selectors — multi-method diagnostic against a product page */}
                  {draft.scrapeConfig && draft.scrapeModeId !== '0' && (
                    <div className="mt-2 p-3 bg-slate-50 border border-slate-200 rounded-md space-y-3">
                      <div className="text-xs font-medium text-gray-700">Test Price Selectors <span className="font-normal text-gray-500">(against a product page)</span></div>

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

                  {/* Test Listing Selectors — diagnoses listing_selectors against a category/listing page */}
                  {draft.scrapeConfig && draft.scrapeModeId !== '0' && (
                    <div className="mt-2 p-3 bg-slate-50 border border-slate-200 rounded-md space-y-3">
                      <div className="text-xs font-medium text-gray-700">Test Listing Selectors <span className="font-normal text-gray-500">(against a category/listing page)</span></div>

                      <input
                        value={testListingUrl}
                        onChange={(e) => setTestListingUrl(e.target.value)}
                        className="w-full px-2 py-1.5 border rounded-md text-xs font-mono"
                        placeholder="https://store.com/category/clearance"
                        disabled={saving}
                      />

                      {/* Simple GET */}
                      <div className="border border-slate-200 rounded-md p-2.5 bg-white">
                        <div className="flex items-center justify-between mb-1.5">
                          <div className="text-xs font-semibold text-gray-700">Simple GET <span className="font-normal text-gray-500">(HttpClient)</span></div>
                          <button
                            type="button"
                            disabled={saving || testListingHttpLoading || !testListingUrl.trim() || !draft.scrapeConfig.trim()}
                            onClick={async () => {
                              setTestListingHttpLoading(true);
                              setTestListingHttpResult(null);
                              try {
                                const res = await authFetch(`${API_URL}/api/stores/admin/test-scrape`, {
                                  method: 'POST',
                                  headers: { 'Content-Type': 'application/json' },
                                  body: JSON.stringify({ url: testListingUrl.trim(), scrapeConfig: draft.scrapeConfig.trim(), method: 'http', mode: 'listing' })
                                });
                                if (!res.ok) throw new Error(`Server returned ${res.status}`);
                                const data = await res.json();
                                setTestListingHttpResult(data);
                              } catch (e) {
                                setTestListingHttpResult({ success: false, error: e.message || 'Request failed' });
                              } finally {
                                setTestListingHttpLoading(false);
                              }
                            }}
                            className="px-2.5 py-1 text-xs bg-emerald-600 text-white rounded hover:bg-emerald-700 disabled:opacity-50 whitespace-nowrap"
                          >
                            {testListingHttpLoading ? 'Testing…' : 'Run'}
                          </button>
                        </div>
                        <p className="text-[10px] text-gray-500 mb-1.5">Raw HTTP fetch — no JavaScript. Fastest, but JS-rendered listings will look empty.</p>
                        {testListingHttpResult && <ListingResultPanel result={testListingHttpResult} />}
                      </div>

                      {/* Playwright */}
                      <div className="border border-slate-200 rounded-md p-2.5 bg-white">
                        <div className="flex items-center justify-between mb-1.5">
                          <div className="text-xs font-semibold text-gray-700">Playwright <span className="font-normal text-gray-500">(Headless Browser)</span></div>
                          <button
                            type="button"
                            disabled={saving || testListingPlaywrightLoading || !testListingUrl.trim() || !draft.scrapeConfig.trim()}
                            onClick={async () => {
                              setTestListingPlaywrightLoading(true);
                              setTestListingPlaywrightResult(null);
                              try {
                                const res = await authFetch(`${API_URL}/api/stores/admin/test-scrape`, {
                                  method: 'POST',
                                  headers: { 'Content-Type': 'application/json' },
                                  body: JSON.stringify({ url: testListingUrl.trim(), scrapeConfig: draft.scrapeConfig.trim(), method: 'playwright', mode: 'listing' })
                                });
                                if (!res.ok) throw new Error(`Server returned ${res.status}`);
                                const data = await res.json();
                                setTestListingPlaywrightResult(data);
                              } catch (e) {
                                setTestListingPlaywrightResult({ success: false, error: e.message || 'Request failed' });
                              } finally {
                                setTestListingPlaywrightLoading(false);
                              }
                            }}
                            className="px-2.5 py-1 text-xs bg-indigo-600 text-white rounded hover:bg-indigo-700 disabled:opacity-50 whitespace-nowrap"
                          >
                            {testListingPlaywrightLoading ? 'Testing…' : 'Run'}
                          </button>
                        </div>
                        <p className="text-[10px] text-gray-500 mb-1.5">Headless Chromium — executes JavaScript. Matches what the discovery crawler uses.</p>
                        {testListingPlaywrightResult && <ListingResultPanel result={testListingPlaywrightResult} />}
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

              {/* Discovery-crawler scan endpoints — only edit-mode (need a saved store id) */}
              {internalMode === 'edit' && storeId && (
                <div className="mt-6">
                  <StoreScanEndpointsEditor storeId={storeId} />
                </div>
              )}

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
