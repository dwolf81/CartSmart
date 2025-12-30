import React from 'react';
import { Helmet } from 'react-helmet-async';
import Header from '../components/Header';

export default function PrivacyPolicy() {
  return (
    <>
      <Header />
      <div className="container mx-auto px-4 py-12">
        <div className="bg-white rounded-lg shadow p-8 max-w-4xl mx-auto">
        <Helmet>
          <title>Privacy Policy — CartSmart</title>
          <meta name="description" content="Learn how CartSmart collects, uses, and protects your information, including cookies, rights, retention, GPC, and cross‑border transfers." />
          <link rel="canonical" href={`${process.env.REACT_APP_SITE_URL || (typeof window !== 'undefined' ? window.location.origin : '')}/privacy`} />
          <meta property="og:type" content="website" />
          <meta property="og:site_name" content="CartSmart" />
          <meta property="og:title" content="Privacy Policy — CartSmart" />
          <meta property="og:description" content="Learn how CartSmart collects, uses, and protects your information, including cookies, rights, retention, GPC, and cross‑border transfers." />
          <meta property="og:url" content={`${process.env.REACT_APP_SITE_URL || (typeof window !== 'undefined' ? window.location.origin : '')}/privacy`} />
          <meta name="twitter:card" content="summary" />
          <meta name="twitter:title" content="Privacy Policy — CartSmart" />
          <meta name="twitter:description" content="Learn how CartSmart collects, uses, and protects your information, including cookies, rights, retention, GPC, and cross‑border transfers." />
        </Helmet>
         <section class="max-w-4xl mx-auto px-4 py-12 text-gray-800">
  <h1 class="text-3xl font-bold mb-6">Privacy Policy</h1>
  <p class="mb-4 text-sm text-gray-500">Last Updated: 11/25/2025</p>

  <h2 class="text-xl font-semibold mt-6 mb-2">1. Information We Collect</h2>
  <p class="mb-4">
    We collect basic account information such as your name, email address, and activity related to deal submissions, flags, and rewards.
  </p>

  <h2 class="text-xl font-semibold mt-6 mb-2">2. How We Use Your Information</h2>
  <ul class="list-disc list-inside mb-4 text-sm">
    <li>To operate and maintain the platform</li>
    <li>To verify deal accuracy and trust scores</li>
    <li>To distribute rewards</li>
    <li>To improve our services</li>
  </ul>

  <h2 class="text-xl font-semibold mt-6 mb-2">3. Cookies & Tracking</h2>
  <p class="mb-4">
    We use cookies and similar technologies to operate and improve the Service. You can manage preferences in the in‑app cookie settings.
  </p>
  <ul class="list-disc list-inside mb-4 text-sm">
    <li><strong>Strictly Necessary:</strong> Required for core functionality (auth, security).</li>
    <li><strong>Functional:</strong> Preferences like language and saved settings.</li>
    <li><strong>Analytics:</strong> Understand usage to improve features.</li>
    <li><strong>Advertising/Affiliate:</strong> Track referrals and measure campaign performance.</li>
  </ul>
  <p class="mb-4">
    We honor browser Global Privacy Control (GPC) signals by disabling non‑essential tracking when detected. You may also opt out of sale/share of personal information via our cookie preferences.
  </p>

  <h2 class="text-xl font-semibold mt-6 mb-2">4. Third-Party Services</h2>
  <p class="mb-4">
    We integrate with third-party merchants and affiliate platforms. Their use of your data is governed by their own privacy policies.
  </p>

  <h2 class="text-xl font-semibold mt-6 mb-2">5. Data Security</h2>
  <p class="mb-4">
    We employ industry-standard security measures to protect user information. However, no method of transmission is 100% secure.
  </p>

  <h2 class="text-xl font-semibold mt-6 mb-2">6. Blockchain & Bitcoin Rewards</h2>
  <p class="mb-4">
    If you receive Bitcoin rewards, public blockchain transactions may be visible on public ledgers. CartSmart does not control blockchain privacy.
  </p>

  <h2 class="text-xl font-semibold mt-6 mb-2">7. Your Rights</h2>
  <p class="mb-4">
    Subject to applicable law, you may exercise the following rights: access, correction, deletion, portability, objection, and restriction. To submit a request, contact <strong>support@cartsmart.com</strong>. We may need to verify your identity before processing.
  </p>

  <h2 class="text-xl font-semibold mt-6 mb-2">8. Data Retention</h2>
  <p class="mb-4">
    We retain personal information while your account is active and as needed to provide the Service, comply with legal obligations, resolve disputes, and enforce agreements. Account and activity records are typically retained for operational purposes; log data may be retained for a shorter period.
  </p>

  <h2 class="text-xl font-semibold mt-6 mb-2">9. Cross‑Border Transfers</h2>
  <p class="mb-4">
    Your information may be processed in countries other than your own. Where required, we implement appropriate safeguards for international transfers.
  </p>

  <h2 class="text-xl font-semibold mt-6 mb-2">10. Legal Bases (EU/UK)</h2>
  <p class="mb-4">
    For users in the EU/UK, we process personal data under legal bases including consent (e.g., non‑essential cookies), contract (to provide the Service), and legitimate interests (to improve and secure the platform). Where consent is the basis, you may withdraw it at any time.
  </p>

  <h2 class="text-xl font-semibold mt-6 mb-2">11. Children’s Privacy</h2>
  <p class="mb-4">
    The Service is not directed to children under 13, and we do not knowingly collect personal information from children under 13. If you believe a child has provided personal information, contact us to request deletion and account removal.
  </p>

  <h2 class="text-xl font-semibold mt-6 mb-2">12. Sale/Share Opt‑Out</h2>
  <p class="mb-4">
    We do not sell personal information. Where applicable law defines “sale” or “share” broadly (e.g., CPRA), you may opt out via the in‑app cookie preferences. We respect opt‑out signals sent by compatible browsers.
  </p>

  <h2 class="text-xl font-semibold mt-6 mb-2">13. Global Privacy Control (GPC)</h2>
  <p class="mb-4">
    When a GPC signal is detected, we treat it as a request to opt out of sale/share and disable non‑essential tracking by default.
  </p>

  <h2 class="text-xl font-semibold mt-6 mb-2">14. Changes to This Policy</h2>
  <p class="mb-4">
    We may update this Privacy Policy periodically. Continued use of the platform constitutes acceptance of the revised policy.
  </p>

  <h2 class="text-xl font-semibold mt-6 mb-2">15. Contact</h2>
  <p class="mb-4">
    Privacy questions can be sent to: <strong>support@cartsmart.com</strong>
  </p>
</section>
        </div>
      </div>
    </>
  );
}