import React, { useState, useEffect } from 'react';
import { useParams, useLocation } from 'react-router-dom';
import { Link } from 'react-router-dom';
import { useUsers } from '../hooks/useUsers';
import { useAuth } from '../context/AuthContext';
import LoadingSpinner from './LoadingSpinner';
import { useImageUrl } from '../hooks/useImageUrl';
import { FaTag, FaTicketAlt, FaLink, FaLayerGroup, FaFlag, FaPlus, FaCommentDots } from 'react-icons/fa';
import SubmitDealModal from './SubmitDealModal'; // added
import ComboDealModal from './ComboDealModal';   // added
import ReviewCommentsModal from './ReviewCommentsModal';

const API_URL = process.env.REACT_APP_API_URL || 'http://localhost:5000';

const ProfilePage = () => {
  const { username } = useParams();
  const { user: loggedInUser } = useAuth();
  const [activeTab, setActiveTab] = useState('deals');
  const [user, setUser] = useState(null);
  const { getUserBySlug } = useUsers();
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);
  const [deals, setDeals] = useState([]);
  const [currentPage, setCurrentPage] = useState(1);
  const [totalCount, setTotalCount] = useState(0);
  const [isEditModalOpen, setIsEditModalOpen] = useState(false);
  const [updatedProfile, setUpdatedProfile] = useState({ avatar: '', bio: '', displayName: '' });
  const [selectedFile, setSelectedFile] = useState(null);
  // add edit-deal modal state
  const [isEditSubmitDealOpen, setIsEditSubmitDealOpen] = useState(false);
  const [isEditComboOpen, setIsEditComboOpen] = useState(false);
  const [editingDeal, setEditingDeal] = useState(null);
  const [reviewModalOpen, setReviewModalOpen] = useState(false);
  const [reviewModalDeal, setReviewModalDeal] = useState(null);
  const [reviewLoading, setReviewLoading] = useState(false);
  const itemsPerPage = 12;
  const location = useLocation();

// Helper – handles both ?dealId= and ?dealid=
function parseDealId() {
  const qs = new URLSearchParams(location.search || '');
  // Try canonical key first
  let raw = qs.get('dealId');
  if (raw == null) raw = qs.get('dealid'); // fallback (lowercase)
  if (!raw) return null;
  const num = Number(raw);
  return Number.isNaN(num) ? null : num;
}

const [targetDealId, setTargetDealId] = useState(null);
const [showTargetOnly, setShowTargetOnly] = useState(false);

// Re-parse whenever search changes
useEffect(() => {
  const id = parseDealId();  
  setTargetDealId(id);
  setShowTargetOnly(Boolean(id));
}, [location.search]);

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

  const deleteDeal = async (dealId) => {
    if (!dealId) return;
    const ok = window.confirm('Are you sure you want to delete this deal? This action cannot be undone.');
    if (!ok) return;

    try {
      const res = await fetch(`${API_URL}/api/deals/${dealId}`, {
        method: 'DELETE',
        credentials: 'include',
      });
      if (!res.ok) {
        const err = await res.json().catch(() => ({}));
        throw new Error(err.message || 'Failed to delete deal');
      }

      // Optimistically update the UI
      setDeals((prev) => prev.filter((d) => d.deal_id !== dealId));
      setTotalCount((prev) => Math.max(0, prev - 1));
    } catch (e) {
      console.error('Delete deal error:', e);
      alert(e.message || 'Could not delete the deal.');
    }
  };

  // Edit button handler: open proper modal with the selected deal
  const editDeal = async (dealOrId) => {
    try {
      let dealDetail = null;

      if (typeof dealOrId === 'object' && dealOrId !== null) {
        dealDetail = dealOrId;
        // If it's a stacked deal but we don't have steps, fetch full detail
        if (Number(dealDetail.deal_type_id) === 3 && !Array.isArray(dealDetail.steps)) {
          const res = await fetch(`${API_URL}/api/deals/${dealDetail.deal_id}`, { credentials: 'include' });
            if (res.ok) {
              const full = await res.json();
              dealDetail = full;
            }
        }
      } else {
        // Fetch by id
        const res = await fetch(`${API_URL}/api/deals/${dealOrId}`, { credentials: 'include' });
        if (!res.ok) throw new Error('Failed to load deal');
        dealDetail = await res.json();
      }

      // Normalize product id field just in case
      if (!dealDetail.product_id && dealDetail.productId) {
        dealDetail.product_id = dealDetail.productId;
      }

      setEditingDeal(dealDetail);

      if (Number(dealDetail.deal_type_id) === 3) {
        setIsEditComboOpen(true);
      } else {
        setIsEditSubmitDealOpen(true);
      }
    } catch (e) {
      console.error(e);
      alert('Could not load deal for editing.');
    }
  };

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

  // Call useImageUrl at the top level of the component

  const [dealsLoading, setDealsLoading] = useState(false);
  const [dealsLoaded, setDealsLoaded] = useState(false);

  // Update fetchPagedDeals (replace current implementation):
  const fetchPagedDeals = async () => {
    setDealsLoading(true);
    try {
      const params = new URLSearchParams();
      params.set('page', currentPage);
      params.set('pageSize', itemsPerPage);
      if (user?.id) params.set('userId', user.id);
      if (targetDealId !== null) params.set('dealId', String(targetDealId));

      const url = `${API_URL}/api/deals/user-submitted?${params.toString()}`;

      const response = await fetch(url, { credentials: 'include' });
      if (!response.ok) throw new Error('Failed to fetch deals');
      const data = await response.json();

      setDeals(data.deals || []);
      setTotalCount(data.totalCount || 0);
    } catch (err) {
      console.error('Error loading deals:', err);
      setError(err.message);
    } finally {
      setDealsLoading(false);
      setDealsLoaded(true);
    }
  };

  useEffect(() => {
    const loadUser = async () => {
      try {
        setLoading(true);
        let userData;
        if (username) {
          userData = await getUserBySlug(username);
        } else {
          // Always fetch current profile for owner view
          const res = await fetch(`${API_URL}/api/users/profile`, { credentials: 'include' });
          if (res.ok) userData = await res.json();
        }
        if (!userData) { setError('User not found'); return; }        
        setUser(userData);
        setUpdatedProfile({
          imageUrl: userData.imageUrl || '',
          bio: userData.bio || '',
          displayName: userData.displayName || userData.userName || ''
        });
      } catch (err) {
        setError(err.message || 'Failed to load user');
      } finally {
        setLoading(false);
      }
    };
    loadUser();
  }, [username]);

  useEffect(() => {
    const params = new URLSearchParams(location.search);
    const idParam = params.get('dealId');      
    if (idParam && !isNaN(parseInt(idParam, 10))) {
      setTargetDealId(parseInt(idParam, 10));
      setShowTargetOnly(true);
    } else {
      setTargetDealId(null);
      setShowTargetOnly(false);
    }
  }, [location.search]);

  useEffect(() => {
    if (user && loggedInUser?.id === user.id) {
      fetchPagedDeals();
    }
  }, [user, currentPage, loggedInUser?.id, targetDealId]);

  const handleEditProfile = async () => {
    try {
      let imageUrl = updatedProfile.imageUrl;
      if (selectedFile) {
        const formData = new FormData();
        formData.append('file', selectedFile);
        const uploadResponse = await fetch(`${API_URL}/api/users/${loggedInUser?.id}/avatar`, {
          method: 'POST',
          credentials: 'include',
          body: formData
        });
        if (!uploadResponse.ok) throw new Error('Failed to upload image');
    const { imageUrl: newImageUrl } = await uploadResponse.json();
      imageUrl = newImageUrl;
    }

    const response = await fetch(`${API_URL}/api/users/${loggedInUser?.id}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      credentials: 'include',
      body: JSON.stringify({
        ...updatedProfile,
        imageUrl: imageUrl,
        display_name: updatedProfile.displayName // map to API field
      }),
    });

    if (!response.ok) throw new Error('Failed to update profile');

    const updatedUser = await response.json();

    setUser(updatedUser);
    setIsEditModalOpen(false);
    setSelectedFile(null);

    if (updatedProfile.imageUrl.startsWith('blob:')) {
      URL.revokeObjectURL(updatedProfile.imageUrl);
    }
    setUpdatedProfile({
      imageUrl: updatedUser.imageUrl,
      bio: updatedUser.bio,
      displayName: updatedUser.displayName || updatedUser.display_name || updatedUser.userName || ''
    });
  } catch (err) {
    console.error('Error updating profile:', err);
    alert('Failed to update profile.');
  }
  };

  const resetForm = () => {
    if (updatedProfile.imageUrl.startsWith('blob:')) {
      URL.revokeObjectURL(updatedProfile.imageUrl);
    }
    setUpdatedProfile({
      imageUrl: user.imageUrl,
      bio: user.bio,
      displayName: user.displayName || user.display_name || user.userName || ''
    });
    setSelectedFile(null);
    setIsEditModalOpen(false);
  };

  // Derive displayed list
  const displayedDeals = showTargetOnly && targetDealId
    ? deals.filter(d => d.deal_id === targetDealId)
    : deals;

  const totalPages = Math.ceil(
    (showTargetOnly ? displayedDeals.length : totalCount) / itemsPerPage
  );

  if (loading) return <LoadingSpinner />;
  if (error) return <div className="container mx-auto px-4 py-8">Error: {error}</div>;
  if (!user) return <div className="container mx-auto px-4 py-8">User not found</div>;

  const renderPageNumbers = () => {
    const pageNumbers = [];
    for (let i = 1; i <= totalPages; i++) {
      pageNumbers.push(
        <button
          key={i}
          onClick={() => setCurrentPage(i)}
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

  // Fetch review comments (try API, fallback to grouping duplicates in deals array)


const openReviewModal = (deal) => {
  setReviewModalDeal(deal);
  setReviewModalOpen(true);

};

const closeReviewModal = () => {
  setReviewModalOpen(false);
  setReviewModalDeal(null);

};

const truncateUrl = (u, max = 100) => {
  if (!u) return '';
  return u.length <= max ? u : u.slice(0, max - 3) + '...';
};

// Add helper near other utilities (e.g. after truncateUrl):
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

  return (
    <div className="container mx-auto px-4 py-8">
      {/* Profile Header */}
      <div className="bg-white rounded-lg shadow-lg p-6 mb-8">
        <div className="flex flex-col md:flex-row items-start md:items-center gap-6">
          <img
            src={user?.imageUrl || 'https://placehold.co/100x100'}
            alt={user.displayName}
            className="w-24 h-24 rounded-full"
          />
          <div className="flex-grow">
            <div className="flex items-center gap-3 mb-2">
              <h1 className="text-2xl font-bold">{user.displayName}</h1>
              {user.isVerified && (
                <span className="bg-[#e8f5e9] text-[#2E7D32] text-xs px-2 py-1 rounded-full flex items-center">
                  <svg className="w-4 h-4 mr-1" fill="currentColor" viewBox="0 0 20 20">
                    <path d="M6.267 3.455a3.066 3.066 0 001.745-.723 3.066 3.066 0 013.976 0 3.066 3.066 0 001.745.723 3.066 3.066 0 012.812 2.812c.051.643.304 1.254.723 1.745a3.066 3.066 0 010 3.976 3.066 3.066 0 00-.723 1.745 3.066 3.066 0 01-2.812 2.812 3.066 3.066 0 00-1.745.723 3.066 3.066 0 01-3.976 0 3.066 3.066 0 00-1.745-.723 3.066 3.066 0 01-2.812-2.812 3.066 3.066 0 00-.723-1.745 3.066 3.066 0 010-3.976 3.066 3.066 0 00.723-1.745 3.066 3.066 0 012.812-2.812zm7.44 5.252a1 1 0 00-1.414-1.414L9 10.586 7.707 9.293a1 1 0 00-1.414 1.414l2 2a1 1 0 001.414 0l4-4z" />
                  </svg>
                  Verified
                </span>
              )}
            </div>
            <p className="text-gray-600 mb-4">@{user.userName}</p>
            <p className="text-gray-700 mb-4">{user.bio}</p>
            <div className="flex flex-wrap gap-6 text-sm">
              <div>
                <span className="font-semibold">{user.dealsPosted} </span>
                <span className="text-gray-500">Deals Posted</span>
              </div>
              <div>
                <span className="font-semibold text-green-600">{user.level}% </span>
                <span className="text-gray-500">Trust Score</span>
              </div>
            </div>
          </div>
          {loggedInUser?.id === user.id && (
            <button
              onClick={() => setIsEditModalOpen(true)}
              className="px-6 py-2 bg-[#4CAF50] text-white rounded-lg hover:bg-[#3d8b40] transition-colors"
            >
              Edit Profile
            </button>
          )}
        </div>
      </div>

      {/* Edit Profile Modal */}
      {isEditModalOpen && (
        <div className="fixed inset-0 bg-black bg-opacity-50 z-50 flex items-center justify-center">
          <div className="bg-white rounded-lg p-6 w-full max-w-md">
            <h2 className="text-xl font-bold mb-4">Edit Profile</h2>
            <div className="flex items-start gap-4 mb-4">
              {/* Left column: picture + name */}
              <div className="flex flex-col items-start">
                <div className="relative w-24 h-24">
                  <img
                    src={updatedProfile.imageUrl || 'https://placehold.co/100x100'}
                    alt="Profile"
                    className="w-full h-full rounded-full object-cover border cursor-pointer"
                    onClick={() => document.getElementById('profilePictureInput').click()}
                  />
                  <div
                    className="absolute inset-0 bg-black bg-opacity-50 rounded-full flex items-center justify-center opacity-0 hover:opacity-100 transition-opacity cursor-pointer"
                    onClick={() => document.getElementById('profilePictureInput').click()}
                  >
                    <span className="text-white text-sm font-medium">Change Photo</span>
                  </div>
                  <input
                    id="profilePictureInput"
                    type="file"
                    accept="image/*"
                    className="hidden"
                    onChange={(e) => {
                      const file = e.target.files[0];
                      if (file) {
                        setSelectedFile(file);
                        const previewUrl = URL.createObjectURL(file);
                        setUpdatedProfile({ ...updatedProfile, imageUrl: previewUrl });
                      }
                    }}
                  />
                </div>

                {/* Name field under the picture */}
                <label htmlFor="displayName" className="block text-sm font-medium text-gray-700 mt-4">
                  Name
                </label>
                <input
                  id="displayName"
                  type="text"
                  value={updatedProfile.displayName}
                  onChange={(e) => setUpdatedProfile({ ...updatedProfile, displayName: e.target.value })}
                  className="mt-1 w-64 px-3 py-2 border rounded"
                  placeholder="Your name"
                />
              </div>
            </div>
            {/* Bio Section */}
            <div className="mb-4">
              <label className="block text-sm font-medium text-gray-700 mb-2">Bio</label>
              <textarea
                value={updatedProfile.bio}
                onChange={(e) => setUpdatedProfile({ ...updatedProfile, bio: e.target.value })}
                rows="3"
                className="w-full px-3 py-2 border rounded"
              />
            </div>
            <div className="flex justify-end gap-4">
              <button
                onClick={resetForm}
                className="px-4 py-2 border rounded text-gray-700 hover:bg-gray-100"
              >
                Cancel
              </button>
              <button
                onClick={handleEditProfile}
                className="px-4 py-2 bg-[#4CAF50] text-white rounded hover:bg-[#3d8b40]"
              >
                Save Changes
              </button>
            </div>
          </div>
        </div>
      )}

      {/* My Deals (only visible to the profile owner) */}
      {loggedInUser?.id === user.id  && (
        <>
          <div className="flex items-center justify-between mb-4">
            <h2 className="text-xl font-bold">My Deals</h2>
            {targetDealId && showTargetOnly &&  (
              <button
                onClick={() => setShowTargetOnly(s => !s)}
                className="px-3 py-1 text-sm rounded-md border bg-white hover:bg-gray-50"
                title="Toggle filtering by dealId from query string"
              >
                {showTargetOnly ? 'Show All Deals' : `Filter Deal #${targetDealId}`}
              </button>
            )}
          </div>


          {targetDealId &&
 showTargetOnly &&
 dealsLoaded &&                // ensure first load finished
 !dealsLoading &&              // not mid-request
 displayedDeals.length === 0 && 
 totalCount > 0 && (            // only if user actually has some deals
  <div className="mb-4 text-sm text-red-600">
    Requested deal not found, click the "Show All Deals" button to see all.
  </div>
)}

          {showTargetOnly && targetDealId && dealsLoading && (
  <div className="mb-4 text-sm text-gray-500">Loading deal data...</div>
)}

          <div className="space-y-4">
            {displayedDeals.map((deal) => (
              <div key={deal.deal_product_id} className="bg-white rounded-lg shadow-md p-6 mb-4">
                    <div className="flex gap-4 items-start">
                      <Link to={`/products/${deal.slug}`} className="block hover:opacity-90 transition-opacity">
                        <img
                          src={deal.product_image_url || 'https://placehold.co/112x112'}
                          alt={deal.name}
                          className="w-24 h-24 object-cover rounded-lg border mt-1"
                        />
                      </Link>
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
                              <span className="font-medium">Product:</span> <a href={`/products/${deal.slug}`}  rel="noopener noreferrer" className="text-blue-600 no-underline hover:underline break-all">{deal.product_name}</a>
                            </div>
                          
                           {/*
                          <div className="text-sm mb-2">
                            <span className="font-medium">Deal Type:</span> {deal.deal_type_name}
                          </div>
                          */}
                          <div className="flex flex-row gap-4 mb-2">
                            <div className="text-sm">
                              <span className="font-medium">Condition:</span> {deal.condition_name}
                            </div>
                            <div className="text-sm">
                              <span className="font-medium">Free Shipping:</span> {deal.free_shipping ? 'Yes' : 'No'}
                            </div>
                          </div>
              
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
                                <span className="font-medium">Product URL:</span>{' '}
                                {deal.url ? (
  <a
    href={deal.url}
    target="_blank"
    rel="noopener noreferrer"
    title={deal.url}
    className="text-blue-600 no-underline hover:underline break-all"
  >
    {truncateUrl(deal.url)}
  </a>
) : 'N/A'}
                              </div>
                {deal.external_offer_url && (
                  <div className="text-sm mb-2">
                    <span className="font-medium">External Offer URL:</span>{' '}
                    <a
      href={deal.external_offer_url}
      target="_blank"
      rel="noopener noreferrer"
      title={deal.external_offer_url}
      className="text-blue-600 no-underline hover:underline break-all"
    >
      {truncateUrl(deal.external_offer_url)}
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
                            {/* Price in top right, keep slightly larger for emphasis */}
                                                  {deal.discount_percent > 0 && (
                                        <span className="bg-green-100 text-green-700 text-xs font-semibold px-2 py-1 rounded-full">
                                          {deal.discount_percent}% Off
                                        </span>
                                      )}
                                      <span className="font-bold text-green-600 text-xl">{formatPrice(deal.price)}</span>
  
                                    </div>
                          <div className="text-sm text-gray-500 line-through">{formatPrice(deal.msrp)}</div>
                        </div>
                      </div>
                    </div>
                    {deal.steps && (
                                <div className="mt-2 flex flex-col gap-4">
                                  {deal.steps.map((step, idx) => (
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
                                        {step.url ? (
  <a
    href={step.url}
    target="_blank"
    rel="noopener noreferrer"
    title={step.url}
    className="text-blue-600 no-underline hover:underline break-all"
  >
    {truncateUrl(step.url, 100)}
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
                                  ))}
                                </div>
                              )}
                    {/* Action Buttons Row */}
                    <div className="flex items-center justify-end gap-4 mt-4">
                     

                      
                       
                          <div className="flex items-center gap-2">
                            <span className={`font-semibold ${deal.deal_status_id === 2 ? 'text-green-600' : deal.deal_status_id === 3 || deal.deal_status_id === 4 || deal.deal_status_id === 6 ? 'text-red-600' : 'text-yellow-600'}`}>
                              {/* replace raw ID with lookup text */}
                              {getStatusText(deal.deal_status_id)}
                            </span>
                            {deal.deal_status_id === 3 && deal.reviews?.length > 0  && (
    <button
      type="button"
      onClick={() => openReviewModal(deal)}
      className="inline-flex items-center gap-1 text-gray-600 hover:text-gray-800 text-sm"
      title="View review comments"
    >
      <FaCommentDots />
      { /* Optional badge with count */ }
     
        <span className="text-xs bg-gray-200 px-1 rounded">
          {deal.reviews.length}
        </span>
      
    </button>
  )}
                          </div>
                        

                                  <div className="space-x-2">
            <button
              onClick={() => editDeal(deal)}
              className="px-4 py-2 bg-green-600 text-white rounded-lg hover:bg-green-700"
            >
              Edit
            </button>
            <button
              onClick={() => deleteDeal(deal.deal_id)}
              className="px-4 py-2 bg-red-600 text-white rounded-lg hover:bg-red-700"
            >
              Delete
            </button>
          </div>
                      
                    </div>
                  </div>
            ))}
          </div>

          {/* Pagination hidden when filtering single deal */}
          {!showTargetOnly && totalPages > 1 && (
            <div className="flex justify-center mt-6">
              {Array.from({ length: totalPages }, (_, i) => i + 1).map(i => (
                <button
                  key={i}
                  onClick={() => setCurrentPage(i)}
                  className={`px-3 py-1 mx-1 rounded-lg ${
                    currentPage === i ? 'bg-green-600 text-white' : 'bg-gray-200 text-gray-700 hover:bg-gray-300'
                  }`}
                >
                  {i}
                </button>
              ))}
            </div>
          )}

          {/* Edit Modals (unchanged) */}
          <SubmitDealModal
            isOpen={isEditSubmitDealOpen}
            onClose={async () => {
              setIsEditSubmitDealOpen(false);
              setEditingDeal(null);
              await fetchPagedDeals();
            }}
            productId={editingDeal?.product_id || editingDeal?.productId}
            msrpPrice={editingDeal?.msrp}   // ADDED
            mode="edit"
            deal={editingDeal}
          />
          <ComboDealModal
            isOpen={isEditComboOpen}
            onClose={async () => {
              setIsEditComboOpen(false);
              setEditingDeal(null);
              await fetchPagedDeals();
            }}
            productId={editingDeal?.product_id || editingDeal?.productId || editingDeal?.product?.id}
            mode="edit"
            deal={editingDeal}
            onComboCreated={fetchPagedDeals}
            msrpPrice={editingDeal?.msrp} // ADDED: pass MSRP to compute discount
          />
        </>
      )}

      <ReviewCommentsModal
  isOpen={reviewModalOpen}
  onClose={closeReviewModal}
  deal={reviewModalDeal}  
  loading={reviewLoading}
/>
    </div>
  );
};

export default ProfilePage;