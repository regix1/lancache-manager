import React from 'react';

type GroupAccent = 'accent' | 'steam' | 'epic' | 'blizzard' | 'riot' | 'xbox';

interface GroupHeadingProps {
  label: string;
  /** Bar colour token suffix. Defaults to the accent; only IntegrationsSection passes a brand. */
  accent?: GroupAccent;
  /** Trailing controls, e.g. AccordionGroupToggle. 10 of the 23 sites pass one. */
  actions?: React.ReactNode;
}

// Class names must appear as literal strings (never template-built), or Tailwind's content
// scanner purges the matching @layer components rules - same reasoning as RetroRow's
// GAUGE_TIER_CLASS.
const ACCENT_BAR_CLASS: Record<GroupAccent, string> = {
  accent: 'bg-[var(--theme-accent)]',
  steam: 'bg-[var(--theme-steam)]',
  epic: 'bg-[var(--theme-epic)]',
  blizzard: 'bg-[var(--theme-blizzard)]',
  riot: 'bg-[var(--theme-riot)]',
  xbox: 'bg-[var(--theme-xbox)]'
};

// Accent bar + caps-label heading used above a management accordion group. The
// two-level row keeps the trailing control pinned to the far right edge via
// justify-between - flattening it into one row would drag actions in beside
// the label instead.
export const GroupHeading: React.FC<GroupHeadingProps> = ({
  label,
  accent = 'accent',
  actions
}) => (
  <div className="flex items-center justify-between gap-2 mb-3 sm:mb-4">
    <div className="flex items-center gap-2 min-w-0">
      <div className={`w-1 h-5 rounded-full ${ACCENT_BAR_CLASS[accent]}`} />
      <h3 className="management-group-label caps-label">{label}</h3>
    </div>
    {actions}
  </div>
);
