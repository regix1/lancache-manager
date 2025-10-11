import React from 'react';
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
      // Calculate scrollbar width before hiding overflow
      const scrollbarWidth = window.innerWidth - document.documentElement.clientWidth;

      // Apply padding to compensate for scrollbar removal
      if (scrollbarWidth > 0) {
        document.body.style.paddingRight = `${scrollbarWidth}px`;
        // Also apply padding to any fixed position elements
        const fixedElements = document.querySelectorAll('.fixed, [style*="position: fixed"]');
        fixedElements.forEach((el) => {
          if (el instanceof HTMLElement && !el.classList.contains('modal-backdrop')) {
            el.style.paddingRight = `${scrollbarWidth}px`;
          }
        });
      }
      document.body.style.overflow = 'hidden';

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
        document.body.style.overflow = 'unset';
        document.body.style.paddingRight = '0px';
        // Reset padding on fixed elements
        const fixedElements = document.querySelectorAll('.fixed, [style*="position: fixed"]');
        fixedElements.forEach((el) => {
          if (el instanceof HTMLElement && !el.classList.contains('modal-backdrop')) {
            el.style.paddingRight = '';
          }
        });
      }, 250); // Match transition duration
    }

    return () => {
      document.body.style.overflow = 'unset';
      document.body.style.paddingRight = '0px';
      const fixedElements = document.querySelectorAll('.fixed, [style*="position: fixed"]');
      fixedElements.forEach((el) => {
        if (el instanceof HTMLElement && !el.classList.contains('modal-backdrop')) {
          el.style.paddingRight = '';
        }
      });
    };
  }, [opened]);

  if (!isVisible) return null;

  return (
    <div
      className={`modal-backdrop fixed inset-0 bg-black flex items-center justify-center z-50 transition-all duration-250 ease-out ${
        isAnimating ? 'bg-opacity-50' : 'bg-opacity-0'
      }`}
      onClick={(e) => e.target === e.currentTarget && onClose()}
    >
      <div
        className={`themed-card rounded-lg ${sizes[size]} w-full mx-4 max-h-[90vh] overflow-y-auto custom-scrollbar transform transition-all duration-250 ease-out ${
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
  );
};
