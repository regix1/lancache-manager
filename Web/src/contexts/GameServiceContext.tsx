import React, { useState, useCallback, type ReactNode } from 'react';
import { GAME_SERVICES, type GameServiceConfig, type GameServiceId } from '@/types/gameService';
import type { ShowToastEvent } from '@contexts/SignalRContext/types';
import { GameServiceContext } from './GameServiceContext.types';

const STORAGE_KEY = 'lancache-selected-service';

// Called from a useState lazy initializer below (render phase, before any provider - including
// NotificationsProvider - has mounted), so no notification channel is reachable here even in
// principle. Falls back to the 'steam' default, which is harmless. Deliberately silent.
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
      // User-initiated action (switching the game-service tab). The selection still applies for
      // this session via state above, but won't survive a reload - surface it. GameServiceProvider
      // is an ancestor of NotificationsProvider in AppProviders.tsx, so useErrorHandler is not
      // reachable here; use the existing show-toast bridge instead (mirrors
      // NotificationsContext.tsx:332-356).
      console.error('[GameService] Failed to persist selected service:', error);
      window.dispatchEvent(
        new CustomEvent<ShowToastEvent>('show-toast', {
          detail: {
            type: 'error',
            message: 'Could not save your service selection for next time.',
            duration: 4000
          }
        })
      );
    }
  }, []);

  return (
    <GameServiceContext.Provider value={{ selectedService, setSelectedService, availableServices }}>
      {children}
    </GameServiceContext.Provider>
  );
};
