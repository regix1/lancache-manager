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

/**
 * Get inline styles for service badges
 */
export function getServiceBadgeStyles(service: string): { backgroundColor: string; color: string } {
  const serviceLower = service.toLowerCase();
  switch (serviceLower) {
    case 'steam':
      return {
        backgroundColor: 'var(--theme-bg-tertiary)',
        color: 'var(--theme-steam)'
      };
    case 'epic':
      return {
        backgroundColor: 'var(--theme-bg-tertiary)',
        color: 'var(--theme-epic)'
      };
    case 'origin':
    case 'ea':
      return {
        backgroundColor: 'var(--theme-bg-tertiary)',
        color: 'var(--theme-origin)'
      };
    case 'blizzard':
    case 'battle.net':
    case 'battlenet':
      return {
        backgroundColor: 'var(--theme-bg-tertiary)',
        color: 'var(--theme-blizzard)'
      };
    case 'wsus':
    case 'windows':
      return {
        backgroundColor: 'var(--theme-bg-tertiary)',
        color: 'var(--theme-wsus)'
      };
    case 'riot':
    case 'riotgames':
      return {
        backgroundColor: 'var(--theme-bg-tertiary)',
        color: 'var(--theme-riot)'
      };
    case 'xbox':
    case 'xboxlive':
      return {
        backgroundColor: 'var(--theme-bg-tertiary)',
        color: 'var(--theme-xbox)'
      };
    default:
      return {
        backgroundColor: 'var(--theme-bg-tertiary)',
        color: 'var(--theme-text-secondary)'
      };
  }
}
