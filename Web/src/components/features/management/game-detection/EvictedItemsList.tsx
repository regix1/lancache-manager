import React, { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Database } from 'lucide-react';
import { LoadingState, EmptyState } from '@components/ui/ManagerCard';
import GamesList from './GamesList';
import ServicesList from './ServicesList';
import { getEvictedGames, getEvictedServices } from './cacheEntityFilters';
import type { GameCacheInfo, ServiceCacheInfo } from '../../../../types';

interface EvictedItemsListProps {
  games: GameCacheInfo[];
  services: ServiceCacheInfo[];
  isAnyRemovalRunning: boolean;
  isAdmin: boolean;
  cacheReadOnly: boolean;
  dockerSocketAvailable: boolean;
  checkingPermissions: boolean;
  onRemoveGame: (game: GameCacheInfo) => void;
  onRemoveService: (service: ServiceCacheInfo) => void;
  loading?: boolean;
}

const EvictedItemsList: React.FC<EvictedItemsListProps> = ({
  games,
  services,
  isAnyRemovalRunning,
  isAdmin,
  cacheReadOnly,
  dockerSocketAvailable,
  checkingPermissions,
  onRemoveGame,
  onRemoveService,
  loading = false
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
          isAnyRemovalRunning={isAnyRemovalRunning}
          isAdmin={isAdmin}
          cacheReadOnly={cacheReadOnly}
          dockerSocketAvailable={dockerSocketAvailable}
          checkingPermissions={checkingPermissions}
          onRemoveService={onRemoveService}
          variant="evicted"
        />
      )}
      {evictedGames.length > 0 && (
        <GamesList
          games={evictedGames}
          isAnyRemovalRunning={isAnyRemovalRunning}
          isAdmin={isAdmin}
          cacheReadOnly={cacheReadOnly}
          dockerSocketAvailable={dockerSocketAvailable}
          checkingPermissions={checkingPermissions}
          onRemoveGame={onRemoveGame}
          variant="evicted"
        />
      )}
    </div>
  );
};

export default EvictedItemsList;
