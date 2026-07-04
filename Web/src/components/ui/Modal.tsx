import React from 'react';
import { createPortal } from 'react-dom';
import { X } from 'lucide-react';

// Global modal tracking for nested modal support
type ModalStackPriority = 'normal' | 'elevated';
interface StackedModal {
  id: number;
  priority: ModalStackPriority;
}
let modalStack: StackedModal[] = [];
let modalIdCounter = 0;

// Monotonic z-index for the most recently opened modal. A strictly increasing counter (reset once a
// band has no modals left) guarantees a later modal always stacks ABOVE an earlier one, regardless
// of open/close churn. The previous `80 + modalStack.length` scheme could hand a reopened modal a
// z-index at or below one already on screen - e.g. after a rapid open->close->open (which
// container-list reconcile churn drives) corrupted the shared stack - leaving the reopened modal
// stuck BEHIND another modal. NOTE: this only actually took effect once the `.modal-backdrop`
// `z-index: 80 !important` in modals.css was de-`!important`-ed; before that, the inline value here
// was silently overridden and every backdrop rendered at a flat 80.
const MODAL_BASE_Z = 80;
// 'elevated' modals (keep-pending login prompts - SteamAuthModal etc.) open in a band strictly ABOVE
// every 'normal' modal, so a later-opened normal modal (e.g. the Configure modal reopened while a
// persistent login prompt is still up) can NEVER leapfrog and cover the login prompt. The 10000 gap
// is far more headroom than any realistic modal-nesting depth could consume, so the two bands never
// collide. Each band has its own monotonic counter, reset independently when that band empties.
const MODAL_ELEVATED_BASE_Z = MODAL_BASE_Z + 10000;
let modalTopZ = MODAL_BASE_Z;
let elevatedTopZ = MODAL_ELEVATED_BASE_Z;

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
  /**
   * Stacking band. 'normal' (default) uses the standard monotonic z-index. 'elevated' opens the
   * modal in a strictly-higher band so it always sits above every 'normal' modal regardless of open
   * order - used by the persistent-container login prompts (the keep-pending SteamAuthModal and its
   * Epic/Xbox equivalents), which must stay clickable above the Configure modal even if Configure is
   * reopened after them.
   */
  stackPriority?: ModalStackPriority;
  /**
   * When true, the body wrapper becomes a flex column itself and never scrolls on its own -
   * instead of the default (every other modal) where the wrapper's own `overflow-y-auto` scrolls
   * whatever `children` renders as one block. Use this when `children` manages its own internal
   * scroll region (e.g. a fixed header/footer around a `CustomScrollbar` middle): a plain block
   * child's `height: 100%` does not reliably resolve against this wrapper's flex-computed height
   * (measured: it fell back to its content height instead), so the wrapper's own overflow-y-auto
   * kicked in and showed a second, native scrollbar alongside the child's own. Making the wrapper
   * a flex column lets the child fill it via `flex: 1 1 auto` (flex-grow, not a percentage), which
   * resolves reliably.
   */
  bodyFlexLayout?: boolean;
}

export const Modal: React.FC<ModalProps> = ({
  opened,
  onClose,
  title,
  children,
  size = 'md',
  stackPriority = 'normal',
  bodyFlexLayout = false
}) => {
  const [isVisible, setIsVisible] = React.useState(false);
  const [isAnimating, setIsAnimating] = React.useState(false);
  const [zIndex, setZIndex] = React.useState(80);
  const modalId = React.useRef<number | null>(null);
  const animationTimerRef = React.useRef<ReturnType<typeof setTimeout> | null>(null);
  const closeTimerRef = React.useRef<ReturnType<typeof setTimeout> | null>(null);
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
    // Drop this modal's id from the shared stack; when it was the last one, release the scroll lock
    // and reset the monotonic z counter so it never grows without bound across a long session.
    const removeFromStack = (id: number) => {
      modalStack = modalStack.filter((stacked) => stacked.id !== id);
      if (modalStack.length === 0) {
        document.documentElement.classList.remove('modal-open');
        setBackgroundInert(false);
      }
      // Reset each band's counter as soon as that band is empty (not only when the whole stack is).
      // This keeps a 'normal' modal reopened while an 'elevated' login prompt lingers from climbing
      // toward the portaled dropdown/tooltip layer (z 85-90): the moment the last normal modal
      // closes its counter drops back to the base, so the reopened one restarts at base+1.
      if (!modalStack.some((stacked) => stacked.priority === 'normal')) {
        modalTopZ = MODAL_BASE_Z;
      }
      if (!modalStack.some((stacked) => stacked.priority === 'elevated')) {
        elevatedTopZ = MODAL_ELEVATED_BASE_Z;
      }
    };

    // Cancel any pending open/close animation timers from a previous toggle. This is what makes a
    // rapid open -> close -> open (which container-list reconcile churn can drive) safe: without it a
    // stale close timer fires ~250ms later and forces isVisible=false on a modal that has since
    // reopened, so it flashes open and then vanishes even though `opened` is still true.
    if (animationTimerRef.current) {
      clearTimeout(animationTimerRef.current);
      animationTimerRef.current = null;
    }
    if (closeTimerRef.current) {
      clearTimeout(closeTimerRef.current);
      closeTimerRef.current = null;
    }

    if (opened) {
      // Assign a unique id to this open instance.
      const id = ++modalIdCounter;
      modalId.current = id;

      // Remember what had focus so we can restore it on close.
      previouslyFocusedRef.current = (document.activeElement as HTMLElement) ?? null;

      // Only lock scroll / hide background for the first modal.
      if (modalStack.length === 0) {
        document.documentElement.classList.add('modal-open');
        setBackgroundInert(true);
      }

      // Add this modal to the stack and give it a z-index strictly above every modal already open in
      // its band ('elevated' login prompts always outrank 'normal' modals regardless of open order).
      modalStack.push({ id, priority: stackPriority });
      if (stackPriority === 'elevated') {
        elevatedTopZ += 1;
        setZIndex(elevatedTopZ);
      } else {
        modalTopZ += 1;
        setZIndex(modalTopZ);
      }

      // Start animation with slight delay for smoother appearance.
      setIsVisible(true);
      animationTimerRef.current = setTimeout(() => {
        setIsAnimating(true);
      }, 25);

      return () => {
        // Torn down while still open (unmount, or `opened` flipped to false and this effect re-ran):
        // clear timers and drop THIS open's id - captured locally, never read back from the ref,
        // which a later reopen may have already reassigned.
        if (animationTimerRef.current) {
          clearTimeout(animationTimerRef.current);
          animationTimerRef.current = null;
        }
        removeFromStack(id);
        if (modalId.current === id) {
          modalId.current = null;
        }
      };
    }

    // opened === false: play the close animation, then hide and restore focus. Stack bookkeeping for
    // this modal already ran in the open branch's cleanup (React runs it before this effect), so the
    // close path only has to finish the visual close.
    setIsAnimating(false);
    closeTimerRef.current = setTimeout(() => {
      setIsVisible(false);

      // Restore focus to whatever was focused before this modal opened.
      const prev = previouslyFocusedRef.current;
      if (prev && typeof prev.focus === 'function' && document.contains(prev)) {
        prev.focus();
      }
      previouslyFocusedRef.current = null;
    }, 250); // Match transition duration

    return () => {
      if (closeTimerRef.current) {
        clearTimeout(closeTimerRef.current);
        closeTimerRef.current = null;
      }
    };
    // stackPriority is constant per modal instance; listed so the open branch always reads the value
    // it renders with (it never actually changes mid-open, so this cannot cause a spurious re-run).
  }, [opened, stackPriority]);

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
          <div
            className={`p-4 sm:p-6 flex-1 min-h-0 ${
              bodyFlexLayout ? 'flex flex-col overflow-hidden' : 'overflow-y-auto overflow-x-hidden'
            }`}
          >
            {children}
          </div>
        </div>
      </div>
    </div>
  );

  // Render modal using portal directly to document.body to escape any stacking contexts
  return createPortal(modalContent, document.body);
};
