import React, { useState, useCallback, type ReactNode } from 'react';
import { GAME_SERVICES, type GameServiceConfig, type GameServiceId } from '@/types/gameService';
import { GameServiceContext } from './GameServiceContext.types';

const STORAGE_KEY = 'lancache-selected-service';

function loadPersistedService(): GameServiceId {
  try {
    const stored = localStorage.getItem(STORAGE_KEY);
    if (stored && GAME_SERVICES.some((service: GameServiceConfig) => service.id === stored)) {
      return stored as GameServiceId;
    }
  } catch (error) {
    console.error('[GameService] Failed to read from localStorage:', error);
  }
  return 'steam';
}

interface GameServiceProviderProps {
  children: ReactNode;
}

export const GameServiceProvider: React.FC<GameServiceProviderProps> = ({ children }) => {
  const [selectedService, setSelectedServiceState] = useState<GameServiceId>(loadPersistedService);

  const availableServices = GAME_SERVICES.filter(
    (service: GameServiceConfig) => service.enabled
  ).sort((a: GameServiceConfig, b: GameServiceConfig) => a.order - b.order);

  const setSelectedService = useCallback((id: GameServiceId) => {
    setSelectedServiceState(id);
    try {
      localStorage.setItem(STORAGE_KEY, id);
    } catch (error) {
      console.error('[GameService] Failed to persist selected service:', error);
    }
  }, []);

  return (
    <GameServiceContext.Provider value={{ selectedService, setSelectedService, availableServices }}>
      {children}
    </GameServiceContext.Provider>
  );
};
