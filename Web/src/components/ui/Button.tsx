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
        gray: 'bg-themed-hover hover:bg-themed-tertiary text-themed-primary',
        orange: 'action-reset',
        default: 'bg-themed-tertiary hover:bg-themed-hover text-themed-primary'
      };
      return colors[color];
    }
    if (variant === 'subtle') {
      // Borderless muted solid (no transparent/ghost look) — same neutral fill for every color.
      return 'bg-themed-tertiary hover:bg-themed-hover text-themed-primary';
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
        gray: 'bg-themed-hover hover:bg-themed-tertiary text-themed-primary',
        default: 'bg-themed-hover hover:bg-themed-tertiary text-themed-primary'
      };
      return colors[color];
    }
    return 'bg-themed-tertiary hover:bg-themed-hover text-themed-primary';
  };

  const sizes = {
    xs: 'px-2 py-1 text-xs',
    sm: 'px-3 py-1.5 text-sm',
    md: 'px-4 py-2',
    lg: 'px-6 py-3 text-lg'
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
      {showLoading ? <LoadingSpinner inline size="sm" /> : leftSection}
      {children}
      {rightSection}
    </button>
  );
};
