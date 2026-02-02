import React, { useEffect, useRef, useState } from 'react';

const DEFAULT_MESSAGE =
  'Early rewards are reputation and trust. As CartSmart grows, top contributors unlock more benefits.';

const RewardsTooltipPill = ({
  label = 'Earn rewards',
  message = DEFAULT_MESSAGE,
  pillClassName = 'text-xs bg-white/15 px-2 py-0.5 rounded-full',
  tooltipClassName =
    'absolute right-0 top-full mt-2 w-72 bg-gray-900 text-white text-xs rounded-md px-3 py-2 shadow-lg z-50',
  stopPropagation = true
}) => {
  const [open, setOpen] = useState(false);
  const rootRef = useRef(null);

  useEffect(() => {
    if (!open) return;

    const onMouseDown = (e) => {
      if (!rootRef.current) return;
      if (rootRef.current.contains(e.target)) return;
      setOpen(false);
    };

    window.addEventListener('mousedown', onMouseDown);
    return () => window.removeEventListener('mousedown', onMouseDown);
  }, [open]);

  const toggle = (e) => {
    if (stopPropagation) {
      e.preventDefault();
      e.stopPropagation();
    }
    setOpen((v) => !v);
  };

  const onKeyDown = (e) => {
    if (e.key !== 'Enter' && e.key !== ' ') return;
    toggle(e);
  };

  return (
    <span ref={rootRef} className="relative inline-flex">
      <span
        role="button"
        tabIndex={0}
        onClick={toggle}
        onKeyDown={onKeyDown}
        className={`${pillClassName} cursor-pointer select-none`}
        aria-label="Rewards info"
        title="Click for rewards info"
      >
        {label}
      </span>
      {open && <span className={tooltipClassName}>{message}</span>}
    </span>
  );
};

export default RewardsTooltipPill;
