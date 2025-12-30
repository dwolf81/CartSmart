import { useMemo, useState } from 'react';
import { Helmet } from 'react-helmet-async';
import { useLocation, useNavigate } from 'react-router-dom';
const API_URL = process.env.REACT_APP_API_URL || 'http://localhost:5000';

const ResetPasswordPage = () => {
  const location = useLocation();
  const navigate = useNavigate();
  const token = useMemo(() => new URLSearchParams(location.search).get('token') || '', [location.search]);
  const [pwd, setPwd] = useState('');
  const [confirm, setConfirm] = useState('');
  const [done, setDone] = useState(false);
  const [error, setError] = useState('');

  const submit = async (e) => {
    e.preventDefault();
    setError('');
    if (pwd.length < 8) { setError('Password must be at least 8 characters.'); return; }
    if (pwd !== confirm) { setError('Passwords do not match.'); return; }
    const pe = passwordPolicyError(pwd);
    if (pe) { setError(pe); return; }
    const res = await fetch(`${API_URL}/api/auth/reset-password`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      credentials: 'include',
      body: JSON.stringify({ token, newPassword: pwd })
    });
    if (!res.ok) {
      const data = await res.json().catch(() => ({}));
      setError(data?.message || 'Reset failed.');
      return;
    }
    setDone(true);
    setTimeout(() => navigate('/login'), 1200);
  };

  const passwordPolicyError = (pwd) => {
    if (!pwd || pwd.length < 8) return 'Use at least 8 characters.';
    if (/\s/.test(pwd)) return 'No spaces allowed in password.';
    const cats = [/[a-z]/, /[A-Z]/, /\d/, /[^A-Za-z0-9]/].reduce((a, r) => a + (r.test(pwd) ? 1 : 0), 0);
    if (cats < 3) return 'Use a mix of letters, numbers, and symbols.';
    return null;
  };

  if (!token) return (
    <div className="max-w-md mx-auto p-6">
      <Helmet>
        <title>Reset Password — CartSmart</title>
        <meta name="description" content="Set a new password for your CartSmart account." />
        <link rel="canonical" href={`${process.env.REACT_APP_SITE_URL || (typeof window !== 'undefined' ? window.location.origin : '')}/reset-password`} />
        <meta name="robots" content="noindex,follow" />
        <meta property="og:type" content="website" />
        <meta property="og:site_name" content="CartSmart" />
        <meta property="og:title" content="Reset Password — CartSmart" />
        <meta property="og:description" content="Set a new password for your CartSmart account." />
        <meta property="og:url" content={`${process.env.REACT_APP_SITE_URL || (typeof window !== 'undefined' ? window.location.origin : '')}/reset-password`} />
        <meta name="twitter:card" content="summary" />
        <meta name="twitter:title" content="Reset Password — CartSmart" />
        <meta name="twitter:description" content="Set a new password for your CartSmart account." />
      </Helmet>
      Invalid or missing token.
    </div>
  );
  if (done) return (
    <div className="max-w-md mx-auto p-6">
      <Helmet>
        <title>Reset Password — CartSmart</title>
        <meta name="description" content="Set a new password for your CartSmart account." />
        <link rel="canonical" href={`${process.env.REACT_APP_SITE_URL || (typeof window !== 'undefined' ? window.location.origin : '')}/reset-password`} />
        <meta name="robots" content="noindex,follow" />
        <meta property="og:type" content="website" />
        <meta property="og:site_name" content="CartSmart" />
        <meta property="og:title" content="Reset Password — CartSmart" />
        <meta property="og:description" content="Set a new password for your CartSmart account." />
        <meta property="og:url" content={`${process.env.REACT_APP_SITE_URL || (typeof window !== 'undefined' ? window.location.origin : '')}/reset-password`} />
        <meta name="twitter:card" content="summary" />
        <meta name="twitter:title" content="Reset Password — CartSmart" />
        <meta name="twitter:description" content="Set a new password for your CartSmart account." />
      </Helmet>
      Password updated. Redirecting…
    </div>
  );

  return (
    <div className="max-w-md mx-auto p-6">
      <Helmet>
        <title>Reset Password — CartSmart</title>
        <meta name="description" content="Set a new password for your CartSmart account." />
        <link rel="canonical" href={`${process.env.REACT_APP_SITE_URL || (typeof window !== 'undefined' ? window.location.origin : '')}/reset-password`} />
        <meta name="robots" content="noindex,follow" />
        <meta property="og:type" content="website" />
        <meta property="og:site_name" content="CartSmart" />
        <meta property="og:title" content="Reset Password — CartSmart" />
        <meta property="og:description" content="Set a new password for your CartSmart account." />
        <meta property="og:url" content={`${process.env.REACT_APP_SITE_URL || (typeof window !== 'undefined' ? window.location.origin : '')}/reset-password`} />
        <meta name="twitter:card" content="summary" />
        <meta name="twitter:title" content="Reset Password — CartSmart" />
        <meta name="twitter:description" content="Set a new password for your CartSmart account." />
      </Helmet>
      <h1 className="text-lg font-semibold mb-4">Reset password</h1>
      <form onSubmit={submit} className="space-y-4">
        <input type="password" placeholder="New password" className="w-full border rounded px-3 py-2" value={pwd} onChange={(e) => setPwd(e.target.value)} required />
        <input type="password" placeholder="Confirm new password" className="w-full border rounded px-3 py-2" value={confirm} onChange={(e) => setConfirm(e.target.value)} required />
        <button type="submit" className="w-full bg-blue-600 text-white rounded py-2">Update password</button>
        {error && <p className="text-sm text-red-600">{error}</p>}
      </form>
    </div>
  );
};
export default ResetPasswordPage;