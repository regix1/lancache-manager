import React from 'react';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';
import { formatBytes, formatPercent } from '@utils/formatters';
import type { ChartLegendProps } from './types';

interface MeterStyle extends React.CSSProperties {
  '--meter-fill'?: string;
}

const ChartLegend: React.FC<ChartLegendProps> = React.memo(({ items }) => {
  return (
    <div className="data-side">
      <CustomScrollbar maxHeight="100%" paddingMode="none" radius="none" className="legend-scroll">
        <div className="legend-list divided-list">
          {items.map((item) => {
            // A row that measured something carries a floor so a fraction of a percent still draws.
            // A row that measured nothing stays empty rather than painting a bar beside its "0%".
            const meterStyle: MeterStyle = {
              '--meter-fill': `${item.percentage > 0 ? Math.max(item.percentage, 0.5) : 0}%`
            };
            return (
              <div key={item.label} className={`legend-item ${item.colorClassName ?? ''}`}>
                <div className="legend-row">
                  <div className="legend-label">
                    <span className="legend-dot" />
                    <span className="legend-name">{item.label}</span>
                  </div>
                  <div className="legend-figures">
                    <span className="legend-bytes">
                      {item.valueLabel ?? formatBytes(item.value)}
                    </span>
                    <span className="legend-value">{formatPercent(item.percentage)}</span>
                  </div>
                </div>
                <div
                  role="progressbar"
                  aria-label={`${item.label} ${formatPercent(item.percentage)}`}
                  aria-valuenow={Math.round(item.percentage)}
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
