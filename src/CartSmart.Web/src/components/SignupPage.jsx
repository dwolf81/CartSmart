import React, { useState, useRef, useEffect } from 'react';
import { Helmet } from 'react-helmet-async';
import { useNavigate, useLocation, useSearchParams } from 'react-router-dom';
import { GoogleLogin } from '@react-oauth/google';
import ReCAPTCHA from 'react-google-recaptcha';
import { useAuth } from '../context/AuthContext';

const API_URL = process.env.REACT_APP_API_URL || 'http://localhost:5000';
const RECAPTCHA_SITE_KEY = process.env.REACT_APP_RECAPTCHA_SITE_KEY;
const APPLE_CLIENT_ID = process.env.REACT_APP_APPLE_CLIENT_ID;
const APPLE_REDIRECT_URI = process.env.REACT_APP_APPLE_REDIRECT_URI;

const SignupPage = () => {
  const [formData, setFormData] = useState({
    firstName: '',
    lastName: '',
    email: '',
    password: '',
    confirmPassword: '',
    optedIntoEmails: false,
  });
  const [error, setError] = useState('');
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [appleLoading, setAppleLoading] = useState(false);
  const [appleReady, setAppleReady] = useState(false);
  const [ssoOptIn, setSsoOptIn] = useState(false);
  const navigate = useNavigate();
  const recaptchaRef = useRef(null);
  const { setUser, rehydrate } = useAuth(); // remove setIsAuthenticated
  const location = useLocation();

  /*useEffect(() => {
    // Avoid double loading
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
      } catch {  }
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
        setError('Apple Sign-In initialization failed.');
      }
    };
    script.onerror = () => {
      setError('Failed to load Apple Sign-In script.');
    };
    document.body.appendChild(script);
  }, [appleReady]);*/
  const getRedirectPath = () => {
    const params = new URLSearchParams(location.search);
    return params.get('redirect') || '/';
  };

  const passwordPolicyError = (pwd, email, first, last) => {
    if (!pwd || pwd.length < 8) return 'Use at least 8 characters.';
    if (/\s/.test(pwd)) return 'No spaces allowed in password.';
    const hasLower = /[a-z]/.test(pwd);
    const hasUpper = /[A-Z]/.test(pwd);
    const hasDigit = /\d/.test(pwd);
    const hasSymbol = /[^A-Za-z0-9]/.test(pwd);
    if ((hasLower + hasUpper + hasDigit + hasSymbol) < 3) return 'Use a mix of letters, numbers, and symbols.';
    const local = (email || '').split('@')[0];
    if (local && local.length >= 3 && pwd.toLowerCase().includes(local.toLowerCase())) return 'Avoid using your email in the password.';
    if (first && first.length >= 3 && pwd.toLowerCase().includes(first.toLowerCase())) return 'Avoid using your first name in the password.';
    if (last && last.length >= 3 && pwd.toLowerCase().includes(last.toLowerCase())) return 'Avoid using your last name in the password.';
    return null;
  };

  const handleSubmit = async (e) => {
    e.preventDefault();
    setError('');
    setIsSubmitting(true);

    if (formData.password !== formData.confirmPassword) {
      setError('Passwords do not match');
      setIsSubmitting(false);
      return;
    }

    const recaptchaToken = recaptchaRef.current?.getValue();
    if (!recaptchaToken) {
      setError('Please complete the reCAPTCHA');
      setIsSubmitting(false);
      return;
    }

    const policyErr = passwordPolicyError(formData.password, formData.email, formData.firstName, formData.lastName);
    if (policyErr) { setError(policyErr); setIsSubmitting(false); return; }

    try {
      const response = await fetch(`${API_URL}/api/auth/register`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          firstName: formData.firstName,
          lastName: formData.lastName,
          email: formData.email,
          password: formData.password,
          emailOptIn: !!formData.optedIntoEmails,
          recaptchaToken
        }),
      });

      const data = await response.json();

      if (!response.ok) throw new Error(data.message || 'Registration failed');

      if (data.success) {
        navigate(`/login?pendingActivation=1&email=${encodeURIComponent(formData.email)}`);
        return;
      }

      localStorage.removeItem('token');
      localStorage.removeItem('user');
      navigate('/');
    } catch (err) {
      setError(err.message);
      recaptchaRef.current?.reset();
    } finally {
      setIsSubmitting(false);
    }
  };

  // Google SSO (no recaptcha needed)
  const handleGoogleSignup = async (credentialResponse) => {
    setError('');
    try {
      if (!credentialResponse?.credential) throw new Error('Missing Google credential.');
      const response = await fetch(`${API_URL}/api/auth/social-login`, {
        method: 'POST',
        credentials: 'include',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          provider: 'Google',
          token: credentialResponse.credential,
          optedIntoEmails: ssoOptIn
        }),
      });
      const data = await response.json();
      if (!response.ok || !data.success) throw new Error(data.message || 'Google signup failed');

      // optimistic set, then hydrate authoritative profile (adds allowReview, imageUrl, etc.)
      if (data.user) setUser(data.user);
      await rehydrate();

      navigate(getRedirectPath(), { replace: true });
    } catch (err) {
      setError(err.message);
    }
  };

  // Apple SSO (no recaptcha needed)
  const handleAppleSignup = async () => {
    setError('');
    if (!appleReady) {
      setError('Apple Sign-In not ready. Please wait a moment and try again.');
      return;
    }
    setAppleLoading(true);
    try {
      const resp = await window.AppleID.auth.signIn();
      console.debug('Apple response:', resp);
      const idToken = resp?.authorization?.id_token;
      if (!idToken) throw new Error('No Apple id_token returned.');
      const response = await fetch(`${API_URL}/api/auth/social-login`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify({ provider: 'apple', token: idToken, optedIntoEmails: true })
      });
      const data = await response.json().catch(() => ({}));
      if (!response.ok || !data.success) throw new Error(data.message || 'Apple signup failed.');
      localStorage.setItem('token', data.token);
      localStorage.setItem('user', JSON.stringify(data.user));
      navigate('/');
    } catch (e) {
      console.error('Apple sign-in error:', e);
      setError(e instanceof Error ? e.message : 'Apple login failed.');
    } finally {
      setAppleLoading(false);
    }
  };

  return (
    <div className="min-h-screen bg-gray-100 flex flex-col justify-center py-12 sm:px-6 lg:px-8">
      <Helmet>
        <title>Sign Up — CartSmart</title>
        <meta name="description" content="Create your CartSmart account to discover and track the best deals." />
        <link rel="canonical" href={`${process.env.REACT_APP_SITE_URL || (typeof window !== 'undefined' ? window.location.origin : '')}/signup`} />
        <meta name="robots" content="noindex,follow" />
        <meta property="og:type" content="website" />
        <meta property="og:site_name" content="CartSmart" />
        <meta property="og:title" content="Sign Up — CartSmart" />
        <meta property="og:description" content="Create your CartSmart account to discover and track the best deals." />
        <meta property="og:url" content={`${process.env.REACT_APP_SITE_URL || (typeof window !== 'undefined' ? window.location.origin : '')}/signup`} />
        <meta name="twitter:card" content="summary" />
        <meta name="twitter:title" content="Sign Up — CartSmart" />
        <meta name="twitter:description" content="Create your CartSmart account to discover and track the best deals." />
      </Helmet>
      <div className="sm:mx-auto sm:w-full sm:max-w-md">
        <div className="flex justify-center">
          <a href="/" className="flex items-center">
            <span className="text-3xl font-bold text-[#4CAF50]">CartSmart</span>
            
          </a>
        </div>
        <h2 className="mt-6 text-center text-3xl font-extrabold text-gray-900">
          Create your account
        </h2>
        <p className="mt-2 text-center text-sm text-gray-600">
          Or{' '}
          <a href="/login" className="font-medium text-[#4CAF50] hover:text-[#3d8b40]">
            sign in to your existing account
          </a>
        </p>
      </div>

      <div className="mt-8 sm:mx-auto sm:w-full sm:max-w-md">
        <div className="bg-white py-8 px-4 shadow sm:rounded-lg sm:px-10">
          {error && <div className="mb-4 text-red-600 text-center">{error}</div>}

            {/* SSO first */}
            <div className="mt-2 flex justify-center">
              <GoogleLogin
                onSuccess={handleGoogleSignup}
                onError={() => setError('Google login failed')}
              />
            </div>
            <div className="mt-4 flex items-center justify-center">
              <input
                id="ssoOptIn"
                type="checkbox"
                checked={ssoOptIn}
                onChange={(e) => setSsoOptIn(e.target.checked)}
                className="h-4 w-4 text-[#4CAF50] border-gray-300 rounded"
              />
              <label htmlFor="ssoOptIn" className="ml-2 text-sm text-gray-700">
                Email me the best deals and offers
              </label>
            </div>
            <div className="mt-6">
              <div className="relative">
                <div className="absolute inset-0 flex items-center">
                  <div className="w-full border-t border-gray-300" />
                </div>
                <div className="relative flex justify-center text-sm">
                  <span className="px-2 bg-white text-gray-500">Or sign up with email</span>
                </div>
              </div>
            </div>

            <form className="space-y-6" onSubmit={handleSubmit}>
              <div className="grid grid-cols-2 gap-4">
                <div>
                  <label htmlFor="firstName" className="block text-sm font-medium text-gray-700">
                    First Name
                  </label>
                  <div className="mt-1">
                    <input
                      id="firstName"
                      name="firstName"
                      type="text"
                      required
                      value={formData.firstName}
                      onChange={(e) => setFormData({ ...formData, firstName: e.target.value })}
                      className="appearance-none block w-full px-3 py-2 border border-gray-300 rounded-md shadow-sm placeholder-gray-400 focus:outline-none focus:ring-[#4CAF50] focus:border-[#4CAF50] sm:text-sm"
                    />
                  </div>
                </div>
                <div>
                  <label htmlFor="lastName" className="block text-sm font-medium text-gray-700">
                    Last Name
                  </label>
                  <div className="mt-1">
                    <input
                      id="lastName"
                      name="lastName"
                      type="text"
                      required
                      value={formData.lastName}
                      onChange={(e) => setFormData({ ...formData, lastName: e.target.value })}
                      className="appearance-none block w-full px-3 py-2 border border-gray-300 rounded-md shadow-sm placeholder-gray-400 focus:outline-none focus:ring-[#4CAF50] focus:border-[#4CAF50] sm:text-sm"
                    />
                  </div>
                </div>
              </div>

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
                    value={formData.email}
                    onChange={(e) => setFormData({ ...formData, email: e.target.value })}
                    className="appearance-none block w-full px-3 py-2 border border-gray-300 rounded-md shadow-sm placeholder-gray-400 focus:outline-none focus:ring-[#4CAF50] focus:border-[#4CAF50] sm:text-sm"
                  />
                </div>
              </div>

              <div>
                <label htmlFor="password" className="block text-sm font-medium text-gray-700">
                  New Password
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

              <div>
                <label htmlFor="confirmPassword" className="block text sm font-medium text-gray-700">
                  Confirm New Password
                </label>
                <div className="mt-1">
                  <input
                    id="confirmPassword"
                    name="confirmPassword"
                    type="password"
                    required
                    value={formData.confirmPassword}
                    onChange={(e) => setFormData({ ...formData, confirmPassword: e.target.value })}
                    className="appearance-none block w-full px-3 py-2 border border-gray-300 rounded-md shadow-sm placeholder-gray-400 focus:outline-none focus:ring-[#4CAF50] focus:border-[#4CAF50] sm:text-sm"
                  />
                </div>
              </div>
              <div className="text-xs text-gray-500 mt-1">
                Use 8+ characters and include letters, numbers, and symbols. Avoid personal info.
              </div>

              <div className="flex items-center">
                <input
                  id="optIn"
                  type="checkbox"
                  checked={!!formData.optedIntoEmails}
                  onChange={(e) => setFormData({ ...formData, optedIntoEmails: e.target.checked })}
                  className="h-4 w-4 text-[#4CAF50] border-gray-300 rounded"
                />
                <label htmlFor="optIn" className="ml-2 block text-sm text-gray-700">
                  Email me the best deals and offers
                </label>
              </div>

              <div className="flex justify-center">
                {RECAPTCHA_SITE_KEY && (
                  <ReCAPTCHA
                    ref={recaptchaRef}
                    sitekey={RECAPTCHA_SITE_KEY}
                    theme="light"
                  />
                )}
              </div>

              <div>
                <button
                  type="submit"
                  disabled={isSubmitting}
                  className={`w-full flex justify-center py-2 px-4 border border-transparent rounded-md shadow-sm text-sm font-medium text-white ${
                    isSubmitting ? 'bg-gray-400 cursor-not-allowed' : 'bg-[#4CAF50] hover:bg-[#3d8b40]'
                  } focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-[#4CAF50]`}
                >
                  {isSubmitting ? 'Creating Account...' : 'Sign up'}
                </button>
              </div>
              <p className="mt-3 text-xs text-gray-600 text-center">
                By signing up, you agree to our
                {' '}<a href="/terms" className="underline">Terms of Service</a>{' '}and{' '}
                <a href="/privacy" className="underline">Privacy Policy</a>.
              </p>
            </form>
          </div>
        </div>
      </div>

  );
};

export default SignupPage;