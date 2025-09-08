import React from 'react';
import { useData } from '../../contexts/DataContext';
import { X } from 'lucide-react';

const ProcessingStatus: React.FC = () => {
  const { processingStatus, setProcessingStatus } = useData();

  if (!processingStatus) return null;

  return (
    <div className="fixed bottom-5 right-5 w-80 themed-card rounded-lg shadow-xl z-50 p-4">
      <div className="flex items-start justify-between mb-2">
        <div className="flex-1">
          <h4 className="text-sm font-medium text-themed-primary">
            {processingStatus.type || 'Processing'}
          </h4>
          {processingStatus.estimatedTime && (
            <p className="text-xs text-themed-muted mt-1">{processingStatus.estimatedTime}</p>
          )}
        </div>
        <button
          onClick={() => setProcessingStatus?.(null)}
          className="text-themed-muted hover:text-themed-secondary smooth-transition"
        >
          <X className="w-4 h-4" />
        </button>
      </div>
      
      <p className="text-xs text-themed-muted mb-3">{processingStatus.message}</p>
      
      {processingStatus.progress !== undefined && (
        <div className="w-full progress-track rounded-full h-2 overflow-hidden">
          <div 
            className="progress-bar-medium h-2 rounded-full smooth-transition relative"
            style={{ width: `${Math.min(100, Math.max(0, processingStatus.progress))}%` }}
          >
            <div className="absolute inset-0 bg-gradient-to-r from-transparent via-white/20 to-transparent animate-shimmer" />
          </div>
        </div>
      )}
      
      {processingStatus.progress !== undefined && (
        <p className="text-xs text-themed-muted mt-1 text-right">
          {Math.round(processingStatus.progress)}%
        </p>
      )}
    </div>
  );
};

export default ProcessingStatus;