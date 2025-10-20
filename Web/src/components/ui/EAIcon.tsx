import React from 'react';

interface EAIconProps {
  size?: number;
  className?: string;
  style?: React.CSSProperties;
}

export const EAIcon: React.FC<EAIconProps> = ({ size = 24, className = '', style = {} }) => (
  <svg
    viewBox="0 0 24 24"
    xmlns="http://www.w3.org/2000/svg"
    width={size}
    height={size}
    className={className}
    style={style}
    fill="currentColor"
  >
    <g transform="scale(1.15) translate(-1.565, -1.565)">
      <path d="m12 22.425c-5.748 0-10.425-4.677-10.425-10.425 0-5.749 4.677-10.425 10.425-10.425 5.748 0 10.425 4.676 10.425 10.425 0 5.748-4.677 10.425-10.425 10.425zm1.764-14.457h-5.893l-0.894 1.437h5.885zm1.204 0.014-3.602 5.735h-3.995l0.922-1.438h2.394l0.915-1.437h-5.536l-0.915 1.437h1.311l-1.819 2.871h7.593l2.805-4.423 1.023 1.552h-0.922l-0.873 1.438h2.731l0.948 1.433h1.742z" clipRule="evenodd" fillRule="evenodd"/>
    </g>
  </svg>
);
