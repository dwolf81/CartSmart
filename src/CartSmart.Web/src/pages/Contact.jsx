import React, { useState } from 'react';
import Header from '../components/Header';
import { Helmet } from 'react-helmet-async';

export default function Contact() {
  const [form, setForm] = useState({ name: '', email: '', message: '' });
  const [status, setStatus] = useState(null);

  const isValidEmail = (email) => {
    if (!email) return false;
    // Case-insensitive pattern to allow lowercase letters
    const pattern = /^[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}$/i;
    return pattern.test(email.trim());
  };

  const handleSubmit = async (e) => {
    e.preventDefault();
    const email = (form.email || '').trim();
    if (!isValidEmail(email)) {
      setStatus('Please enter a valid email address.');
      return;
    }
    setStatus('sending');
    try {
      const res = await fetch(`${process.env.REACT_APP_API_URL}/api/contact`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(form),
      });
      if (!res.ok) {
        const err = await res.json().catch(()=>({}));
        throw new Error(err.message || 'Send failed');
      }
      setStatus('sent');
      setForm({ name: '', email: '', message: '' });
    } catch (ex) {
      setStatus(ex.message || 'error');
    }
  };

  return (
    <>
      <Header />
      <div className="container mx-auto px-4 py-12">
        <div className="bg-white rounded-lg shadow p-8 max-w-2xl mx-auto">
          <Helmet>
            <title>Contact CartSmart — Get In Touch</title>
            <meta name="description" content="Questions or feedback? Contact CartSmart using our simple form. We’re here to help." />
            <link rel="canonical" href={`${process.env.REACT_APP_SITE_URL || (typeof window !== 'undefined' ? window.location.origin : '')}/contact`} />
            <meta name="robots" content="index,follow" />
            <meta property="og:type" content="website" />
            <meta property="og:site_name" content="CartSmart" />
            <meta property="og:title" content="Contact CartSmart — Get In Touch" />
            <meta property="og:description" content="Questions or feedback? Contact CartSmart using our simple form. We’re here to help." />
            <meta property="og:url" content={`${process.env.REACT_APP_SITE_URL || (typeof window !== 'undefined' ? window.location.origin : '')}/contact`} />
            <meta name="twitter:card" content="summary" />
            <meta name="twitter:title" content="Contact CartSmart — Get In Touch" />
            <meta name="twitter:description" content="Questions or feedback? Contact CartSmart using our simple form. We’re here to help." />
          </Helmet>
          <h1 className="text-2xl font-bold mb-4">Contact Us</h1>
          <p className="text-gray-700 mb-6">Have questions or feedback? Reach out to us using the form below.</p>
          <form onSubmit={handleSubmit} className="space-y-4">
            <input
              className="w-full border rounded px-3 py-2"
              placeholder="Name"
              value={form.name}
              onChange={(e) => setForm({ ...form, name: e.target.value })}
              required
            />
            <input
              className="w-full border rounded px-3 py-2"
              type="email"
              placeholder="Email"
              value={form.email}
              onChange={(e) => setForm({ ...form, email: e.target.value })}
              required
            />
            <textarea
              className="w-full border rounded px-3 py-2"
              rows="6"
              placeholder="Message"
              value={form.message}
              onChange={(e) => setForm({ ...form, message: e.target.value })}
              required
            />
            <button type="submit" className="bg-green-600 text-white px-4 py-2 rounded">
              Send Message
            </button>
            {status === 'sending' && <p className="text-gray-500">Sending...</p>}
            {status === 'sent' && <p className="text-green-600">Message sent. Thank you!</p>}
            {status === 'error' && <p className="text-red-600">Error sending message. Please try again.</p>}
            {status && status !== 'sending' && status !== 'sent' && status !== 'error' && (
              <p className="text-red-600 text-sm">{status}</p>
            )}
          </form>
        </div>
      </div>
    </>
  );
}