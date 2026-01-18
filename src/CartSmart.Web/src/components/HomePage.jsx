import React, { useState, useEffect } from 'react';
import { Helmet } from 'react-helmet-async';
import { Link } from 'react-router-dom';
import SubmitDealModal from './SubmitDealModal';
import LoadingSpinner from './LoadingSpinner';

const API_URL = process.env.REACT_APP_API_URL || 'http://localhost:5000';
const SITE_URL = process.env.REACT_APP_SITE_URL || (typeof window !== 'undefined' ? window.location.origin : '');


const HomePage = () => {
  const [displayCount, setDisplayCount] = useState(6);
  const [isModalOpen, setIsModalOpen] = useState(false);
  const [featuredDeals, setFeaturedDeals] = useState([]);
  const [loading, setLoading] = useState(false);
  const [loadingProducts, setLoadingProducts] = useState(true);

useEffect(() => {
  const loadBestProductDeals = async () => {
      try {
        setLoadingProducts(true);
        // Fetch all deals for this product
        const response = await fetch(`${API_URL}/api/products/getbestproductdeals`, {
          credentials: 'include'
        });

        if (!response.ok) {
          throw new Error('Failed to fetch deals');
        }

        const dealsData = await response.json();
        setFeaturedDeals(dealsData || []);
      } catch (err) {
        console.error('Error loading product:', err);
        //setError(err.message);
      } finally {
        setLoadingProducts(false);
      }
  }
  loadBestProductDeals();
  },[]);

  // Add this utility function at the top of your component, after the imports
  const formatPrice = (price) => {
    if (price === null || price === undefined) return '$0.00';
    return new Intl.NumberFormat('en-US', {
      style: 'currency',
      currency: 'USD',
      minimumFractionDigits: 2,
      maximumFractionDigits: 2
    }).format(parseFloat(price));
  };

  const handleViewMore = () => {
    setDisplayCount(prevCount => Math.min(prevCount + 6, featuredDeals.length));
  };

  const visibleDeals = featuredDeals.slice(0, displayCount);
  const hasMoreDeals = displayCount < featuredDeals.length;

  if (loading) return <LoadingSpinner />;

  return (
    <div className="min-h-screen">
      <Helmet>
        <title>CartSmart — Golf, Priced Smarter</title>
        <meta name="description" content="We find the lowest real prices on golf equipment by comparing retail, resale, and certified pre-owned listings." />
        <link rel="canonical" href={`${SITE_URL}/`} />
        <meta name="robots" content="index,follow" />
        <meta property="og:type" content="website" />
        <meta property="og:site_name" content="CartSmart" />
        <meta property="og:title" content="CartSmart — Golf, Priced Smarter" />
        <meta property="og:description" content="We find the lowest real prices on golf equipment by comparing retail, resale, and certified pre-owned listings." />
        <meta property="og:url" content={`${SITE_URL}/`} />
        <meta name="twitter:card" content="summary_large_image" />
        <meta name="twitter:title" content="CartSmart — Golf, Priced Smarter" />
        <meta name="twitter:description" content="We find the lowest real prices on golf equipment by comparing retail, resale, and certified pre-owned listings." />
        <script type="application/ld+json">{JSON.stringify({
          '@context': 'https://schema.org',
          '@type': 'WebSite',
          name: 'CartSmart',
          url: `${SITE_URL}/`,
          potentialAction: {
            '@type': 'SearchAction',
            target: `${SITE_URL}/search?q={query}`,
            'query-input': 'required name=query'
          }
        })}</script>
      </Helmet>
      {/* Hero Section */}
      <div className="bg-gradient-to-r from-[#4CAF50] to-[#2E7D32] text-white">
        <div className="container mx-auto px-4 py-16">
          <div className="max-w-3xl mx-auto text-center">
            <h1 className="text-4xl md:text-6xl font-bold mb-6">
             {/* Never Pay Full Price Again */}
              The Smart Way to Buy Golf Equipment.
            </h1>
            <p className="text-xl md:text-2xl mb-8">
           CartSmart evaluates every legitimate way to save — retail pricing, pre-owned, third party offers and coupons — and shows golfers the lowest real cost.
                {/*Deal hunters compete to find you the cheapest way to get your favorite products.*/}
                 {/*Don’t hunt deals — CartSmart builds the deal for you.*/}
            </p>

          </div>
        </div>
      </div>

      {/* Featured Deals Section */}
      <div className="container mx-auto px-4 py-16">
        <h2 className="text-3xl font-bold text-center mb-12">Today's Best Deals</h2>
        {loadingProducts ? (
          <LoadingSpinner />
        ) : (
          <div className="grid md:grid-cols-2 lg:grid-cols-3 gap-8">
            {visibleDeals.map((bestDeal) => (
              <div key={bestDeal.id} className="bg-white rounded-lg shadow-lg overflow-hidden">
                <Link to={`products/${bestDeal.slug}`} className="block hover:opacity-90 transition-opacity">
                  <img 
                    src={bestDeal.product_image_url} 
                    alt={bestDeal.name}
                    className="w-full h-48 object-cover"
                  />
                </Link>
                <div className="p-6">            
                  <Link 
                    to={`products/${bestDeal.slug}`}
                    className="text-xl font-semibold text-gray-900 hover:text-[#4CAF50] mb-2 block"
                  >
                    {bestDeal.product_name}
                  </Link>
                  <p className="text-gray-600 mb-4">{bestDeal.description}</p>
                  <div className="flex items-center justify-between mb-4">
                    <div>
                      <span className="text-sm text-gray-500">Regular price</span>
                      <div className="text-lg text-gray-900 line-through">{formatPrice(bestDeal.msrp)}                   </div>
                    </div>
                    <div className="text-right">
                      <span className="text-sm text-gray-500">Best price</span>
                      <div className="text-2xl font-bold text-green-600">{formatPrice(bestDeal.price)}</div>
                      <div>
                        <span className="text-sm text-red-600 font-semibold">
    Save {formatPrice(bestDeal.discount_amt)}
  </span>
                      </div>
                    </div>
                  </div>
                  <div className="flex items-end justify-between">
  <Link to={`/profile/${bestDeal.user_name}`} className="flex items-end gap-2">
    <img
      src={bestDeal.user_image_url}
      alt={bestDeal.user_name}
      className="w-6 h-6 rounded-full"
    />
    <div className="flex items-center gap-1">
      <span className="text-sm text-gray-600">@{bestDeal.user_name}</span>
      {bestDeal.isVerified && (
        <svg className="w-4 h-4 text-[#4CAF50]" fill="currentColor" viewBox="0 0 20 20">
          <path d="M6.267 3.455a3.066 3.066 0 001.745-.723 3.066 3.066 0 013.976 0 3.066 3.066 0 001.745.723 3.066 3.066 0 012.812 2.812c.051.643.304 1.254.723 1.745a3.066 3.066 0 010 3.976 3.066 3.066 0 00-.723 1.745 3.066 3.066 0 01-2.812 2.812 3.066 3.066 0 00-1.745.723 3.066 3.066 0 01-3.976 0 3.066 3.066 0 00-1.745-.723 3.066 3.066 0 01-2.812-2.812 3.066 3.066 0 00-.723-1.745 3.066 3.066 0 010-3.976 3.066 3.066 0 00.723-1.745 3.066 3.066 0 012.812-2.812zm7.44 5.252a1 1 0 00-1.414-1.414L9 10.586 7.707 9.293a1 1 0 00-1.414 1.414l2 2a1 1 0 001.414 0l4-4z" />
        </svg>
      )}
      <span className="text-xs text-gray-500">({bestDeal.level}%)</span>
    </div>
  </Link>
  <a
    href={`products/${bestDeal.slug}`}
    rel="noopener noreferrer"
    className="bg-[#4CAF50] text-white px-6 py-2 rounded-lg hover:bg-[#3d8b40] transition-colors"
  >
    See Deals
  </a>
</div>
                </div>
              </div>
            ))}
          </div>
        )}
        {/* View More Section */}
        {!loadingProducts && hasMoreDeals && (
          <div className="text-center mt-12">
            <button
              onClick={handleViewMore}
              className="inline-block bg-gray-900 text-white px-8 py-3 rounded-lg font-semibold hover:bg-gray-800 transition-colors"
            >
              View More Deals
            </button>
          </div>
        )}
      </div>

      {/* Why Choose Us Section */}
      <div className="bg-gray-50 py-16">
        <div className="container mx-auto px-4">
          <h2 className="text-3xl font-bold text-center mb-12">Why Choose CartSmart?</h2>
          <div className="grid md:grid-cols-3 gap-8">
            <div className="text-center">
              <div className="text-4xl mb-4">💰</div>
              <h3 className="text-xl font-semibold mb-2">Best Prices</h3>
              <p className="text-gray-600">We find and verify the lowest possible prices through various methods</p>
            </div>
            <div className="text-center">
              <div className="text-4xl mb-4">🤝</div>
              <h3 className="text-xl font-semibold mb-2">Community Driven</h3>
              <p className="text-gray-600">Deals are submitted and verified by our active community</p>
            </div>
            <div className="text-center">
              <div className="text-4xl mb-4">🎯</div>
              <h3 className="text-xl font-semibold mb-2">Smart Shopping</h3>
              <p className="text-gray-600">Learn creative ways to stack deals and save more</p>
            </div>
          </div>
        </div>
      </div>

      <SubmitDealModal 
        isOpen={isModalOpen}
        onClose={() => setIsModalOpen(false)}
      />
    </div>
  );
};

export default HomePage;