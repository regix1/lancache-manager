import React from 'react';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';
import { formatBytes, formatPercent } from '@utils/formatters';
import type { ChartLegendProps } from './types';

interface MeterStyle extends React.CSSProperties {
  '--meter-fill'?: string;
}

const ChartLegend: React.FC<ChartLegendProps> = React.memo(({ items }) => {
  if (items.length === 0) {
    return null;
  }

  return (
    <div className="data-side">
      <CustomScrollbar maxHeight="286px" paddingMode="default" className="legend-scroll">
        <div className="legend-list">
          {items.map((item) => {
            const fillPct = Math.max(item.percentage, 0.5);
            const meterStyle: MeterStyle = { '--meter-fill': `${fillPct}%` };
            return (
              <div key={item.label} className={`legend-item ${item.colorClassName ?? ''}`}>
                <div className="legend-row">
                  <div className="legend-label">
                    <span className="legend-dot" />
                    <span className="legend-name">{item.label}</span>
                  </div>
                  <span className="legend-value">{formatPercent(item.percentage)}</span>
                </div>
                <div className="legend-detail">{item.valueLabel ?? formatBytes(item.value)}</div>
                <div
                  role="progressbar"
                  aria-label={`${item.label} ${formatPercent(item.percentage)}`}
                  aria-valuenow={Math.round(fillPct)}
                  aria-valuemin={0}
                  aria-valuemax={100}
                  className="legend-meter"
                  style={meterStyle}
                >
                  <div className="legend-meter-fill" />
                </div>
              </div>
            );
          })}
        </div>
      </CustomScrollbar>
    </div>
  );
});

ChartLegend.displayName = 'ChartLegend';

export default ChartLegend;
