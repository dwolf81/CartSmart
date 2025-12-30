import React from 'react';
import { Helmet } from 'react-helmet-async';
import Header from '../components/Header';

export default function TermsOfService() {
  return (
    <>
      <Header />
      <div className="container mx-auto px-4 py-12">
        <div className="bg-white rounded-lg shadow p-8 max-w-4xl mx-auto">
        <Helmet>
          <title>Terms of Service — CartSmart</title>
          <meta name="description" content="Read the CartSmart Terms of Service covering eligibility, account rules, disclaimers, rewards, liability, and more." />
          <link rel="canonical" href={`${process.env.REACT_APP_SITE_URL || (typeof window !== 'undefined' ? window.location.origin : '')}/terms`} />
          <meta property="og:type" content="website" />
          <meta property="og:site_name" content="CartSmart" />
          <meta property="og:title" content="Terms of Service — CartSmart" />
          <meta property="og:description" content="Read the CartSmart Terms of Service covering eligibility, account rules, disclaimers, rewards, liability, and more." />
          <meta property="og:url" content={`${process.env.REACT_APP_SITE_URL || (typeof window !== 'undefined' ? window.location.origin : '')}/terms`} />
          <meta name="twitter:card" content="summary" />
          <meta name="twitter:title" content="Terms of Service — CartSmart" />
          <meta name="twitter:description" content="Read the CartSmart Terms of Service covering eligibility, account rules, disclaimers, rewards, liability, and more." />
        </Helmet>
        <section class="max-w-4xl mx-auto px-4 py-12 text-gray-800">
  <h1 class="text-3xl font-bold mb-6">Terms of Service</h1>
  <p class="mb-4 text-sm text-gray-500">Last Updated: 11/25/2025</p>

  <p class="mb-6">
    Welcome to CartSmart. These Terms of Service ("Terms") govern your access to and use of CartSmart, operated by <strong>CartSmart LLC</strong> ("we", "us", or "our"). By accessing or using our website, mobile applications, or services, you agree to be bound by these Terms.
  </p>

  <h2 class="text-xl font-semibold mt-8 mb-2">1. Eligibility</h2>
  <p class="mb-4">
    By using CartSmart, you represent and warrant that you have the legal right and authority to use the service under applicable laws and, where required, have consent from a parent or legal guardian. Our service is not directed to children under 13, and we do not knowingly collect personal information from children under 13.
  </p>

  <h2 class="text-xl font-semibold mt-8 mb-2">2. Account Registration</h2>
  <p class="mb-4">
    Creating an account is required to view deal details, submit deals, flag issues, earn rewards, and participate in the CartSmart community. You are responsible for maintaining the confidentiality of your account credentials.
  </p>

  <h2 class="text-xl font-semibold mt-8 mb-2">3. Deals & Pricing Disclaimer</h2>
  <p class="mb-4">
    CartSmart aggregates and displays pricing and promotional information from third-party merchants. We do not guarantee availability, accuracy, or pricing. Prices may change without notice. You agree that all purchases are made at your own discretion through third-party websites.
  </p>

  <h2 class="text-xl font-semibold mt-8 mb-2">4. Affiliate Disclosure</h2>
  <p class="mb-4">
    Some links on CartSmart are affiliate links. If you click on them and make a purchase, we may earn a commission — at no additional cost to you. These earnings help fund our platform and community rewards.
  </p>

  <h2 class="text-xl font-semibold mt-8 mb-2">5. Community Standards & Trust Scores</h2>
  <p class="mb-4">
    Trust scores reflect user contribution quality, including accurate deal submissions and verified flags. Abuse, spam, manipulation, or fraudulent behavior may result in penalties, loss of reputation, or permanent account suspension.
  </p>

  <h2 class="text-xl font-semibold mt-8 mb-2">5a. User Content License</h2>
  <p class="mb-4">
    By submitting content (including deal titles, descriptions, images, comments), you grant CartSmart a non‑exclusive, worldwide, royalty‑free, sublicensable license to host, reproduce, display, adapt, and distribute such content for operating and improving the service.
  </p>

  <h2 class="text-xl font-semibold mt-8 mb-2">6. Rewards & Bitcoin Distributions</h2>
  <p class="mb-4">
    Any rewards, including Bitcoin or cash‑equivalent incentives, are discretionary and may change or be discontinued at any time. Eligibility may depend on jurisdiction, identity verification, and tax reporting obligations. We may withhold or reverse rewards in cases of suspected fraud, abuse, or errors.
  </p>

  <h2 class="text-xl font-semibold mt-8 mb-2">7. Prohibited Conduct</h2>
  <ul class="list-disc list-inside mb-4 text-sm">
    <li>Automated scraping or bot activity</li>
    <li>Fraudulent deal submissions</li>
    <li>Attempting to manipulate pricing or reward systems</li>
    <li>Harassment, spam, or abusive behavior</li>
    <li>Uploading malware or attempting to compromise security</li>
    <li>Impersonation or misrepresentation of identity</li>
    <li>Circumventing rate limits or access controls</li>
  </ul>

  <h2 class="text-xl font-semibold mt-8 mb-2">8. Termination</h2>
  <p class="mb-4">
    We reserve the right to suspend or terminate any account that violates these Terms or harms the integrity of the platform.
  </p>

  <h2 class="text-xl font-semibold mt-8 mb-2">9. Limitation of Liability</h2>
  <p class="mb-4">
    To the maximum extent permitted by law, CartSmart LLC is not liable for any indirect, incidental, special, consequential, or punitive damages, or any loss of profits or revenues, arising from your use of the service. Our total liability for any claim is capped at USD $100.
  </p>

  <h2 class="text-xl font-semibold mt-8 mb-2">10. Governing Law</h2>
  <p class="mb-4">
    These Terms are governed by and construed under the laws of the United States.
  </p>

  <h2 class="text-xl font-semibold mt-8 mb-2">10a. Third‑Party Links</h2>
  <p class="mb-4">
    CartSmart may link to third‑party websites. Those sites are not under our control, and you agree that your use of them is subject to their terms and privacy policies.
  </p>

  <h2 class="text-xl font-semibold mt-8 mb-2">11. Contact Us</h2>
  <p class="mb-4">
    For legal inquiries, contact: <strong>support@cartsmart.com</strong>
  </p>

  <h2 class="text-xl font-semibold mt-8 mb-2">12. Changes to These Terms</h2>
  <p class="mb-4">
    We may update these Terms from time to time. Material changes will be communicated via the site (and, where appropriate, by email or banner). The “Last Updated” date reflects the effective date. Continued use of the service constitutes acceptance of the updated Terms.
  </p>

  <h2 class="text-xl font-semibold mt-8 mb-2">13. Indemnification</h2>
  <p class="mb-4">
    You agree to indemnify and hold harmless CartSmart LLC and its affiliates, officers, employees, and agents from any claims, liabilities, damages, losses, and expenses, including reasonable attorney’s fees, arising out of or in any way connected with your use of the service or your content.
  </p>

  <h2 class="text-xl font-semibold mt-8 mb-2">14. Content Takedown</h2>
  <p class="mb-4">
    If you believe content on CartSmart violates your rights or applicable law (including copyright), please contact <strong>support@cartsmart.com</strong> with details. We will review and may remove content at our discretion.
  </p>

  <h2 class="text-xl font-semibold mt-8 mb-2">15. Privacy & Cookies</h2>
  <p class="mb-4">
    Our <a href="/privacy" class="underline">Privacy Policy</a> and <a href="/cookie-policy" class="underline">Cookie Policy</a> explain how we process personal data and honor preferences (including Global Privacy Control and sale/share opt‑out under CPRA). These policies are incorporated by reference into these Terms.
  </p>

  <h2 class="text-xl font-semibold mt-8 mb-2">16. Service Availability</h2>
  <p class="mb-4">
    We may modify, suspend, or discontinue features at any time without notice. We are not liable for downtime or interruptions.
  </p>

    <h2 class="text-xl font-semibold mt-8 mb-2">No Advice</h2>
    <p class="mb-4">
      The content on the Service, including deals, product information,
      ratings, reviews, articles, and any other materials, is provided for
      general informational purposes only and does not constitute financial,
      legal, medical, or other professional advice. You are solely
      responsible for evaluating the accuracy, suitability, and risks of any
      offer or decision based on such content. Before making purchasing,
      financial, legal, medical, or other decisions, you should consider
      seeking advice from a qualified professional.
    </p>
</section>

</div>
      </div>
    </>
  );
}