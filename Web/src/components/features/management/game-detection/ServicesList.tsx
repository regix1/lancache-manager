import React from 'react';
import { useTranslation } from 'react-i18next';
import ServiceCard from './ServiceCard';
import CacheEntityList from './CacheEntityList';
import type { ServiceCacheInfo, CacheEntityVariant } from '../../../../types';

interface ServicesListProps {
  services: ServiceCacheInfo[];
  isAnyRemovalRunning: boolean;
  isAdmin: boolean;
  cacheReadOnly: boolean;
  dockerSocketAvailable: boolean;
  checkingPermissions: boolean;
  onRemoveService: (service: ServiceCacheInfo) => void;
  variant?: CacheEntityVariant;
}

const filterAndSortServices = (services: ServiceCacheInfo[], searchQuery: string) => {
  const query = searchQuery.toLowerCase();
  const filtered = services.filter((service) => service.service_name.toLowerCase().includes(query));

  filtered.sort((a, b) =>
    a.service_name.localeCompare(b.service_name, undefined, { sensitivity: 'base' })
  );

  return filtered;
};

const ServicesList: React.FC<ServicesListProps> = ({
  services,
  isAnyRemovalRunning,
  isAdmin,
  cacheReadOnly,
  dockerSocketAvailable,
  checkingPermissions,
  onRemoveService,
  variant = 'active'
}) => {
  const { t } = useTranslation();
  return (
    <CacheEntityList
      items={services}
      searchPlaceholder={t('management.gameDetection.placeholders.searchServices')}
      getEmptyMessage={(query) => t('management.gameDetection.noServicesMatching', { query })}
      itemLabel={t('management.gameDetection.servicesLabel')}
      getItemKey={(service) => service.service_name}
      filterAndSortItems={filterAndSortServices}
      renderItem={(service, state) => (
        <ServiceCard
          service={service}
          isExpanded={state.isExpanded}
          isExpanding={state.isExpanding}
          isAnyRemovalRunning={isAnyRemovalRunning}
          isAdmin={isAdmin}
          cacheReadOnly={cacheReadOnly}
          dockerSocketAvailable={dockerSocketAvailable}
          checkingPermissions={checkingPermissions}
          onToggleDetails={state.onToggleDetails}
          onRemove={onRemoveService}
          variant={variant}
        />
      )}
    />
  );
};

export default ServicesList;
