import React from 'react';
import { Loader2 } from 'lucide-react';

interface LoadingSpinnerProps {
  message?: string;
  size?: 'xs' | 'sm' | 'md' | 'lg' | 'xl';
  fullScreen?: boolean;
  /** When true, renders just the spinning icon with no wrapper div */
  inline?: boolean;
  /** Additional CSS classes applied to the Loader2 icon (useful for color/positioning overrides in inline mode) */
  className?: string;
  /** Inline style applied to the Loader2 icon (for dynamic values like runtime colors) */
  style?: React.CSSProperties;
}

const sizeClasses: Record<string, string> = {
  xs: 'w-3 h-3',
  sm: 'w-4 h-4',
  md: 'w-5 h-5',
  lg: 'w-6 h-6',
  xl: 'w-8 h-8'
};

const LoadingSpinner: React.FC<LoadingSpinnerProps> = ({
  message,
  size = 'md',
  fullScreen = false,
  inline = false,
  className,
  style
}) => {
  if (inline) {
    return (
      <Loader2
        className={`${sizeClasses[size]} animate-spin${className ? ` ${className}` : ''}`}
        style={style}
      />
    );
  }

  // Legacy block-level sizes (larger defaults for centered loading states)
  const blockSizeClasses: Record<string, string> = {
    xs: 'w-4 h-4',
    sm: 'w-6 h-6',
    md: 'w-8 h-8',
    lg: 'w-12 h-12',
    xl: 'w-16 h-16'
  };

  const content = (
    <div className="flex flex-col items-center justify-center space-y-4">
      <Loader2
        className={`${blockSizeClasses[size]} text-themed-accent animate-spin${className ? ` ${className}` : ''}`}
      />
      {message && <p className="text-sm text-themed-secondary">{message}</p>}
    </div>
  );

  if (fullScreen) {
    return (
      <div className="fixed inset-0 bg-themed-primary bg-opacity-50 flex items-center justify-center z-50">
        {content}
      </div>
    );
  }

  return <div className="flex items-center justify-center min-h-[200px]">{content}</div>;
};

export default LoadingSpinner;
