import React from 'react';
import { FaTimes } from 'react-icons/fa';

const ReviewCommentsModal = ({ isOpen, onClose, deal, loading }) => {
  if (!isOpen) return null;
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40">
      <div className="bg-white w-full max-w-md rounded-lg shadow-lg p-5">
        <div className="flex items-center justify-between mb-3">
          <h3 className="text-lg font-semibold">Review Comments</h3>
          <button onClick={onClose} className="p-2 text-gray-500 hover:text-gray-700">
            <FaTimes />
          </button>
        </div>
        {loading && <div className="text-sm text-gray-500 mb-2">Loading comments...</div>}
        {!loading && deal?.reviews?.length === 0 && (
          <div className="text-sm text-gray-500">No review comments.</div>
        )}
        <ul className="space-y-3 max-h-[50vh] overflow-y-auto">
          {deal?.reviews?.map(c => (
            <li key={c.id || c._idx} className="border rounded p-3 bg-gray-50">
              <div className="text-sm text-gray-800 whitespace-pre-line">{c.review_comment}</div>
              
            </li>
          ))}
        </ul>
        <div className="mt-4 text-right">
          <button
            onClick={onClose}
            className="px-4 py-2 text-sm rounded border bg-white hover:bg-gray-100"
          >
            Close
          </button>
        </div>
      </div>
    </div>
  );
};

export default ReviewCommentsModal;