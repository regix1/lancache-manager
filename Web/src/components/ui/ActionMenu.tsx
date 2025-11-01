import React, { useEffect, useRef, ReactNode } from 'react';

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
  const dropdownRef = useRef<HTMLDivElement>(null);

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

    if (isOpen) {
      document.addEventListener('mousedown', handleClickOutside);
      document.addEventListener('keydown', handleEscape);
      return () => {
        document.removeEventListener('mousedown', handleClickOutside);
        document.removeEventListener('keydown', handleEscape);
      };
    }
  }, [isOpen, onClose]);

  const alignmentClass = align === 'left' ? 'left-0' : 'right-0';

  return (
    <div className="relative">
      {/* Trigger button wrapper - adds data attribute */}
      <div data-action-menu-trigger="true">
        {trigger}
      </div>

      {/* Dropdown Menu */}
      {isOpen && (
        <div
          ref={dropdownRef}
          className={`absolute ${alignmentClass} mt-1 ${width} bg-themed-secondary rounded-lg shadow-lg z-50 animate-fadeIn origin-top-${align} overflow-hidden`}
          style={{
            border: '1px solid var(--theme-border-primary)',
            animation: 'dropdownSlide 0.2s ease-out'
          }}
        >
          {children}
        </div>
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
  return (
    <div
      className="border-t my-1"
      style={{ borderColor: 'var(--theme-border-primary)' }}
    />
  );
};

export const ActionMenuDangerItem: React.FC<ActionMenuDangerItemProps> = ({
  onClick,
  icon,
  children
}) => {
  return (
    <button
      onClick={onClick}
      className="w-full px-3 py-2 text-left text-sm flex items-center gap-2 transition-all duration-150"
      style={{
        color: 'var(--theme-error-text)',
        backgroundColor: 'transparent'
      }}
      onMouseEnter={(e) => {
        e.currentTarget.style.backgroundColor = 'var(--theme-error-bg)';
      }}
      onMouseLeave={(e) => {
        e.currentTarget.style.backgroundColor = 'transparent';
      }}
    >
      {icon}
      {children}
    </button>
  );
};
