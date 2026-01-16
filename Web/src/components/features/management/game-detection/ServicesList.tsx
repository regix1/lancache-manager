import React, { useState } from 'react';
import ServiceCard from './ServiceCard';
import type { ServiceCacheInfo } from '../../../../types';
import type { UnifiedNotification } from '@contexts/NotificationsContext';

interface ServicesListProps {
  services: ServiceCacheInfo[];
  totalServices: number;
  notifications: UnifiedNotification[];
  isAuthenticated: boolean;
  cacheReadOnly: boolean;
  dockerSocketAvailable: boolean;
  checkingPermissions: boolean;
  onRemoveService: (service: ServiceCacheInfo) => void;
}

const ServicesList: React.FC<ServicesListProps> = ({
  services,
  totalServices,
  notifications,
  isAuthenticated,
  cacheReadOnly,
  dockerSocketAvailable,
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
            dockerSocketAvailable={dockerSocketAvailable}
            checkingPermissions={checkingPermissions}
            onToggleDetails={toggleServiceDetails}
            onRemove={onRemoveService}
          />
        ))}
    </div>
  );
};

export default ServicesList;
