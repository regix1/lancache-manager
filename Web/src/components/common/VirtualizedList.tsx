import React, { useRef } from 'react';
import { useVirtualizer } from '@tanstack/react-virtual';

interface VirtualizedListProps<T> {
  items: T[];
  height: number;
  itemHeight: number | ((index: number, item: T) => number);
  renderItem: (item: T, index: number) => React.ReactNode;
  overscan?: number;
  className?: string;
}

function VirtualizedList<T>({
  items,
  height,
  itemHeight,
  renderItem,
  overscan = 5,
  className = ''
}: VirtualizedListProps<T>) {
  const parentRef = useRef<HTMLDivElement>(null);

  const virtualizer = useVirtualizer({
    count: items.length,
    getScrollElement: () => parentRef.current,
    estimateSize: (index) => {
      if (typeof itemHeight === 'function') {
        return itemHeight(index, items[index]);
      }
      return itemHeight;
    },
    overscan
  });

  return (
    <div ref={parentRef} className={`overflow-auto ${className}`} style={{ height: `${height}px` }}>
      <div
        style={{
          height: `${virtualizer.getTotalSize()}px`,
          width: '100%',
          position: 'relative'
        }}
      >
        {virtualizer.getVirtualItems().map((virtualItem) => (
          <div
            key={virtualItem.key}
            style={{
              position: 'absolute',
              top: 0,
              left: 0,
              width: '100%',
              height: `${virtualItem.size}px`,
              transform: `translateY(${virtualItem.start}px)`
            }}
          >
            <div style={{ position: 'relative', zIndex: 1 }}>
              {renderItem(items[virtualItem.index], virtualItem.index)}
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}

export default VirtualizedList;
