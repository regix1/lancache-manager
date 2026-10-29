import React, { useId } from 'react';
import { Tooltip } from '@components/ui/Tooltip';

interface DisabledActionGateProps {
  disabled: boolean;
  /** Why the action is unavailable. Empty when there is nothing to explain. */
  reason: string;
  /** The action's own name, announced when the keyboard reaches it while it is disabled. */
  label: string;
  position?: 'top' | 'bottom' | 'left' | 'right';
  /** Trigger wrapper class. Full-width menu items pass "block w-full"; inline buttons keep the default. */
  className?: string;
  children: React.ReactNode;
}

/**
 * A disabled button leaves the Tab order, so a reason shown only on hover never reaches a keyboard
 * user. While the action is disabled with a reason, this wrapper takes its place in the Tab order
 * and carries the reason as its description, the way a blocked SelectableCard does. The wrapper is
 * always rendered, so the action keeps its element when it is disabled or enabled again.
 */
export const DisabledActionGate: React.FC<DisabledActionGateProps> = ({
  disabled,
  reason,
  label,
  position = 'top',
  className = 'inline-flex',
  children
}) => {
  const reasonId = useId();
  const blocked = disabled && reason !== '';

  return (
    <Tooltip content={reason} position={position} className={className}>
      <span
        className="flex min-w-0 themed-border-radius-sm focus-ring"
        role={blocked ? 'group' : undefined}
        tabIndex={blocked ? 0 : undefined}
        aria-label={blocked ? label : undefined}
        aria-describedby={blocked ? reasonId : undefined}
        // Space on a focused span scrolls the page, which carries the stop out of view and closes
        // its reason box.
        onKeyDown={
          blocked
            ? (event: React.KeyboardEvent) => {
                if (event.key === ' ') event.preventDefault();
              }
            : undefined
        }
      >
        {children}
        {blocked && (
          <span id={reasonId} className="sr-only">
            {reason}
          </span>
        )}
      </span>
    </Tooltip>
  );
};
