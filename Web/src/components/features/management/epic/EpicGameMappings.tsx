import React, { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import type { DataTableColumn } from '@components/ui/DataTable';
import { Tooltip } from '@components/ui/Tooltip';
import Badge from '@components/ui/Badge';
import ApiService from '@services/api.service';
import GameMappingsCatalog from '../game-mappings/GameMappingsCatalog';
import type { EpicGameMappingDto, EpicMappingStats } from '../../../../types';

// Called on ApiService so the statics keep their `this` (they read it for the fetch options), and
// declared at module scope so the catalog's load effect sees a stable identity.
const loadEpicMappings = (): Promise<EpicGameMappingDto[]> => ApiService.getEpicGameMappings();
const loadEpicStats = (): Promise<EpicMappingStats> => ApiService.getEpicMappingStats();
const searchEpicMappings = (query: string): Promise<EpicGameMappingDto[]> =>
  ApiService.searchEpicGames(query);

/** Discovery source to Badge variant; unknown or legacy values fall back to neutral. */
const SOURCE_VARIANTS: Record<string, 'success' | 'info' | 'waiting' | 'neutral'> = {
  'mapping-login': 'success',
  'prefill-login': 'info',
  'free-games': 'waiting',
  manual: 'neutral'
};

/** Source badge with tooltip explaining discovery method */
const SourceBadgeCell: React.FC<{ source: string }> = ({ source }) => {
  const { t } = useTranslation();
  const key = source.toLowerCase();
  const descriptionKey = key in SOURCE_VARIANTS ? key : 'unknown';
  return (
    <Tooltip
      content={t(
        `management.sections.integrations.epicGameMappings.sourceDescriptions.${descriptionKey}`
      )}
      position="top"
    >
      <Badge variant={SOURCE_VARIANTS[key] ?? 'neutral'} className="epic-source-badge">
        {source}
      </Badge>
    </Tooltip>
  );
};

const CATALOG_LABELS = {
  title: 'management.sections.integrations.epicGameMappings.title',
  description: 'management.sections.integrations.epicGameMappings.description',
  gamesInLibrary: 'management.sections.integrations.epicGameMappings.gamesInLibrary',
  discovered: 'management.sections.integrations.epicGameMappings.discovered',
  lastSeen: 'management.sections.integrations.epicGameMappings.lastSeen',
  search: 'management.sections.integrations.epicGameMappings.search',
  noGames: 'management.sections.integrations.epicGameMappings.noGames',
  noResults: 'management.sections.integrations.epicGameMappings.noResults'
};

const NAME_COLUMN = {
  key: 'name',
  headerKey: 'management.sections.integrations.epicGameMappings.name',
  defaultWidth: 260,
  minWidth: 140,
  value: (mapping: EpicGameMappingDto) => mapping.name
};

const APP_ID_COLUMN = {
  key: 'appId',
  headerKey: 'management.sections.integrations.epicGameMappings.appId',
  defaultWidth: 190,
  minWidth: 100,
  value: (mapping: EpicGameMappingDto) => mapping.appId
};

/**
 * Epic library catalog. Epic records how each title was discovered (login, library sync, free
 * games, ...), so it adds a discovery-source column the other services have no equivalent for.
 */
const EpicGameMappings: React.FC = () => {
  const { t } = useTranslation();

  const sourceColumn: DataTableColumn<EpicGameMappingDto> = useMemo(
    () => ({
      key: 'source',
      header: t('management.sections.integrations.epicGameMappings.source'),
      defaultWidth: 150,
      minWidth: 110,
      align: 'center' as const,
      render: (mapping: EpicGameMappingDto) => <SourceBadgeCell source={mapping.source} />
    }),
    [t]
  );

  return (
    <GameMappingsCatalog<EpicGameMappingDto>
      accordionId="integrations-epic-game-mappings"
      accentColor="--theme-epic"
      columnWidthStorageKey="epic-game-mappings-column-widths-v2"
      updateEvent="EpicGameMappingsUpdated"
      labels={CATALOG_LABELS}
      nameColumn={NAME_COLUMN}
      identifierColumn={APP_ID_COLUMN}
      sourceColumn={sourceColumn}
      dateColumnWidth={140}
      loadMappings={loadEpicMappings}
      loadStats={loadEpicStats}
      searchMappings={searchEpicMappings}
      loadErrorMessage={t('management.sections.integrations.epicGameMappings.loadError')}
    />
  );
};

export default EpicGameMappings;
