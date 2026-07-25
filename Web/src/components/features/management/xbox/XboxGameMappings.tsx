import React from 'react';
import ApiService from '@services/api.service';
import GameMappingsCatalog from '../game-mappings/GameMappingsCatalog';
import type { XboxGameMappingDto, XboxMappingStats } from '../../../../types';

// Called on ApiService so the statics keep their `this` (they read it for the fetch options), and
// declared at module scope so the catalog's load effect sees a stable identity.
const loadXboxMappings = (): Promise<XboxGameMappingDto[]> => ApiService.getXboxGameMappings();
const loadXboxStats = (): Promise<XboxMappingStats> => ApiService.getXboxMappingStats();
const searchXboxMappings = (query: string): Promise<XboxGameMappingDto[]> =>
  ApiService.searchXboxGames(query);

const CATALOG_LABELS = {
  title: 'management.sections.integrations.xboxGameMappings.title',
  description: 'management.sections.integrations.xboxGameMappings.description',
  gamesInLibrary: 'management.sections.integrations.xboxGameMappings.gamesInLibrary',
  discovered: 'management.sections.integrations.xboxGameMappings.discovered',
  lastSeen: 'management.sections.integrations.xboxGameMappings.lastSeen',
  search: 'management.sections.integrations.xboxGameMappings.search',
  noGames: 'management.sections.integrations.xboxGameMappings.noGames',
  noResults: 'management.sections.integrations.xboxGameMappings.noResults'
};

const TITLE_COLUMN = {
  key: 'title',
  headerKey: 'management.sections.integrations.xboxGameMappings.name',
  defaultWidth: 280,
  minWidth: 140,
  value: (mapping: XboxGameMappingDto) => mapping.title
};

const PRODUCT_ID_COLUMN = {
  key: 'productId',
  headerKey: 'management.sections.integrations.xboxGameMappings.productId',
  defaultWidth: 200,
  minWidth: 100,
  value: (mapping: XboxGameMappingDto) => mapping.productId
};

/**
 * Xbox library catalog: the cumulative, SHARED set of Microsoft Store titles discovered through
 * user prefill logins. Unlike Epic there is no per-mapping discovery source - the resolution path
 * is the Rust ingest pass, so the table shows ProductId + title + discovery timestamps only.
 */
const XboxGameMappings: React.FC = () => (
  <GameMappingsCatalog<XboxGameMappingDto>
    accordionId="integrations-xbox-game-mappings"
    accentColor="var(--theme-xbox)"
    columnWidthStorageKey="xbox-game-mappings-column-widths-v1"
    updateEvent="XboxGameMappingsUpdated"
    labels={CATALOG_LABELS}
    nameColumn={TITLE_COLUMN}
    identifierColumn={PRODUCT_ID_COLUMN}
    dateColumnWidth={150}
    loadMappings={loadXboxMappings}
    loadStats={loadXboxStats}
    searchMappings={searchXboxMappings}
    loadErrorMessage="Failed to load Xbox game mappings"
  />
);

export default XboxGameMappings;
