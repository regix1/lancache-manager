import { createContext } from 'react';
import type { GameServiceId, GameServiceConfig } from '@/types/gameService';

interface GameServiceContextType {
  selectedService: GameServiceId;
  setSelectedService: (id: GameServiceId) => void;
  availableServices: GameServiceConfig[];
}

export const GameServiceContext = createContext<GameServiceContextType | undefined>(undefined);
