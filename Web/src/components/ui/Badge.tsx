import type { ReactNode } from 'react';

interface BadgeProps {
  variant: 'error' | 'warning' | 'success' | 'info' | 'neutral';
  children: ReactNode;
  className?: string;
}

export default function Badge({ variant, children, className }: BadgeProps) {
  return (
    <span className={`themed-badge status-badge-${variant}${className ? ` ${className}` : ''}`}>
      {children}
    </span>
  );
}
