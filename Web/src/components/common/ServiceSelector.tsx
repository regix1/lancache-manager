import React from 'react';
import { useGameService } from '@contexts/GameServiceContext';
import type { GameServiceConfig } from '@/types/gameService';

const ServiceSelector: React.FC = () => {
  const { selectedService, setSelectedService, availableServices } = useGameService();

  return (
    <div className="service-selector">
      {availableServices.map((service: GameServiceConfig) => (
        <button
          key={service.id}
          className={`service-selector-item ${selectedService === service.id ? 'active' : ''}`}
          onClick={() => setSelectedService(service.id)}
        >
          <span className="service-selector-label">{service.name}</span>
        </button>
      ))}
    </div>
  );
};

export default ServiceSelector;
