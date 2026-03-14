import { createContext } from 'react';
import type { SignalRContextType } from './types';

export const SignalRContext = createContext<SignalRContextType | undefined>(undefined);
