import React, { useEffect, useState, useCallback } from 'react';
import { Helmet } from 'react-helmet-async';
import { useAuth } from '../context/AuthContext';
import LoadingSpinner from './LoadingSpinner';
import { useNavigate } from 'react-router-dom';

const API_URL = process.env.REACT_APP_API_URL || 'http://localhost:5000';

const inputClasses =
  'appearance-none block w-full px-3 py-2 border border-gray-300 rounded-md shadow-sm placeholder-gray-400 ' +
  'focus:outline-none focus:ring-[#4CAF50] focus:border-[#4CAF50] sm:text-sm';

const SettingsPage = () => {
  const auth = useAuth();
  const {
    user,
    setUser,
    isAuthenticated = false,
    loading: authLoading = true,
    rehydrate = async () => {}
  } = auth || {};
  const navigate = useNavigate();
  const [activeTab, setActiveTab] = useState('profile');
  const [message, setMessage] = useState({ type: '', content: '' });
  const [loading, setLoading] = useState(false); // keep as local "loading" for API ops

  const [profileData, setProfileData] = useState({
    userName: user?.userName || '',
    email: user?.email || '',
    firstName: user?.firstName || '',
    lastName: user?.lastName || ''
  });

  const [currentPasswordConfirm, setCurrentPasswordConfirm] = useState(''); // required on both tabs

  const [passwordData, setPasswordData] = useState({
    currentPassword: '',
    newPassword: '',
    confirmPassword: ''
  });

  const [optedIntoEmails, setOptedIntoEmails] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');

  const [usernameAvailability, setUsernameAvailability] = useState({ checking: false, available: true, message: '' });
  const originalUserName = user?.userName || '';

  // New local state for account deletion
  const [deleteConfirm, setDeleteConfirm] = useState('');
  const [deletePassword, setDeletePassword] = useState('');
  const [deleting, setDeleting] = useState(false);

  // DEBUG: see auth state transitions
  useEffect(() => {
    console.log('SettingsPage auth state', { isAuthenticated, authLoading, user });
  }, [isAuthenticated, authLoading, user]);

  useEffect(() => {
    if (!authLoading && !isAuthenticated) navigate('/login');
  }, [authLoading, isAuthenticated, navigate]);

  // Load profile (and email opt-in if the API returns it) from API instead of cookie
  useEffect(() => {
    if (!isAuthenticated) return;
    let cancelled = false;
    (async () => {
      setLoading(true);
      try {
        const token = localStorage.getItem('token');
        const res = await fetch(`${API_URL}/api/users/profile`, {
          method: 'GET',
          credentials: 'include',
          headers: {
            'Content-Type': 'application/json',
            ...(token ? { Authorization: `Bearer ${token}` } : {})
          }
        });
        const data = await res.json().catch(() => ({}));
        if (!res.ok) throw new Error(data.message || 'Failed to load profile');
        if (!cancelled) {
          setProfileData({
            userName: data.userName || data.username || '',
            email: data.email || '',
            firstName: data.firstName || '',
            lastName: data.lastName || ''
          });
          if (typeof data.optedIntoEmails !== 'undefined') setOptedIntoEmails(!!data.optedIntoEmails);
          setUser?.((prev) => ({ ...(prev || {}), ...data }));
        }
      } catch (e) {
        if (!cancelled) setMessage({ type: 'error', content: e.message || 'Failed to load profile' });
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();
    return () => { cancelled = true; };
  }, [isAuthenticated, setUser]);

  // Username availability + policy check
  const checkUsername = useCallback(async (userName) => {
    if (!userName || userName === originalUserName) {
      setUsernameAvailability({ checking: false, available: true, message: '' });
      return true;
    }
    setUsernameAvailability({ checking: true, available: true, message: '' });
    try {
      const res = await fetch(`${API_URL}/api/users/validate-username?username=${encodeURIComponent(userName)}`, {
        method: 'GET',
        credentials: 'include'
      });
      const data = await res.json().catch(() => ({}));
      const valid = data.valid !== false;
      const available = !!data.available;
      const message = data.message || (valid && !available ? 'Username is already taken.' : '');
      setUsernameAvailability({
        checking: false,
        available: valid && available,
        message
      });
      return valid && available;
    } catch {
      setUsernameAvailability({
        checking: false,
        available: false,
        message: 'Unable to validate username.'
      });
      return false;
    }
  }, [API_URL, originalUserName]);

  const passwordPolicyError = (pwd, email, first, last) => {
    if (!pwd || pwd.length < 8) return 'Use at least 8 characters.';
    if (/\s/.test(pwd)) return 'No spaces allowed in password.';
    const hasLower = /[a-z]/.test(pwd);
    const hasUpper = /[A-Z]/.test(pwd);
    const hasDigit = /\d/.test(pwd);
    const hasSymbol = /[^A-Za-z0-9]/.test(pwd);
    if ((hasLower + hasUpper + hasDigit + hasSymbol) < 3) return 'Use a mix of letters, numbers, and symbols.';
    if (email) {
      const local = email.split('@')[0];
      if (local && local.length >= 3 && pwd.toLowerCase().includes(local.toLowerCase())) return 'Avoid using your email in the password.';
    }
    if (first && first.length >= 3 && pwd.toLowerCase().includes(first.toLowerCase())) return 'Avoid using your first name in the password.';
    if (last && last.length >= 3 && pwd.toLowerCase().includes(last.toLowerCase())) return 'Avoid using your last name in the password.';
    return null;
  };

  const handleProfileUpdate = async (e) => {
    e.preventDefault();
    setMessage({ type: '', content: '' });

    // Require current password confirmation on profile updates
    if (!currentPasswordConfirm) {
      setMessage({ type: 'error', content: 'Please enter your current password to save profile changes.' });
      return;
    }

    // Validate username availability if changed
    const userNameOk = await checkUsername(profileData.userName);
    if (!userNameOk) {
      setMessage({ type: 'error', content: usernameAvailability.message || 'Username is not available.' });
      return;
    }

    setLoading(true);
    try {
      const response = await fetch(`${API_URL}/api/users/profile`, {
        method: 'PUT',
        credentials: 'include',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          ...profileData,
          currentPassword: currentPasswordConfirm // let API verify before applying changes
        })
      });
      const data = await response.json();
      if (!response.ok) throw new Error(data.message || 'Failed to update profile');

      // Sync local auth user
      setUser({ ...(user || {}), ...profileData });
      await rehydrate();
      setMessage({ type: 'success', content: 'Profile updated successfully!' });
      setCurrentPasswordConfirm('');
    } catch (error) {
      setMessage({ type: 'error', content: error.message });
    } finally {
      setLoading(false);
    }
  };

  const handlePasswordChange = async (e) => {
    e.preventDefault();
    setMessage({ type: '', content: '' });

    if (passwordData.newPassword !== passwordData.confirmPassword) {
      setMessage({ type: 'error', content: 'New passwords do not match' });
      return;
    }
    const pe = passwordPolicyError(passwordData.newPassword, profileData.email, profileData.firstName, profileData.lastName);
    if (pe) {
      setMessage({ type: 'error', content: pe });
      return;
    }

    if (!passwordData.currentPassword) {
      setMessage({ type: 'error', content: 'Please enter your current password.' });
      return;
    }

    setLoading(true);
    try {
      const response = await fetch(`${API_URL}/api/users/change-password`, {
        method: 'POST',
        credentials: 'include',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          currentPassword: passwordData.currentPassword,
          newPassword: passwordData.newPassword
        })
      });

      const data = await response.json();
      if (!response.ok) throw new Error(data.message || 'Failed to change password');

      setPasswordData({ currentPassword: '', newPassword: '', confirmPassword: '' });
      setMessage({ type: 'success', content: 'Password changed successfully!' });
    } catch (error) {
      setMessage({ type: 'error', content: error.message });
    } finally {
      setLoading(false);
    }
  };

  // New handler for account deletion
  const handleAccountDelete = async (e) => {
    e.preventDefault();
    setMessage({ type: '', content: '' });
    if (deleteConfirm.trim().toUpperCase() !== 'DELETE') {
      setMessage({ type: 'error', content: 'Type DELETE to confirm.' });
      return;
    }
    if (!deletePassword) {
      setMessage({ type: 'error', content: 'Enter current password.' });
      return;
    }
    setDeleting(true);
    try {
      const res = await fetch(`${API_URL}/api/users/account`, {
        method: 'DELETE',
        credentials: 'include',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          currentPassword: deletePassword,
          confirmation: deleteConfirm
        })
      });
      const data = await res.json().catch(() => ({}));
      if (!res.ok) throw new Error(data.message || 'Failed to delete account.');
      // Clear auth context & redirect
      setUser?.(null);
      localStorage.removeItem('token');
      setMessage({ type: 'success', content: 'Account deleted.' });
      setTimeout(() => {
        navigate('/signup', { replace: true });
      }, 1200);
    } catch (err) {
      setMessage({ type: 'error', content: err.message });
    } finally {
      setDeleting(false);
    }
  };

  if (authLoading || loading) return <LoadingSpinner />;

  return (
    <div className="container mx-auto px-4 py-8">
      <Helmet>
        <title>Account Settings — CartSmart</title>
        <meta name="description" content="Manage your CartSmart profile, security settings, and preferences." />
        <link rel="canonical" href={`${process.env.REACT_APP_SITE_URL || (typeof window !== 'undefined' ? window.location.origin : '')}/settings`} />
        <meta property="og:type" content="website" />
        <meta property="og:site_name" content="CartSmart" />
        <meta property="og:title" content="Account Settings — CartSmart" />
        <meta property="og:description" content="Manage your CartSmart profile, security settings, and preferences." />
        <meta property="og:url" content={`${process.env.REACT_APP_SITE_URL || (typeof window !== 'undefined' ? window.location.origin : '')}/settings`} />
        <meta name="twitter:card" content="summary" />
        <meta name="twitter:title" content="Account Settings — CartSmart" />
        <meta name="twitter:description" content="Manage your CartSmart profile, security settings, and preferences." />
      </Helmet>
      <div className="max-w-3xl mx-auto">
        <h1 className="text-3xl font-bold mb-8">Account Settings</h1>

        {message.content && (
          <div className={`mb-4 p-4 rounded ${message.type === 'success' ? 'bg-green-100 text-green-700' : 'bg-red-100 text-red-700'}`}>
            {message.content}
          </div>
        )}

        {/* Settings Navigation */}
        <div className="flex border-b mb-6">
          <button
            className={`py-2 px-4 ${activeTab === 'profile' ? 'border-b-2 border-[#4CAF50] text-[#4CAF50]' : 'text-gray-600'}`}
            onClick={() => setActiveTab('profile')}
          >
            Profile
          </button>
          <button
            className={`py-2 px-4 ${activeTab === 'security' ? 'border-b-2 border-[#4CAF50] text-[#4CAF50]' : 'text-gray-600'}`}
            onClick={() => setActiveTab('security')}
          >
            Security
          </button>
          <button
            className={`py-2 px-4 ${activeTab === 'delete' ? 'border-b-2 border-red-600 text-red-600' : 'text-gray-600'}`}
            onClick={() => setActiveTab('delete')}
          >
            Delete Account
          </button>
        </div>

        {/* Profile Settings */}
        {activeTab === 'profile' && (
          <form onSubmit={handleProfileUpdate} className="space-y-6">
            {/* Current password first */}
            <div>
              <label className="block text-sm font-medium text-gray-700">Current Password</label>
              <input
                type="password"
                value={currentPasswordConfirm}
                onChange={(e) => setCurrentPasswordConfirm(e.target.value)}
                className={inputClasses}
                placeholder="Enter current password"
              />              
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700">Username</label>
              <input
                type="text"
                value={profileData.userName}
                onChange={(e) => {
                  setProfileData({ ...profileData, userName: e.target.value.trim() });
                  setUsernameAvailability({ checking: false, available: true, message: '' });
                }}
                onBlur={() => checkUsername(profileData.userName)}
                className={inputClasses}
                placeholder="Your username"
              />
              {usernameAvailability.message && (
                <p className={`mt-1 text-xs ${usernameAvailability.available ? 'text-green-600' : 'text-red-600'}`}>
                  {usernameAvailability.message}
                </p>
              )}
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700">Email</label>
              <input
                type="email"
                value={profileData.email}
                readOnly
                disabled
                title="Email changes coming soon"
                className={`${inputClasses} bg-gray-100 cursor-not-allowed`}
              />
              <p className="text-xs text-gray-500 mt-1">Email changes will be available soon.</p>
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700">First Name</label>
              <input
                type="text"
                value={profileData.firstName}
                onChange={(e) => setProfileData({ ...profileData, firstName: e.target.value })}
                className={inputClasses}
                placeholder="First name"
              />
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700">Last Name</label>
              <input
                type="text"
                value={profileData.lastName}
                onChange={(e) => setProfileData({ ...profileData, lastName: e.target.value })}
                className={inputClasses}
                placeholder="Last name"
              />
            </div>

            {/* Opt-in toggle remains */}
            <div>
              <input
                id="settings-optin"
                type="checkbox"
                checked={optedIntoEmails}
                onChange={(e) => {
                  const v = e.target.checked;
                  setOptedIntoEmails(v);
                 
                }}
                className="h-4 w-4 text-[#4CAF50] border-gray-300 rounded"
              />
              <label htmlFor="settings-optin" className="ml-2 text-sm text-gray-700">
                Send me the best deals and offers in my inbox
              </label>
              {error && <div className="text-xs text-red-600 mt-1">{error}</div>}
            </div>

            <button
              type="submit"
              disabled={loading || usernameAvailability.checking || (!usernameAvailability.available && profileData.userName !== originalUserName)}
              className="w-full flex justify-center py-2 px-4 border border-transparent rounded-md shadow-sm text-sm font-medium text-white bg-[#4CAF50] hover:bg-[#3d8b40] focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-[#4CAF50] disabled:opacity-50"
            >
              {loading ? 'Saving...' : 'Save Changes'}
            </button>
          </form>
        )}

        {/* Security Settings */}
        {activeTab === 'security' && (
          <form onSubmit={handlePasswordChange} className="space-y-6">
            <div>
              <label className="block text-sm font-medium text-gray-700">Current Password</label>
              <input
                type="password"
                value={passwordData.currentPassword}
                onChange={(e) => setPasswordData({ ...passwordData, currentPassword: e.target.value })}
                className={inputClasses}
                placeholder="Enter current password"
              />
              
            </div>
            <div>
              <label className="block text-sm font-medium text-gray-700">New Password</label>
              <input
                type="password"
                value={passwordData.newPassword}
                onChange={(e) => setPasswordData({ ...passwordData, newPassword: e.target.value })}
                className={inputClasses}
                placeholder="Enter new password"
              />
              <div className="text-xs text-gray-500 mt-1">
                Use 8+ characters and include letters, numbers, and symbols. Avoid personal info.
              </div>
            </div>
            <div>
              <label className="block text-sm font-medium text-gray-700">Confirm New Password</label>
              <input
                type="password"
                value={passwordData.confirmPassword}
                onChange={(e) => setPasswordData({ ...passwordData, confirmPassword: e.target.value })}
                className={inputClasses}
                placeholder="Confirm new password"
              />
            </div>
            <button
              type="submit"
              disabled={loading}
              className="w-full flex justify-center py-2 px-4 border border-transparent rounded-md shadow-sm text-sm font-medium text-white bg-[#4CAF50] hover:bg-[#3d8b40] focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-[#4CAF50] disabled:opacity-50"
            >
              {loading ? 'Changing Password...' : 'Change Password'}
            </button>
          </form>
        )}

        {/* Delete Account tab content */}
        {activeTab === 'delete' && (
          <form onSubmit={handleAccountDelete} className="space-y-6 max-w-md">            
            <p className="text-sm text-gray-600">
              Deleting your account is permanent. All your deals will be marked as deleted and hidden.
            </p>
            <div>
              <label className="block text-sm font-medium text-gray-700">Current Password</label>
              <input
                type="password"
                value={deletePassword}
                onChange={e => setDeletePassword(e.target.value)}
                className={inputClasses}
                placeholder="Current password"
              />
            </div>
            <div>
              <label className="block text-sm font-medium text-gray-700">Confirmation</label>
              <input
                type="text"
                value={deleteConfirm}
                onChange={e => setDeleteConfirm(e.target.value)}
                className={inputClasses}
                placeholder='Type DELETE to confirm'
              />
              <p className="text-xs text-gray-500 mt-1">You must type DELETE (all caps) to confirm.</p>
            </div>
            <button
              type="submit"
              disabled={deleting}
              className="w-full py-2 px-4 rounded-md text-sm font-medium bg-red-600 text-white hover:bg-red-700 disabled:opacity-50"
            >
              {deleting ? 'Deleting...' : 'Delete Account'}
            </button>
          </form>
        )}
      </div>
    </div>
  );
};

export default SettingsPage;