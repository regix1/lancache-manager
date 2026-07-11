import type { ReactNode } from 'react';

type BadgeVariant =
  | 'error'
  | 'warning'
  | 'success'
  | 'info'
  | 'neutral'
  | 'waiting'
  | 'waiting-outline';

interface BadgeProps {
  variant: BadgeVariant;
  children: ReactNode;
  className?: string;
}

// Literal class names (never `status-badge-${variant}`) so Tailwind's content
// scanner keeps these @layer components rules. A templated class string is not
// detected as a candidate and the rule gets tree-shaken out of the build, which
// silently strips the badge's background/color.
const VARIANT_CLASS: Record<BadgeVariant, string> = {
  error: 'status-badge-error',
  warning: 'status-badge-warning',
  success: 'status-badge-success',
  info: 'status-badge-info',
  neutral: 'status-badge-neutral',
  waiting: 'status-badge-waiting',
  'waiting-outline': 'status-badge-waiting-outline'
};

export default function Badge({ variant, children, className }: BadgeProps) {
  return (
    <span className={`themed-badge ${VARIANT_CLASS[variant]}${className ? ` ${className}` : ''}`}>
      {children}
    </span>
  );
}
