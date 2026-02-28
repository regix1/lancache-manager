export interface GameServiceConfig {
  id: GameServiceId;
  name: string;
  enabled: boolean;
  order: number;
}

export type GameServiceId = 'steam' | 'epic';

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
  }
];
