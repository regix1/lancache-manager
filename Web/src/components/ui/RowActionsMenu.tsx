import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { MoreHorizontal } from 'lucide-react';
import { Button } from './Button';
import { ActionMenu } from './ActionMenu';

interface RowActionsMenuProps {
  /** Render-prop: receives a `close` fn each item's onClick must call after firing its action. */
  children: (close: () => void) => ReactNode;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /**
   * Hides the trigger until the row is hovered or focused. Only ThemeCard's grid needs this - the
   * other four sites show the trigger at rest. Reveals on group-focus-within too, so a keyboard
   * user tabbing through the row can still find and activate it. [27]
   */
  revealOnHover?: boolean;
  /** Passed to ActionMenu. Row labels such as "Terminate session" need more than the menu default. */
  width?: string;
}

// Controlled row-level kebab menu, the per-row sibling of SectionActionsMenu (which owns its own
// open state). A row list re-renders many trigger instances, so the open row's state has to live
// with the list, not inside each trigger. [11]
export function RowActionsMenu({
  children,
  open,
  onOpenChange,
  revealOnHover = false,
  width = 'w-56'
}: RowActionsMenuProps) {
  const { t } = useTranslation();
  const close = () => onOpenChange(false);

  return (
    <ActionMenu
      isOpen={open}
      onClose={close}
      width={width}
      trigger={
        <Button
          type="button"
          variant="menu"
          size="sm"
          open={open}
          className={`btn-icon-square btn-icon-square--sm pointer-target-44${
            revealOnHover ? ' opacity-0 group-hover:opacity-100 group-focus-within:opacity-100' : ''
          }${open ? ' opacity-100' : ''}`}
          onClick={() => onOpenChange(!open)}
          aria-label={t('common.moreActions')}
        >
          <MoreHorizontal className="w-4 h-4" />
        </Button>
      }
    >
      {children(close)}
    </ActionMenu>
  );
}
