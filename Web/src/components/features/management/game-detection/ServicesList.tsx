import React from 'react';
import { useTranslation } from 'react-i18next';
import ServiceCard from './ServiceCard';
import CacheEntityList from './CacheEntityList';
import type { ServiceCacheInfo, CacheEntityVariant } from '../../../../types';

interface ServicesListProps {
  services: ServiceCacheInfo[];
  isAdmin: boolean;
  dockerSocketAvailable: boolean;
  onRemoveService: (service: ServiceCacheInfo) => void;
  variant?: CacheEntityVariant;
  /**
   * Optional client-only multi-select surface, forwarded straight to
   * CacheEntityList. Keyed on `service_name` (the list's item key). Absent =
   * no checkboxes / no select-all (behaviour identical to today).
   */
  selection?: {
    isSelected: (key: string) => boolean;
    onToggle: (key: string) => void;
    allSelected?: (keys: string[]) => boolean;
    setMany?: (keys: string[], selected: boolean) => void;
  };
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
  isAdmin,
  dockerSocketAvailable,
  onRemoveService,
  variant = 'active',
  selection
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
      selection={selection}
      renderItem={(service, state) => (
        <ServiceCard
          service={service}
          isExpanded={state.isExpanded}
          isExpanding={state.isExpanding}
          isAdmin={isAdmin}
          dockerSocketAvailable={dockerSocketAvailable}
          onToggleDetails={state.onToggleDetails}
          onRemove={onRemoveService}
          variant={variant}
          selectable={state.selectable}
          selected={state.selected}
          onSelectToggle={state.onSelectToggle}
        />
      )}
    />
  );
};

export default ServicesList;
