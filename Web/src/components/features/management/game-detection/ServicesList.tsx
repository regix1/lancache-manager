import React, { useState, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Search } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { Pagination } from '@components/ui/Pagination';
import { usePaginatedList } from '@hooks/usePaginatedList';
import ServiceCard from './ServiceCard';
import type { ServiceCacheInfo, CacheEntityVariant } from '../../../../types';

interface ServicesListProps {
  services: ServiceCacheInfo[];
  totalServices: number;
  isAnyRemovalRunning: boolean;
  isAdmin: boolean;
  cacheReadOnly: boolean;
  dockerSocketAvailable: boolean;
  checkingPermissions: boolean;
  onRemoveService: (service: ServiceCacheInfo) => void;
  variant?: CacheEntityVariant;
}

const ITEMS_PER_PAGE = 20;
const PAGINATION_TOP_THRESHOLD = 100;

const ServicesList: React.FC<ServicesListProps> = ({
  services,
  totalServices,
  isAnyRemovalRunning,
  isAdmin,
  cacheReadOnly,
  dockerSocketAvailable,
  checkingPermissions,
  onRemoveService,
  variant = 'active'
}) => {
  const { t } = useTranslation();
  const [searchQuery, setSearchQuery] = useState('');
  const [expandedServiceName, setExpandedServiceName] = useState<string | null>(null);
  const [expandingServiceName, setExpandingServiceName] = useState<string | null>(null);

  const filteredAndSortedServices = useMemo(() => {
    const query = searchQuery.toLowerCase();
    const filtered = services.filter((service) =>
      service.service_name.toLowerCase().includes(query)
    );

    filtered.sort((a, b) =>
      a.service_name.localeCompare(b.service_name, undefined, { sensitivity: 'base' })
    );

    return filtered;
  }, [services, searchQuery]);

  const {
    page: currentPage,
    setPage: setCurrentPage,
    totalPages,
    paginatedItems: paginatedServices
  } = usePaginatedList<ServiceCacheInfo>({
    items: filteredAndSortedServices,
    pageSize: ITEMS_PER_PAGE,
    resetKey: searchQuery
  });

  const toggleServiceDetails = (serviceName: string) => {
    if (expandedServiceName === serviceName) {
      setExpandedServiceName(null);
      return;
    }

    setExpandingServiceName(serviceName);

    setTimeout(() => {
      setExpandedServiceName(serviceName);
      setExpandingServiceName(null);
    }, 50);
  };

  if (totalServices === 0) {
    return null;
  }

  return (
    <div>
      {/* Search Bar */}
      <div className="mb-3">
        <div className="relative">
          <Search className="input-icon absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-themed-muted" />
          <input
            type="text"
            placeholder={t('management.gameDetection.placeholders.searchServices')}
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
            className="w-full pl-10 pr-4 py-2 rounded-lg border text-sm bg-themed-secondary border-themed-secondary text-themed-primary"
          />
          {searchQuery && (
            <button
              onClick={() => setSearchQuery('')}
              className="absolute right-3 top-1/2 transform -translate-y-1/2 text-themed-muted hover:text-themed-primary text-xs"
            >
              {t('common.clear')}
            </button>
          )}
        </div>
      </div>

      {/* No Results Message */}
      {filteredAndSortedServices.length === 0 && (
        <div className="text-center py-8 text-themed-muted">
          <Search className="w-12 h-12 mx-auto mb-3 opacity-50" />
          <div className="mb-2">
            {t('management.gameDetection.noServicesMatching', { query: searchQuery })}
          </div>
          <Button variant="subtle" size="sm" onClick={() => setSearchQuery('')}>
            {t('management.gameDetection.clearSearch')}
          </Button>
        </div>
      )}

      {filteredAndSortedServices.length > 0 && (
        <>
          {/* Top Pagination Controls (shown for long lists) */}
          {filteredAndSortedServices.length > PAGINATION_TOP_THRESHOLD && totalPages > 1 && (
            <Pagination
              currentPage={currentPage}
              totalPages={totalPages}
              totalItems={filteredAndSortedServices.length}
              itemsPerPage={ITEMS_PER_PAGE}
              onPageChange={setCurrentPage}
              itemLabel={t('management.gameDetection.servicesLabel')}
            />
          )}

          <div className="space-y-3">
            {paginatedServices.map((service) => (
              <ServiceCard
                key={service.service_name}
                service={service}
                isExpanded={expandedServiceName === service.service_name}
                isExpanding={expandingServiceName === service.service_name}
                isAnyRemovalRunning={isAnyRemovalRunning}
                isAdmin={isAdmin}
                cacheReadOnly={cacheReadOnly}
                dockerSocketAvailable={dockerSocketAvailable}
                checkingPermissions={checkingPermissions}
                onToggleDetails={toggleServiceDetails}
                onRemove={onRemoveService}
                variant={variant}
              />
            ))}
          </div>

          {/* Pagination Controls */}
          <Pagination
            currentPage={currentPage}
            totalPages={totalPages}
            totalItems={filteredAndSortedServices.length}
            itemsPerPage={ITEMS_PER_PAGE}
            onPageChange={setCurrentPage}
            itemLabel={t('management.gameDetection.servicesLabel')}
          />
        </>
      )}
    </div>
  );
};

export default ServicesList;
