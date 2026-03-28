import React, { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';

interface ExpandableListProps {
  items: string[];
  maxInitial: number;
  labelKey: string;
  showingLabelKey: string;
}

const LOAD_MORE_BATCH = 50;

const ExpandableList: React.FC<ExpandableListProps> = ({
  items,
  maxInitial,
  labelKey,
  showingLabelKey
}) => {
  const { t } = useTranslation();
  const [visibleCount, setVisibleCount] = useState(maxInitial);

  if (items.length === 0) {
    return null;
  }

  const displayedItems = items.slice(0, visibleCount);
  const hasMore = visibleCount < items.length;
  const remaining = items.length - visibleCount;

  const handleLoadMore = () => {
    setVisibleCount((prev: number) => Math.min(prev + LOAD_MORE_BATCH, items.length));
  };

  const handleShowLess = () => {
    setVisibleCount(maxInitial);
  };

  return (
    <div>
      <div className="flex items-center justify-between mb-1.5">
        <p className="text-xs text-themed-muted font-medium">
          {t(labelKey, { count: items.length })}
        </p>
        {visibleCount > maxInitial && (
          <Button variant="subtle" size="xs" onClick={handleShowLess} className="text-xs">
            {t('management.gameDetection.showLess')}
          </Button>
        )}
      </div>
      <div className="space-y-1 max-h-48 overflow-y-auto">
        {displayedItems.map((item, idx) => (
          <div key={idx} className="p-2 rounded border bg-themed-secondary border-themed-primary">
            <span className="text-xs font-mono text-themed-primary break-all block">{item}</span>
          </div>
        ))}
      </div>
      {hasMore && (
        <div className="flex items-center justify-between mt-2">
          <p className="text-xs text-themed-muted italic">
            {t(showingLabelKey, { showing: visibleCount, total: items.length })}
          </p>
          <Button variant="subtle" size="xs" onClick={handleLoadMore} className="text-xs">
            {t('management.gameDetection.loadMore', {
              count: Math.min(LOAD_MORE_BATCH, remaining)
            })}
          </Button>
        </div>
      )}
    </div>
  );
};

export default ExpandableList;
