import React from 'react';
import { createPortal } from 'react-dom';
import { X } from 'lucide-react';

// Global modal tracking for nested modal support
let modalStack: number[] = [];
let modalIdCounter = 0;
let savedScrollbarWidth = 0;

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
  const [zIndex, setZIndex] = React.useState(80);
  const modalId = React.useRef<number | null>(null);

  const sizes = {
    sm: 'max-w-md',
    md: 'max-w-lg',
    lg: 'max-w-2xl',
    xl: 'max-w-4xl'
  };

  React.useEffect(() => {
    if (opened) {
      // Assign unique ID to this modal instance
      modalId.current = ++modalIdCounter;

      // Only lock scroll and save scrollbar width for the first modal
      if (modalStack.length === 0) {
        savedScrollbarWidth = window.innerWidth - document.documentElement.clientWidth;
        document.body.style.overflow = 'hidden';
        document.body.style.paddingRight = `${savedScrollbarWidth}px`;
      }

      // Add this modal to the stack
      modalStack.push(modalId.current);

      // Set z-index based on stack position (each modal gets higher z-index)
      setZIndex(80 + modalStack.length);

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

        // Remove this modal from the stack
        if (modalId.current !== null) {
          modalStack = modalStack.filter(id => id !== modalId.current);
          modalId.current = null;
        }

        // Only restore scroll when all modals are closed
        if (modalStack.length === 0) {
          document.body.style.overflow = '';
          document.body.style.paddingRight = '';
        }
      }, 250); // Match transition duration
    }

    return () => {
      // Cleanup: remove from stack if component unmounts while open
      if (modalId.current !== null) {
        modalStack = modalStack.filter(id => id !== modalId.current);
        modalId.current = null;

        // Restore scroll if this was the last modal
        if (modalStack.length === 0) {
          document.body.style.overflow = '';
          document.body.style.paddingRight = '';
        }
      }
    };
  }, [opened]);

  if (!isVisible) return null;

  const modalContent = (
    <div
      className={`modal-backdrop fixed inset-0 overflow-y-auto overflow-x-hidden py-4 sm:py-8 transition-all duration-250 ease-out ${
        isAnimating ? 'bg-black/50 pointer-events-auto' : 'bg-transparent pointer-events-none'
      }`}
      style={{ zIndex }}
      onClick={(e) => e.target === e.currentTarget && onClose()}
    >
      <div className="min-h-full flex items-center justify-center px-4">
        <div
          className={`themed-card border themed-border-radius ${sizes[size]} w-full max-h-[calc(100vh-2rem)] sm:max-h-[calc(100vh-4rem)] flex flex-col transform transition-all duration-250 ease-out ${
            isAnimating ? 'opacity-100 scale-100 translate-y-0 delay-[50ms]' : 'opacity-0 scale-90 translate-y-8 delay-0'
          }`}
        >
          {title && (
            <div className="flex items-center justify-between p-4 sm:p-6 border-b border-themed-secondary flex-shrink-0">
              <div className="text-base sm:text-lg font-semibold text-themed-primary">{title}</div>
              <button
                onClick={onClose}
                className="p-1 hover:bg-themed-hover themed-border-radius smooth-transition"
              >
                <X className="w-5 h-5 text-themed-muted" />
              </button>
            </div>
          )}
          <div className="p-4 sm:p-6 overflow-y-auto overflow-x-hidden flex-1 min-h-0">{children}</div>
        </div>
      </div>
    </div>
  );

  // Render modal using portal directly to document.body to escape any stacking contexts
  return createPortal(modalContent, document.body);
};
