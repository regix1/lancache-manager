import React, { useEffect, useRef, useState, useLayoutEffect, type ReactNode } from 'react';
import { createPortal } from 'react-dom';

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
  const [position, setPosition] = useState({ top: 0, left: 0 });

  // Calculate position and track trigger movement
  useLayoutEffect(() => {
    if (!isOpen || !triggerRef.current) return;

    const calculatePosition = () => {
      if (!triggerRef.current) return null;

      const rect = triggerRef.current.getBoundingClientRect();
      const menuWidth = 160; // w-40 = 10rem = 160px

      let left: number;
      if (align === 'right') {
        // Align right edge of menu with right edge of trigger
        left = rect.right - menuWidth;
      } else {
        // Align left edge of menu with left edge of trigger
        left = rect.left;
      }

      // Ensure menu doesn't go off-screen
      const viewportWidth = window.innerWidth;
      if (left + menuWidth > viewportWidth - 8) {
        left = viewportWidth - menuWidth - 8;
      }
      if (left < 8) {
        left = 8;
      }

      return {
        top: rect.bottom + 4, // 4px gap below trigger
        left,
        triggerTop: rect.top
      };
    };

    // Initial position
    const initialPos = calculatePosition();
    if (initialPos) {
      setPosition({ top: initialPos.top, left: initialPos.left });
    }

    // Track position changes (e.g., from notification dismissal causing layout shift)
    let lastTriggerTop = initialPos?.triggerTop ?? 0;
    let animationFrameId: number;

    const checkPosition = () => {
      const newPos = calculatePosition();
      if (newPos) {
        // If trigger moved significantly (more than 2px), close the menu
        // This handles layout shifts from notifications disappearing
        if (Math.abs(newPos.triggerTop - lastTriggerTop) > 2) {
          onClose();
          return;
        }

        // Update position if it changed slightly
        setPosition((prev) => {
          if (prev.top !== newPos.top || prev.left !== newPos.left) {
            return { top: newPos.top, left: newPos.left };
          }
          return prev;
        });
      }
      animationFrameId = requestAnimationFrame(checkPosition);
    };

    animationFrameId = requestAnimationFrame(checkPosition);

    return () => {
      cancelAnimationFrame(animationFrameId);
    };
  }, [isOpen, align, onClose]);

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

    // Close on scroll to prevent menu from being mispositioned
    const handleScroll = (e: Event) => {
      if (isOpen) {
        // Ignore scroll events originating from inside the dropdown
        if (dropdownRef.current && dropdownRef.current.contains(e.target as Node)) {
          return;
        }
        onClose();
      }
    };

    if (isOpen) {
      document.addEventListener('mousedown', handleClickOutside);
      document.addEventListener('keydown', handleEscape);
      window.addEventListener('scroll', handleScroll, true);
      return () => {
        document.removeEventListener('mousedown', handleClickOutside);
        document.removeEventListener('keydown', handleEscape);
        window.removeEventListener('scroll', handleScroll, true);
      };
    }
  }, [isOpen, onClose]);

  return (
    <div className="relative inline-flex">
      {/* Trigger button wrapper - adds data attribute */}
      <div ref={triggerRef} data-action-menu-trigger="true" className="inline-flex">{trigger}</div>

      {/* Dropdown Menu - rendered via portal to escape stacking context */}
      {isOpen && createPortal(
        <div
          ref={dropdownRef}
          className={`fixed ${width} bg-themed-secondary rounded-lg shadow-xl overflow-hidden border border-themed-primary z-[9999] animate-[dropdownSlide_0.15s_ease-out]`}
          style={{
            top: position.top,
            left: position.left,
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
  children
}) => {
  return (
    <button
      onClick={onClick}
      className="w-full px-3 py-2 text-left text-sm flex items-center gap-2 transition-all duration-150 text-themed-error bg-transparent hover:bg-[var(--theme-error-bg)]"
    >
      {icon}
      {children}
    </button>
  );
};
