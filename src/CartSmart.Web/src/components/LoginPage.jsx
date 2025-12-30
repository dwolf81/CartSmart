import React, { useEffect, useState } from 'react';
import { Helmet } from 'react-helmet-async';
import { useNavigate, useLocation, useSearchParams } from 'react-router-dom';
import { GoogleLogin } from '@react-oauth/google';
import { useAuth } from '../context/AuthContext';
const API_URL = process.env.REACT_APP_API_URL || 'http://localhost:5000';
const APPLE_CLIENT_ID = process.env.REACT_APP_APPLE_CLIENT_ID;
const APPLE_REDIRECT_URI = process.env.REACT_APP_APPLE_REDIRECT_URI;

const LoginPage = () => {
  const [formData, setFormData] = useState({
    emailAddress: '',
    password: ''
  });
  const [error, setError] = useState('');
  const [appleReady, setAppleReady] = useState(false);
  const [appleLoading, setAppleLoading] = useState(false);
  const [resendMessage, setResendMessage] = useState('');
  const [resendLoading, setResendLoading] = useState(false);
  const navigate = useNavigate();
  const location = useLocation();
  const [searchParams] = useSearchParams();
  const { setUser, rehydrate } = useAuth(); // REMOVE setIsAuthenticated

  const pendingActivation = searchParams.get('pendingActivation') === '1';
  const pendingEmail = searchParams.get('email');
  const activated = searchParams.get('activated') === '1';

  // Init Apple JS when available
  /*useEffect(() => {
    if (window.__appleScriptLoading || window.AppleID) {
      try {
        if (window.AppleID && !appleReady) {
          window.AppleID.auth.init({
            clientId: APPLE_CLIENT_ID,
            scope: 'name email',
            redirectURI: APPLE_REDIRECT_URI,
            usePopup: true
          });
          setAppleReady(true);
        }
      } catch {}
      return;
    }
    window.__appleScriptLoading = true;
    const script = document.createElement('script');
    script.src = 'https://appleid.cdn-apple.com/appleauth/static/jsapi/appleid/1/en_US/appleid.auth.js';
    script.async = true;
    script.onload = () => {
      try {
        window.AppleID.auth.init({
          clientId: APPLE_CLIENT_ID,
          scope: 'name email',
          redirectURI: APPLE_REDIRECT_URI,
          usePopup: true
        });
        setAppleReady(true);
      } catch (e) {
        console.error('Apple init failed:', e);
      }
    };
    script.onerror = () => {
      setError('Failed to load Apple Sign-In script.');
    };
    document.body.appendChild(script);
  }, [appleReady]);*/

  // Helper to get redirect param from URL
  const getRedirectPath = () => {
    const params = new URLSearchParams(location.search);
    return params.get('redirect') || '/';
  };

  const handleSubmit = async (e) => {
    e.preventDefault();
    setError('');

    const { emailAddress, password } = formData;

    try {
      const response = await fetch(`${API_URL}/api/auth/login`, {
        method: 'POST',
        credentials: 'include',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ emailAddress, password })
      });

      const data = await response.json();

      // Replace handleSubmit logic after const data = await response.json();
      if (!response.ok || !data.success) {
        if (data.activationRequired) {
          setError("Account not activated. Check your email.");
        } else {
          setError(data.message || 'Login failed');
        }
        return;
      }

      // Update auth state
      setUser(data.user);
      await rehydrate();

      // Redirect to original page if present
      navigate(getRedirectPath(), { replace: true });
    } catch (err) {
      setError(err.message);
    }
  };

  const handleGoogleLogin = async (credentialResponse) => {
    try {
      const response = await fetch(`${API_URL}/api/auth/social-login`, {
        method: 'POST',
        credentials: 'include',
        headers: {
          'Content-Type': 'application/json',
        },
        body: JSON.stringify({
          provider: 'Google',
          token: credentialResponse.credential
        }),
      });

      const data = await response.json();

      if (!response.ok) {
        throw new Error(data.message || 'Google login failed');
      }

      // Update auth state
      setUser(data.user);
      await rehydrate();

      navigate(getRedirectPath(), { replace: true });
    } catch (err) {
      setError(err.message);
    }
  };

  const handleAppleLogin = async () => {
    setError('');
    if (!appleReady) {
      setError('Apple Sign-In not ready yet.');
      return;
    }
    setAppleLoading(true);
    try {
      const resp = await window.AppleID.auth.signIn();
      const idToken = resp?.authorization?.id_token;
      if (!idToken) throw new Error('Apple authentication failed.');
      const res = await fetch(`${API_URL}/api/auth/social-login`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify({ provider: 'apple', token: idToken })
      });
      const data = await res.json().catch(() => ({}));
      if (!res.ok || !data.success) throw new Error(data.message || 'Apple login failed.');
      setUser(data.user);
      navigate(getRedirectPath());
    } catch (e) {
      console.error('Apple login error:', e);
      setError(e instanceof Error ? e.message : 'Apple login interrupted.');
    } finally {
      setAppleLoading(false);
    }
  };

  const handleResend = async (email) => {
    if (!email) return;
    setResendLoading(true);
    setResendMessage('');
    try {
      const resp = await fetch(`${API_URL}/api/auth/send-activation`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ email })
      });
      if (!resp.ok) {
        setResendMessage('Failed to resend activation email.');
      } else {
        setResendMessage(`Activation email sent to ${email}.`);
      }
    } catch {
      setResendMessage('Failed to resend activation email.');
    } finally {
      setResendLoading(false);
    }
  };

  return (
    <div className="min-h-screen bg-gray-100 flex flex-col justify-center py-12 sm:px-6 lg:px-8">
      <Helmet>
        <title>Log In — CartSmart</title>
        <meta name="description" content="Access your CartSmart account to track deals and manage settings." />
        <link rel="canonical" href={`${process.env.REACT_APP_SITE_URL || (typeof window !== 'undefined' ? window.location.origin : '')}/login`} />
        <meta name="robots" content="noindex,follow" />
        <meta property="og:type" content="website" />
        <meta property="og:site_name" content="CartSmart" />
        <meta property="og:title" content="Log In — CartSmart" />
        <meta property="og:description" content="Access your CartSmart account to track deals and manage settings." />
        <meta property="og:url" content={`${process.env.REACT_APP_SITE_URL || (typeof window !== 'undefined' ? window.location.origin : '')}/login`} />
        <meta name="twitter:card" content="summary" />
        <meta name="twitter:title" content="Log In — CartSmart" />
        <meta name="twitter:description" content="Access your CartSmart account to track deals and manage settings." />
      </Helmet>
      <div className="sm:mx-auto sm:w-full sm:max-w-md">
        {/* Logo */}
        <div className="flex justify-center">
          <a href="/" className="flex items-center">
            <span className="text-3xl font-bold text-[#4CAF50]">CartSmart</span>            
          </a>
        </div>
        <h2 className="mt-6 text-center text-3xl font-extrabold text-gray-900">
          Sign in to your account
        </h2>
        <p className="mt-2 text-center text-sm text-gray-600">
          Or{' '}
          <a href="/signup" className="font-medium text-[#4CAF50] hover:text-[#3d8b40]">
            create a new account
          </a>
        </p>
      </div>

      <div className="mt-8 sm:mx-auto sm:w-full sm:max-w-md">
        <div className="bg-white py-8 px-4 shadow sm:rounded-lg sm:px-10">
          {/* Unified message area (top) */}
          <div className="space-y-4 mb-6">
            {resendMessage && (                         // NEW
              <div className="text-sm bg-blue-50 border border-blue-300 text-blue-800 p-3 rounded text-center">
                {resendMessage}
              </div>
            )}

            {error && (
              <div className="text-sm bg-red-50 border border-red-300 text-red-700 p-3 rounded text-center">
                {error}
                {error.toLowerCase().includes('activate') && formData.emailAddress && (
                  <button
                    type="button"
                    onClick={() => handleResend(formData.emailAddress)} // CHANGED
                    disabled={resendLoading}
                    className="ml-2 text-blue-600 underline disabled:opacity-50"
                  >
                    {resendLoading ? 'Resending…' : 'Resend activation email'}
                  </button>
                )}
              </div>
            )}

            {pendingActivation && (
              <div className="text-sm bg-yellow-50 border border-yellow-300 text-yellow-800 p-3 rounded text-center">
                Account created. Please check {pendingEmail} for an activation link.
                <button
                  type="button"
                  onClick={() => handleResend(pendingEmail)} // CHANGED
                  disabled={resendLoading}
                  className="ml-2 text-blue-600 underline disabled:opacity-50"
                >
                  {resendLoading ? 'Resending…' : 'Resend email'}
                </button>
              </div>
            )}

            {activated && (
              <div className="text-sm bg-green-50 border border-green-300 text-green-800 p-3 rounded text-center">
                Your account is activated. Please sign in.
              </div>
            )}
          </div>
          {/* SSO first */}
          <div className="mt-2 flex justify-center">
            <GoogleLogin
              onSuccess={handleGoogleLogin}
              onError={() => setError('Google login failed')}
            />
          </div>
          <div className="mt-6">
            <div className="relative">
              <div className="absolute inset-0 flex items-center">
                <div className="w-full border-t border-gray-300" />
              </div>
              <div className="relative flex justify-center text-sm">
                <span className="px-2 bg-white text-gray-500">Or sign in with email</span>
              </div>
            </div>
          </div>

          <form className="space-y-6" onSubmit={handleSubmit}>
            <div>
              <label htmlFor="email" className="block text-sm font-medium text-gray-700">
                Email address
              </label>
              <div className="mt-1">
                <input
                  id="email"
                  name="email"
                  type="email"
                  required
                  value={formData.emailAddress}
                  onChange={(e) => setFormData({ ...formData, emailAddress: e.target.value })}
                  className="appearance-none block w-full px-3 py-2 border border-gray-300 rounded-md shadow-sm placeholder-gray-400 focus:outline-none focus:ring-[#4CAF50] focus:border-[#4CAF50] sm:text-sm"
                />
              </div>
            </div>

            <div>
              <label htmlFor="password" className="block text-sm font-medium text-gray-700">
                Password
              </label>
              <div className="mt-1">
                <input
                  id="password"
                  name="password"
                  type="password"
                  required
                  value={formData.password}
                  onChange={(e) => setFormData({ ...formData, password: e.target.value })}
                  className="appearance-none block w-full px-3 py-2 border border-gray-300 rounded-md shadow-sm placeholder-gray-400 focus:outline-none focus:ring-[#4CAF50] focus:border-[#4CAF50] sm:text-sm"
                />
              </div>
            </div>

            <div className="flex items-center justify-between">
              <div />
              <button
                type="button"
                onClick={() => navigate('/forgot-password')}
                className="text-sm text-blue-600 hover:underline"
              >
                Forgot password?
              </button>
            </div>

            <div>
              <button
                type="submit"
                className="w-full flex justify-center py-2 px-4 border border-transparent rounded-md shadow-sm text-sm font-medium text-white bg-[#4CAF50] hover:bg-[#3d8b40] focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-[#4CAF50]"
              >
                Sign in
              </button>
            </div>
          </form>
        </div>
      </div>
    </div>
  );
};

export default LoginPage;