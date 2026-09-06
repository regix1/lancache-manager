import React from 'react';
import { useTranslation } from 'react-i18next';
import { FileQuestion } from 'lucide-react';
import { LoadingState, EmptyState } from '@components/ui/ManagerCard';
import { Checkbox } from '@components/ui/Checkbox';
import { formatBytes, formatRelativeTime } from '@utils/formatters';
import type { OrphanedDownloadGroup } from '../../../../types';
import type { SelectionAdapter } from '@hooks/useSelectionSet';

interface OrphanedDownloadsListProps {
  groups: OrphanedDownloadGroup[];
  isAdmin: boolean;
  loading?: boolean;
  selection?: SelectionAdapter;
}

const OrphanedDownloadsList: React.FC<OrphanedDownloadsListProps> = ({
  groups,
  isAdmin,
  loading = false,
  selection
}) => {
  const { t } = useTranslation();

  if (loading) {
    return (
      <LoadingState
        message={t('management.gameDetection.loadingOrphanedDownloads')}
        shape="cards"
      />
    );
  }

  if (groups.length === 0) {
    return (
      <EmptyState
        icon={FileQuestion}
        title={t('management.gameDetection.noOrphanedDownloads')}
        subtitle={t('management.gameDetection.noOrphanedDownloadsDescription')}
      />
    );
  }

  const allSelected = selection ? groups.every((group) => selection.isSelected(group.key)) : false;

  return (
    <div className="space-y-2">
      {selection && isAdmin && (
        <label className="flex items-center gap-2 cursor-pointer px-1">
          <Checkbox
            checked={allSelected}
            onChange={() =>
              selection.setMany?.(
                groups.map((group) => group.key),
                !allSelected
              )
            }
          />
          <span className="text-sm text-themed-secondary">
            {t('management.batchSelect.selectAll')}
          </span>
        </label>
      )}

      <div className="well-surface divided-list">
        {groups.map((group) => (
          <div key={group.key} className="flex items-center gap-3 px-3 py-2.5">
            {selection && isAdmin && (
              <Checkbox
                checked={selection.isSelected(group.key)}
                onChange={() => selection.onToggle(group.key)}
              />
            )}
            <div className="flex-1 min-w-0">
              <div className="font-medium text-themed-primary truncate">{group.gameName}</div>
              <div className="text-sm text-themed-secondary">
                {t('management.gameDetection.orphanedDownloadsMeta', {
                  service: group.service,
                  count: group.downloadCount,
                  lastSeen: formatRelativeTime(group.lastSeenUtc)
                })}
              </div>
            </div>
            <div className="text-sm text-themed-secondary tabular-nums flex-shrink-0">
              {formatBytes(group.totalBytes)}
            </div>
          </div>
        ))}
      </div>
    </div>
  );
};

export default OrphanedDownloadsList;
