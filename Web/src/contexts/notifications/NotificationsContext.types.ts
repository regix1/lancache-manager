import { createContext } from 'react';
import type { NotificationsContextType } from './types';

export const NotificationsContext = createContext<NotificationsContextType | undefined>(undefined);
