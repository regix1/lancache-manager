import React from 'react';
import { X } from 'lucide-react';

interface ModalProps {
  opened: boolean;
  onClose: () => void;
  title?: React.ReactNode;
  children: React.ReactNode;
  size?: 'sm' | 'md' | 'lg' | 'xl';
}

export const Modal: React.FC<ModalProps> = ({
  opened,
  onClose,
  title,
  children,
  size = 'md'
}) => {
  const sizes = {
    sm: 'max-w-md',
    md: 'max-w-lg',
    lg: 'max-w-2xl',
    xl: 'max-w-4xl'
  };

  React.useEffect(() => {
    if (opened) {
      document.body.style.overflow = 'hidden';
    } else {
      document.body.style.overflow = 'unset';
    }
    return () => {
      document.body.style.overflow = 'unset';
    };
  }, [opened]);

  if (!opened) return null;

  return (
    <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50">
      <div className={`themed-card rounded-lg ${sizes[size]} w-full mx-4 max-h-[90vh] overflow-y-auto custom-scrollbar`}>
        {title && (
          <div className="flex items-center justify-between p-6 border-b border-themed-secondary">
            <div className="text-lg font-semibold text-themed-primary">{title}</div>
            <button
              onClick={onClose}
              className="p-1 hover:bg-themed-hover rounded smooth-transition"
            >
              <X className="w-5 h-5 text-themed-muted" />
            </button>
          </div>
        )}
        <div className="p-6">
          {children}
        </div>
      </div>
    </div>
  );
};