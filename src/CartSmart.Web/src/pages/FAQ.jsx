import React from 'react';
import Header from '../components/Header';
import { Helmet } from 'react-helmet-async';

export default function FAQ() {
  const faqs = [
    { q: 'Why can\'t I see the details of a deal unless I login or create an account?', a: 'To keep CartSmart fair, accurate, and spam-free, full deal details are only available to members.' },
    { q: 'How do I submit a deal?', a: 'Click "Add Deal" on any product page to submit a deal.' },
    { q: 'What is a stacked deal?', a: 'A stacked deal combines multiple steps to maximize savings. It could require using a coupon code, applying an external offer, and taking advantage of a sale all at once.' },
    { q: 'How do I know if a deal is still valid?', a: 'Deals are validated through a combination of automated checks and community feedback. If a price changes or a promo expires, our system and users work together to update or remove it as quickly as possible. Always verify prices and availability before purchasing.' },
    { q: 'How do I report an issue with a deal?', a: 'Use the "Not Working?" link on the deal card to notify us of any problems.' },
    { q: 'Is CartSmart free to use?', a: 'Yes. CartSmart is completely free to use. We may offer optional premium features in the future, but core deal access will always remain free.' },
    { q: 'How do trust scores work?', a: 'Trust scores reflect how reliable and helpful you are in the CartSmart community. They’re based on the accuracy of your submitted deals, how often your deal flags are correct, and your overall contribution history—and higher scores unlock faster approvals and priority access to advanced features.' },
    
  ];

  return (
    <>
      <Header />
      <div className="container mx-auto px-4 py-12">
        <div className="bg-white rounded-lg shadow p-8 max-w-3xl mx-auto">
          <Helmet>
            <title>CartSmart FAQ — Answers to Common Questions</title>
            <meta name="description" content="Learn why login is required, how to submit and review deals, and how trust scores work on CartSmart." />
            <link rel="canonical" href={`${process.env.REACT_APP_SITE_URL || (typeof window !== 'undefined' ? window.location.origin : '')}/faq`} />
            <meta name="robots" content="index,follow" />
            <meta property="og:type" content="website" />
            <meta property="og:site_name" content="CartSmart" />
            <meta property="og:title" content="CartSmart FAQ — Answers to Common Questions" />
            <meta property="og:description" content="Learn why login is required, how to submit and review deals, and how trust scores work." />
            <meta property="og:url" content={`${process.env.REACT_APP_SITE_URL || (typeof window !== 'undefined' ? window.location.origin : '')}/faq`} />
            <meta name="twitter:card" content="summary" />
            <meta name="twitter:title" content="CartSmart FAQ — Answers to Common Questions" />
            <meta name="twitter:description" content="Learn why login is required, how to submit and review deals, and how trust scores work." />
          </Helmet>
          <h1 className="text-2xl font-bold mb-6">Frequently Asked Questions</h1>
          <div className="space-y-4">
            {faqs.map((faq, index) => (
              <details key={index} className="group border rounded p-4">
                <summary className="cursor-pointer font-semibold">{faq.q}</summary>
                <p className="mt-2 text-gray-700">{faq.a}</p>
              </details>
            ))}
          </div>
        </div>
      </div>
    </>
  );
}