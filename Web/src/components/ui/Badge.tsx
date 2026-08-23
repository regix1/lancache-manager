import type { BadgeProps, BadgeVariant } from './Badge.types';

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

export default function Badge({ variant, children, className, ariaLabel, onClick }: BadgeProps) {
  const classes = `themed-badge ${VARIANT_CLASS[variant]}${className ? ` ${className}` : ''}`;

  if (onClick) {
    return (
      <button type="button" className={classes} aria-label={ariaLabel} onClick={onClick}>
        {children}
      </button>
    );
  }

  return (
    <span className={classes} aria-label={ariaLabel}>
      {children}
    </span>
  );
}
