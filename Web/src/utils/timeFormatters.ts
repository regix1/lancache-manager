import { parseUtcDate } from './timezone';

export function formatSessionTimeRemaining(expiresAt: string | null): string | null {
  if (!expiresAt) return null;
  const diff = parseUtcDate(expiresAt).getTime() - Date.now();
  if (diff <= 0) return null;
  const hours = Math.floor(diff / (1000 * 60 * 60));
  const minutes = Math.floor((diff % (1000 * 60 * 60)) / (1000 * 60));
  if (hours > 0) return `${hours}h ${minutes}m`;
  return `${minutes}m`;
}
