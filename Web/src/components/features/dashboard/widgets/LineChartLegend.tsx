import React, { memo } from 'react';

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
        <button
          key={`${item.label}-${index}`}
          type="button"
          className={`line-trend-legend-item${item.hidden ? ' is-hidden' : ''}`}
          aria-pressed={!item.hidden}
          onClick={() => onToggle(index)}
        >
          <span className={`line-trend-legend-swatch ${item.colorClass}`} />
          <span className="line-trend-legend-label">{item.label}</span>
        </button>
      ))}
    </div>
  );
});

LineChartLegend.displayName = 'LineChartLegend';

export default LineChartLegend;
