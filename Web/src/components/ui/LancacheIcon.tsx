import React from 'react';

interface LancacheIconProps {
  className?: string;
  size?: number;
  style?: React.CSSProperties;
}

const LancacheIcon: React.FC<LancacheIconProps> = ({ className = '', size = 24, style = {} }) => {
  return (
    <svg
      className={className}
      width={size}
      height={size}
      viewBox="0 0 512 512"
      xmlns="http://www.w3.org/2000/svg"
      fill="none"
      style={{ display: 'block', ...style }}
    >
      {/* Main controller body - PlayStation style with flat top */}
      <path
        d="M 90 130 L 422 130 C 450 130, 475 155, 480 200 L 490 280 C 495 340, 480 400, 450 430 C 430 450, 420 455, 400 455 C 370 455, 350 435, 340 400 L 320 330 L 256 320 L 192 330 L 172 400 C 162 435, 142 455, 112 455 C 92 455, 82 450, 62 430 C 32 400, 17 340, 22 280 L 32 200 C 37 155, 62 130, 90 130 Z"
        fill="var(--theme-primary)"
        stroke="#000000"
        strokeWidth="8"
      />

      {/* Top connecting bar - solid */}
      <rect
        x="190"
        y="130"
        width="132"
        height="15"
        fill="var(--theme-primary)"
        stroke="#000000"
        strokeWidth="6"
      />

      {/* D-Pad */}
      <g>
        <rect x="120" y="200" width="25" height="70" rx="6" fill="#000000" stroke="#000000" strokeWidth="4" />
        <rect x="95" y="225" width="75" height="20" rx="6" fill="#000000" stroke="#000000" strokeWidth="4" />
      </g>

      {/* Action Buttons - Four colored buttons */}
      <circle cx="390" cy="190" r="16" fill="#9ACD32" stroke="#000000" strokeWidth="4" />
      <circle cx="360" cy="220" r="16" fill="#FF6B6B" stroke="#000000" strokeWidth="4" />
      <circle cx="420" cy="220" r="16" fill="#4FC3F7" stroke="#000000" strokeWidth="4" />
      <circle cx="390" cy="250" r="16" fill="#FFD700" stroke="#000000" strokeWidth="4" />

      {/* Center buttons */}
      <rect x="220" y="218" width="30" height="12" rx="6" fill="#000000" opacity="0.6" />
      <rect x="262" y="218" width="30" height="12" rx="6" fill="#000000" opacity="0.6" />

      {/* Left analog stick */}
      <circle cx="160" cy="300" r="32" fill="#FFFFFF" stroke="#000000" strokeWidth="6" />
      <circle cx="160" cy="300" r="20" fill="#E0E0E0" />

      {/* Right analog stick */}
      <circle cx="352" cy="300" r="32" fill="#FFFFFF" stroke="#000000" strokeWidth="6" />
      <circle cx="352" cy="300" r="20" fill="#E0E0E0" />
    </svg>
  );
};

export default LancacheIcon;
