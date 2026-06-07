import type { NotificationType, UnifiedNotification } from './types';

/**
 * Builds a running notification pre-seeded with operationId from a 202/API response.
 * SignalR Started handlers merge into this instead of racing the cancel button.
 */
export function buildSeededRunningNotification(
  type: NotificationType,
  operationId: string,
  message: string,
  extraDetails?: Record<string, unknown>
): Omit<UnifiedNotification, 'id' | 'startedAt'> {
  return {
    type,
    status: 'running',
    message,
    progress: 0,
    details: { operationId, ...extraDetails }
  };
}
