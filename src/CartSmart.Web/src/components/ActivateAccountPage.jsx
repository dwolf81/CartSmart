import React, { useEffect, useState } from 'react';
import { Helmet } from 'react-helmet-async';
import { useLocation, useNavigate } from 'react-router-dom';

const API_URL = process.env.REACT_APP_API_URL || 'http://localhost:5000';

const ActivateAccountPage = () => {
  const location = useLocation();
  const navigate = useNavigate();
  const [status, setStatus] = useState('pending');
  const [message, setMessage] = useState('Activating your account...');
  const token = new URLSearchParams(location.search).get('token') || '';

  useEffect(() => {
    const run = async () => {
      if (!token) {
        setStatus('error');
        setMessage('Missing activation token.');
        return;
      }
      try {
        const res = await fetch(`${API_URL}/api/auth/activate`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ token })
        });
        const data = await res.json().catch(() => ({}));
        if (!res.ok) {
          setStatus('error');
          setMessage(data.message || 'Invalid or expired activation link.');
          return;
        }
        setStatus('success');
        setMessage('Account activated. Redirecting to login…');
        setTimeout(() => navigate('/login?activated=1'), 1500);
      } catch {
        setStatus('error');
        setMessage('Network error activating account.');
      }
    };
    run();
  }, [token, navigate]);

  return (
    <div className="max-w-md mx-auto p-6">
      <Helmet>
        <title>Activate Account — CartSmart</title>
        <meta name="description" content="Activate your CartSmart account to start finding and sharing deals." />
        <link rel="canonical" href={`${process.env.REACT_APP_SITE_URL || (typeof window !== 'undefined' ? window.location.origin : '')}/activate`} />
        <meta name="robots" content="noindex,follow" />
        <meta property="og:type" content="website" />
        <meta property="og:site_name" content="CartSmart" />
        <meta property="og:title" content="Activate Account — CartSmart" />
        <meta property="og:description" content="Activate your CartSmart account to start finding and sharing deals." />
        <meta property="og:url" content={`${process.env.REACT_APP_SITE_URL || (typeof window !== 'undefined' ? window.location.origin : '')}/activate`} />
        <meta name="twitter:card" content="summary" />
        <meta name="twitter:title" content="Activate Account — CartSmart" />
        <meta name="twitter:description" content="Activate your CartSmart account to start finding and sharing deals." />
      </Helmet>
      <h1 className="text-xl font-semibold mb-4">Account Activation</h1>
      <p className={status === 'error' ? 'text-red-600' : 'text-gray-800'}>{message}</p>
      {status === 'error' && (
        <button
          onClick={() => navigate('/login')}
          className="mt-4 px-4 py-2 rounded bg-blue-600 text-white"
        >
          Go to Login
        </button>
      )}
    </div>
  );
};

export default ActivateAccountPage;