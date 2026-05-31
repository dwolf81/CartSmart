import React from 'react';
import { Link } from 'react-router-dom';
import { Helmet } from 'react-helmet-async';

/**
 * 404 page shown for any route that doesn't match a defined SPA path. Paired
 * with the server-side fallback in Program.cs, which sets HTTP status 404 and
 * X-Robots-Tag: noindex so Google removes the URL from its index instead of
 * flagging the page as "Soft 404" in Search Console.
 */
export default function NotFound() {
  return (
    <main className="max-w-2xl mx-auto px-4 py-16 text-center">
      <Helmet>
        <title>Page not found — CartSmart</title>
        <meta name="robots" content="noindex" />
      </Helmet>

      <p className="text-sm font-semibold text-blue-600 tracking-wide">404</p>
      <h1 className="mt-2 text-3xl font-bold text-gray-900">
        We couldn't find that page
      </h1>
      <p className="mt-3 text-gray-600">
        The link you followed may be broken, or the page may have been removed.
      </p>

      <div className="mt-8 flex items-center justify-center gap-3">
        <Link
          to="/"
          className="px-4 py-2 rounded-md bg-blue-600 text-white text-sm font-medium hover:bg-blue-700"
        >
          Back to home
        </Link>
        <Link
          to="/categories"
          className="px-4 py-2 rounded-md border border-gray-300 text-sm font-medium text-gray-700 hover:bg-gray-50"
        >
          Browse categories
        </Link>
      </div>
    </main>
  );
}
