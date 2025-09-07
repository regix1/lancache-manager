import React from 'react';
import { useData } from '../../contexts/DataContext';
import { X } from 'lucide-react';

const ProcessingStatus: React.FC = () => {
  const { processingStatus, setProcessingStatus } = useData();

  if (!processingStatus) return null;

  return (
    <div className="fixed bottom-5 right-5 w-80 bg-gray-800 rounded-lg border border-gray-700 shadow-xl z-50 p-4">
      <div className="flex items-start justify-between mb-2">
        <div className="flex-1">
          <h4 className="text-sm font-medium text-white">
            {processingStatus.type || 'Processing'}
          </h4>
          {processingStatus.estimatedTime && (
            <p className="text-xs text-gray-400 mt-1">{processingStatus.estimatedTime}</p>
          )}
        </div>
        <button
          onClick={() => setProcessingStatus?.(null)}
          className="text-gray-400 hover:text-gray-200 transition-colors"
        >
          <X className="w-4 h-4" />
        </button>
      </div>
      
      <p className="text-xs text-gray-400 mb-3">{processingStatus.message}</p>
      
      {processingStatus.progress !== undefined && (
        <div className="w-full bg-gray-700 rounded-full h-2 overflow-hidden">
          <div 
            className="bg-blue-500 h-2 rounded-full transition-all duration-300 ease-out relative"
            style={{ width: `${Math.min(100, Math.max(0, processingStatus.progress))}%` }}
          >
            <div className="absolute inset-0 bg-gradient-to-r from-transparent via-white/20 to-transparent animate-shimmer" />
          </div>
        </div>
      )}
      
      {processingStatus.progress !== undefined && (
        <p className="text-xs text-gray-500 mt-1 text-right">
          {Math.round(processingStatus.progress)}%
        </p>
      )}
    </div>
  );
};

export default ProcessingStatus;