import React from 'react';
import { useTranslation } from 'react-i18next';
import { HardDrive, FolderOpen } from 'lucide-react';
import { formatBytes } from '@utils/formatters';
import type { ServiceCacheInfo } from '../../../../types';
import ExpandableItemCard, { ExpandableItemStat } from './ExpandableItemCard';
import ExpandableList from './ExpandableList';

interface ServiceCardProps {
  service: ServiceCacheInfo;
  isExpanded: boolean;
  isExpanding: boolean;
  isRemoving: boolean;
  isAnyRemovalRunning: boolean;
  isAdmin: boolean;
  cacheReadOnly: boolean;
  dockerSocketAvailable: boolean;
  checkingPermissions: boolean;
  onToggleDetails: (serviceName: string) => void;
  onRemove: (service: ServiceCacheInfo) => void;
}

const MAX_INITIAL_PATHS = 50;
const MAX_INITIAL_URLS = 20;

const ServiceCard: React.FC<ServiceCardProps> = ({
  service,
  isExpanded,
  isExpanding,
  isRemoving,
  isAnyRemovalRunning,
  isAdmin,
  cacheReadOnly,
  dockerSocketAvailable,
  checkingPermissions,
  onToggleDetails,
  onRemove
}) => {
  const { t } = useTranslation();

  const stats: ExpandableItemStat[] = [
    {
      icon: FolderOpen,
      value: service.cache_files_found.toLocaleString(),
      label: 'management.gameDetection.files'
    },
    {
      icon: HardDrive,
      value: formatBytes(service.total_size_bytes),
      label: ''
    }
  ];

  return (
    <ExpandableItemCard
      id={service.service_name}
      title={service.service_name}
      titleClassName="text-themed-primary font-semibold truncate capitalize"
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
      onToggleDetails={(id) => onToggleDetails(id as string)}
      onRemove={() => onRemove(service)}
      removeTooltip={t('management.gameDetection.removeServiceCache')}
    >
      {/* Sample URLs */}
      <ExpandableList
        items={service.sample_urls}
        maxInitial={MAX_INITIAL_URLS}
        labelKey="management.gameDetection.sampleUrls"
        showingLabelKey="management.gameDetection.showingUrls"
      />

      {/* Cache File Paths */}
      {service.cache_file_paths && (
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
