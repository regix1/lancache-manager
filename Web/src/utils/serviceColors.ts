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
    case 'arenanet':
      return 'service-arenanet';
    case 'bsg':
      return 'service-bsg';
    case 'cityofheroes':
      return 'service-cityofheroes';
    case 'cod':
      return 'service-cod';
    case 'daybreak':
      return 'service-daybreak';
    case 'frontier':
      return 'service-frontier';
    case 'neverwinter':
      return 'service-neverwinter';
    case 'nexusmods':
      return 'service-nexusmods';
    case 'nintendo':
      return 'service-nintendo';
    case 'pathofexile':
      return 'service-pathofexile';
    case 'renegadex':
      return 'service-renegadex';
    case 'sony':
      return 'service-sony';
    case 'square':
      return 'service-square';
    case 'teso':
      return 'service-teso';
    case 'test':
      return 'service-test';
    case 'warframe':
      return 'service-warframe';
    case 'wargaming':
      return 'service-wargaming';
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
    case 'epicgames':
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
    case 'arenanet':
      return {
        backgroundColor: 'var(--theme-bg-tertiary)',
        color: 'var(--theme-arenanet)'
      };
    case 'bsg':
      return {
        backgroundColor: 'var(--theme-bg-tertiary)',
        color: 'var(--theme-bsg)'
      };
    case 'cityofheroes':
      return {
        backgroundColor: 'var(--theme-bg-tertiary)',
        color: 'var(--theme-cityofheroes)'
      };
    case 'cod':
      return {
        backgroundColor: 'var(--theme-bg-tertiary)',
        color: 'var(--theme-cod)'
      };
    case 'daybreak':
      return {
        backgroundColor: 'var(--theme-bg-tertiary)',
        color: 'var(--theme-daybreak)'
      };
    case 'frontier':
      return {
        backgroundColor: 'var(--theme-bg-tertiary)',
        color: 'var(--theme-frontier)'
      };
    case 'neverwinter':
      return {
        backgroundColor: 'var(--theme-bg-tertiary)',
        color: 'var(--theme-neverwinter)'
      };
    case 'nexusmods':
      return {
        backgroundColor: 'var(--theme-bg-tertiary)',
        color: 'var(--theme-nexusmods)'
      };
    case 'nintendo':
      return {
        backgroundColor: 'var(--theme-bg-tertiary)',
        color: 'var(--theme-nintendo)'
      };
    case 'pathofexile':
      return {
        backgroundColor: 'var(--theme-bg-tertiary)',
        color: 'var(--theme-pathofexile)'
      };
    case 'renegadex':
      return {
        backgroundColor: 'var(--theme-bg-tertiary)',
        color: 'var(--theme-renegadex)'
      };
    case 'sony':
      return {
        backgroundColor: 'var(--theme-bg-tertiary)',
        color: 'var(--theme-sony)'
      };
    case 'square':
      return {
        backgroundColor: 'var(--theme-bg-tertiary)',
        color: 'var(--theme-square)'
      };
    case 'teso':
      return {
        backgroundColor: 'var(--theme-bg-tertiary)',
        color: 'var(--theme-teso)'
      };
    case 'test':
      return {
        backgroundColor: 'var(--theme-bg-tertiary)',
        color: 'var(--theme-test)'
      };
    case 'warframe':
      return {
        backgroundColor: 'var(--theme-bg-tertiary)',
        color: 'var(--theme-warframe)'
      };
    case 'wargaming':
      return {
        backgroundColor: 'var(--theme-bg-tertiary)',
        color: 'var(--theme-wargaming)'
      };
    default:
      return {
        backgroundColor: 'var(--theme-bg-tertiary)',
        color: 'var(--theme-text-secondary)'
      };
  }
}
