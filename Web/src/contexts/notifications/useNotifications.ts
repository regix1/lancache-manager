import { createContextHook } from '../createContextHook';
import { NotificationsContext } from './NotificationsContext.types';

export const useNotifications = createContextHook(NotificationsContext, 'useNotifications');
