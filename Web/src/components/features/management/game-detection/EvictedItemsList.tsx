import React, { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Database } from 'lucide-react';
import { LoadingState, EmptyState } from '@components/ui/ManagerCard';
import GamesList from './GamesList';
import ServicesList from './ServicesList';
import type { GameCacheInfo, ServiceCacheInfo } from '../../../../types';
import type { UnifiedNotification } from '@contexts/notifications';

interface EvictedItemsListProps {
  games: GameCacheInfo[];
  services: ServiceCacheInfo[];
  notifications: UnifiedNotification[];
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
  notifications,
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

  const evictedGames = useMemo(
    () => games.filter((g) => (g.evicted_downloads_count ?? 0) > 0 || g.is_evicted === true),
    [games]
  );

  const evictedServices = useMemo(
    () => services.filter((s) => (s.evicted_downloads_count ?? 0) > 0 || s.is_evicted === true),
    [services]
  );

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
          totalServices={evictedServices.length}
          notifications={notifications}
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
          totalGames={evictedGames.length}
          notifications={notifications}
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
