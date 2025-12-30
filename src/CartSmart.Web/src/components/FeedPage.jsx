import React, { useState } from 'react';
import { Link } from 'react-router-dom';
import LoadingSpinner from './LoadingSpinner';

const FeedPage = () => {
  const [activeFilter, setActiveFilter] = useState('all');
  // Simulate loading state for demonstration (replace with real loading logic if fetching data)
  const [loading, setLoading] = useState(false);

  // Example: Uncomment and use this if you fetch data asynchronously
  // useEffect(() => {
  //   setLoading(true);
  //   fetchData().then(() => setLoading(false));
  // }, []);

  if (loading) return <LoadingSpinner />;

  // Example feed data
  const feedDeals = [
    {
      id: 1,
      productName: "Sony WH-1000XM4",
      price: 199.99,
      regularPrice: 349.99,
      postedAt: "2024-01-15T10:30:00",
      postedBy: {
        username: "dealfinder",
        isVerified: true,
        trustScore: 98,
        avatar: "https://placehold.co/50x50"
      },
      likes: 45,
      status: "active",
      savings: 150,
      dealType: "Direct",
      source: "Amazon",
      description: "Limited time sale price",
      productImage: "https://placehold.co/300x200"
    },
    // ... more deals
  ];

  return (
    <div className="container mx-auto px-4 py-8">
      <div className="flex justify-between items-center mb-8">
        <h1 className="text-3xl font-bold">Your Deal Feed</h1>
        <div className="flex gap-2">
          {['all', 'direct', 'coupon', 'multistep'].map(filter => (
            <button
              key={filter}
              onClick={() => setActiveFilter(filter)}
              className={`px-4 py-2 rounded-lg ${
                activeFilter === filter
                  ? 'bg-[#4CAF50] text-white'
                  : 'bg-gray-100 text-gray-700 hover:bg-gray-200'
              }`}
            >
              {filter.charAt(0).toUpperCase() + filter.slice(1)}
            </button>
          ))}
        </div>
      </div>

      <div className="grid md:grid-cols-2 lg:grid-cols-3 gap-6">
        {feedDeals.map(deal => (
          <div key={deal.id} className="bg-white rounded-lg shadow-lg overflow-hidden">
            <div className="p-4 border-b">
              <Link to={`/profile/${deal.postedBy.username}`} className="flex items-center gap-3">
                <img
                  src={deal.postedBy.avatar}
                  alt={deal.postedBy.username}
                  className="w-10 h-10 rounded-full"
                />
                <div>
                  <div className="flex items-center gap-2">
                    <span className="font-semibold">@{deal.postedBy.username}</span>
                    {deal.postedBy.isVerified && (
                      <svg className="w-4 h-4 text-[#4CAF50]" fill="currentColor" viewBox="0 0 20 20">
                        <path d="M6.267 3.455a3.066 3.066 0 001.745-.723 3.066 3.066 0 013.976 0 3.066 3.066 0 001.745.723 3.066 3.066 0 012.812 2.812c.051.643.304 1.254.723 1.745a3.066 3.066 0 010 3.976 3.066 3.066 0 00-.723 1.745 3.066 3.066 0 01-2.812 2.812 3.066 3.066 0 00-1.745.723 3.066 3.066 0 01-3.976 0 3.066 3.066 0 00-1.745-.723 3.066 3.066 0 01-2.812-2.812 3.066 3.066 0 00-.723-1.745 3.066 3.066 0 010-3.976 3.066 3.066 0 00.723-1.745 3.066 3.066 0 012.812-2.812zm7.44 5.252a1 1 0 00-1.414-1.414L9 10.586 7.707 9.293a1 1 0 00-1.414 1.414l2 2a1 1 0 001.414 0l4-4z" />
                      </svg>
                    )}
                  </div>
                  <div className="text-sm text-gray-500">
                    Trust Score: {deal.postedBy.trustScore}%
                  </div>
                </div>
              </Link>
            </div>

            <Link to={`/${deal.productName.toLowerCase().replace(/\s+/g, '-')}`}>
              <img 
                src={deal.productImage}
                alt={deal.productName}
                className="w-full h-48 object-cover"
              />
            </Link>

            <div className="p-4">
              <Link to={`/${deal.productName.toLowerCase().replace(/\s+/g, '-')}`} className="block mb-4">
                <h2 className="text-xl font-semibold mb-2">{deal.productName}</h2>
                <div className="flex justify-between items-end mb-3">
                  <div>
                    <div className="text-2xl font-bold text-green-600">${deal.price}</div>
                    <div className="text-sm text-gray-500 line-through">${deal.regularPrice}</div>
                  </div>
                  <div className="text-right">
                    <div className="text-red-600 font-semibold">Save ${deal.savings}</div>
                    <div className="text-sm text-gray-500">{deal.source}</div>
                  </div>
                </div>
                <p className="text-gray-700">{deal.description}</p>
              </Link>

              <div className="flex justify-between items-center">
                <span className="px-3 py-1 bg-gray-100 text-gray-700 rounded-full text-sm">
                  {deal.dealType}
                </span>
                <div className="flex items-center gap-4">
                  <button className="text-gray-400 hover:text-red-500 flex items-center gap-1">
                    <svg className="w-5 h-5" fill="currentColor" viewBox="0 0 20 20">
                      <path d="M2 10.5a1.5 1.5 0 113 0v6a1.5 1.5 0 01-3 0v-6zM6 10.333v5.43a2 2 0 001.106 1.79l.05.025A4 4 0 008.943 18h5.416a2 2 0 001.962-1.608l1.2-6A2 2 0 0015.56 8H12V4a2 2 0 00-2-2 1 1 0 00-1 1v.667a4 4 0 01-.8 2.4L6.8 7.933a4 4 0 00-.8 2.4z" />
                    </svg>
                    <span>{deal.likes}</span>
                  </button>
                  <a
                    href={deal.dealUrl}
                    target="_blank"
                    rel="noopener noreferrer"
                    className="px-4 py-2 bg-[#4CAF50] text-white rounded-lg hover:bg-[#3d8b40] transition-colors"
                  >
                    Get Deal
                  </a>
                </div>
              </div>
            </div>
          </div>
        ))}
      </div>
    </div>
  );
};

export default FeedPage;