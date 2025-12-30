import { useEffect } from 'react';

export const useScrollLock = (isLocked) => {
  useEffect(() => {
    // Get the width of the scrollbar by comparing window width with and without scrollbar
    const scrollbarWidth = window.innerWidth - document.documentElement.clientWidth;
    
    if (isLocked) {
      // Save the current padding if any
      const currentPadding = parseInt(getComputedStyle(document.body).paddingRight, 10) || 0;
      
      // Apply the scrollbar width as padding to prevent content shift
      document.body.style.paddingRight = `${currentPadding + scrollbarWidth}px`;
      document.body.style.overflow = 'hidden';
    } else {
      document.body.style.paddingRight = '';
      document.body.style.overflow = 'auto';
    }

    return () => {
      document.body.style.paddingRight = '';
      document.body.style.overflow = 'auto';
    };
  }, [isLocked]);
};