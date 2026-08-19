import React, { memo } from 'react';
import { Tooltip } from '@components/ui/Tooltip';

interface LineChartLegendItem {
  label: string;
  colorClass: string;
  hidden: boolean;
}

interface LineChartLegendProps {
  items: LineChartLegendItem[];
  onToggle: (index: number) => void;
}

const LineChartLegend: React.FC<LineChartLegendProps> = memo(({ items, onToggle }) => {
  if (items.length === 0) {
    return null;
  }

  return (
    <div className="line-trend-legend">
      {items.map((item, index) => (
        // Series names come from user data and can be far longer than the chart is wide, so
        // the label truncates and the tooltip carries the full name. Tooltip handles touch,
        // so the name stays reachable without a pointer.
        <Tooltip key={`${item.label}-${index}`} content={item.label} position="top">
          <button
            type="button"
            className={`line-trend-legend-item${item.hidden ? ' is-hidden' : ''}`}
            aria-pressed={!item.hidden}
            aria-label={item.label}
            onClick={() => onToggle(index)}
          >
            <span className={`line-trend-legend-swatch ${item.colorClass}`} />
            <span className="line-trend-legend-label">{item.label}</span>
          </button>
        </Tooltip>
      ))}
    </div>
  );
});

LineChartLegend.displayName = 'LineChartLegend';

export default LineChartLegend;
