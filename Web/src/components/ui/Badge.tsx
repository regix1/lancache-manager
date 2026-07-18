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
  /**
   * Filled emphasis: keep the solid tinted fill instead of the default quiet
   * outline. Reserve for deliberate emphasis (a single per-card warning, a
   * stale-scan callout), never inside a dense row of peer badges. Ignored for
   * the neutral and waiting variants, which are their own axes.
   */
  emphasis?: boolean;
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

// Status variants that default to the quiet outline treatment. The neutral
// count-chip axis and the notification waiting pills always keep their own look.
const OUTLINE_VARIANTS = new Set<BadgeVariant>(['error', 'warning', 'success', 'info']);

export default function Badge({ variant, children, className, emphasis }: BadgeProps) {
  const outline = !emphasis && OUTLINE_VARIANTS.has(variant);
  return (
    <span
      className={`themed-badge ${VARIANT_CLASS[variant]}${outline ? ' badge-outline' : ''}${className ? ` ${className}` : ''}`}
    >
      {children}
    </span>
  );
}
