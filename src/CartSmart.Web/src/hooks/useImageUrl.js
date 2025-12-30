import { useMemo } from 'react';

export const useImageUrl = (baseUrl, size = 'large') => {
  return useMemo(() => {
    if (!baseUrl) return 'https://placehold.co/100x100';
    
    // If it's already a complete URL with size, return as-is
    if (baseUrl.includes('_') && baseUrl.includes('.webp')) {
      return baseUrl;
    }

    const sizeMap = {
      small: '_32x32.webp',
      large: '_100x100.webp',
      original: '' // no suffix for original
    };

    return `${baseUrl}${sizeMap[size] || sizeMap.large}`;
  }, [baseUrl, size]);
};