import React from 'react';

interface ButtonProps extends React.ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: 'filled' | 'subtle' | 'outline' | 'default';
  color?: 'blue' | 'green' | 'red' | 'yellow' | 'purple' | 'gray' | 'orange' | 'default';
  size?: 'xs' | 'sm' | 'md' | 'lg';
  loading?: boolean;
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
      const colors = {
        red: 'bg-transparent hover:bg-red-500/10 text-red-400 hover:text-red-300',
        blue: 'bg-transparent hover:bg-themed-hover text-themed-secondary',
        green: 'bg-transparent hover:bg-themed-hover text-themed-secondary',
        yellow: 'bg-transparent hover:bg-themed-hover text-themed-secondary',
        purple: 'bg-transparent hover:bg-themed-hover text-themed-secondary',
        gray: 'bg-transparent hover:bg-themed-hover text-themed-secondary',
        orange: 'bg-transparent hover:bg-themed-hover text-themed-secondary',
        default: 'bg-transparent hover:bg-themed-hover text-themed-secondary'
      };
      return colors[color];
    }
    if (variant === 'outline') {
      return 'border border-themed-secondary hover:bg-themed-hover text-themed-primary';
    }
    return 'bg-themed-tertiary hover:bg-themed-hover text-themed-primary';
  };

  const sizes = {
    xs: 'px-2 py-1 text-xs',
    sm: 'px-3 py-1.5 text-sm',
    md: 'px-4 py-2',
    lg: 'px-6 py-3 text-lg'
  };

  return (
    <button
      className={`
        ${getVariantClasses()}
        ${sizes[size]}
        ${fullWidth ? 'w-full' : ''}
        rounded-lg font-medium
        smooth-transition
        disabled:opacity-50 disabled:cursor-not-allowed
        flex items-center justify-center gap-2
        button-press
        ${className}
      `}
      disabled={disabled || loading}
      {...props}
    >
      {loading && <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-current" />}
      {leftSection}
      {children}
      {rightSection}
    </button>
  );
};
