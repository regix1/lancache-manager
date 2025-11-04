import React from 'react';

interface SteamIconProps {
  size?: number;
  className?: string;
  style?: React.CSSProperties;
}

export const SteamIcon: React.FC<SteamIconProps> = ({ size = 24, className = '', style = {} }) => (
  <svg
    viewBox="0 0 24 24"
    xmlns="http://www.w3.org/2000/svg"
    width={size}
    height={size}
    className={className}
    style={style}
  >
    <g fill="currentColor">
      <circle cx="15.5" cy="9.5" r="2.5"></circle>
      <path d="m8.67 18.34a1.49 1.49 0 0 1 -1.67-.21.5.5 0 0 0 -.66.75 2.5 2.5 0 1 0 2-4.35.49.49 0 0 0 -.56.43.5.5 0 0 0 .43.56 1.5 1.5 0 0 1 .47 2.83z"></path>
      <path d="m12 0a12 12 0 0 0 -12 11.5.5.5 0 0 0 .14.37.5.5 0 0 0 .26.13c.34.11 3 1.26 4.55 2a.51.51 0 0 0 .52-.07 3.84 3.84 0 0 1 2.86-.93.5.5 0 0 0 .45-.19l2.11-2.76a.5.5 0 0 0 .1-.35c0-.08 0-.15 0-.22a4.5 4.5 0 1 1 4.81 4.52.5.5 0 0 0 -.28.11l-3.35 2.75a.5.5 0 0 0 -.18.36 4 4 0 0 1 -3.99 3.78 3.94 3.94 0 0 1 -3.84-2.93.5.5 0 0 0 -.26-.32l-1.9-.93a.5.5 0 0 0 -.67.68 12 12 0 1 0 10.67-17.5z"></path>
    </g>
  </svg>
);
