/**
 * Utility for consistent service colors across the application
 * These use theme CSS variables defined in theme.service.ts
 */

export function getServiceColorClass(service: string): string {
  const serviceLower = service.toLowerCase();

  // Map service names to their CSS color classes
  switch (serviceLower) {
    case 'steam':
      return 'service-steam';
    case 'epic':
    case 'epicgames':
      return 'service-epic';
    case 'origin':
    case 'ea':
      return 'service-origin';
    case 'blizzard':
    case 'battle.net':
    case 'battlenet':
      return 'service-blizzard';
    case 'wsus':
    case 'windows':
      return 'service-wsus';
    case 'riot':
    case 'riotgames':
      return 'service-riot';
    case 'xbox':
    case 'xboxlive':
      return 'service-xbox';
    default:
      return 'text-[var(--theme-text-secondary)]';
  }
}
