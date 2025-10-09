import React from 'react';

interface WsusIconProps {
  size?: number;
  className?: string;
  style?: React.CSSProperties;
}

export const WsusIcon: React.FC<WsusIconProps> = ({ size = 24, className = '', style = {} }) => {
  const gradientId = React.useId();

  return (
    <svg
      viewBox="0 0 24 24"
      width={size}
      height={size}
      className={className}
      style={style}
      enableBackground="new 0 0 24 24"
      xmlns="http://www.w3.org/2000/svg"
    >
      <defs>
        <linearGradient id={gradientId} gradientUnits="userSpaceOnUse" x1="-0.1" x2="25.632" y1="5.358" y2="17.357">
          <stop offset="0" stopColor="#fff" stopOpacity="0.2" />
          <stop offset="1" stopColor="#fff" stopOpacity="0" />
        </linearGradient>
      </defs>
      <g>
        <path d="m12 11h11v-10l-11 1z" fill="currentColor" />
        <path d="m10 11v-8.8181763l-9 .8181763v8z" fill="currentColor" />
        <path d="m12 2v.25l11-1v-.25z" fill="#fff" opacity={0.2} />
        <path d="m12 10.75h11v.25h-11z" opacity={0.1} />
        <path d="m1 3v.25l9-.8181763v-.25z" fill="#fff" opacity={0.2} />
        <path d="m1 10.75h9v.25h-9z" opacity={0.1} />
        <path d="m12 13h11v10l-11-1z" fill="currentColor" />
        <path d="m10 13v8.8181763l-9-.8181763v-8z" fill="currentColor" />
        <path d="m12 22v-.25l11 1v.25z" opacity={0.1} />
        <path d="m12 13h11v.25h-11z" fill="#fff" opacity={0.2} />
        <path d="m1 21v-.25l9 .8181763v.25z" opacity={0.1} />
        <path d="m1 13h9v.25h-9z" fill="#fff" opacity={0.2} />
        <path
          d="m12 2v9h11v-10zm-11 9h9v-8.8181763l-9 .8181763zm11 11 11 1v-10h-11zm-11-1 9 .8181763v-8.8181763h-9z"
          fill={`url(#${gradientId})`}
        />
      </g>
    </svg>
  );
};
