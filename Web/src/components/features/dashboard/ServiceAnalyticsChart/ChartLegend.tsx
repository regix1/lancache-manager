import React from 'react';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';
import type { ChartLegendProps } from './types';

const ChartLegend: React.FC<ChartLegendProps> = React.memo(({ items }) => {
  if (items.length === 0) {
    return null;
  }

  return (
    <div className="data-side">
      <CustomScrollbar maxHeight="280px" paddingMode="compact">
        {items.map((item) => (
          <div key={item.label} className="legend-item">
            <div className="legend-row">
              <div className="legend-label">
                <span className="legend-dot" style={{ backgroundColor: item.color }} />
                <span className="legend-name">{item.label}</span>
              </div>
              <span className="legend-value">{item.percentage.toFixed(1)}%</span>
            </div>
            <div className="legend-bar-track">
              <div
                className="legend-bar-fill"
                style={{
                  width: `${Math.max(item.percentage, 0.5)}%`,
                  backgroundColor: item.color
                }}
              />
            </div>
          </div>
        ))}
      </CustomScrollbar>
    </div>
  );
});

ChartLegend.displayName = 'ChartLegend';

export default ChartLegend;
