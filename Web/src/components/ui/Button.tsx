import React from 'react';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { useOptionalDirectoryPermissionsContext } from '@contexts/useDirectoryPermissionsContext';

interface ButtonProps extends React.ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: 'filled' | 'subtle' | 'outline' | 'default';
  color?: 'blue' | 'green' | 'red' | 'yellow' | 'purple' | 'gray' | 'orange' | 'default';
  size?: 'xs' | 'sm' | 'md' | 'lg';
  loading?: boolean;
  /** Explicit permission-check state (overrides awaitPermissions). */
  checkingPermissions?: boolean;
  /** Read checkingPermissions from DirectoryPermissionsProvider and gate this button. */
  awaitPermissions?: boolean;
  leftSection?: React.ReactNode;
  rightSection?: React.ReactNode;
  fullWidth?: boolean;
  /**
   * When true, the loading spinner OVERLAYS the button content (centered, absolutely positioned)
   * instead of being inserted inline, so the button's width does not change between its idle and
   * loading states. Use it when a button that can enter a loading state must stay the same size —
   * e.g. to stay aligned with a sibling button in the same row. Default false (spinner is inserted
   * inline, which can change the button's width).
   */
  stableWidth?: boolean;
}

export const Button: React.FC<ButtonProps> = ({
  children,
  variant = 'default',
  color = 'blue',
  size = 'md',
  loading = false,
  checkingPermissions,
  awaitPermissions = false,
  leftSection,
  rightSection,
  fullWidth = false,
  stableWidth = false,
  className = '',
  disabled,
  ...props
}) => {
  const getVariantClasses = () => {
    if (variant === 'filled') {
      const colors = {
        blue: 'themed-button-primary',
        green: 'action-process',
        red: 'action-delete',
        yellow: 'action-reset',
        purple: 'themed-button-primary',
        gray: 'bg-themed-surface hover:bg-themed-surface-hover text-themed-primary',
        orange: 'action-reset',
        default: 'bg-themed-surface hover:bg-themed-surface-hover text-themed-primary'
      };
      return colors[color];
    }
    if (variant === 'subtle') {
      // Borderless muted solid (no transparent/ghost look) — same neutral fill for every color.
      return 'bg-themed-surface hover:bg-themed-surface-hover text-themed-primary';
    }
    if (variant === 'outline') {
      // Outline variants render as solid fills (aliases of the filled buttons), so no
      // literal border — it would add ~2px height vs the filled buttons in a cluster.
      // Focus is still indicated via the global button:focus-visible rule.
      const base = '';
      const colors = {
        blue: `${base} outline-primary`,
        green: `${base} outline-process`,
        red: `${base} outline-delete`,
        yellow: `${base} outline-reset`,
        orange: `${base} outline-reset`,
        purple: `${base} outline-primary`,
        gray: 'bg-themed-surface hover:bg-themed-surface-hover text-themed-primary',
        default: 'bg-themed-surface hover:bg-themed-surface-hover text-themed-primary'
      };
      return colors[color];
    }
    return 'bg-themed-surface hover:bg-themed-surface-hover text-themed-primary';
  };

  // min-height per size = the text-button height, so an icon-only button (a shorter glyph)
  // still matches a text button of the same size in a control cluster instead of rendering
  // a few px shorter.
  const sizes = {
    xs: 'min-h-6 px-2 py-1 text-xs',
    sm: 'min-h-8 px-3 py-1.5 text-sm',
    md: 'min-h-10 px-4 py-2',
    lg: 'min-h-[3.25rem] px-6 py-3 text-lg'
  };

  const permissionsContext = useOptionalDirectoryPermissionsContext();
  const resolvedCheckingPermissions =
    checkingPermissions ??
    (awaitPermissions ? (permissionsContext?.checkingPermissions ?? false) : false);

  const showLoading = loading || resolvedCheckingPermissions;

  return (
    <button
      className={`
        ${getVariantClasses()}
        ${sizes[size]}
        ${fullWidth ? 'w-full' : ''}
        ${stableWidth ? 'relative' : ''}
        themed-button-radius font-medium
        smooth-transition
        disabled:opacity-50 disabled:cursor-not-allowed
        flex items-center justify-center gap-2
        button-press
        ${className}
      `}
      disabled={disabled || showLoading}
      {...props}
    >
      {stableWidth ? (
        <>
          <span
            className={`inline-flex items-center justify-center gap-2 ${showLoading ? 'invisible' : ''}`}
          >
            {leftSection}
            {children}
            {rightSection}
          </span>
          {showLoading && (
            <span className="absolute inset-0 flex items-center justify-center">
              <LoadingSpinner inline size="sm" />
            </span>
          )}
        </>
      ) : (
        <>
          {showLoading ? <LoadingSpinner inline size="sm" /> : leftSection}
          {children}
          {rightSection}
        </>
      )}
    </button>
  );
};
