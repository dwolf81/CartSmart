import React, { useState, useEffect } from 'react';
import { Link } from 'react-router-dom';
import { useAuth } from '../context/AuthContext';
import { useNavigate } from 'react-router-dom';
import LoadingSpinner from './LoadingSpinner';
import { FaTag, FaTicketAlt, FaLink, FaLayerGroup } from 'react-icons/fa';
import { appendAffiliateParam, getAffiliateFields } from '../utils/affiliateUrl';
const API_URL = process.env.REACT_APP_API_URL || 'http://localhost:5000';

// Replace REJECT / ISSUE constants with IDs
const DEAL_ISSUE_TYPE = [
  { value: 1, label: 'Inaccurate' },
  { value: 2, label: 'Out of Stock' },
  { value: 3, label: 'Coupon Invalid' },
  { value: 4, label: 'Expired' },
  { value: 5, label: 'Spam' },
  { value: 6, label: 'Other (Describe)' }
];

// Add below DEAL_TYPE_META
const DEAL_STATUS_TEXT = {
  1: 'Not Reviewed',
  2: 'Approved',
  3: 'Rejected',
  4: 'Deleted',
  5: 'Review Again',
  6: 'Expired',
};
const getStatusText = (id) => DEAL_STATUS_TEXT[Number(id)] || 'Unknown';

const truncateUrl = (u, max = 100) => {
  if (!u) return '';
  return u.length <= max ? u : u.slice(0, max - 3) + '...';
};

const ensureHttps = (u) => {
  if (!u) return u;
  const s = String(u).trim();
  if (!s) return s;
  if (s.startsWith('http://') || s.startsWith('https://')) return s;
  return `https://${s.replace(/^\/+/, '')}`;
};

// UPDATED modal to use numeric reasonId
function RejectModal({ show, onClose, comment, onCommentChange, onConfirm, reasonId, onReasonIdChange }) {
  if (!show) return null;
  const requireComment = reasonId === 6;
  return (
    <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50" onClick={onClose}>
      <div
        className="bg-white rounded-lg p-6 w-96"
        onClick={e => e.stopPropagation()}
      >
        <h3 className="text-xl font-bold mb-4">Reject Deal</h3>

        <label className="block text-sm font-medium text-gray-700 mb-1">
          Reason *
        </label>
        <select
          value={reasonId ?? ''}
          onChange={e => onReasonIdChange(e.target.value ? Number(e.target.value) : null)}
          className="w-full mb-4 p-2 border rounded-lg text-sm focus:outline-none focus:ring-2 focus:ring-green-500"
        >
          <option value="">Select a reason...</option>
          {DEAL_ISSUE_TYPE.map(r => (
            <option key={r.value} value={r.value}>{r.label}</option>
          ))}
        </select>

        <textarea
          className={`w-full h-32 p-2 border rounded-lg mb-4 focus:outline-none focus:ring-2 focus:ring-green-500 resize-none text-sm ${
            requireComment && !comment.trim() ? 'border-red-400' : ''
          }`}
          placeholder={
            requireComment
              ? 'Describe the issue...'
              : 'Optional moderator notes (visible to submitter)...'
          }
          value={comment}
          onChange={e => onCommentChange(e.target.value)}
          disabled={!reasonId}
        />

        {requireComment && !comment.trim() && (
            <p className="text-xs text-red-600 -mt-3 mb-3">
              Comment required for "Other".
            </p>
        )}

        <div className="flex justify-end gap-2">
          <button
            type="button"
            onClick={onClose}
            className="px-4 py-2 border rounded-lg hover:bg-gray-100 text-sm"
          >
            Cancel
          </button>
          <button
            type="button"
            onClick={onConfirm}
            className="px-4 py-2 bg-red-600 text-white rounded-lg hover:bg-red-700 text-sm disabled:opacity-60"
            disabled={
              !reasonId ||
              (requireComment && !comment.trim())
            }
          >
            Confirm Reject
          </button>
        </div>
      </div>
    </div>
  );
}

const DealReviewPage = () => {
  const [activeTab, setActiveTab] = useState('1'); // 1=pending, 5=Reviewed
  const [sortBy, setSortBy] = useState('newest');
  const [isDropdownOpen, setIsDropdownOpen] = useState(false);
  const [deals, setDeals] = useState([]);
  const [currentPage, setCurrentPage] = useState(1);
  const [totalCount, setTotalCount] = useState(0);
  const [showRejectModal, setShowRejectModal] = useState(false);
  const [rejectComment, setRejectComment] = useState('');
  const [selectedDealId, setSelectedDealId] = useState(null);
  const [pendingCount, setPendingCount] = useState(0);
  const [reviewedCount, setReviewedCount] = useState(0);
  const [submittedCount, setSubmittedCount] = useState(0);
  const [dealsLoading, setDealsLoading] = useState(true);
  const [rejectReason, setRejectReason] = useState('');
  const [rejectReasonId, setRejectReasonId] = useState(null);
  const itemsPerPage = 10;

  const { isAuthenticated, loading } = useAuth();
  const navigate = useNavigate();

  useEffect(() => {
    if (!loading && !isAuthenticated) navigate('/login');
  }, [loading, isAuthenticated, navigate]);

  useEffect(() => {
    if (!isAuthenticated) return;
    fetchPagedDeals();
    fetchCounts();
  }, [isAuthenticated, activeTab, currentPage]);

  if (loading) return <LoadingSpinner />;

  // Define sortOptions at the top level
  const sortOptions = [
    { value: 'newest', label: 'Newest First' },
    { value: 'oldest', label: 'Oldest First' },
    { value: 'productName', label: 'Product Name (A-Z)' },
    { value: 'highestPrice', label: 'Highest Price' },
    { value: 'lowestPrice', label: 'Lowest Price' },
    { value: 'bestDeal', label: 'Best Deal (% off)' },
    { value: 'worstDeal', label: 'Worst Deal (% off)' },
    { value: 'userName', label: 'User Name (A-Z)' }
  ];

    // Add this utility function at the top of your component, after the imports
  const formatPrice = (price) => {
    if (price === null || price === undefined) return '$0.00';
    return new Intl.NumberFormat('en-US', {
      style: 'currency',
      currency: 'USD',
      minimumFractionDigits: 2,
      maximumFractionDigits: 2
    }).format(parseFloat(price));
  };

  // Move fetchPagedDeals outside useEffect
  const fetchPagedDeals = async () => {
    try {
      setDealsLoading(true);
      let endpoint = '';
      if (activeTab === '1') {
        endpoint = `/api/deals/review-queue?page=${currentPage}&pageSize=${itemsPerPage}`;
      } 
      else if (activeTab === '5') {
        endpoint = `/api/deals/reviewed?page=${currentPage}&pageSize=${itemsPerPage}`;
      }
      else {
        endpoint = `/api/deals/user-submitted?page=${currentPage}&pageSize=${itemsPerPage}`;
      }
      const response = await fetch(`${API_URL}${endpoint}`, { credentials: 'include' });
      if (!response.ok) throw new Error('Failed to fetch deals');
      const data = await response.json();
      setDeals(data.deals || []);
      setTotalCount(data.totalCount || 0);
    } catch (error) {
      setDeals([]);
      setTotalCount(0);
      console.error('Error fetching deals:', error);
    } finally {
      setDealsLoading(false);
    }
  };

  // Fetch counts for each tab independently
  const fetchCounts = async () => {
    try {
      const pendingRes = await fetch(`${API_URL}/api/deals/review-queue?page=1&pageSize=1`, { credentials: 'include' });
      const pendingData = await pendingRes.json();
      setPendingCount(pendingData.totalCount || 0);

      const reviewedRes = await fetch(`${API_URL}/api/deals/reviewed?page=1&pageSize=1`, { credentials: 'include' });
      const reviewedData = await reviewedRes.json();
      setReviewedCount(reviewedData.totalCount || 0);
    } catch (err) {
      setPendingCount(0);
      setReviewedCount(0);
    }
  };

const copyCoupon = (code, event) => {
  if (!code) return;
  try {
    navigator.clipboard.writeText(code);
    const tip = document.createElement('span');
    tip.textContent = 'Copied!';
    tip.className = 'text-green-600 text-xs ml-2';
    event.target.parentNode.appendChild(tip);
    setTimeout(() => tip.remove(), 1500);
  } catch { /* ignore */ }
};

  const totalPages = Math.ceil(totalCount / itemsPerPage);

  const handlePageChange = (page) => {
    if (page >= 1 && page <= totalPages) {
      setCurrentPage(page);
    }
  };

  const renderPageNumbers = () => {
    const pageNumbers = [];
    for (let i = 1; i <= totalPages; i++) {
      pageNumbers.push(
        <button
          key={i}
          onClick={() => handlePageChange(i)}
          className={`px-3 py-1 mx-1 rounded-lg ${
            currentPage === i ? 'bg-green-600 text-white' : 'bg-gray-200 text-gray-700 hover:bg-gray-300'
          }`}
        >
          {i}
        </button>
      );
    }
    return pageNumbers;
  };

  const handleDealAction = async (dealId, dealProductId, action, comment = '') => {
    if (action === 3) {
      if (!rejectReasonId) {
        setSelectedDealId(String(dealId) + '|' + (dealProductId ?? ''));
        setShowRejectModal(true);
        return;
      }
      if (rejectReasonId === 6 && !rejectComment.trim()) {
        return; // modal enforces, safety
      }
    }
    try {
      const params = new URLSearchParams({
        dealId: String(dealId),
        dealStatusId: String(action)
      });

      // Store-wide deals have no deal_product_id
      if (dealProductId != null && String(dealProductId) !== '' && !Number.isNaN(Number(dealProductId))) {
        params.append('dealProductId', String(dealProductId));
      }
      if (action === 3) {
        params.append('dealIssueTypeId', String(rejectReasonId));
        if (rejectComment.trim()) params.append('comment', rejectComment.trim());
      } else if (comment) {
        params.append('comment', comment);
      }
      const response = await fetch(`${API_URL}/api/deals/reviewdeal?${params}`, {
        method: 'POST',
        credentials: 'include'
      });
      const data = await response.json();
      if (!response.ok) {
        throw new Error(data.message || 'review failed');
      } else {
        if (showRejectModal) {
          setShowRejectModal(false);
          setRejectComment('');
          setSelectedDealId(null);
          setRejectReasonId(null);
        }
        fetchPagedDeals();
        fetchCounts();
      }
    } catch (err) {
      throw new Error(err.message);
    }
  };

  const handleRejectConfirm = () => {
    if (selectedDealId) {
      const [dealId, dealProductId] = selectedDealId.split('|');
      handleDealAction(dealId, dealProductId ? Number(dealProductId) : null, 3, rejectComment);
    }
  };

  // Helper to format relative time
  function getRelativeTime(dateString) {
    const now = new Date();
    const created = new Date(dateString);
    const diffMs = now - created;
    const diffMins = Math.floor(diffMs / 60000);
    if (diffMins < 1) return 'just now';
    if (diffMins < 60) return `${diffMins} minute${diffMins === 1 ? '' : 's'} ago`;
    const diffHours = Math.floor(diffMins / 60);
    if (diffHours < 24) return `${diffHours} hour${diffHours === 1 ? '' : 's'} ago`;
    const diffDays = Math.floor(diffHours / 24);
    return `${diffDays} day${diffDays === 1 ? '' : 's'} ago`;
  }

const DEAL_TYPE_META = {
  1: {
    label: 'Direct',
    icon: <FaTag className="inline mr-1 text-slate-600" title="Direct Deal" />,
    badge: 'inline-flex items-center gap-2 bg-white border border-slate-200 text-slate-700 px-2 py-1 rounded-md shadow-sm'
  },
  2: {
    label: 'Coupon',
    icon: <FaTicketAlt className="inline mr-1 text-emerald-600" title="Coupon Deal" />,
    badge: 'inline-flex items-center gap-2 bg-white border border-emerald-200 text-emerald-700 px-2 py-1 rounded-md shadow-sm'
  },
  3: {
    label: 'Stacked',
    icon: <FaLayerGroup className="inline mr-1 text-amber-600" title="Stacked Deal" />,
    badge: 'inline-flex items-center gap-2 bg-white border border-amber-200 text-amber-700 px-2 py-1 rounded-md shadow-sm'
  },
  4: {
    label: 'External',
    icon: <FaLink className="inline mr-1 text-indigo-600" title="External Offer" />,
    badge: 'inline-flex items-center gap-2 bg-white border border-indigo-200 text-indigo-700 px-2 py-1 rounded-md shadow-sm'
  }
};

  const DealCard = ({ deal, type }) => {
    const isStoreDeal = !deal?.deal_product_id && (deal?.store_url || deal?.store_name || deal?.store_id);
    const imageSrc = isStoreDeal
      ? (deal?.store_image_url || 'https://placehold.co/112x112')
      : (deal?.product_image_url || 'https://placehold.co/112x112');
    const primaryUrl = isStoreDeal
      ? `/stores/${deal.store_slug}`
      : `/products/${deal?.slug}`;

    const affiliate = getAffiliateFields(deal, 'normal');
    const affiliateCodeVar = affiliate.affiliateCodeVar;
    const affiliateCode = affiliate.affiliateCode;
    const externalAffiliate = getAffiliateFields(deal, 'external');
    const externalAffiliateCodeVar = externalAffiliate.affiliateCodeVar;
    const externalAffiliateCode = externalAffiliate.affiliateCode;
    const productOrStoreUrl = appendAffiliateParam(
      (deal.store_url ? ensureHttps(deal.store_url) : null) || deal.url,
      affiliateCodeVar,
      affiliateCode
    );
    const externalOfferUrl = appendAffiliateParam(
      deal.external_offer_url,
      externalAffiliateCodeVar,
      externalAffiliateCode
    );

    return (
    <div className="bg-white rounded-lg shadow-md p-6 mb-4">
      <div className="flex gap-4 items-start">
        {isStoreDeal ? (
          <a href={primaryUrl || '#'} target="_blank" rel="noopener noreferrer" className="block hover:opacity-90 transition-opacity">
            <img
              src={imageSrc}
              alt={deal?.store_name || 'Store'}
              className="w-24 h-24 object-cover rounded-lg border mt-1"
            />
          </a>
        ) : (
          <Link to={primaryUrl} className="block hover:opacity-90 transition-opacity">
            <img
              src={imageSrc}
              alt={deal?.product_name || 'Product'}
              className="w-24 h-24 object-cover rounded-lg border mt-1"
            />
          </Link>
        )}
        <div className="flex-1 flex flex-row justify-between items-start">
   
          <div>
                                   <div className="mb-2 flex items-center">
                  <span className={`flex items-center px-2 py-1 rounded text-xs font-semibold mr-2 ${DEAL_TYPE_META[deal.deal_type_id]?.badge}`}
                    title={DEAL_TYPE_META[deal.deal_type_id]?.desc}>
                    {DEAL_TYPE_META[deal.deal_type_id]?.icon} {DEAL_TYPE_META[deal.deal_type_id]?.label} Deal
                  </span>
                    {deal.steps?.length && (
                  <span className="text-xs text-blue-700">{deal.steps?.length || 0} Steps</span>
                    )}
                </div>
         
              <div className="text-sm mb-2">
                <span className="font-medium">{isStoreDeal ? 'Store:' : 'Product:'}</span>{' '}
                {isStoreDeal ? (
                  <a href={`/stores/${deal.store_slug}`} rel="noopener noreferrer" className="text-blue-600 no-underline hover:underline break-all">{deal.store_name}</a>
                ) : (
                  <a href={`/products/${deal.slug}`} rel="noopener noreferrer" className="text-blue-600 no-underline hover:underline break-all">{deal.product_name}</a>
                )}
              </div>
            
             {/*
            <div className="text-sm mb-2">
              <span className="font-medium">Deal Type:</span> {deal.deal_type_name}
            </div>
            */}
            {!isStoreDeal && (
              <div className="flex flex-row gap-4 mb-2">
                <div className="text-sm">
                  <span className="font-medium">Condition:</span> {deal.condition_name}
                </div>
                <div className="text-sm">
                  <span className="font-medium">Free Shipping:</span> {deal.free_shipping ? 'Yes' : 'No'}
                </div>
              </div>
            )}

            {/* Stacked/Combo Deal Details */}
            {deal.deal_type_id === 3 ? (
              <>
                {deal.additional_details && (
                  <div className="text-sm">
                    <span className="font-medium">Additional Details:</span> {deal.additional_details}
                  </div>
                )}
                
              </>
            ) : (
              <>
                {deal.coupon_code && (
                  <div className="text-sm mb-2">
                    <span className="font-medium">Coupon Code:</span>{' '}
                    <code
                      onClick={(event) => copyCoupon(deal.coupon_code, event)}
                      className="bg-gray-100 px-2 py-1 rounded cursor-pointer hover:bg-gray-200 transition-colors"
                      title="Click to copy"
                    >
                      {deal.coupon_code}
                    </code>
                  </div>
                )}
                <div className="text-sm mb-2">
                  <span className="font-medium">{isStoreDeal ? 'Store URL:' : 'Product URL:'}</span>{' '}
                  {productOrStoreUrl ? (
                    <a
                      href={productOrStoreUrl}
                      target="_blank"
                      rel="noopener noreferrer"
                      title={productOrStoreUrl}
                      className="text-blue-600 no-underline hover:underline break-all"
                    >
                      {truncateUrl(productOrStoreUrl)}
                    </a>
                  ) : 'N/A'}
                </div>
                {deal.external_offer_url && (
                  <div className="text-sm mb-2">
                    <span className="font-medium">External Offer URL:</span>{' '}
                    <a
                      href={externalOfferUrl}
                      target="_blank"
                      rel="noopener noreferrer"
                      title={externalOfferUrl}
                      className="text-blue-600 no-underline hover:underline break-all"
                    >
                      {truncateUrl(externalOfferUrl)}
                    </a>
                  </div>
                )}                
                {deal.additional_details && (
                  <div className="text-sm  mb-2">
                    <span className="font-medium">Additional Details:</span> {deal.additional_details}
                  </div>
                )}
              </>
            )}
          </div>
          <div className="text-right min-w-[100px] ml-4">
            <div className="text-2xl font-bold text-green-600">
              {/* Store-wide deals: show discount badge only (no price/MSRP). */}
              {deal.discount_percent > 0 && (
                <span className="bg-green-100 text-green-700 text-xs font-semibold px-2 py-1 rounded-full">
                  {deal.discount_percent}% Off
                </span>
              )}
              {!isStoreDeal && (
                <>
                  <span className="font-bold text-green-600 text-xl">{formatPrice(deal.price)}</span>
                </>
              )}
            </div>
            {!isStoreDeal && (
              <div className="text-sm text-gray-500 line-through">{formatPrice(deal.msrp)}</div>
            )}
          </div>
        </div>
      </div>
      {deal.steps && (
  <div className="mt-2 flex flex-col gap-4">
    {deal.steps.map((step, idx) => {
      const stepAffiliate = getAffiliateFields(step, 'normal');
      const stepAffiliateCodeVar = stepAffiliate.affiliateCodeVar || affiliateCodeVar;
      const stepAffiliateCode = stepAffiliate.affiliateCode || affiliateCode;
      const stepUrl = appendAffiliateParam(step.url, stepAffiliateCodeVar, stepAffiliateCode);

      return (
      <div key={idx} className="relative rounded-xl border bg-gray-50 p-3">
        <div className="flex items-center justify-between mb-2">
          <div className="flex items-center gap-2">
            <span className="inline-flex h-7 w-7 items-center justify-center rounded-full bg-white border text-slate-700 text-xs font-semibold">
              {idx + 1}
            </span>
            <span className={`inline-flex items-center px-2 py-1 rounded text-xs font-semibold ${DEAL_TYPE_META[step.deal_type_id]?.badge}`}>
              {DEAL_TYPE_META[step.deal_type_id]?.icon} {DEAL_TYPE_META[step.deal_type_id]?.label} Deal
            </span>
          </div>
          {step.discount_percent > 0 && (
            <span className="bg-green-100 text-green-700 text-xs font-semibold px-2 py-1 rounded-full">
              {step.discount_percent}% Off
            </span>
          )}
        </div>



        <div className="text-sm mb-1">
          <span className="font-medium">Source:</span>{' '}
          {stepUrl ? (
            <a
              href={stepUrl}
              target="_blank"
              rel="noopener noreferrer"
              title={stepUrl}
              className="text-blue-600 no-underline hover:underline break-all"
            >
              {truncateUrl(stepUrl, 100)}
            </a>
          ) : 'N/A'}
        </div>
        {step.coupon_code && (
          <div className="text-sm mb-1">
            <span className="font-medium">Coupon Code:</span>{' '}
            <code
              onClick={(event) => copyCoupon(step.coupon_code, event)}
              className="bg-gray-100 px-2 py-1 rounded cursor-pointer hover:bg-gray-200 transition-colors"
              title="Click to copy"
            >
              {step.coupon_code}
            </code>
          </div>
        )}
        {step.additional_details && (
          <div className="text-sm mb-1">
            <span className="font-medium">Additional Details:</span> {step.additional_details}
          </div>
        )}
      </div>
    );
    })}
  </div>
)}
      {/* User Info and Action Buttons Row */}
      <div className="flex items-center justify-between mt-4">
        <div className="flex items-center gap-2 text-xs text-gray-400"></div>
        {type === '1' && (
          <div className="flex items-center justify-end gap-4 mt-4">
                                    <div className="flex items-center gap-2">
                            <span className={`font-semibold ${deal.deal_status_id === 2 ? 'text-green-600' : deal.deal_status_id === 3 || deal.deal_status_id === 4 || deal.deal_status_id === 6 ? 'text-red-600' : 'text-yellow-600'}`}>
                              {/* replace raw ID with lookup text */}
                              {getStatusText(deal.deal_status_id)}
                            </span>
                            {(deal.review_comment && deal.deal_status_id === 3) &&  (
                              <span className="text-gray-500">| {deal.review_comment}</span>
                            )}
                          </div>
          <div className="space-x-2">
            <button
              onClick={() => handleDealAction(deal.deal_id, deal.deal_product_id, 2)}
              className="px-4 py-2 bg-green-600 text-white rounded-lg hover:bg-green-700"
            >
              Approve
            </button>
            <button
              onClick={() => handleDealAction(deal.deal_id, deal.deal_product_id, 3)}
              className="px-4 py-2 bg-red-600 text-white rounded-lg hover:bg-red-700"
            >
              Reject
            </button>
          </div>
          </div>
        )}
        {type !== '1' && (
          <div className="mt-4">
            <div className="flex items-center gap-2">
              <span className={`font-semibold ${deal.review_deal_status_id ? deal.review_deal_status_id === 2 ? 'text-green-600' : 'text-red-600' : 'text-yellow-600'}`}>
                {deal.review_deal_status_id ? deal.review_deal_status_id === 2 ? 'Approved' : 'Rejected' : 'Not Reviewed'}
              </span>
              {deal.review_comment && deal.deal_status_id === 3 && (
                <span className="text-gray-500">| {deal.review_comment}</span>
              )}
            </div>
          </div>
        )}
      </div>
    </div>
  );
  };

  return (
    <div className="container mx-auto px-4 py-8">
      <RejectModal
        show={showRejectModal}
        onClose={() => {
          setShowRejectModal(false);
          setRejectComment('');
          setSelectedDealId(null);
          setRejectReasonId(null);
        }}
        comment={rejectComment}
        onCommentChange={setRejectComment}
        onConfirm={handleRejectConfirm}
        reasonId={rejectReasonId}
        onReasonIdChange={setRejectReasonId}
      />
      {/* Header with Sort */}
      <div className="flex flex-col sm:flex-row justify-between items-start sm:items-center mb-8">
        <h1 className="text-3xl font-bold">Deal Review Dashboard</h1>
        
        {/* Sort Dropdown */}
        {/*<div className="relative mt-4 sm:mt-0">
          <button
            onClick={() => setIsDropdownOpen(!isDropdownOpen)}
            className="flex items-center gap-2 px-4 py-2 bg-white border rounded-lg shadow-sm hover:bg-gray-50"
          >
            <svg className="w-5 h-5 text-gray-500" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M3 4h13M3 8h9m-9 4h6m4 0l4-4m0 0l4 4m-4-4v12" />
            </svg>
            <span>Sort by: {sortOptions.find(opt => opt.value === sortBy)?.label}</span>
            <svg className="w-5 h-5 text-gray-500" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M19 9l-7 7-7-7" />
            </svg>
          </button>


          {isDropdownOpen && (
            <div className="absolute right-0 mt-2 w-72 bg-white border rounded-lg shadow-lg z-10">
              <div className="py-1">
                {sortOptions.map((option) => (
                  <button
                    key={option.value}
                    onClick={() => {
                      setSortBy(option.value);
                      setIsDropdownOpen(false);
                    }}
                    className={`w-full text-left px-4 py-2 hover:bg-gray-100 ${
                      sortBy === option.value ? 'bg-[#e8f5e9] text-[#4CAF50]' : 'text-gray-700'
                    }`}
                  >
                    <div className="flex items-center justify-between">
                      <span>{option.label}</span>
                      {sortBy === option.value && (
                        <svg className="w-5 h-5" fill="currentColor" viewBox="0 0 20 20">
                          <path fillRule="evenodd" d="M16.707 5.293a1 1 0 010 1.414l-8 8a1 1 0 01-1.414 0l-4-4a1 1 0 011.414-1.414L8 12.586l7.293-7.293a1 1 0 011.414 0z" clipRule="evenodd" />
                        </svg>
                      )}
                    </div>
                  </button>
                ))}
              </div>
            </div>
          )}
        </div>*/}
      </div>

      {/* Tabs */}
      <div className="flex border-b mb-6">
        <button
          className={`px-6 py-3 text-lg font-medium ${
            activeTab === '1'
              ? 'border-b-2 border-[#4CAF50] text-[#4CAF50]'
              : 'text-gray-500 hover:text-gray-700'
          }`}
          onClick={() => { setActiveTab('1'); setCurrentPage(1); }}
        >
          Deals to Review ({pendingCount})
        </button>
        <button
          className={`px-6 py-3 text-lg font-medium ${
            activeTab === '5'
              ? 'border-b-2 border-[#4CAF50] text-[#4CAF50]'
              : 'text-gray-500 hover:text-gray-700'
          }`}
          onClick={() => { setActiveTab('5'); setCurrentPage(1); }}
        >
          Deals I Reviewed ({reviewedCount})
        </button>
      </div>

      {/* Deal Lists */}
      <div className="space-y-4">
        {dealsLoading ? (
          <LoadingSpinner />
        ) : deals.length === 0 ? (
          <div className="text-center text-gray-500 py-8">No deals found.</div>
        ) : (
          deals.map(deal => (
            <DealCard key={deal.deal_id} deal={deal} type={activeTab} />
          ))
        )}
      </div>

      {/* Pagination Controls */}
      <div className="flex justify-center mt-6">
        {totalPages > 1 && renderPageNumbers()}
      </div>
    </div>
  );
};

export default DealReviewPage;