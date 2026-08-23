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
 * A thin passthrough - AccordionSection already wraps `badge` in its own
 * `.section-header-actions` div (AccordionSection.tsx), so this component no longer
 * renders a second one. A caller keeps writing `<SectionHeaderActions>{...}</SectionHeaderActions>`
 * unchanged; it just resolves to a fragment now instead of a nested duplicate div.
 */
export function SectionHeaderActions({ children }: SectionHeaderActionsProps) {
  return <>{children}</>;
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
