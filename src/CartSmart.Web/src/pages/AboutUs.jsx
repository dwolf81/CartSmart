import React from 'react';
import Header from '../components/Header';
import { Helmet } from 'react-helmet-async';

export default function AboutUs() {
  return (
    <>
      <Header />
      <div className="container mx-auto px-4 py-12">
        <div className="bg-white rounded-lg shadow p-8">
          <Helmet>
            <title>About CartSmart — Mission & Values</title>
            <meta name="description" content="CartSmart is a community-powered platform focused on real savings, trust, and transparent deals. Learn about our mission and how we do things differently." />
            <link rel="canonical" href={`${process.env.REACT_APP_SITE_URL || (typeof window !== 'undefined' ? window.location.origin : '')}/about`} />
            <meta name="robots" content="index,follow" />
            <meta property="og:type" content="website" />
            <meta property="og:site_name" content="CartSmart" />
            <meta property="og:title" content="About CartSmart — Mission & Values" />
            <meta property="og:description" content="CartSmart is a community-powered platform focused on real savings, trust, and transparent deals." />
            <meta property="og:url" content={`${process.env.REACT_APP_SITE_URL || (typeof window !== 'undefined' ? window.location.origin : '')}/about`} />
            <meta name="twitter:card" content="summary" />
            <meta name="twitter:title" content="About CartSmart — Mission & Values" />
            <meta name="twitter:description" content="CartSmart is a community-powered platform focused on real savings, trust, and transparent deals." />
          </Helmet>
          <h1 className="text-2xl font-bold mb-4">About CartSmart</h1>
          <p className="text-gray-700 mb-4">
            CartSmart is a community-powered shopping platform built to help people find the true lowest price on popular, high-ticket products. We go beyond simple coupons by combining direct discounts, external offers, and stacked savings into one clear view. Every deal on CartSmart is reviewed, validated, and continuously checked by both our community and automation so you can shop with confidence.
          </p>
          <p className="text-gray-700">
            Unlike most deal sites, CartSmart is designed around trust, transparency, and real savings — not just clicks.
          </p>
          <h2 className="text-xl font-semibold mt-6 mb-2">Our Mission</h2>
          <p className="text-gray-700 mb-4">
            Our mission is simple: help people pay the lowest possible price — and give as much value back to our community as we can.
          </p>
           <p className="text-gray-700 mb-4">
            While many deal sites focus only on sending traffic to earn affiliate revenue, CartSmart is built differently. We care whether a deal actually works. We reward real contributors. And we reinvest as much of our success as possible back into the community through rewards, giveaways, and future incentives.
            </p>
           <p className="text-gray-700">
            We believe saving money should be collaborative, transparent, and fair — and that the people who help make that possible should benefit the most.
            </p>
        </div>
      </div>
    </>
  );
}