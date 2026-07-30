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
 * The gap is `gap-2 sm:gap-3`, the same pair AccordionSection's own header row uses.
 * It has to be repeated because this is a nested flex container and gap does not
 * inherit: when the two values drifted apart, the chip-to-kebab gap was 8px while the
 * kebab-to-chevron gap beside it was 12px on every screen above the sm breakpoint.
 * Declaring it once here is what keeps them from drifting again.
 */
export function SectionHeaderActions({ children }: SectionHeaderActionsProps) {
  return (
    <div className="flex flex-wrap items-center gap-2 sm:gap-3 w-full justify-start sm:w-auto sm:justify-end">
      {children}
    </div>
  );
}

/**
 * A status chip in a section header, either on its own in AccordionSection's `badge`
 * slot or alongside buttons inside SectionHeaderActions.
 *
 * The same Badge, pinned to the 40px height (`control-h-md`) and the corner radius
 * (`themed-border-radius-sm`) the kebab and the accordion chevron beside it already
 * use, so the row reads as one line of equal boxes instead of a short pill next to
 * two tall buttons. It keeps the badge's own tinted fill and gains no hover state, no
 * pointer cursor and no button border, so it still reads as a label, not a control.
 *
 * Use this, not a bare Badge, for anything that lands in a section header. A plain
 * Badge stays small on purpose for inline text, table cells and list rows.
 */
export function SectionHeaderChip({ className, ...badge }: BadgeProps) {
  return (
    <Badge
      {...badge}
      className={`control-h-md themed-border-radius-sm${className ? ` ${className}` : ''}`}
    />
  );
}
