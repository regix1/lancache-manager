import React from 'react';

interface CardProps {
  children: React.ReactNode;
  className?: string;
  padding?: 'none' | 'sm' | 'md' | 'lg';
  glassmorphism?: boolean;
}

export const Card: React.FC<CardProps> = ({ children, className = '', padding = 'lg', glassmorphism = false }) => {
  const paddings = {
    none: '',
    sm: 'p-3',
    md: 'p-4',
    lg: 'p-6'
  };

  const baseClass = glassmorphism ? 'glass-card' : 'themed-card';

  return (
    <div className={`${baseClass} rounded-lg border ${paddings[padding]} ${className}`}>{children}</div>
  );
};
