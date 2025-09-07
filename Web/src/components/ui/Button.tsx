import React from 'react';

interface ButtonProps extends React.ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: 'filled' | 'subtle' | 'outline' | 'default';
  color?: 'blue' | 'green' | 'red' | 'yellow' | 'purple' | 'gray' | 'orange';
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
        blue: 'bg-blue-600 hover:bg-blue-700 text-white',
        green: 'bg-green-600 hover:bg-green-700 text-white',
        red: 'bg-red-600 hover:bg-red-700 text-white',
        yellow: 'bg-yellow-600 hover:bg-yellow-700 text-white',
        purple: 'bg-purple-600 hover:bg-purple-700 text-white',
        gray: 'bg-gray-600 hover:bg-gray-700 text-white',
        orange: 'bg-orange-600 hover:bg-orange-700 text-white'
      };
      return colors[color];
    }
    if (variant === 'subtle') {
      return 'bg-transparent hover:bg-gray-700 text-gray-300';
    }
    if (variant === 'outline') {
      return 'border border-gray-600 hover:bg-gray-700 text-white';
    }
    return 'bg-gray-700 hover:bg-gray-600 text-white';
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
        transition-colors duration-200
        disabled:opacity-50 disabled:cursor-not-allowed
        flex items-center justify-center gap-2
        ${className}
      `}
      disabled={disabled || loading}
      {...props}
    >
      {loading && (
        <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-current" />
      )}
      {leftSection}
      {children}
      {rightSection}
    </button>
  );
};