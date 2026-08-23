import React, { useEffect, useRef, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { useAnchoredPanel } from '@hooks/useAnchoredPanel';
import { MENU_GUTTER_PX } from '@utils/viewportClamp';

interface ActionMenuProps {
  isOpen: boolean;
  onClose: () => void;
  trigger: ReactNode;
  children: ReactNode;
  align?: 'left' | 'right';
  width?: string;
}

interface ActionMenuItemProps {
  onClick: () => void;
  icon?: ReactNode;
  children: ReactNode;
  disabled?: boolean;
}

interface ActionMenuDangerItemProps {
  onClick: () => void;
  icon?: ReactNode;
  children: ReactNode;
  disabled?: boolean;
}

export const ActionMenu: React.FC<ActionMenuProps> = ({
  isOpen,
  onClose,
  trigger,
  children,
  align = 'right',
  width = 'w-40'
}) => {
  const triggerRef = useRef<HTMLDivElement>(null);
  const dropdownRef = useRef<HTMLDivElement>(null);

  const { present, closing, position } = useAnchoredPanel({
    open: isOpen,
    anchorRef: triggerRef,
    panelRef: dropdownRef,
    onClose,
    gutter: MENU_GUTTER_PX,
    align
  });

  // Click-outside stays here: which element counts as the trigger differs panel by
  // panel, so the shared hook deliberately owns neither this nor the scroll story.
  useEffect(() => {
    if (!isOpen) return;

    const handleClickOutside = (event: MouseEvent) => {
      const target = event.target as HTMLElement;

      // Check if click is on trigger button or its children
      const isTriggerButton = target.closest('[data-action-menu-trigger="true"]');

      // Check if click is inside dropdown
      const isInsideDropdown = dropdownRef.current && dropdownRef.current.contains(target);

      // Close dropdown if click is outside both the button and dropdown
      if (!isTriggerButton && !isInsideDropdown) {
        onClose();
      }
    };

    document.addEventListener('mousedown', handleClickOutside);
    return () => document.removeEventListener('mousedown', handleClickOutside);
  }, [isOpen, onClose]);

  return (
    <div className="relative inline-flex">
      {/* Trigger button wrapper - adds data attribute */}
      <div ref={triggerRef} data-action-menu-trigger="true" className="inline-flex">
        {trigger}
      </div>

      {/* Dropdown Menu - rendered via portal to escape stacking context.
          Rendered while `present` (not just `isOpen`) so the exit animation plays. */}
      {present &&
        createPortal(
          <div
            ref={dropdownRef}
            className={`am-dropdown absolute ${width} bg-themed-secondary themed-border-radius-sm shadow-xl overflow-hidden border border-themed-primary z-[85] ${
              closing
                ? 'animate-[dropdownSlideOut_0.14s_ease-in_forwards]'
                : 'animate-[dropdownSlide_0.15s_ease-out]'
            }`}
            style={{
              top: position.top,
              left: position.left,
              pointerEvents: closing ? 'none' : undefined
            }}
          >
            {children}
          </div>,
          document.body
        )}
    </div>
  );
};

export const ActionMenuItem: React.FC<ActionMenuItemProps> = ({
  onClick,
  icon,
  children,
  disabled = false
}) => {
  return (
    <button
      onClick={onClick}
      disabled={disabled}
      className="w-full px-3 py-2 text-left text-sm whitespace-nowrap hover:bg-themed-hover flex items-center gap-2 transition-colors duration-150 disabled:opacity-50 disabled:cursor-not-allowed"
    >
      {icon}
      {children}
    </button>
  );
};

export const ActionMenuDivider: React.FC = () => {
  return <div className="border-t border-themed-primary my-1" />;
};

export const ActionMenuDangerItem: React.FC<ActionMenuDangerItemProps> = ({
  onClick,
  icon,
  children,
  disabled = false
}) => {
  return (
    <button
      onClick={onClick}
      disabled={disabled}
      className="w-full px-3 py-2 text-left text-sm whitespace-nowrap flex items-center gap-2 transition-colors duration-150 text-themed-error bg-transparent hover:bg-[var(--theme-error-bg)] disabled:opacity-50 disabled:cursor-not-allowed"
    >
      {icon}
      {children}
    </button>
  );
};
