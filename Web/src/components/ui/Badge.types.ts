import type { ReactNode } from 'react';

export type BadgeVariant =
  | 'error'
  | 'warning'
  | 'success'
  | 'info'
  | 'neutral'
  | 'waiting'
  | 'waiting-outline';

export interface BadgeProps {
  variant: BadgeVariant;
  children: ReactNode;
  className?: string;
  /** Accessible name for badges whose text alone is not self-describing (a bare count). */
  ariaLabel?: string;
  /** Renders a button with the same pill chrome so a clickable chip matches its static twin. */
  onClick?: () => void;
}
