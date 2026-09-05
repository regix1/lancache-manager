import React from 'react';

interface MicrosoftIconProps {
  size?: number;
  className?: string;
}

/** The four-square Microsoft symbol from the Microsoft Entra sign-in branding assets. */
export const MicrosoftIcon: React.FC<MicrosoftIconProps> = ({ size = 24, className = '' }) => (
  <svg
    viewBox="0 0 21 21"
    xmlns="http://www.w3.org/2000/svg"
    width={size}
    height={size}
    className={className}
    aria-hidden="true"
    focusable="false"
  >
    <rect x="1" y="1" width="9" height="9" fill="#f25022" />
    <rect x="1" y="11" width="9" height="9" fill="#00a4ef" />
    <rect x="11" y="1" width="9" height="9" fill="#7fba00" />
    <rect x="11" y="11" width="9" height="9" fill="#ffb900" />
  </svg>
);
