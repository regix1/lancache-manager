import React, {
  useCallback,
  useEffect,
  useRef,
  useState,
  useLayoutEffect,
  type ReactNode
} from 'react';
import { createPortal } from 'react-dom';
import { useExitPresence, DROPDOWN_EXIT_MS } from '@hooks/useExitPresence';
import { useAnchorFollow, readAnchorRect, type AnchorRect } from '@hooks/useAnchorFollow';

interface MenuPosition {
  top: number;
  left: number;
}

const MENU_GAP_PX = 4;
const VIEWPORT_PADDING_PX = 8;
/** Position deltas at or below this are rounding noise, not movement. */
const POSITION_EPSILON_PX = 0.5;

function isSamePosition(a: MenuPosition, b: MenuPosition): boolean {
  return (
    Math.abs(a.top - b.top) <= POSITION_EPSILON_PX &&
    Math.abs(a.left - b.left) <= POSITION_EPSILON_PX
  );
}

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

const TAILWIND_UNIT_PX = 4; // 0.25rem at 16px root

// Derives the pixel width of a Tailwind `w-<n>` class so the off-screen clamp
// agrees with whatever width the caller actually rendered (falls back to the
// component's own default of w-40 = 160px if the class doesn't match).
const parseMenuWidthPx = (widthClass: string): number => {
  const match = /^w-(\d+(?:\.\d+)?)$/.exec(widthClass.trim());
  return match ? Math.round(parseFloat(match[1]) * TAILWIND_UNIT_PX) : 160;
};

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
  const [position, setPosition] = useState<MenuPosition>({ top: 0, left: 0 });
  const { present, closing } = useExitPresence(isOpen, DROPDOWN_EXIT_MS);

  const calculatePosition = useCallback(
    (anchor: AnchorRect): MenuPosition => {
      const menuWidth = parseMenuWidthPx(width);

      // Align the menu's matching edge with the trigger's, then keep it on screen.
      let left = align === 'right' ? anchor.right - menuWidth : anchor.left;
      const viewportWidth = window.innerWidth;
      if (left + menuWidth > viewportWidth - VIEWPORT_PADDING_PX) {
        left = viewportWidth - menuWidth - VIEWPORT_PADDING_PX;
      }
      if (left < VIEWPORT_PADDING_PX) {
        left = VIEWPORT_PADDING_PX;
      }

      // Clamped against the viewport, returned in document coordinates: the menu is
      // absolutely positioned in a body portal so that scrolling carries it and its
      // trigger together (see useAnchorFollow).
      return {
        top: anchor.bottom + MENU_GAP_PX + window.scrollY,
        left: left + window.scrollX
      };
    },
    [align, width]
  );

  /**
   * Carries the menu with its trigger. The page reflows under the portalled menu
   * whenever UniversalNotificationBar (an in-flow sticky bar) shows or finishes an
   * operation; this used to close the menu on any trigger movement, which yanked the
   * menu out from under the user mid-click.
   */
  const handleAnchorMove = useCallback(
    (anchor: AnchorRect): void => {
      const next = calculatePosition(anchor);
      setPosition((prev) => (isSamePosition(prev, next) ? prev : next));
    },
    [calculatePosition]
  );

  useLayoutEffect(() => {
    if (!isOpen || !triggerRef.current) return;
    handleAnchorMove(readAnchorRect(triggerRef.current));
  }, [isOpen, handleAnchorMove]);

  useAnchorFollow({
    enabled: present,
    anchorRef: triggerRef,
    onAnchorMove: handleAnchorMove,
    // Nothing left to anchor to once the trigger is scrolled off screen.
    onAnchorLost: onClose
  });

  // Handle click outside and escape key
  useEffect(() => {
    const handleClickOutside = (event: MouseEvent) => {
      const target = event.target as HTMLElement;

      // Check if click is on trigger button or its children
      const isTriggerButton = target.closest('[data-action-menu-trigger="true"]');

      // Check if click is inside dropdown
      const isInsideDropdown = dropdownRef.current && dropdownRef.current.contains(target);

      // Close dropdown if click is outside both the button and dropdown
      if (isOpen && !isTriggerButton && !isInsideDropdown) {
        onClose();
      }
    };

    const handleEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && isOpen) {
        onClose();
      }
    };

    // No scroll handler: the menu follows its trigger (see useAnchorFollow) and
    // dismisses itself once the trigger leaves the viewport, so scrolling can no
    // longer misposition it.
    if (isOpen) {
      document.addEventListener('mousedown', handleClickOutside);
      document.addEventListener('keydown', handleEscape);
      return () => {
        document.removeEventListener('mousedown', handleClickOutside);
        document.removeEventListener('keydown', handleEscape);
      };
    }
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
            className={`am-dropdown absolute ${width} bg-themed-secondary themed-border-radius shadow-xl overflow-hidden border border-themed-primary z-[85] ${
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
      className="w-full px-3 py-2 text-left text-sm hover:bg-themed-hover flex items-center gap-2 transition-colors duration-150 disabled:opacity-50 disabled:cursor-not-allowed"
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
      className="w-full px-3 py-2 text-left text-sm flex items-center gap-2 transition-colors duration-150 text-themed-error bg-transparent hover:bg-[var(--theme-error-bg)] disabled:opacity-50 disabled:cursor-not-allowed"
    >
      {icon}
      {children}
    </button>
  );
};
