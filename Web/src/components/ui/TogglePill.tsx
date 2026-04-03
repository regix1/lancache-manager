import React from 'react';

interface TogglePillProps extends Omit<React.ButtonHTMLAttributes<HTMLButtonElement>, 'size'> {
  active?: boolean;
  size?: 'sm' | 'md';
}

export const TogglePill: React.FC<TogglePillProps> = ({
  active = false,
  size = 'sm',
  className = '',
  children,
  ...props
}) => {
  return (
    <button
      className={`toggle-pill toggle-pill--${size}${active ? ' active' : ''} ${className}`}
      {...props}
    >
      {children}
    </button>
  );
};
