import type { ReactNode } from 'react';

interface SetupGateProps {
  /** Card width. `4xl` is the multi-step depot wizard, `xl` is the sign-in card. */
  maxWidth: 'xl' | '4xl';
  /** Non-scrolling header row. */
  header: ReactNode;
  /** Optional full-bleed strip between the header row and the scrollable body, e.g. the depot
   *  wizard's step-count progress bar. Renders with no padding, so it stays flush against the
   *  card edges and outside the scrolling area, unlike `children`. */
  belowHeader?: ReactNode;
  /** Scrollable body. */
  children: ReactNode;
}

/**
 * Full-screen backdrop + striped card shell shared by the sign-in gate and the depot
 * initialization wizard. `min-h-0` on the body is what lets it shrink inside the flex
 * column; without it the card overflows its own `max-height` and the last control is
 * clipped away with no way to scroll to it.
 */
export const SetupGate: React.FC<SetupGateProps> = ({
  maxWidth,
  header,
  belowHeader,
  children
}) => (
  <div className="fixed inset-0 z-[80] flex items-center justify-center p-4 bg-themed-primary">
    <div className="absolute inset-0 opacity-5 setup-gate-stripe" />

    <div
      role="dialog"
      aria-modal="true"
      aria-labelledby="setup-gate-title"
      tabIndex={-1}
      className={`relative z-10 w-full ${maxWidth === '4xl' ? 'max-w-4xl' : 'max-w-xl'} themed-border-radius border overflow-hidden flex flex-col max-h-[calc(100dvh-2rem)] bg-themed-secondary border-themed-primary`}
    >
      <div
        id="setup-gate-title"
        className="px-5 sm:px-8 py-4 sm:py-5 border-b flex items-center justify-between border-themed-secondary"
      >
        {header}
      </div>

      {belowHeader}

      <div className="flex-1 min-h-0 overflow-y-auto p-5 sm:p-8">{children}</div>
    </div>
  </div>
);
