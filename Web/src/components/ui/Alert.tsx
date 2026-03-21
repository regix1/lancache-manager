import React from 'react';
import { AlertCircle, CheckCircle, Info, AlertTriangle, X } from 'lucide-react';

interface AlertProps {
  children: React.ReactNode;
  color?: 'blue' | 'green' | 'yellow' | 'red' | 'orange';
  icon?: React.ReactNode;
  withCloseButton?: boolean;
  onClose?: () => void;
  title?: string;
  className?: string;
}

const COLOR_TO_CLASS: Record<NonNullable<AlertProps['color']>, string> = {
  blue: 'alert-info',
  green: 'alert-success',
  yellow: 'alert-warning',
  red: 'alert-error',
  orange: 'alert-warning'
};

const DEFAULT_ICONS: Record<NonNullable<AlertProps['color']>, React.ReactNode> = {
  blue: <Info className="w-4 h-4" />,
  green: <CheckCircle className="w-4 h-4" />,
  yellow: <AlertTriangle className="w-4 h-4" />,
  red: <AlertCircle className="w-4 h-4" />,
  orange: <AlertTriangle className="w-4 h-4" />
};

export const Alert: React.FC<AlertProps> = ({
  children,
  color = 'blue',
  icon,
  withCloseButton,
  onClose,
  title,
  className
}) => {
  const displayIcon = icon !== undefined ? icon : DEFAULT_ICONS[color];

  return (
    <div className={`alert ${COLOR_TO_CLASS[color]} ${className || ''}`}>
      {displayIcon && <div className="alert-icon">{displayIcon}</div>}
      <div className="alert-content">
        {title && <div className="font-medium mb-1">{title}</div>}
        <div>{children}</div>
      </div>
      {withCloseButton && onClose && (
        <button onClick={onClose} className="alert-close">
          <X className="w-4 h-4" />
        </button>
      )}
    </div>
  );
};
