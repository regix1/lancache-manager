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
    default:
      return 'text-[var(--theme-text-secondary)]';
  }
}

export function getServiceBadgeClasses(service: string): string {
  const serviceLower = service.toLowerCase();

  // Return background and text classes using theme colors
  switch (serviceLower) {
    case 'steam':
      return 'bg-[var(--theme-steam)]/20 text-[var(--theme-steam)]';
    case 'epic':
    case 'epicgames':
      return 'bg-[var(--theme-epic)]/20 text-[var(--theme-epic)]';
    case 'origin':
    case 'ea':
      return 'bg-[var(--theme-origin)]/20 text-[var(--theme-origin)]';
    case 'blizzard':
    case 'battle.net':
    case 'battlenet':
      return 'bg-[var(--theme-blizzard)]/20 text-[var(--theme-blizzard)]';
    case 'wsus':
    case 'windows':
      return 'bg-[var(--theme-wsus)]/20 text-[var(--theme-wsus)]';
    case 'riot':
    case 'riotgames':
      return 'bg-[var(--theme-riot)]/20 text-[var(--theme-riot)]';
    default:
      return 'bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-secondary)]';
  }
}