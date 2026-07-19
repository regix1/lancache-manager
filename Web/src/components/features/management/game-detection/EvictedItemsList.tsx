import React, { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Database } from 'lucide-react';
import { LoadingState, EmptyState } from '@components/ui/ManagerCard';
import GamesList from './GamesList';
import ServicesList from './ServicesList';
import { getEvictedGames, getEvictedServices } from './cacheEntityFilters';
import type { DatasourceInfo, GameCacheInfo, ServiceCacheInfo } from '../../../../types';
import type { SelectionAdapter } from '@hooks/useSelectionSet';

interface EvictedItemsListProps {
  games: GameCacheInfo[];
  services: ServiceCacheInfo[];
  isAdmin: boolean;
  datasourceConfigs: readonly DatasourceInfo[];
  onRemoveGame: (game: GameCacheInfo) => void;
  onRemoveService: (service: ServiceCacheInfo) => void;
  loading?: boolean;
  servicesSelection?: SelectionAdapter;
  gamesSelection?: SelectionAdapter;
}

const EvictedItemsList: React.FC<EvictedItemsListProps> = ({
  games,
  services,
  isAdmin,
  datasourceConfigs,
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
          datasourceConfigs={datasourceConfigs}
          onRemoveService={onRemoveService}
          variant="evicted"
          selection={servicesSelection}
        />
      )}
      {evictedGames.length > 0 && (
        <GamesList
          games={evictedGames}
          isAdmin={isAdmin}
          datasourceConfigs={datasourceConfigs}
          onRemoveGame={onRemoveGame}
          variant="evicted"
          selection={gamesSelection}
        />
      )}
    </div>
  );
};

export default EvictedItemsList;
