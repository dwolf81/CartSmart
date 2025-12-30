import { useState } from 'react';
import { Helmet } from 'react-helmet-async';
const API_URL = process.env.REACT_APP_API_URL || 'http://localhost:5000';

const ForgotPasswordPage = () => {
  const [email, setEmail] = useState('');
  const [sent, setSent] = useState(false);
  const [error, setError] = useState('');

  const submit = async (e) => {
    e.preventDefault();
    setError('');
    try {
      await fetch(`${API_URL}/api/auth/request-password-reset`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify({ email })
      });
      setSent(true); // Always OK to avoid account enumeration
    } catch {
      setSent(true);
    }
  };

  if (sent) {
    return (
      <div className="max-w-md mx-auto p-6">
        <Helmet>
          <title>Forgot Password — CartSmart</title>
          <meta name="description" content="Request a password reset for your CartSmart account." />
          <link rel="canonical" href={`${process.env.REACT_APP_SITE_URL || (typeof window !== 'undefined' ? window.location.origin : '')}/forgot-password`} />
          <meta name="robots" content="noindex,follow" />
          <meta property="og:type" content="website" />
          <meta property="og:site_name" content="CartSmart" />
          <meta property="og:title" content="Forgot Password — CartSmart" />
          <meta property="og:description" content="Request a password reset for your CartSmart account." />
          <meta property="og:url" content={`${process.env.REACT_APP_SITE_URL || (typeof window !== 'undefined' ? window.location.origin : '')}/forgot-password`} />
          <meta name="twitter:card" content="summary" />
          <meta name="twitter:title" content="Forgot Password — CartSmart" />
          <meta name="twitter:description" content="Request a password reset for your CartSmart account." />
        </Helmet>
        <h1 className="text-lg font-semibold mb-2">Check your email</h1>
        <p>If an account exists for that address, a reset link has been sent.</p>
      </div>
    );
  }

  return (
    <div className="max-w-md mx-auto p-6">
      <Helmet>
        <title>Forgot Password — CartSmart</title>
        <meta name="description" content="Request a password reset for your CartSmart account." />
        <link rel="canonical" href={`${process.env.REACT_APP_SITE_URL || (typeof window !== 'undefined' ? window.location.origin : '')}/forgot-password`} />
        <meta name="robots" content="noindex,follow" />
        <meta property="og:type" content="website" />
        <meta property="og:site_name" content="CartSmart" />
        <meta property="og:title" content="Forgot Password — CartSmart" />
        <meta property="og:description" content="Request a password reset for your CartSmart account." />
        <meta property="og:url" content={`${process.env.REACT_APP_SITE_URL || (typeof window !== 'undefined' ? window.location.origin : '')}/forgot-password`} />
        <meta name="twitter:card" content="summary" />
        <meta name="twitter:title" content="Forgot Password — CartSmart" />
        <meta name="twitter:description" content="Request a password reset for your CartSmart account." />
      </Helmet>
      <h1 className="text-lg font-semibold mb-4">Forgot your password?</h1>
      <form onSubmit={submit} className="space-y-4">
        <input
          type="email"
          required
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          placeholder="Email address"
          className="w-full border rounded px-3 py-2"
        />
        <button type="submit" className="w-full bg-blue-600 text-white rounded py-2">
          Send reset link
        </button>
        {error && <p className="text-sm text-red-600">{error}</p>}
      </form>
    </div>
  );
};
export default ForgotPasswordPage;