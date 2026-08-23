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

  return (
    <div>
      <div className="game-detail-toolbar">
        <p className="game-detail-label">{t(labelKey, { count: items.length })}</p>
        {visibleCount > maxInitial && (
          <Button
            variant="filled"
            color="secondary"
            size="sm"
            onClick={() => setVisibleCount(maxInitial)}
          >
            {t('management.gameDetection.showLess')}
          </Button>
        )}
      </div>
      <div className="game-detail-list">
        {displayedItems.map((item, idx) => (
          <p key={idx} className="game-detail-path">
            {item}
          </p>
        ))}
      </div>
      {hasMore && (
        <div className="game-detail-toolbar game-detail-toolbar--after">
          <p className="game-detail-label">
            {t(showingLabelKey, { showing: visibleCount, total: items.length })}
          </p>
          <Button
            variant="filled"
            color="secondary"
            size="sm"
            onClick={() =>
              setVisibleCount((prev) => Math.min(prev + LOAD_MORE_BATCH, items.length))
            }
          >
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
