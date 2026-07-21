import React from 'react';
import { useTranslation } from 'react-i18next';
import { HardDrive, Database, FolderOpen } from 'lucide-react';
import { EpicIcon } from '@components/ui/EpicIcon';
import { formatBytes, formatCount } from '@utils/formatters';
import type { GameCacheInfo, CacheEntityVariant, DatasourceInfo } from '../../../../types';
import ExpandableItemCard, { type ExpandableItemStat } from './ExpandableItemCard';
import ExpandableList from './ExpandableList';
import { getGameUniqueId } from './gameUtils';
import EvictedBadge from '@components/common/EvictedBadge';
import Badge from '@components/ui/Badge';
import { useIsEntityBusy } from '@hooks/useIsEntityBusy';
import { getNginxReopenGate } from '@utils/nginxReopenAvailability';

interface GameCardProps {
  game: GameCacheInfo;
  isExpanded: boolean;
  isAdmin: boolean;
  diskActionBlocked: boolean;
  datasourceConfigs: readonly DatasourceInfo[];
  onToggleDetails: (gameId: string) => void;
  onRemove: (game: GameCacheInfo) => void;
  variant?: CacheEntityVariant;
  selectable?: boolean;
  selected?: boolean;
  onSelectToggle?: () => void;
}

const MAX_INITIAL_PATHS = 50;
const MAX_INITIAL_URLS = 20;

const GameCard: React.FC<GameCardProps> = ({
  game,
  isExpanded,
  isAdmin,
  diskActionBlocked,
  datasourceConfigs,
  onToggleDetails,
  onRemove,
  variant = 'active',
  selectable = false,
  selected = false,
  onSelectToggle
}) => {
  const { t } = useTranslation();
  const isEpic = game.service === 'epicgames';
  // Named (Blizzard/Riot) games: game_app_id === 0 with a non-Steam, non-Epic service. They have
  // no Steam AppId and no depots, and must NOT fall through to the Steam badge/label.
  const isNamed = game.game_app_id === 0 && !!game.service && game.service !== 'steam' && !isEpic;
  const isSteam = !isEpic && !isNamed;
  const gameUniqueId = getGameUniqueId(game);
  const isRemoving = useIsEntityBusy(
    isEpic
      ? { kind: 'epicGame', epicAppId: game.epic_app_id, gameName: game.game_name }
      : isNamed
        ? { kind: 'namedGame', service: game.service!, gameName: game.game_name }
        : { kind: 'steamGame', gameAppId: game.game_app_id }
  );
  const isEvictedVariant = variant === 'evicted';
  const nginxReopenGate = getNginxReopenGate(datasourceConfigs, game.datasources);
  const nginxReopenUnavailableMessage = nginxReopenGate.messageKey
    ? t(nginxReopenGate.messageKey)
    : '';

  const stats: ExpandableItemStat[] = [
    {
      icon: FolderOpen,
      value: isEvictedVariant
        ? formatCount(game.evicted_downloads_count ?? 0)
        : formatCount(game.cache_files_found),
      label: 'management.gameDetection.files'
    },
    {
      icon: HardDrive,
      value: isEvictedVariant
        ? formatBytes(game.evicted_bytes ?? 0)
        : formatBytes(game.total_size_bytes),
      label: ''
    }
  ];

  // Only show depot count for Steam games
  if (isSteam && game.depot_ids.length > 0) {
    stats.push({
      icon: Database,
      value: game.depot_ids.length,
      label: 'management.gameDetection.depot',
      labelCount: game.depot_ids.length
    });
  }

  // Service badge: Epic and Steam have dedicated styling; named (Blizzard/Riot and any future
  // name-keyed service) get a per-service modifier when one exists, falling back to a neutral
  // named modifier so they never masquerade as Steam.
  const namedBadgeModifier =
    game.service === 'blizzard' || game.service === 'riot' || game.service === 'xbox'
      ? `game-card-service-badge--${game.service}`
      : 'game-card-service-badge--named';
  const serviceBadgeClass = isEpic
    ? 'game-card-service-badge game-card-service-badge--epic'
    : isNamed
      ? `game-card-service-badge ${namedBadgeModifier}`
      : 'game-card-service-badge game-card-service-badge--steam';

  // Named-service label: prefer an explicit translation (blizzard/riot), otherwise capitalize the
  // raw service id so unrecognised name-keyed services still render a sensible label.
  const namedServiceLabel = (service: string): string => {
    const key = `management.gameDetection.service${service.charAt(0).toUpperCase()}${service.slice(1)}`;
    const translated = t(key);
    return translated === key ? service.charAt(0).toUpperCase() + service.slice(1) : translated;
  };

  const serviceBadgeLabel = isEpic
    ? t('management.gameDetection.serviceEpicGames')
    : isNamed
      ? namedServiceLabel(game.service as string)
      : t('management.gameDetection.serviceSteam');

  const isEvicted = game.is_evicted === true;

  const subtitle = (
    <span className="flex items-center gap-1.5 flex-shrink-0">
      <span className={serviceBadgeClass}>
        {isEpic && <EpicIcon size={10} className="game-card-epic-icon" />}
        {serviceBadgeLabel}
      </span>
      {isSteam && (
        <span className="text-xs text-themed-muted bg-themed-elevated px-2 py-0.5 rounded">
          AppID: {game.game_app_id}
        </span>
      )}
      {isEvicted && <EvictedBadge />}
      {!isEvicted && variant === 'active' && (game.evicted_downloads_count ?? 0) > 0 && (
        <Badge variant="warning">
          {t('management.gameDetection.partialEvictedBadge', {
            count: game.evicted_downloads_count
          })}
        </Badge>
      )}
    </span>
  );

  const removeTooltip = isEvictedVariant
    ? t('management.gameDetection.removePartialEvictedTooltip')
    : t('management.gameDetection.removeGameCache');

  const depotIdsForExpansion = isEvictedVariant ? (game.evicted_depot_ids ?? []) : game.depot_ids;
  const urlsForExpansion = isEvictedVariant ? (game.evicted_sample_urls ?? []) : game.sample_urls;
  const pathsForExpansion = !isEvictedVariant ? (game.cache_file_paths ?? []) : [];
  const hasExpandableContent =
    (isSteam && depotIdsForExpansion.length > 0) ||
    urlsForExpansion.length > 0 ||
    pathsForExpansion.length > 0;

  return (
    <div className={isEvicted ? 'game-card-evicted' : undefined}>
      <ExpandableItemCard
        id={gameUniqueId}
        title={game.game_name}
        subtitle={subtitle}
        gameAppId={game.game_app_id}
        epicAppId={game.epic_app_id}
        service={game.service}
        stats={stats}
        datasources={game.datasources}
        isExpanded={isExpanded}
        isRemoving={isRemoving}
        isAdmin={isAdmin}
        diskActionBlocked={diskActionBlocked}
        nginxReopenAvailable={nginxReopenGate.available}
        nginxReopenUnavailableMessage={nginxReopenUnavailableMessage}
        hasExpandableContent={hasExpandableContent}
        onToggleDetails={(id) => onToggleDetails(String(id))}
        onRemove={() => onRemove(game)}
        removeTooltip={removeTooltip}
        selectable={selectable}
        selected={selected}
        onSelectToggle={onSelectToggle}
        selectLabel={t('management.batchSelect.selectItem', { name: game.game_name })}
      >
        {/* Depot IDs - Steam only */}
        {isSteam &&
          (() => {
            const depotIds = isEvictedVariant ? (game.evicted_depot_ids ?? []) : game.depot_ids;
            return depotIds.length > 0 ? (
              <div>
                <p className="text-xs text-themed-muted mb-1.5 font-medium">
                  {t('management.gameDetection.depotIds')}
                </p>
                <div className="flex flex-wrap gap-1">
                  {depotIds.map((depotId) => (
                    <span
                      key={depotId}
                      className="text-xs px-2 py-0.5 rounded border bg-themed-elevated border-themed-primary text-themed-secondary"
                    >
                      {depotId}
                    </span>
                  ))}
                </div>
              </div>
            ) : null;
          })()}

        {/* Sample URLs */}
        <ExpandableList
          items={isEvictedVariant ? (game.evicted_sample_urls ?? []) : game.sample_urls}
          maxInitial={MAX_INITIAL_URLS}
          labelKey="management.gameDetection.sampleUrls"
          showingLabelKey="management.gameDetection.showingUrls"
        />

        {/* Cache File Paths - only available for active (on-disk) items */}
        {!isEvictedVariant && game.cache_file_paths && (
          <ExpandableList
            items={game.cache_file_paths}
            maxInitial={MAX_INITIAL_PATHS}
            labelKey="management.gameDetection.cacheFileLocations"
            showingLabelKey="management.gameDetection.showingPaths"
          />
        )}
      </ExpandableItemCard>
    </div>
  );
};

export default GameCard;
