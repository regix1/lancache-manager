import { createContext } from 'react';
import type { GameServiceId, GameServiceConfig } from '@/types/gameService';

export interface GameServiceContextType {
  selectedService: GameServiceId;
  setSelectedService: (id: GameServiceId) => void;
  availableServices: GameServiceConfig[];
}

export const GameServiceContext = createContext<GameServiceContextType | undefined>(undefined);
