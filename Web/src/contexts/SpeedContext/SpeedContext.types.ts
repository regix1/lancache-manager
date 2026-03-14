import { createContext } from 'react';
import type { SpeedContextType } from './types';

export const SpeedContext = createContext<SpeedContextType | undefined>(undefined);
