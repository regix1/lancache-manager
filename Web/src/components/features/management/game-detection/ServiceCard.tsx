import React from 'react';
import { useTranslation } from 'react-i18next';
import { HardDrive, FolderOpen } from 'lucide-react';
import { formatBytes, formatCount } from '@utils/formatters';
import type { ServiceCacheInfo, CacheEntityVariant } from '../../../../types';
import ExpandableItemCard, { type ExpandableItemStat } from './ExpandableItemCard';
import ExpandableList from './ExpandableList';
import Badge from '@components/ui/Badge';
import { useIsEntityBusy } from '@hooks/useIsEntityBusy';

interface ServiceCardProps {
  service: ServiceCacheInfo;
  isExpanded: boolean;
  isExpanding: boolean;
  isAnyRemovalRunning: boolean;
  isAdmin: boolean;
  cacheReadOnly: boolean;
  dockerSocketAvailable: boolean;
  checkingPermissions: boolean;
  onToggleDetails: (serviceName: string) => void;
  onRemove: (service: ServiceCacheInfo) => void;
  variant?: CacheEntityVariant;
}

const MAX_INITIAL_PATHS = 50;
const MAX_INITIAL_URLS = 20;

const ServiceCard: React.FC<ServiceCardProps> = ({
  service,
  isExpanded,
  isExpanding,
  isAnyRemovalRunning,
  isAdmin,
  cacheReadOnly,
  dockerSocketAvailable,
  checkingPermissions,
  onToggleDetails,
  onRemove,
  variant = 'active'
}) => {
  const { t } = useTranslation();
  const isEvictedVariant = variant === 'evicted';
  const isEvicted = service.is_evicted === true;
  const isRemoving = useIsEntityBusy({ kind: 'service', service: service.service_name });

  const stats: ExpandableItemStat[] = [
    {
      icon: FolderOpen,
      value: isEvictedVariant
        ? formatCount(service.evicted_downloads_count ?? 0)
        : formatCount(service.cache_files_found),
      label: 'management.gameDetection.files'
    },
    {
      icon: HardDrive,
      value: isEvictedVariant
        ? formatBytes(service.evicted_bytes ?? 0)
        : formatBytes(service.total_size_bytes),
      label: ''
    }
  ];

  const subtitle =
    !isEvicted && variant === 'active' && (service.evicted_downloads_count ?? 0) > 0 ? (
      <Badge variant="warning">
        {t('management.gameDetection.partialEvictedBadge', {
          count: service.evicted_downloads_count
        })}
      </Badge>
    ) : undefined;

  const removeTooltip = isEvictedVariant
    ? t('management.gameDetection.removePartialEvictedTooltip')
    : t('management.gameDetection.removeServiceCache');

  const urlsForExpansion = isEvictedVariant
    ? (service.evicted_sample_urls ?? [])
    : service.sample_urls;
  const pathsForExpansion = !isEvictedVariant ? (service.cache_file_paths ?? []) : [];
  const hasExpandableContent = urlsForExpansion.length > 0 || pathsForExpansion.length > 0;

  return (
    <ExpandableItemCard
      id={service.service_name}
      title={service.service_name}
      titleClassName="text-themed-primary font-semibold truncate capitalize"
      subtitle={subtitle}
      stats={stats}
      datasources={service.datasources}
      isExpanded={isExpanded}
      isExpanding={isExpanding}
      isRemoving={isRemoving}
      isAnyRemovalRunning={isAnyRemovalRunning}
      isAdmin={isAdmin}
      cacheReadOnly={cacheReadOnly}
      dockerSocketAvailable={dockerSocketAvailable}
      checkingPermissions={checkingPermissions}
      hasExpandableContent={hasExpandableContent}
      onToggleDetails={(id) => onToggleDetails(id as string)}
      onRemove={() => onRemove(service)}
      removeTooltip={removeTooltip}
    >
      {/* Sample URLs */}
      <ExpandableList
        items={isEvictedVariant ? (service.evicted_sample_urls ?? []) : service.sample_urls}
        maxInitial={MAX_INITIAL_URLS}
        labelKey="management.gameDetection.sampleUrls"
        showingLabelKey="management.gameDetection.showingUrls"
      />

      {/* Cache File Paths — only available for active (on-disk) items */}
      {!isEvictedVariant && service.cache_file_paths && (
        <ExpandableList
          items={service.cache_file_paths}
          maxInitial={MAX_INITIAL_PATHS}
          labelKey="management.gameDetection.cacheFileLocations"
          showingLabelKey="management.gameDetection.showingPaths"
        />
      )}
    </ExpandableItemCard>
  );
};

export default ServiceCard;
