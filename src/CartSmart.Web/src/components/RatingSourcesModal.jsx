import React, { useState, useEffect } from 'react';

const RatingSourcesModal = ({ isOpen, onClose, sources: initialSources }) => {
  const [sources, setSources] = useState(initialSources || []);
  const [showForm, setShowForm] = useState(false);
  const [form, setForm] = useState({ url: '', rating: '', source: '' });
  const [error, setError] = useState('');

  useEffect(() => {
    setSources(initialSources || []);
  }, [initialSources]);

  if (!isOpen) return null;

  const handleInputChange = (e) => {
    const { name, value } = e.target;
    setForm((prev) => ({ ...prev, [name]: value }));
  };

  const handleAddSource = (e) => {
    e.preventDefault();
    setError('');
    const ratingNum = Number(form.rating);
    if (!form.url || !form.source || isNaN(ratingNum) || ratingNum < 0 || ratingNum > 10) {
      setError('Please enter a valid source, URL, and a rating between 0 and 10.');
      return;
    }
    setSources((prev) => [
      ...prev,
      { source: form.source, url: form.url, rating: ratingNum }
    ]);
    setForm({ url: '', rating: '', source: '' });
    setShowForm(false);
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black bg-opacity-40">
      <div className="bg-white rounded-lg shadow-lg p-6 w-full max-w-md relative">
        <button
          className="absolute top-2 right-2 text-gray-400 hover:text-gray-700"
          onClick={onClose}
          aria-label="Close"
        >
          <svg className="w-6 h-6" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M6 18L18 6M6 6l12 12" />
          </svg>
        </button>
        <h2 className="text-xl font-bold mb-4">Rating Breakdown</h2>
        <ul className="space-y-3 mb-4">
          {sources && sources.length > 0 ? (
            sources.map((src, idx) => (
              <li key={idx} className="flex justify-between items-center border-b pb-2">
                <span className="font-medium">{src.source}</span>
                <span className="text-green-700 font-bold">{src.rating} / 10</span>
                {src.url && (
                  <a href={src.url} target="_blank" rel="noopener noreferrer" className="text-blue-500 underline ml-2 text-xs">View</a>
                )}
              </li>
            ))
          ) : (
            <li>No sources available.</li>
          )}
        </ul>
        {showForm ? (
          <form onSubmit={handleAddSource} className="mb-4 space-y-2">
            <input
              type="text"
              name="source"
              placeholder="Source name (e.g. Amazon)"
              value={form.source}
              onChange={handleInputChange}
              className="w-full border rounded px-3 py-2 mb-2"
              required
            />
            <input
              type="url"
              name="url"
              placeholder="Source URL"
              value={form.url}
              onChange={handleInputChange}
              className="w-full border rounded px-3 py-2 mb-2"
              required
            />
            <input
              type="number"
              name="rating"
              placeholder="Rating (0-10)"
              min="0"
              max="10"
              value={form.rating}
              onChange={handleInputChange}
              className="w-full border rounded px-3 py-2 mb-2"
              required
            />
            {error && <div className="text-red-500 text-sm mb-2">{error}</div>}
            <div className="flex gap-2 justify-end">
              <button type="submit" className="bg-[#4CAF50] text-white px-4 py-2 rounded hover:bg-[#3d8b40]">Add</button>
              <button type="button" className="bg-gray-200 px-4 py-2 rounded" onClick={() => { setShowForm(false); setError(''); }}>Cancel</button>
            </div>
          </form>
        ) : (
          <button
            className="bg-[#4CAF50] text-white px-4 py-2 rounded hover:bg-[#3d8b40] w-full"
            onClick={() => setShowForm(true)}
          >
            + Add Rating Source
          </button>
        )}
      </div>
    </div>
  );
};

export default RatingSourcesModal;
