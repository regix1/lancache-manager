import React from 'react';
import { AlertCircle, CheckCircle, Info, AlertTriangle, X } from 'lucide-react';

interface AlertProps {
  children: React.ReactNode;
  color?: 'blue' | 'green' | 'yellow' | 'red' | 'orange';
  icon?: React.ReactNode;
  withCloseButton?: boolean;
  onClose?: () => void;
  title?: string;
}

export const Alert: React.FC<AlertProps> = ({
  children,
  color = 'blue',
  icon,
  withCloseButton,
  onClose,
  title
}) => {
  const getColorClasses = () => {
    const colors = {
      blue: 'bg-blue-900 bg-opacity-30 border-blue-700 text-blue-400',
      green: 'bg-green-900 bg-opacity-30 border-green-700 text-green-400',
      yellow: 'bg-yellow-900 bg-opacity-30 border-yellow-700 text-yellow-400',
      red: 'bg-red-900 bg-opacity-30 border-red-700 text-red-400',
      orange: 'bg-orange-900 bg-opacity-30 border-orange-700 text-orange-400'
    };
    return colors[color];
  };

  const defaultIcons = {
    blue: <Info className="w-5 h-5" />,
    green: <CheckCircle className="w-5 h-5" />,
    yellow: <AlertTriangle className="w-5 h-5" />,
    red: <AlertCircle className="w-5 h-5" />,
    orange: <AlertTriangle className="w-5 h-5" />
  };

  return (
    <div className={`rounded-lg p-4 border ${getColorClasses()}`}>
      <div className="flex items-start">
        {(icon || defaultIcons[color]) && (
          <div className="flex-shrink-0 mr-3">
            {icon || defaultIcons[color]}
          </div>
        )}
        <div className="flex-1">
          {title && <div className="font-medium mb-1">{title}</div>}
          <div>{children}</div>
        </div>
        {withCloseButton && onClose && (
          <button
            onClick={onClose}
            className="ml-4 flex-shrink-0 hover:opacity-75 transition-opacity"
          >
            <X className="w-4 h-4" />
          </button>
        )}
      </div>
    </div>
  );
};