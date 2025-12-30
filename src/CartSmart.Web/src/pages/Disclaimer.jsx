import React from 'react';
import Header from '../components/Header';
import { Helmet } from 'react-helmet-async';

export default function Disclaimer() {
  return (
    <>
      <Header />
      <div className="container mx-auto px-4 py-12">
        <div className="bg-white rounded-lg shadow p-8 max-w-4xl mx-auto">
          <Helmet>
            <title>Disclaimer — Pricing & Availability</title>
            <meta name="description" content="User-submitted deals may change; CartSmart does not guarantee prices or availability. Verify before purchasing." />
            <link rel="canonical" href={`${process.env.REACT_APP_SITE_URL || (typeof window !== 'undefined' ? window.location.origin : '')}/disclaimer`} />
            <meta name="robots" content="index,follow" />
            <meta property="og:type" content="website" />
            <meta property="og:site_name" content="CartSmart" />
            <meta property="og:title" content="Disclaimer — Pricing & Availability" />
            <meta property="og:description" content="User-submitted deals may change; CartSmart does not guarantee prices or availability." />
            <meta property="og:url" content={`${process.env.REACT_APP_SITE_URL || (typeof window !== 'undefined' ? window.location.origin : '')}/disclaimer`} />
            <meta name="twitter:card" content="summary" />
            <meta name="twitter:title" content="Disclaimer — Pricing & Availability" />
            <meta name="twitter:description" content="User-submitted deals may change; CartSmart does not guarantee prices or availability." />
          </Helmet>
          <h1 className="text-2xl font-bold mb-4">Disclaimer</h1>
          <p className="text-gray-700 mb-4">
            Prices and availability can change rapidly. Verify details on the merchant’s site before purchasing. Purchases are made on third‑party websites, subject to their terms and privacy policies.
          </p>
          <p className="text-gray-700 mb-4">
            See our <a href="/terms" className="underline">Terms of Service</a> for the full pricing and availability disclaimer. If you spot an issue, please use the flag option on the deal so moderators can review.
          </p>
          <div className="text-sm text-gray-600">
            Tip: Community flags and reviews improve accuracy over time.
          </div>
        </div>
      </div>
    </>
  );
}