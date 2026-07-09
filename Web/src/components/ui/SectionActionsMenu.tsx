import { useState, type ReactNode } from 'react';
import { MoreHorizontal } from 'lucide-react';
import { Button } from './Button';
import { ActionMenu } from './ActionMenu';

interface SectionActionsMenuProps {
  /** Render-prop: receives a `close` fn each item's onClick must call after firing its action. */
  children: (close: () => void) => ReactNode;
  /** aria-label for the trigger (i18n string). */
  label: string;
  /** Menu alignment relative to the trigger. */
  align?: 'left' | 'right';
  /** Menu width utility class. */
  width?: string;
  /** Disables the whole trigger. */
  disabled?: boolean;
}

export function SectionActionsMenu({
  children,
  label,
  align = 'right',
  width = 'w-48',
  disabled
}: SectionActionsMenuProps) {
  const [open, setOpen] = useState(false);
  const close = () => setOpen(false);

  return (
    <ActionMenu
      isOpen={open}
      onClose={close}
      align={align}
      width={width}
      trigger={
        <Button
          variant="filled"
          color="gray"
          size="sm"
          className="!px-0 w-8 h-8 justify-center"
          onClick={() => setOpen((o) => !o)}
          disabled={disabled}
          aria-label={label}
        >
          <MoreHorizontal className="w-4 h-4" />
        </Button>
      }
    >
      {children(close)}
    </ActionMenu>
  );
}
