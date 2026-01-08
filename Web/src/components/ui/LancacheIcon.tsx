import React from 'react';

interface LancacheIconProps {
  className?: string;
  size?: number;
  style?: React.CSSProperties;
}

const LancacheIcon: React.FC<LancacheIconProps> = ({ className = '', size = 24, style = {} }) => {
  return (
    <svg
      className={`block ${className}`}
      width={size}
      height={size}
      viewBox="0 0 48 48"
      xmlns="http://www.w3.org/2000/svg"
      style={style}
    >
      {/* Main controller body */}
      <path
        d="m42.059 40c-3.252 0-7.162-3.224-10.812-9h-14.494c-4.237 6.706-8.824 9.973-12.323 8.75a5.5 5.5 0 0 1 -3.077-3.056c-2.215-4.646-1.653-13.749 1.373-22.137a10.049 10.049 0 0 1 15.443-4.557h11.662a10.049 10.049 0 0 1 15.443 4.557c3.024 8.388 3.588 17.491 1.373 22.137a5.5 5.5 0 0 1 -3.077 3.056 4.555 4.555 0 0 1 -1.511.25z"
        fill="var(--theme-primary)"
      />

      {/* Shadow/depth */}
      <path
        d="m44.794 36.735a4.854 4.854 0 0 1 -1.606.265c-3.455 0-7.609-3.425-11.488-9.565h-15.4c-4.5 7.125-9.376 10.6-13.094 9.3a5.249 5.249 0 0 1 -2.567-2.035 11.421 11.421 0 0 0 .714 2 5.5 5.5 0 0 0 3.077 3.05c3.5 1.223 8.086-2.044 12.323-8.75h14.494c3.65 5.778 7.56 9 10.812 9a4.555 4.555 0 0 0 1.511-.252 5.5 5.5 0 0 0 3.077-3.056 11.421 11.421 0 0 0 .714-2 5.249 5.249 0 0 1 -2.567 2.043z"
        fill="#000000"
        opacity="0.2"
      />

      {/* Center button divider */}
      <path d="m26 25h-4a1 1 0 0 1 0-2h4a1 1 0 0 1 0 2z" fill="#000000" opacity="0.3" />

      {/* Right button circle (dark background) */}
      <circle cx="36" cy="19" r="7.5" fill="#000000" opacity="0.3" />

      {/* Left D-pad circle (dark background) */}
      <circle cx="12" cy="19" r="7" fill="#000000" opacity="0.3" />

      {/* Action buttons - colored */}
      <circle cx="36" cy="15.5" r="1.5" fill="var(--theme-icon-yellow)" />
      <circle cx="36" cy="22.5" r="1.5" fill="var(--theme-icon-blue)" />
      <circle cx="39.5" cy="19" r="1.5" fill="var(--theme-icon-green)" />
      <circle cx="32.5" cy="19" r="1.5" fill="var(--theme-icon-red)" />

      {/* D-pad */}
      <path
        d="m15.5 17.5h-2v-2a1.5 1.5 0 0 0 -3 0v2h-2a1.5 1.5 0 0 0 0 3h2v2a1.5 1.5 0 0 0 3 0v-2h2a1.5 1.5 0 0 0 0-3z"
        fill="var(--theme-button-text)"
        opacity="0.9"
      />
    </svg>
  );
};

export default LancacheIcon;
