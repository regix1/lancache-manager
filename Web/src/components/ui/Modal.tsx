import React from 'react';
import { createPortal } from 'react-dom';
import { X } from 'lucide-react';

interface ModalProps {
  opened: boolean;
  onClose: () => void;
  title?: React.ReactNode;
  children: React.ReactNode;
  size?: 'sm' | 'md' | 'lg' | 'xl';
}

export const Modal: React.FC<ModalProps> = ({ opened, onClose, title, children, size = 'md' }) => {
  const [isVisible, setIsVisible] = React.useState(false);
  const [isAnimating, setIsAnimating] = React.useState(false);

  const sizes = {
    sm: 'max-w-md',
    md: 'max-w-lg',
    lg: 'max-w-2xl',
    xl: 'max-w-4xl'
  };

  React.useEffect(() => {
    if (opened) {
      // Calculate scrollbar width before hiding it
      const scrollbarWidth = window.innerWidth - document.documentElement.clientWidth;

      // Prevent scrolling and compensate for scrollbar width to prevent layout shift
      document.body.style.overflow = 'hidden';
      document.body.style.paddingRight = `${scrollbarWidth}px`;

      // Start animation with slight delay for smoother appearance
      setIsVisible(true);
      setTimeout(() => {
        setIsAnimating(true);
      }, 25);
    } else {
      // Start closing animation
      setIsAnimating(false);
      setTimeout(() => {
        setIsVisible(false);
        // Restore scrolling and remove padding compensation
        document.body.style.overflow = '';
        document.body.style.paddingRight = '';
      }, 250); // Match transition duration
    }

    return () => {
      // Cleanup: restore scrolling if component unmounts while modal is open
      document.body.style.overflow = '';
      document.body.style.paddingRight = '';
    };
  }, [opened]);

  if (!isVisible) return null;

  const modalContent = (
    <div
      className={`modal-backdrop fixed inset-0 overflow-y-auto py-8 transition-all duration-250 ease-out`}
      style={{
        zIndex: 100001,
        backgroundColor: isAnimating ? 'color-mix(in srgb, var(--theme-text-primary) 50%, transparent)' : 'transparent'
      }}
      onClick={(e) => e.target === e.currentTarget && onClose()}
    >
      <div className="min-h-full flex items-center justify-center">
        <div
          className={`themed-card rounded-lg ${sizes[size]} w-full mx-4 transform transition-all duration-250 ease-out ${
            isAnimating ? 'opacity-100 scale-100 translate-y-0' : 'opacity-0 scale-90 translate-y-8'
          }`}
          style={{
            transitionDelay: isAnimating ? '0.05s' : '0s'
          }}
        >
          {title && (
            <div className="flex items-center justify-between p-6 border-b border-themed-secondary">
              <div className="text-lg font-semibold text-themed-primary">{title}</div>
              <button
                onClick={onClose}
                className="p-1 hover:bg-themed-hover rounded-lg smooth-transition"
              >
                <X className="w-5 h-5 text-themed-muted" />
              </button>
            </div>
          )}
          <div className="p-6">{children}</div>
        </div>
      </div>
    </div>
  );

  // Render modal using portal directly to document.body to escape any stacking contexts
  return createPortal(modalContent, document.body);
};
