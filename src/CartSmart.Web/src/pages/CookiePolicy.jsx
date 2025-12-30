import React from 'react';
import CookiePreferencesModal from '../components/CookiePreferencesModal';
import { useState } from 'react';
import Header from '../components/Header';
import { Helmet } from 'react-helmet-async';

const CookiePolicy = () => {
  const [open, setOpen] = useState(false);
  return (
    <>
          
    <Header />
    <div className="container mx-auto px-4 py-12">
      <div className="bg-white rounded-lg shadow p-8 max-w-4xl mx-auto">
      <Helmet>
        <title>Cookie Policy — Manage Preferences</title>
        <meta name="description" content="Manage your cookie and privacy preferences on CartSmart. Learn about categories and opt-out options." />
        <link rel="canonical" href={`${process.env.REACT_APP_SITE_URL || (typeof window !== 'undefined' ? window.location.origin : '')}/cookie-policy`} />
        <meta name="robots" content="index,follow" />
        <meta property="og:type" content="website" />
        <meta property="og:site_name" content="CartSmart" />
        <meta property="og:title" content="Cookie Policy — Manage Preferences" />
        <meta property="og:description" content="Manage your cookie and privacy preferences on CartSmart. Learn about categories and opt-out options." />
        <meta property="og:url" content={`${process.env.REACT_APP_SITE_URL || (typeof window !== 'undefined' ? window.location.origin : '')}/cookie-policy`} />
        <meta name="twitter:card" content="summary" />
        <meta name="twitter:title" content="Cookie Policy — Manage Preferences" />
        <meta name="twitter:description" content="Manage your cookie and privacy preferences on CartSmart. Learn about categories and opt-out options." />
      </Helmet>
      <h1 className="text-3xl font-bold mb-6">Cookie & Privacy Choices</h1>
      <p className="mb-4 text-sm text-gray-700">
        Legally (GDPR / CPRA) consent or opt‑out must be offered on first visit regardless of landing page.
        We present a banner before setting any non‑essential cookies.
      </p>
      <p className="mb-4 text-sm text-gray-700">
        This page describes how CartSmart uses cookies and similar technologies. Essential cookies are required
        to operate the service. Other categories (Performance, Analytics, Advertising) are optional.
        Under the California Consumer Privacy Act (CCPA) as amended by the California Privacy Rights Act (CPRA),
        you may opt out of the sale or sharing of personal information.
      </p>
      <h2 className="text-xl font-semibold mt-6 mb-2">Categories</h2>
      <ul className="list-disc pl-6 text-sm text-gray-700 space-y-2">
        <li><strong>Essential:</strong> Authentication, security, core features.</li>
        <li><strong>Performance:</strong> Site reliability and speed metrics.</li>
        <li><strong>Analytics:</strong> Usage patterns to improve content and features.</li>
        <li><strong>Advertising:</strong> Personalized or interest-based ads (disabled if you opt out).</li>
      </ul>
      <h2 className="text-xl font-semibold mt-6 mb-2">California Opt-Out</h2>
      <p className="text-sm text-gray-700 mb-4">
        Selecting “Do Not Sell or Share My Personal Information” prevents us from using third-party
        advertising identifiers for cross-context behavioral advertising and from selling or sharing
        personal data except as allowed for essential operations.
      </p>
      <button
        onClick={() => setOpen(true)}
        className="mt-4 px-4 py-2 bg-[#4CAF50] text-white rounded hover:bg-[#3d8b40]"
      >
        Manage Preferences
      </button>
      <CookiePreferencesModal open={open} onClose={() => setOpen(false)} />
    </div>
    </div>
    </>
  );
};

export default CookiePolicy;