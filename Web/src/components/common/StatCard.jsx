import React, { memo } from 'react';
import { COLOR_CLASSES } from '../../utils/constants';
import { Info } from 'lucide-react';
import { Tooltip } from '../common/Tooltip';

const StatCard = memo(({ title, value, subtitle, icon: Icon, color = 'blue', tooltip }) => {
  // Use a transition for smooth value updates
  return (
    <div className="bg-gray-800 rounded-lg p-6 border border-gray-700 transition-all duration-300">
      <div className="flex items-start justify-between">
        <div className="flex-1">
          <div className="flex items-center gap-2 mb-1">
            <p className="text-gray-400 text-sm">{title}</p>
            {tooltip && (
              <Tooltip content={tooltip} width="w-64">
                <Info className="w-3.5 h-3.5 text-gray-500 hover:text-gray-400 cursor-help transition-colors" />
              </Tooltip>
            )}
          </div>
          <p className="text-3xl font-bold text-white transition-all duration-500">{value}</p>
          <p className="text-gray-500 text-sm mt-1">{subtitle}</p>
        </div>
        <div className={`p-3 rounded-lg bg-gradient-to-br ${COLOR_CLASSES[color]} transition-transform hover:scale-105`}>
          <Icon className="w-6 h-6 text-white" />
        </div>
      </div>
    </div>
  );
}, (prevProps, nextProps) => {
  // Custom comparison to prevent unnecessary re-renders
  return prevProps.value === nextProps.value && 
         prevProps.subtitle === nextProps.subtitle &&
         prevProps.title === nextProps.title &&
         prevProps.color === nextProps.color &&
         prevProps.tooltip === nextProps.tooltip;
});

StatCard.displayName = 'StatCard';

export default StatCard;