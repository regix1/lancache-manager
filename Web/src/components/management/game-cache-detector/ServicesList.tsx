import React, { useState } from 'react';
import { Database } from 'lucide-react';
import ServiceCard from './ServiceCard';
import type { ServiceCacheInfo } from '../../../types';
import type { UnifiedNotification } from '@contexts/NotificationsContext';

interface ServicesListProps {
  services: ServiceCacheInfo[];
  totalServices: number;
  notifications: UnifiedNotification[];
  isAuthenticated: boolean;
  cacheReadOnly: boolean;
  checkingPermissions: boolean;
  onRemoveService: (service: ServiceCacheInfo) => void;
}

const ServicesList: React.FC<ServicesListProps> = ({
  services,
  totalServices,
  notifications,
  isAuthenticated,
  cacheReadOnly,
  checkingPermissions,
  onRemoveService
}) => {
  const [expandedServiceName, setExpandedServiceName] = useState<string | null>(null);
  const [expandingServiceName, setExpandingServiceName] = useState<string | null>(null);

  const toggleServiceDetails = (serviceName: string) => {
    // If already expanded, collapse immediately
    if (expandedServiceName === serviceName) {
      setExpandedServiceName(null);
      return;
    }

    // Show loading state for expansion
    setExpandingServiceName(serviceName);

    // Use setTimeout to allow the loading spinner to render before heavy DOM updates
    setTimeout(() => {
      setExpandedServiceName(serviceName);
      setExpandingServiceName(null);
    }, 50); // Small delay to let spinner show
  };

  if (totalServices === 0) {
    return null;
  }

  return (
    <>
      <div
        className="mb-3 p-3 rounded-lg border"
        style={{
          backgroundColor: 'var(--theme-bg-elevated)',
          borderColor: 'var(--theme-border-secondary)'
        }}
      >
        <div className="flex items-center gap-2 text-themed-primary font-medium">
          <Database className="w-5 h-5 text-themed-accent" />
          Found {totalServices} service{totalServices !== 1 ? 's' : ''} with cache files
        </div>
      </div>

      <div className="space-y-3">
        {services.map((service) => (
          <ServiceCard
            key={service.service_name}
            service={service}
            isExpanded={expandedServiceName === service.service_name}
            isExpanding={expandingServiceName === service.service_name}
            isRemoving={notifications.some(
              (n) =>
                n.type === 'service_removal' &&
                n.details?.service === service.service_name &&
                n.status === 'running'
            )}
            isAuthenticated={isAuthenticated}
            cacheReadOnly={cacheReadOnly}
            checkingPermissions={checkingPermissions}
            onToggleDetails={toggleServiceDetails}
            onRemove={onRemoveService}
          />
        ))}
      </div>
    </>
  );
};

export default ServicesList;
