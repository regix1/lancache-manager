import { useContext } from 'react';
import { NotificationsContext } from './NotificationsContext.types';

export const useNotifications = () => {
  const context = useContext(NotificationsContext);
  if (!context) {
    throw new Error('useNotifications must be used within NotificationsProvider');
  }
  return context;
};
