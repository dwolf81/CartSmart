import React from 'react';

const LoadingSpinner = () => (
  <div className="flex justify-center items-center h-40">
    <div className="w-12 h-12 border-4 border-green-500 border-t-transparent border-solid rounded-full animate-spin"></div>
  </div>
);

export default LoadingSpinner;
