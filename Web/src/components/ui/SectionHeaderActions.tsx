import type { ReactNode } from 'react';
import Badge from './Badge';
import type { BadgeProps } from './Badge.types';

interface SectionHeaderActionsProps {
  children: ReactNode;
}

/**
 * The row of status chips and action buttons a section header hands to
 * AccordionSection's `badge` slot.
 *
 * The gap is `gap-2 sm:gap-3`, the same column-gap pair AccordionSection's own header row uses.
 * It has to be repeated because this is a nested flex container and gap does not
 * inherit: when the two values drifted apart, the chip-to-kebab gap was 8px while the
 * kebab-to-chevron gap beside it was 12px on every screen above the sm breakpoint.
 * Declaring it once here is what keeps them from drifting again.
 *
 * `.section-header-actions` carries the phone behaviour (patterns.css): the row is
 * full width there, so the trailing control moves to the right edge instead of the
 * whole cluster huddling in the left third.
 */
export function SectionHeaderActions({ children }: SectionHeaderActionsProps) {
  return (
    <div className="section-header-actions flex flex-wrap items-center gap-2 sm:gap-3 w-full justify-start sm:w-auto sm:justify-end">
      {children}
    </div>
  );
}

/**
 * A status chip in a section header, either on its own in AccordionSection's `badge`
 * slot or alongside buttons inside SectionHeaderActions.
 *
 * Keeps the badge's own compact box: about 16px tall on a 3px corner. It was previously
 * pinned to the 40px height and 8px corner of the kebab and chevron beside it so the row
 * read as one line of equal boxes, but that made a read-only label look like a button you
 * could press, and it was worst on phones where the row drops to its own full-width line.
 * A status chip is meant to read as a label, so it stays badge-sized and squarer than the
 * controls around it.
 *
 * Named separately from Badge so section-header chips stay findable as a group and can take
 * a shared treatment later without touching every call site.
 */
export function SectionHeaderChip({ className, ...badge }: BadgeProps) {
  return <Badge {...badge} className={className} />;
}
