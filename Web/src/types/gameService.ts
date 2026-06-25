export interface GameServiceConfig {
  id: GameServiceId;
  name: string;
  enabled: boolean;
  order: number;
}

export type GameServiceId = 'steam' | 'epic' | 'battlenet' | 'riot' | 'xbox';

export const GAME_SERVICES: GameServiceConfig[] = [
  {
    id: 'steam',
    name: 'Steam',
    enabled: true,
    order: 0
  },
  {
    id: 'epic',
    name: 'Epic Games',
    enabled: true,
    order: 1
  },
  {
    id: 'battlenet',
    name: 'Battle.net',
    enabled: true,
    order: 2
  },
  {
    id: 'riot',
    name: 'Riot Games',
    enabled: true,
    order: 3
  },
  {
    id: 'xbox',
    name: 'Xbox',
    enabled: true,
    order: 4
  }
];
