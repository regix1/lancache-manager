import React, { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import { Tooltip } from '@components/ui/Tooltip';

interface ExpandableListProps {
  items: string[];
  maxInitial: number;
  labelKey: string;
  showingLabelKey: string;
}

const ExpandableList: React.FC<ExpandableListProps> = ({
  items,
  maxInitial,
  labelKey,
  showingLabelKey
}) => {
  const { t } = useTranslation();
  const [showAll, setShowAll] = useState(false);

  if (items.length === 0) {
    return null;
  }

  return (
    <div>
      <div className="flex items-center justify-between mb-1.5">
        <p className="text-xs text-themed-muted font-medium">
          {t(labelKey, { count: items.length })}
        </p>
        {items.length > maxInitial && (
          <Button
            variant="subtle"
            size="xs"
            onClick={() => setShowAll(!showAll)}
            className="text-xs"
          >
            {showAll
              ? t('management.gameDetection.showLess')
              : t('management.gameDetection.showAll', { count: items.length })}
          </Button>
        )}
      </div>
      <div className="space-y-1 max-h-48 overflow-y-auto">
        {(showAll ? items : items.slice(0, maxInitial)).map((item, idx) => (
          <div
            key={idx}
            className="p-2 rounded border bg-themed-secondary border-themed-primary"
          >
            <Tooltip content={item}>
              <span className="text-xs font-mono text-themed-primary truncate block">
                {item}
              </span>
            </Tooltip>
          </div>
        ))}
      </div>
      {!showAll && items.length > maxInitial && (
        <p className="text-xs text-themed-muted mt-2 italic">
          {t(showingLabelKey, { showing: maxInitial, total: items.length })}
        </p>
      )}
    </div>
  );
};

export default ExpandableList;
