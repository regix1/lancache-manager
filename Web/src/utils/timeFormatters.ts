export function formatSessionTimeRemaining(expiresAt: string | null): string | null {
  if (!expiresAt) return null;
  const expiryStr =
    expiresAt.endsWith('Z') || /[+-]\d{2}:\d{2}$/.test(expiresAt) ? expiresAt : expiresAt + 'Z';
  const diff = new Date(expiryStr).getTime() - Date.now();
  if (diff <= 0) return null;
  const hours = Math.floor(diff / (1000 * 60 * 60));
  const minutes = Math.floor((diff % (1000 * 60 * 60)) / (1000 * 60));
  if (hours > 0) return `${hours}h ${minutes}m`;
  return `${minutes}m`;
}
