import React, { useState } from 'react';
import { Info } from 'lucide-react';

interface TooltipProps {
  children?: React.ReactNode;
  content: React.ReactNode;
  position?: 'top' | 'bottom' | 'left' | 'right';
}

export const Tooltip: React.FC<TooltipProps> = ({ 
  children = <Info className="w-4 h-4 text-themed-muted cursor-help" />, 
  content,
  position = 'top' 
}) => {
  const [visible, setVisible] = useState(false);

  const positionClasses = {
    top: 'bottom-full left-1/2 -translate-x-1/2 mb-2',
    bottom: 'top-full left-1/2 -translate-x-1/2 mt-2',
    left: 'right-full top-1/2 -translate-y-1/2 mr-2',
    right: 'left-full top-1/2 -translate-y-1/2 ml-2'
  };

  return (
    <div className="relative inline-block">
      <div
        onMouseEnter={() => setVisible(true)}
        onMouseLeave={() => setVisible(false)}
      >
        {children}
      </div>
      {visible && (
        <div className={`absolute ${positionClasses[position]} z-50 px-3 py-2 text-xs themed-card text-themed-secondary rounded-lg shadow-xl`}>
          {content}
        </div>
      )}
    </div>
  );
};

export const CacheInfoTooltip: React.FC = () => (
  <Tooltip
    content={
      <div className="flex items-center gap-4 whitespace-nowrap">
        <div className="flex items-center gap-1.5">
          <span className="cache-hit font-medium">Cache Hits:</span>
          <span className="text-themed-secondary">Data served from local cache</span>
        </div>
        <div className="w-px h-4 bg-themed-secondary"></div>
        <div className="flex items-center gap-1.5">
          <span className="cache-miss font-medium">Cache Misses:</span>
          <span className="text-themed-secondary">Data downloaded from internet</span>
        </div>
      </div>
    }
  />
);

export const CachePerformanceTooltip: React.FC = () => (
  <Tooltip
    content="Higher cache hit rates mean better performance and bandwidth savings"
  />
);

export const TimestampTooltip: React.FC<{
  startTime: string;
  endTime: string | null;
  isActive: boolean;
  children: React.ReactNode;
}> = ({ startTime, endTime, isActive, children }) => (
  <Tooltip
    content={
      <div className="space-y-1">
        <div>Started: {startTime}</div>
        {endTime && <div>Ended: {endTime}</div>}
        <div>Status: {isActive ? 'Active' : 'Completed'}</div>
      </div>
    }
  >
    {children}
  </Tooltip>
);