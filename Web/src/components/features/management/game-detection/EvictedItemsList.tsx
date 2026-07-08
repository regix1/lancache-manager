import React, { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Database } from 'lucide-react';
import { LoadingState, EmptyState } from '@components/ui/ManagerCard';
import GamesList from './GamesList';
import ServicesList from './ServicesList';
import { getEvictedGames, getEvictedServices } from './cacheEntityFilters';
import type { GameCacheInfo, ServiceCacheInfo } from '../../../../types';

/**
 * Client-only multi-select surface for one evicted list. Keyed on the raw list
 * key (`service_name` / `getGameUniqueId`); the owning section adapts these to
 * its combined, prefixed selection set.
 */
interface EvictedListSelection {
  isSelected: (key: string) => boolean;
  onToggle: (key: string) => void;
  allSelected?: (keys: string[]) => boolean;
  setMany?: (keys: string[], selected: boolean) => void;
}

interface EvictedItemsListProps {
  games: GameCacheInfo[];
  services: ServiceCacheInfo[];
  isAdmin: boolean;
  dockerSocketAvailable: boolean;
  onRemoveGame: (game: GameCacheInfo) => void;
  onRemoveService: (service: ServiceCacheInfo) => void;
  loading?: boolean;
  servicesSelection?: EvictedListSelection;
  gamesSelection?: EvictedListSelection;
}

const EvictedItemsList: React.FC<EvictedItemsListProps> = ({
  games,
  services,
  isAdmin,
  dockerSocketAvailable,
  onRemoveGame,
  onRemoveService,
  loading = false,
  servicesSelection,
  gamesSelection
}) => {
  const { t } = useTranslation();

  const evictedGames = useMemo(() => getEvictedGames(games), [games]);

  const evictedServices = useMemo(() => getEvictedServices(services), [services]);

  if (loading) {
    return <LoadingState message={t('management.gameDetection.loadingEvictedGames')} />;
  }

  if (evictedGames.length === 0 && evictedServices.length === 0) {
    return <EmptyState icon={Database} title={t('management.gameDetection.noEvictedItems')} />;
  }

  return (
    <div className="space-y-4">
      {evictedServices.length > 0 && (
        <ServicesList
          services={evictedServices}
          isAdmin={isAdmin}
          dockerSocketAvailable={dockerSocketAvailable}
          onRemoveService={onRemoveService}
          variant="evicted"
          selection={servicesSelection}
        />
      )}
      {evictedGames.length > 0 && (
        <GamesList
          games={evictedGames}
          isAdmin={isAdmin}
          dockerSocketAvailable={dockerSocketAvailable}
          onRemoveGame={onRemoveGame}
          variant="evicted"
          selection={gamesSelection}
        />
      )}
    </div>
  );
};

export default EvictedItemsList;
