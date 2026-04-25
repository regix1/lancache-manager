import React from 'react';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';
import { formatBytes, formatPercent } from '@utils/formatters';
import type { ChartLegendProps } from './types';

const ChartLegend: React.FC<ChartLegendProps> = React.memo(({ items }) => {
  if (items.length === 0) {
    return null;
  }

  return (
    <div className="data-side">
      <CustomScrollbar maxHeight="286px" paddingMode="default" className="legend-scroll">
        <div className="legend-list">
          {items.map((item) => (
            <div key={item.label} className={`legend-item ${item.colorClassName ?? ''}`}>
              <div className="legend-row">
                <div className="legend-label">
                  <span className="legend-dot" />
                  <span className="legend-name">{item.label}</span>
                </div>
                <span className="legend-value">{formatPercent(item.percentage)}</span>
              </div>
              <div className="legend-detail">{item.valueLabel ?? formatBytes(item.value)}</div>
              <progress
                className="legend-meter"
                value={Math.max(item.percentage, 0.5)}
                max={100}
                aria-label={`${item.label} ${formatPercent(item.percentage)}`}
              />
            </div>
          ))}
        </div>
      </CustomScrollbar>
    </div>
  );
});

ChartLegend.displayName = 'ChartLegend';

export default ChartLegend;
