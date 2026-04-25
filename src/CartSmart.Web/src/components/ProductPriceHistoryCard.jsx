import React, { useRef, useState } from 'react';

const SERIES_META = {
  new: {
    label: 'New',
    stroke: '#0f766e',
    fill: 'rgba(15, 118, 110, 0.12)',
    badge: 'bg-teal-50 text-teal-800 border-teal-200'
  },
  used: {
    label: 'Used',
    stroke: '#b45309',
    fill: 'rgba(180, 83, 9, 0.12)',
    badge: 'bg-amber-50 text-amber-800 border-amber-200'
  }
};

const moneyFormatter = new Intl.NumberFormat('en-US', {
  style: 'currency',
  currency: 'USD',
  minimumFractionDigits: 0,
  maximumFractionDigits: 2
});

const dateFormatter = new Intl.DateTimeFormat('en-US', {
  month: 'short',
  day: 'numeric'
});

const formatMoney = (value) => {
  if (value == null || Number.isNaN(Number(value))) return '--';
  return moneyFormatter.format(Number(value));
};

const formatDate = (value) => {
  if (!value) return '';
  const date = value instanceof Date ? value : new Date(value);
  if (Number.isNaN(date.getTime())) return '';
  return dateFormatter.format(date);
};

const clamp = (value, min, max) => Math.min(max, Math.max(min, value));

const getDealQuality = (currentPrice, points, msrp = null) => {
  const prices = points.map((point) => point.price).filter(Number.isFinite);
  if (!Number.isFinite(currentPrice) || prices.length === 0) {
    return {
      label: 'Unknown',
      toneClass: 'bg-slate-100 text-slate-600',
      barClass: 'bg-slate-400',
      score: 0,
      low: null,
      high: null,
      aboveLowPct: null,
      detailText: 'Not enough history yet',
      atLow: false
    };
  }

  const low = Math.min(...prices);
  const high = Math.max(...prices);
  const range = high - low;
  const baseScore = range <= 0 ? 1 : clamp((high - currentPrice) / range, 0, 1);
  const aboveLowPct = low > 0 ? ((currentPrice - low) / low) * 100 : null;
  const hasMsrp = Number.isFinite(msrp) && msrp > 0;
  const msrpValue = hasMsrp ? Number(msrp) : null;
  const msrpDeltaPct = hasMsrp ? ((msrpValue - currentPrice) / msrpValue) * 100 : null;

  // Treat tiny floating-point drift as equal to low.
  const atLowTolerance = Math.max(Math.abs(low) * 0.0001, 0.0001);
  // Currency-aware tolerance: per-item math and historical rounding can differ by a cent.
  const msrpTolerance = hasMsrp ? Math.max(Math.abs(msrpValue) * 0.001, 0.01) : 0;
  const atMsrp = hasMsrp ? Math.abs(currentPrice - msrpValue) <= msrpTolerance : false;
  const atLow = Math.abs(currentPrice - low) <= atLowTolerance;

  if (hasMsrp && atMsrp) {
    return {
      label: 'Regular price',
      toneClass: 'bg-slate-100 text-slate-700',
      barClass: 'bg-slate-600',
      // Equal to MSRP should not visually imply a deal.
      score: 0,
      low,
      high,
      aboveLowPct,
      detailText: atLow ? 'At all-time low, but this matches MSRP' : 'At regular MSRP',
      atLow
    };
  }

  if (hasMsrp && currentPrice > msrpValue + msrpTolerance) {
    return {
      label: 'Above MSRP',
      toneClass: 'bg-rose-100 text-rose-800',
      barClass: 'bg-rose-600',
      score: Math.min(baseScore, 0.2),
      low,
      high,
      aboveLowPct,
      detailText: `${Math.abs(msrpDeltaPct).toFixed(1)}% above MSRP`,
      atLow
    };
  }

  if (Math.abs(currentPrice - low) <= atLowTolerance) {
    return {
      label: 'At all-time low',
      toneClass: 'bg-emerald-100 text-emerald-800',
      barClass: 'bg-emerald-600',
      score: 1,
      low,
      high,
      aboveLowPct,
      detailText: hasMsrp && msrpDeltaPct != null && msrpDeltaPct > 0
        ? `${msrpDeltaPct.toFixed(1)}% below MSRP`
        : 'At all-time low',
      atLow: true
    };
  }

  if (aboveLowPct != null && aboveLowPct <= 2) {
    const nearLowScore = hasMsrp && msrpDeltaPct != null && msrpDeltaPct <= 0 ? 0.45 : 0.85;
    return {
      label: 'Near all-time low',
      toneClass: 'bg-emerald-100 text-emerald-800',
      barClass: 'bg-emerald-500',
      score: nearLowScore,
      low,
      high,
      aboveLowPct,
      detailText: hasMsrp && msrpDeltaPct != null
        ? (msrpDeltaPct > 0 ? `${msrpDeltaPct.toFixed(1)}% below MSRP` : `${Math.abs(msrpDeltaPct).toFixed(1)}% above MSRP`)
        : `${Math.max(0, aboveLowPct).toFixed(1)}% above all-time low`,
      atLow: false
    };
  }

  if (baseScore >= 0.75) {
    return {
      label: 'Great',
      toneClass: 'bg-emerald-100 text-emerald-800',
      barClass: 'bg-emerald-500',
      score: baseScore,
      low,
      high,
      aboveLowPct,
      detailText: hasMsrp && msrpDeltaPct != null
        ? (msrpDeltaPct > 0 ? `${msrpDeltaPct.toFixed(1)}% below MSRP` : `${Math.abs(msrpDeltaPct).toFixed(1)}% above MSRP`)
        : `${Math.max(0, aboveLowPct ?? 0).toFixed(1)}% above all-time low`,
      atLow: false
    };
  }

  if (baseScore >= 0.5) {
    return {
      label: 'Good',
      toneClass: 'bg-teal-100 text-teal-800',
      barClass: 'bg-teal-500',
      score: baseScore,
      low,
      high,
      aboveLowPct,
      detailText: hasMsrp && msrpDeltaPct != null
        ? (msrpDeltaPct > 0 ? `${msrpDeltaPct.toFixed(1)}% below MSRP` : `${Math.abs(msrpDeltaPct).toFixed(1)}% above MSRP`)
        : `${Math.max(0, aboveLowPct ?? 0).toFixed(1)}% above all-time low`,
      atLow: false
    };
  }

  if (baseScore >= 0.3) {
    return {
      label: 'Average',
      toneClass: 'bg-amber-100 text-amber-800',
      barClass: 'bg-amber-500',
      score: baseScore,
      low,
      high,
      aboveLowPct,
      detailText: hasMsrp && msrpDeltaPct != null
        ? (msrpDeltaPct > 0 ? `${msrpDeltaPct.toFixed(1)}% below MSRP` : `${Math.abs(msrpDeltaPct).toFixed(1)}% above MSRP`)
        : `${Math.max(0, aboveLowPct ?? 0).toFixed(1)}% above all-time low`,
      atLow: false
    };
  }

  return {
    label: 'Pricey',
    toneClass: 'bg-rose-100 text-rose-800',
    barClass: 'bg-rose-600',
    score: baseScore,
    low,
    high,
    aboveLowPct,
    detailText: hasMsrp && msrpDeltaPct != null
      ? (msrpDeltaPct > 0 ? `${msrpDeltaPct.toFixed(1)}% below MSRP` : `${Math.abs(msrpDeltaPct).toFixed(1)}% above MSRP`)
      : `${Math.max(0, aboveLowPct ?? 0).toFixed(1)}% above all-time low`,
    atLow: false
  };
};

const buildLinePath = (points, xFor, yFor) => {
  if (!Array.isArray(points) || points.length === 0) return '';
  return points
    .map((point, index) => `${index === 0 ? 'M' : 'L'} ${xFor(point, index)} ${yFor(point.price)}`)
    .join(' ');
};

const buildAreaPath = (points, xFor, yFor, chartBottom) => {
  if (!Array.isArray(points) || points.length === 0) return '';
  const line = buildLinePath(points, xFor, yFor);
  const firstX = xFor(points[0], 0);
  const lastX = xFor(points[points.length - 1], points.length - 1);
  return `${line} L ${lastX} ${chartBottom} L ${firstX} ${chartBottom} Z`;
};

const collapseUnchangedPoints = (points) => {
  if (!Array.isArray(points) || points.length <= 1) return points || [];

  const collapsed = [];
  let lastPrice = null;

  for (const point of points) {
    const normalizedPrice = Number(point.price.toFixed(2));
    if (lastPrice == null || normalizedPrice !== lastPrice) {
      collapsed.push(point);
      lastPrice = normalizedPrice;
    }
  }

  return collapsed;
};

const normalizeSeries = (history) => {
  const rawSeries = history?.series || history?.Series || [];
  if (!Array.isArray(rawSeries)) return [];

  return rawSeries
    .map((series) => {
      const key = (series?.key || series?.Key || '').toString().toLowerCase();
      const rawPoints = series?.points || series?.Points || [];
      const points = Array.isArray(rawPoints)
        ? collapseUnchangedPoints(rawPoints
            .map((point) => ({
              date: point?.date || point?.Date,
              price: Number(point?.price ?? point?.Price)
            }))
            .filter((point) => point.date && Number.isFinite(point.price))
            .sort((left, right) => new Date(left.date) - new Date(right.date)))
        : [];

      return {
        key,
        label: series?.label || series?.Label || SERIES_META[key]?.label || key,
        currentPrice: Number(series?.currentPrice ?? series?.CurrentPrice),
        lowestPrice: Number(series?.lowestPrice ?? series?.LowestPrice),
        points
      };
    })
    .filter((series) => series.points.length > 0 && SERIES_META[series.key]);
};

const ChevronIcon = ({ down = true }) => (
  <svg
    className={`h-4 w-4 transition-transform ${down ? '' : 'rotate-180'}`}
    viewBox="0 0 20 20"
    fill="currentColor"
    aria-hidden="true"
  >
    <path fillRule="evenodd" d="M5.22 8.22a.75.75 0 0 1 1.06 0L10 11.94l3.72-3.72a.75.75 0 1 1 1.06 1.06l-4.25 4.25a.75.75 0 0 1-1.06 0L5.22 9.28a.75.75 0 0 1 0-1.06Z" clipRule="evenodd" />
  </svg>
);

const ProductPriceHistoryCard = ({
  history,
  loading,
  error,
  countEnabled = false,
  defaultCount = 1,
  msrp = null,
  collapsible = false,
  expanded,
  onExpandedChange
}) => {
  const svgRef = useRef(null);
  const [hoveredPoint, setHoveredPoint] = useState(null);
  const [internalExpanded, setInternalExpanded] = useState(!collapsible);
  const isExpanded = collapsible ? (expanded ?? internalExpanded) : true;

  const setExpanded = (next) => {
    if (typeof onExpandedChange === 'function') onExpandedChange(next);
    if (expanded === undefined) setInternalExpanded(next);
  };
  const perItem = countEnabled && defaultCount > 1;
  const toDisplayPrice = (price) => price;
  const normalizedMsrp = Number.isFinite(Number(msrp))
    ? (perItem && defaultCount > 0 ? Number(msrp) / defaultCount : Number(msrp))
    : null;
  const rawSeries = normalizeSeries(history);
  const series = perItem
    ? rawSeries.map((entry) => ({
        ...entry,
        currentPrice: toDisplayPrice(entry.currentPrice),
        lowestPrice: toDisplayPrice(entry.lowestPrice),
        points: entry.points.map((point) => ({ ...point, price: toDisplayPrice(point.price) }))
      }))
    : rawSeries;

  if (loading) {
    if (collapsible) {
      return (
        <div className="mb-6 animate-pulse rounded-xl border border-slate-200 bg-slate-50 px-4 py-3">
          <div className="flex items-center gap-3">
            <div className="h-3 w-24 rounded bg-slate-200" />
            <div className="h-3 w-16 rounded bg-slate-200" />
            <div className="h-3 w-16 rounded bg-slate-200" />
          </div>
        </div>
      );
    }
    return (
      <div className="mt-6 border-t border-slate-100 pt-5">
        <div className="animate-pulse rounded-2xl border border-slate-200 bg-slate-50 p-4">
          <div className="h-4 w-32 rounded bg-slate-200" />
          <div className="mt-2 h-3 w-52 rounded bg-slate-200" />
          <div className="mt-4 h-44 rounded-xl bg-white" />
        </div>
      </div>
    );
  }

  if (error) {
    if (collapsible) return null;
    return (
      <div className="mt-6 border-t border-slate-100 pt-5">
        <div className="rounded-2xl border border-rose-100 bg-rose-50 px-4 py-3 text-sm text-rose-700">
          Price history is unavailable right now.
        </div>
      </div>
    );
  }

  if (series.length === 0) {
    if (collapsible) return null;
    return (
      <div className="mt-6 border-t border-slate-100 pt-5">
        <div className="rounded-2xl border border-slate-200 bg-slate-50 px-4 py-4">
          <div className="text-sm font-semibold text-slate-800">Price history</div>
          <p className="mt-1 text-sm text-slate-500">Price history will appear here as CartSmart tracks more listings for this product.</p>
        </div>
      </div>
    );
  }

  const visibleSeries = series;

  const seriesSummaries = series.map((entry) => {
    const currentPrice = Number.isFinite(entry.currentPrice) ? entry.currentPrice : entry.points[entry.points.length - 1]?.price;
    const lowestPrice = Number.isFinite(entry.lowestPrice)
      ? entry.lowestPrice
      : Math.min(...entry.points.map((point) => point.price));
    const quality = getDealQuality(currentPrice, entry.points, normalizedMsrp);
    return { ...entry, currentPrice, lowestPrice, quality };
  });

  const bestVisibleQuality = seriesSummaries
    .sort((left, right) => right.quality.score - left.quality.score)[0]?.quality;

  if (collapsible && !isExpanded) {
    return (
      <div className="mb-6 overflow-hidden rounded-xl border border-slate-200 bg-slate-50">
        <button
          type="button"
          onClick={() => setExpanded(true)}
          className="flex w-full items-center justify-between px-4 py-3 text-left transition-colors hover:bg-slate-100"
          aria-expanded={false}
          aria-label="Show price history chart"
        >
          <div className="flex min-w-0 flex-wrap items-center gap-x-4 gap-y-1.5">
            <span className="shrink-0 text-sm font-semibold text-slate-700">Price history</span>
            {bestVisibleQuality && (
              <span className={`rounded-full px-2 py-0.5 text-[11px] font-semibold ${bestVisibleQuality.toneClass}`}>
                {bestVisibleQuality.label} right now
              </span>
            )}
            {seriesSummaries.map((entry) => {
              const meta = SERIES_META[entry.key] || SERIES_META.new;
              return (
                <span key={entry.key} className="flex items-center gap-1.5 text-xs">
                  <span className="h-2 w-2 shrink-0 rounded-full" style={{ backgroundColor: meta.stroke }} />
                  <span className="text-slate-400">{entry.label}</span>
                  <span className="font-semibold" style={{ color: meta.stroke }}>{formatMoney(entry.currentPrice)}</span>
                  <span className="text-slate-300">·</span>
                  <span className="text-slate-400">low {formatMoney(entry.lowestPrice)}</span>
                </span>
              );
            })}
          </div>
          <span className="ml-3 flex shrink-0 items-center gap-1 text-xs text-slate-500">
            Show chart
            <ChevronIcon down={true} />
          </span>
        </button>
      </div>
    );
  }

  const allPoints = visibleSeries.flatMap((entry) => entry.points.map((point) => ({ ...point, key: entry.key })));
  const timestamps = allPoints.map((point) => new Date(point.date).getTime()).filter(Number.isFinite);
  const prices = allPoints.map((point) => point.price).filter(Number.isFinite);

  const minTime = Math.min(...timestamps);
  const maxTime = Math.max(...timestamps);
  const rawMinPrice = Math.min(...prices);
  const rawMaxPrice = Math.max(...prices);
  const pricePadding = Math.max((rawMaxPrice - rawMinPrice) * 0.12, rawMaxPrice * 0.04, 1);
  const minPrice = Math.max(0, rawMinPrice - pricePadding);
  const maxPrice = rawMaxPrice + pricePadding;

  const width = 360;
  const height = 188;
  const padding = { top: 14, right: 10, bottom: 24, left: 44 };
  const chartLeft = padding.left;
  const chartRight = width - padding.right;
  const chartTop = padding.top;
  const chartBottom = height - padding.bottom;
  const chartWidth = chartRight - chartLeft;
  const chartHeight = chartBottom - chartTop;

  const xFor = (point, index, total = 0) => {
    const time = new Date(point.date).getTime();
    if (!Number.isFinite(time)) return chartLeft;
    if (maxTime === minTime || total <= 1) return chartLeft + chartWidth / 2;
    return chartLeft + ((time - minTime) / (maxTime - minTime)) * chartWidth;
  };

  const yFor = (price) => {
    if (!Number.isFinite(price) || maxPrice === minPrice) return chartBottom - chartHeight / 2;
    return chartBottom - ((price - minPrice) / (maxPrice - minPrice)) * chartHeight;
  };

  const yTicks = [maxPrice, (maxPrice + minPrice) / 2, minPrice];
  const startLabel = formatDate(history?.startDate || history?.StartDate || allPoints[0]?.date);
  const endLabel = formatDate(history?.endDate || history?.EndDate || allPoints[allPoints.length - 1]?.date);
  const plottedPoints = visibleSeries.flatMap((entry) => {
    const meta = SERIES_META[entry.key] || SERIES_META.new;
    return entry.points.map((point, index) => ({
      key: `${entry.key}-${index}`,
      seriesKey: entry.key,
      seriesLabel: entry.label,
      date: point.date,
      price: point.price,
      stroke: meta.stroke,
      x: xFor(point, index, entry.points.length),
      y: yFor(point.price)
    }));
  });

  const updateHoveredPoint = (event) => {
    if (!svgRef.current || plottedPoints.length === 0) return;

    const ctm = svgRef.current.getScreenCTM();
    if (!ctm) return;

    const point = svgRef.current.createSVGPoint();
    point.x = event.clientX;
    point.y = event.clientY;

    const localPoint = point.matrixTransform(ctm.inverse());
    const svgX = localPoint.x;
    const svgY = localPoint.y;

    if (svgX < chartLeft || svgX > chartRight || svgY < chartTop || svgY > chartBottom) {
      setHoveredPoint(null);
      return;
    }

    const nearest = plottedPoints.reduce((best, point) => {
      const distance = Math.hypot(point.x - svgX, point.y - svgY);
      if (!best || distance < best.distance) return { ...point, distance };
      return best;
    }, null);

    // Only activate hover when the cursor is close to a real change point.
    if (!nearest || nearest.distance > 14) {
      setHoveredPoint(null);
      return;
    }

    setHoveredPoint(nearest);
  };

  const clearHoveredPoint = () => setHoveredPoint(null);

  return (
    <div className={collapsible ? 'mb-6' : 'mt-6 border-t border-slate-100 pt-5'}>
      <div className="flex flex-col gap-2 sm:flex-row sm:items-end sm:justify-between">
        <div>
          <h2 className="text-base font-semibold text-slate-900">Price history</h2>
          <p className="text-sm text-slate-500">
            Daily low price trend for tracked new and used listings{perItem ? ` (per item)` : ''}.
          </p>
        </div>
        {collapsible ? (
          <button
            type="button"
            onClick={() => setExpanded(false)}
            className="flex items-center gap-1 self-start text-xs text-slate-500 hover:text-slate-700 sm:self-auto"
            aria-expanded={true}
            aria-label="Hide price history chart"
          >
            Hide chart <ChevronIcon down={false} />
          </button>
        ) : (
          <div className="text-xs uppercase tracking-[0.18em] text-slate-400">
            {startLabel && endLabel ? `${startLabel} to ${endLabel}` : 'Recent history'}
          </div>
        )}
      </div>

      <div className="mt-4 grid gap-2 sm:grid-cols-2">
        {seriesSummaries.map((entry) => {
          const meta = SERIES_META[entry.key] || SERIES_META.new;
          return (
            <div key={entry.key} className={`rounded-2xl border px-4 py-3 text-left ${meta.badge}`}>
              <div className="flex items-center gap-2 text-sm font-semibold">
                <span className="h-2.5 w-2.5 rounded-full" style={{ backgroundColor: meta.stroke }} />
                <span>{entry.label}</span>
                <span className={`rounded-full px-2 py-0.5 text-[10px] font-semibold ${entry.quality.toneClass}`}>
                  {entry.quality.label}
                </span>
              </div>
              <div className="mt-3 flex items-end justify-between gap-3">
                <div>
                  <div className="text-[11px] uppercase tracking-[0.16em] text-slate-500">Current</div>
                  <div className="text-lg font-semibold text-slate-900">{formatMoney(entry.currentPrice)}</div>
                </div>
                <div className="text-right">
                  <div className="text-[11px] uppercase tracking-[0.16em] text-slate-500">Low</div>
                  <div className="text-base font-semibold text-slate-900">{formatMoney(entry.lowestPrice)}</div>
                </div>
              </div>
              <div className="mt-3">
                <div className="relative h-2 w-full overflow-hidden rounded-full bg-gradient-to-r from-rose-200 via-amber-200 to-emerald-200">
                  <div
                    className={`h-2 rounded-full ${entry.quality.barClass}`}
                    style={{ width: `${Math.round(entry.quality.score * 100)}%` }}
                  />
                </div>
                <div className="mt-1 text-[11px] text-slate-600">
                  {entry.quality.detailText}
                </div>
              </div>
            </div>
          );
        })}
      </div>

      <div className="relative mt-4 overflow-hidden rounded-2xl border border-slate-200 bg-[linear-gradient(180deg,#ffffff_0%,#f8fafc_100%)] p-3 shadow-sm">
        <svg
          ref={svgRef}
          viewBox={`0 0 ${width} ${height}`}
          className="h-44 w-full"
          role="img"
          aria-label="Product price history chart"
          onMouseMove={updateHoveredPoint}
          onMouseLeave={clearHoveredPoint}
        >
          <defs>
            {series.map((entry) => {
              const meta = SERIES_META[entry.key] || SERIES_META.new;
              return (
                <linearGradient key={entry.key} id={`history-fill-${entry.key}`} x1="0" y1="0" x2="0" y2="1">
                  <stop offset="0%" stopColor={meta.fill.replace('0.12', '0.22')} />
                  <stop offset="100%" stopColor={meta.fill.replace('0.12', '0')} />
                </linearGradient>
              );
            })}
          </defs>

          {yTicks.map((tick, index) => {
            const y = yFor(tick);
            return (
              <g key={`${tick}-${index}`}>
                <line x1={chartLeft} x2={chartRight} y1={y} y2={y} stroke="#e2e8f0" strokeDasharray="3 4" />
                <text x={chartLeft - 8} y={y + 4} textAnchor="end" fontSize="10" fill="#64748b">
                  {formatMoney(tick)}
                </text>
              </g>
            );
          })}

          {visibleSeries.map((entry) => {
            const meta = SERIES_META[entry.key] || SERIES_META.new;
            const areaPath = buildAreaPath(entry.points, (point, index) => xFor(point, index, entry.points.length), yFor, chartBottom);
            const linePath = buildLinePath(entry.points, (point, index) => xFor(point, index, entry.points.length), yFor);
            const latestPoint = entry.points[entry.points.length - 1];
            const lowPoint = entry.points.reduce((best, point) => (point.price < best.price ? point : best), entry.points[0]);

            return (
              <g key={entry.key}>
                <path d={areaPath} fill={`url(#history-fill-${entry.key})`} />
                <path d={linePath} fill="none" stroke={meta.stroke} strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round" />
                <circle cx={xFor(latestPoint, entry.points.length - 1, entry.points.length)} cy={yFor(latestPoint.price)} r="3.5" fill={meta.stroke} />
                <circle cx={xFor(lowPoint, entry.points.indexOf(lowPoint), entry.points.length)} cy={yFor(lowPoint.price)} r="4.5" fill="#ffffff" stroke={meta.stroke} strokeWidth="2" />
              </g>
            );
          })}

          {hoveredPoint && (
            <g pointerEvents="none">
              <line
                x1={hoveredPoint.x}
                x2={hoveredPoint.x}
                y1={chartTop}
                y2={chartBottom}
                stroke="#cbd5e1"
                strokeDasharray="4 4"
              />
              <circle
                cx={hoveredPoint.x}
                cy={hoveredPoint.y}
                r={6}
                fill="#ffffff"
                stroke={hoveredPoint.stroke}
                strokeWidth={2.5}
              />
              <circle cx={hoveredPoint.x} cy={hoveredPoint.y} r={2.5} fill={hoveredPoint.stroke} />
            </g>
          )}
        </svg>

        {hoveredPoint && (
          <div
            className="pointer-events-none absolute z-10 min-w-[132px] rounded-xl border border-slate-200 bg-white/95 px-3 py-2 text-xs shadow-lg backdrop-blur-sm"
            style={{
              left: `${Math.max(12, Math.min(((hoveredPoint.x / width) * 100), 72))}%`,
              top: hoveredPoint.y < height / 2 ? '56%' : '12px'
            }}
          >
            <div className="flex items-center gap-2 font-semibold text-slate-800">
              <span className="h-2 w-2 rounded-full" style={{ backgroundColor: hoveredPoint.stroke }} />
              <span>{hoveredPoint.seriesLabel}</span>
            </div>
            <div className="mt-1 text-slate-500">{formatDate(hoveredPoint.date)}</div>
            <div className="mt-1 text-sm font-semibold text-slate-900">{formatMoney(hoveredPoint.price)}</div>
          </div>
        )}

        <div className="mt-2 flex items-center justify-between text-xs text-slate-500">
          <span>{startLabel || 'Start'}</span>
          <span>{endLabel || 'Today'}</span>
        </div>
      </div>
    </div>
  );
};

export default ProductPriceHistoryCard;