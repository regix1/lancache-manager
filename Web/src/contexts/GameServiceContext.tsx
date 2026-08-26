import React, { useState, useCallback, type ReactNode } from 'react';
import i18n from '@/i18n';
import { GAME_SERVICES, type GameServiceConfig, type GameServiceId } from '@/types/gameService';
import type { ShowToastEvent } from '@contexts/SignalRContext/types';
import { GameServiceContext } from './GameServiceContext.types';
import { APP_EVENTS } from '@utils/constants';
import { storage } from '@utils/storage';

const STORAGE_KEY = 'lancache-selected-service';

// Called from a useState lazy initializer below (render phase, before any provider - including
// NotificationsProvider - has mounted), so no notification channel is reachable here even in
// principle. Falls back to the 'steam' default, which is harmless. Deliberately silent.
function loadPersistedService(): GameServiceId {
  const stored = storage.getItem(STORAGE_KEY);
  if (stored && GAME_SERVICES.some((service: GameServiceConfig) => service.id === stored)) {
    return stored as GameServiceId;
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
    storage.setItem(STORAGE_KEY, id);
    if (!storage.isAvailable()) {
      // User-initiated action (switching the game-service tab). The selection still applies for
      // this session via the wrapper's memory fallback, but won't survive a reload - surface it.
      // GameServiceProvider is an ancestor of NotificationsProvider in AppProviders.tsx, so
      // useErrorHandler is not reachable here; use the existing show-toast bridge instead
      // (mirrors NotificationsContext.tsx:332-356).
      window.dispatchEvent(
        new CustomEvent<ShowToastEvent>(APP_EVENTS.SHOW_TOAST, {
          detail: {
            type: 'error',
            message: i18n.t('prefill.errors.saveServiceSelectionFailed'),
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
