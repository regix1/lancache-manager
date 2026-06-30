import React from 'react';
import { createPortal } from 'react-dom';
import { X } from 'lucide-react';

// Global modal tracking for nested modal support
let modalStack: number[] = [];
let modalIdCounter = 0;

// Non-exported module constant (keeps Fast Refresh happy — only the component is exported).
const FOCUSABLE_SELECTOR =
  'a[href],area[href],input:not([disabled]),select:not([disabled]),textarea:not([disabled]),' +
  'button:not([disabled]),iframe,object,embed,[tabindex]:not([tabindex="-1"]),[contenteditable="true"]';

type ModalSize = 'sm' | 'md' | 'lg' | 'xl' | '2xl' | 'full';

interface ModalProps {
  opened: boolean;
  onClose: () => void;
  title?: React.ReactNode;
  children: React.ReactNode;
  size?: ModalSize;
}

export const Modal: React.FC<ModalProps> = ({ opened, onClose, title, children, size = 'md' }) => {
  const [isVisible, setIsVisible] = React.useState(false);
  const [isAnimating, setIsAnimating] = React.useState(false);
  const [zIndex, setZIndex] = React.useState(80);
  const modalId = React.useRef<number | null>(null);
  const contentRef = React.useRef<HTMLDivElement>(null);
  const previouslyFocusedRef = React.useRef<HTMLElement | null>(null);
  const titleId = React.useId();

  const sizes: Record<ModalSize, string> = {
    sm: 'max-w-md',
    md: 'max-w-lg',
    lg: 'max-w-2xl',
    xl: 'max-w-4xl',
    '2xl': 'max-w-6xl',
    full: 'max-w-7xl'
  };

  const setBackgroundInert = (inert: boolean) => {
    const appRoot = document.getElementById('root');
    if (!appRoot) return;
    if (inert) appRoot.setAttribute('aria-hidden', 'true');
    else appRoot.removeAttribute('aria-hidden');
  };

  React.useEffect(() => {
    if (opened) {
      // Assign unique ID to this modal instance
      modalId.current = ++modalIdCounter;

      // Remember what had focus so we can restore it on close
      previouslyFocusedRef.current = (document.activeElement as HTMLElement) ?? null;

      // Only lock scroll / hide background for the first modal
      if (modalStack.length === 0) {
        document.documentElement.classList.add('modal-open');
        setBackgroundInert(true);
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
          modalStack = modalStack.filter((id) => id !== modalId.current);
          modalId.current = null;
        }

        // Only restore scroll / background when all modals are closed
        if (modalStack.length === 0) {
          document.documentElement.classList.remove('modal-open');
          setBackgroundInert(false);
        }

        // Restore focus to whatever was focused before this modal opened
        const prev = previouslyFocusedRef.current;
        if (prev && typeof prev.focus === 'function' && document.contains(prev)) {
          prev.focus();
        }
        previouslyFocusedRef.current = null;
      }, 250); // Match transition duration
    }

    return () => {
      // Cleanup: remove from stack if component unmounts while open
      if (modalId.current !== null) {
        modalStack = modalStack.filter((id) => id !== modalId.current);
        modalId.current = null;

        // Restore scroll / background if this was the last modal
        if (modalStack.length === 0) {
          document.documentElement.classList.remove('modal-open');
          setBackgroundInert(false);
        }
      }
    };
  }, [opened]);

  // Move focus into the modal once it becomes visible. Don't steal focus if the
  // content already focused something itself (e.g. an autoFocus input).
  React.useEffect(() => {
    if (!isVisible) return;
    const el = contentRef.current;
    if (!el) return;
    if (el.contains(document.activeElement)) return;
    el.focus();
  }, [isVisible]);

  if (!isVisible) return null;

  const getFocusable = (el: HTMLElement): HTMLElement[] =>
    Array.from(el.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR)).filter(
      (n) => n.offsetParent !== null
    );

  const handleKeyDown = (e: React.KeyboardEvent<HTMLDivElement>) => {
    if (e.key === 'Escape') {
      e.stopPropagation();
      onClose();
      return;
    }
    if (e.key !== 'Tab') return;

    const el = contentRef.current;
    if (!el) return;
    const focusable = getFocusable(el);
    if (focusable.length === 0) {
      e.preventDefault();
      el.focus();
      return;
    }
    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    const active = document.activeElement;

    // Focus is on the container (or escaped the modal) — pull it back in.
    if (!el.contains(active) || active === el) {
      e.preventDefault();
      (e.shiftKey ? last : first).focus();
      return;
    }
    if (e.shiftKey && active === first) {
      e.preventDefault();
      last.focus();
    } else if (!e.shiftKey && active === last) {
      e.preventDefault();
      first.focus();
    }
  };

  const modalContent = (
    <div
      className={`modal-backdrop fixed inset-0 overflow-y-auto overflow-x-hidden py-2 sm:py-4 transition duration-250 ease-out ${
        isAnimating ? 'bg-black/50 pointer-events-auto' : 'bg-transparent pointer-events-none'
      }`}
      style={{ zIndex }}
      onClick={(e) => e.target === e.currentTarget && onClose()}
      onKeyDown={handleKeyDown}
    >
      <div className="min-h-full flex items-center justify-center px-4">
        <div
          ref={contentRef}
          role="dialog"
          aria-modal="true"
          aria-labelledby={title ? titleId : undefined}
          tabIndex={-1}
          className={`themed-card border themed-border-radius ${sizes[size]} w-full max-h-[calc(100vh-1rem)] sm:max-h-[calc(100vh-2rem)] flex flex-col transform transition duration-250 ease-out focus:outline-none ${
            isAnimating
              ? 'opacity-100 scale-100 translate-y-0 delay-[50ms]'
              : 'opacity-0 scale-90 translate-y-8 delay-0'
          }`}
        >
          {title && (
            <div className="flex items-center justify-between p-4 sm:p-6 border-b border-themed-secondary flex-shrink-0">
              <div id={titleId} className="text-base sm:text-lg font-semibold text-themed-primary">
                {title}
              </div>
              <button
                onClick={onClose}
                aria-label="Close"
                className="p-1 hover:bg-themed-hover themed-border-radius smooth-transition"
              >
                <X className="w-5 h-5 text-themed-muted" />
              </button>
            </div>
          )}
          <div className="p-4 sm:p-6 overflow-y-auto overflow-x-hidden flex-1 min-h-0">
            {children}
          </div>
        </div>
      </div>
    </div>
  );

  // Render modal using portal directly to document.body to escape any stacking contexts
  return createPortal(modalContent, document.body);
};
