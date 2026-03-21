import React, { useEffect, useCallback } from 'react';

interface DrawerProps {
  opened: boolean;
  onClose: () => void;
  position?: 'left' | 'right';
  title?: React.ReactNode;
  children: React.ReactNode;
  classNames?: {
    header?: string;
    body?: string;
    content?: string;
    title?: string;
  };
}

const Drawer: React.FC<DrawerProps> = ({
  opened,
  onClose,
  position = 'right',
  title,
  children,
  classNames
}) => {
  const handleKeyDown = useCallback(
    (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    },
    [onClose]
  );

  useEffect(() => {
    if (opened) {
      document.addEventListener('keydown', handleKeyDown);
      document.body.style.overflow = 'hidden';
    }
    return () => {
      document.removeEventListener('keydown', handleKeyDown);
      document.body.style.overflow = '';
    };
  }, [opened, handleKeyDown]);

  if (!opened) return null;

  return (
    <div className="custom-drawer-root">
      <div className="custom-drawer-overlay" onClick={onClose} />
      <div
        className={`custom-drawer-panel custom-drawer-${position} ${classNames?.content ?? ''}`}
        role="dialog"
        aria-modal="true"
      >
        <div className={`custom-drawer-header ${classNames?.header ?? ''}`}>
          <h2 className={`custom-drawer-title ${classNames?.title ?? ''}`}>{title}</h2>
          <button
            className="custom-drawer-close"
            onClick={onClose}
            aria-label="Close"
            type="button"
          >
            <svg
              width="16"
              height="16"
              viewBox="0 0 16 16"
              fill="none"
              stroke="currentColor"
              strokeWidth="2"
              strokeLinecap="round"
            >
              <line x1="3" y1="3" x2="13" y2="13" />
              <line x1="13" y1="3" x2="3" y2="13" />
            </svg>
          </button>
        </div>
        <div className={`custom-drawer-body ${classNames?.body ?? ''}`}>{children}</div>
      </div>
    </div>
  );
};

export default Drawer;
