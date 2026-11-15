import { useEffect, useId, useRef, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import Badge from '@components/ui/Badge';
import { ProgressBar } from '@components/ui/ProgressBar';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';
import { MODAL_EXIT_MS, useExitPresence } from '@hooks/useExitPresence';
import { holdAppRoot, holdScrollLock } from '@utils/appRoot';
import { getFocusable } from '@utils/focus';

interface SetupGateProps {
  /** Card width. `4xl` is the multi-step wizards, `xl` is the sign-in card. */
  maxWidth: 'xl' | '4xl';
  /** Header icon, already sized and colored by the caller, e.g. `<Rocket className="w-5 h-5 text-primary" />`. */
  icon: ReactNode;
  title: string;
  /** Control placed before the icon in the header row, e.g. the depot wizard's back button. */
  leading?: ReactNode;
  /**
   * Step counter for a wizard: a neutral badge beside the title and a full-bleed progress strip
   * between the header row and the scrollable body. `label` names the progress for assistive
   * technology; the count itself is announced from the numbers.
   */
  steps?: { current: number; total: number; label: string };
  footer?: ReactNode;
  /**
   * False fades the gate out and then removes it, so its host keeps it drawn and closes it with
   * `opened={false}`, whether the person closed it or its task succeeded.
   */
  opened: boolean;
  onClose?: () => void;
  /** Scrollable body. */
  children: ReactNode;
}

/**
 * Full-screen backdrop + striped card shell shared by the sign-in gate, the access dialog and
 * the depot initialization wizard. `min-h-0` on the body is what lets it shrink inside the flex
 * column; without it the card overflows its own `max-height` and the last control is
 * clipped away with no way to scroll to it.
 */
export const SetupGate: React.FC<SetupGateProps> = ({
  maxWidth,
  icon,
  title,
  leading,
  steps,
  footer,
  opened,
  onClose,
  children
}) => {
  const dialog = useRef<HTMLDivElement>(null);
  const titleId = useId();
  const { present } = useExitPresence(opened, MODAL_EXIT_MS);
  const stepText = steps ? `${steps.current} / ${steps.total}` : '';
  useEffect(() => {
    if (!present) return;
    const previous = document.activeElement;
    const element = dialog.current;
    if (element && !element.contains(previous)) element.focus();
    const releaseScrollLock = holdScrollLock();
    const releaseRoot = holdAppRoot(true);
    return () => {
      releaseScrollLock();
      releaseRoot();
      if (previous instanceof HTMLElement && document.contains(previous)) previous.focus();
    };
  }, [present]);
  if (!present) return null;
  return createPortal(
    <div
      className={`fixed inset-0 z-[80] flex items-center justify-center p-4 bg-themed-primary transition-opacity duration-[250ms] ease-out ${
        opened ? '' : 'opacity-0 pointer-events-none'
      }`}
    >
      <div className="absolute inset-0 opacity-5 setup-gate-stripe" />

      <div
        role="dialog"
        ref={dialog}
        aria-modal="true"
        aria-labelledby={titleId}
        tabIndex={-1}
        onKeyDown={(event) => {
          if (event.key === 'Escape' && onClose) {
            event.stopPropagation();
            onClose();
            return;
          }
          if (event.key !== 'Tab') return;
          const controls = getFocusable(event.currentTarget);
          const first = controls[0];
          const last = controls[controls.length - 1];
          if (!first || !last) {
            event.preventDefault();
            return;
          }
          const active = document.activeElement;
          const outsideControls = !controls.some((control) => control === active);
          if (event.shiftKey && (active === first || outsideControls)) {
            event.preventDefault();
            last.focus();
          } else if (!event.shiftKey && (active === last || outsideControls)) {
            event.preventDefault();
            first.focus();
          }
        }}
        className={`relative z-10 w-full ${maxWidth === '4xl' ? 'max-w-4xl' : 'max-w-xl'} themed-border-radius border overflow-hidden flex flex-col max-h-[calc(100dvh-2rem)] bg-themed-secondary border-themed-primary`}
      >
        <div className="px-5 sm:px-8 py-4 sm:py-5 border-b flex items-center justify-between gap-3 border-themed-secondary">
          <div className="flex items-center gap-3 min-w-0">
            {leading}
            <div className="flex items-center gap-2 min-w-0">
              {icon}
              <span id={titleId} className="font-semibold text-themed-primary">
                {title}
              </span>
            </div>
          </div>
          {steps && (
            <Badge variant="neutral" ariaLabel={`${steps.label} ${stepText}`}>
              {stepText}
            </Badge>
          )}
        </div>

        {steps && (
          <ProgressBar
            value={steps.current}
            max={steps.total}
            height="sm"
            rounded={false}
            label={steps.label}
            valueText={stepText}
          />
        )}

        <CustomScrollbar maxHeight="none" className="setup-gate-scroll" radius="none">
          {children}
        </CustomScrollbar>
        {footer && <div className="shrink-0 px-5 sm:px-8 pb-5">{footer}</div>}
      </div>
    </div>,
    document.body
  );
};
