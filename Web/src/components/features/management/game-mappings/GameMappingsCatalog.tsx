import React, { useState, useEffect, useCallback, useRef, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Gamepad2 } from 'lucide-react';
import { DataTable, type DataTableColumn } from '@components/ui/DataTable';
import { AccordionSection } from '@components/ui/AccordionSection';
import { SearchInput } from '@components/ui/SearchInput';
import { useAccordionGroupItem } from '@contexts/AccordionGroupContext';
import { Alert } from '@components/ui/Alert';
import { Tooltip } from '@components/ui/Tooltip';
import { EmptyState } from '@components/ui/ManagerCard';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useMockMode } from '@contexts/useMockMode';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import type { SignalREventName } from '@contexts/SignalRContext/types';
import { FormattedDateCell } from '@components/common/FormattedDateTime';
import { getErrorMessage } from '@utils/error';
import { themeColorVar, type ColorToken } from '@utils/eventColors';

/**
 * The fields every service's mapping row carries under the same name. The fields that differ
 * between services (the display name and the store identifier) are reached through the column
 * models below instead of being named here.
 */
interface GameMappingRow {
  discoveredAtUtc: string;
  lastSeenAtUtc: string;
  imageUrl?: string;
}

/** Each service's stats DTO carries more than this; the catalog header only shows the total. */
interface GameMappingCatalogStats {
  totalGames: number;
}

/**
 * A column whose backing field is named differently per service. `value` reads that field with the
 * mapping's own type, so no service can hand over a key the DTO does not have.
 *
 * `key` is what DataTable stores persisted widths under, so it is part of the saved-layout
 * contract: renaming it silently discards every user's saved width for that column.
 */
interface GameMappingColumn<TMapping extends GameMappingRow> {
  key: string;
  headerKey: string;
  defaultWidth: number;
  minWidth: number;
  value: (mapping: TMapping) => string;
}

/**
 * Full i18n key strings, supplied per service. They are passed rather than built from a prefix so
 * that every live key stays greppable in the service file that owns it.
 */
interface GameMappingCatalogLabels {
  title: string;
  description: string;
  gamesInLibrary: string;
  discovered: string;
  lastSeen: string;
  search: string;
  noGames: string;
  noResults: string;
}

interface GameMappingsCatalogProps<TMapping extends GameMappingRow> {
  accordionId: string;
  accentColor: ColorToken;
  /**
   * localStorage bucket for the resizable column widths. Changing it discards every user's saved
   * layout for this catalog.
   */
  columnWidthStorageKey: string;
  updateEvent: SignalREventName;
  labels: GameMappingCatalogLabels;
  nameColumn: GameMappingColumn<TMapping>;
  identifierColumn: GameMappingColumn<TMapping>;
  /**
   * Services that record how a title was discovered supply this column; services that resolve
   * titles through the ingest pass have no per-mapping source and omit it.
   */
  sourceColumn?: DataTableColumn<TMapping>;
  dateColumnWidth: number;
  loadMappings: () => Promise<TMapping[]>;
  loadStats: () => Promise<GameMappingCatalogStats>;
  searchMappings: (query: string) => Promise<TMapping[]>;
  loadErrorMessage: string;
}

/**
 * The cumulative catalog of titles a service has discovered, wrapped in a collapsible
 * AccordionSection (each service's games list is a dropdown). Search, pagination, column resizing
 * and the discovery timestamps behave identically for every service; only the display-name and
 * identifier fields, the optional discovery-source column and the data source differ.
 */
function GameMappingsCatalog<TMapping extends GameMappingRow>({
  accordionId,
  accentColor,
  columnWidthStorageKey,
  updateEvent,
  labels,
  nameColumn,
  identifierColumn,
  sourceColumn,
  dateColumnWidth,
  loadMappings,
  loadStats,
  searchMappings,
  loadErrorMessage
}: GameMappingsCatalogProps<TMapping>): React.ReactElement {
  const { t } = useTranslation();
  const { on, off, isConnected } = useSignalR();
  const { mockMode } = useMockMode();

  const [mappings, setMappings] = useState<TMapping[]>([]);
  const [stats, setStats] = useState<GameMappingCatalogStats | null>(null);
  const [searchQuery, setSearchQuery] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [expanded, setExpanded] = useState(false);
  const toggleExpanded = useCallback(() => setExpanded((prev) => !prev), []);
  useAccordionGroupItem(accordionId, expanded, toggleExpanded);
  const searchTimeoutRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);

  const loadData = useCallback(async () => {
    // Mock mode has no mapping catalog behind it, so the list empties rather than showing the real
    // one a live session had already loaded.
    if (mockMode) {
      setMappings([]);
      setStats(null);
      setError(null);
      return;
    }
    try {
      setError(null);
      const [mappingsData, statsData] = await Promise.all([loadMappings(), loadStats()]);
      setMappings(mappingsData);
      setStats(statsData);
    } catch (err) {
      setError(getErrorMessage(err) || loadErrorMessage);
    }
  }, [mockMode, loadMappings, loadStats, loadErrorMessage]);

  useEffect(() => {
    loadData();
  }, [loadData]);

  // Listen for SignalR updates
  useEffect(() => {
    const handleUpdate = () => {
      loadData();
    };

    on(updateEvent, handleUpdate);
    return () => {
      off(updateEvent, handleUpdate);
    };
  }, [on, off, updateEvent, loadData]);

  // Refresh data when SignalR reconnects (catches events missed during disconnect)
  useReconnectRefetch(isConnected, loadData);

  const handleSearch = (value: string) => {
    setSearchQuery(value);
    if (searchTimeoutRef.current) {
      clearTimeout(searchTimeoutRef.current);
    }

    if (value.length >= 2) {
      searchTimeoutRef.current = setTimeout(async () => {
        try {
          const results = await searchMappings(value);
          setMappings(results);
        } catch {
          // Silently fail search, keep current results
        }
      }, 300);
    } else if (value.length < 2) {
      loadData();
    }
  };

  // Define columns for DataTable (resizable with pixel defaults)
  const columns: DataTableColumn<TMapping>[] = useMemo(() => {
    const baseColumns: DataTableColumn<TMapping>[] = [
      {
        key: 'image',
        header: '',
        defaultWidth: 52,
        minWidth: 48,
        align: 'center' as const,
        render: (mapping: TMapping) =>
          mapping.imageUrl ? (
            <img
              src={mapping.imageUrl}
              alt={nameColumn.value(mapping)}
              className="w-10 h-10 object-cover rounded align-middle"
              onError={(e: React.SyntheticEvent<HTMLImageElement>) => {
                e.currentTarget.classList.add('hidden');
              }}
            />
          ) : (
            <div className="w-10 h-10 rounded flex items-center justify-center bg-[var(--theme-bg-tertiary)]">
              <Gamepad2 className="w-4 h-4 text-themed-muted" />
            </div>
          )
      },
      {
        key: nameColumn.key,
        header: t(nameColumn.headerKey),
        defaultWidth: nameColumn.defaultWidth,
        minWidth: nameColumn.minWidth,
        flexible: true,
        render: (mapping: TMapping) => (
          <Tooltip content={nameColumn.value(mapping)} position="top" className="block min-w-0">
            <span className="block truncate text-xs font-medium text-themed-primary">
              {nameColumn.value(mapping)}
            </span>
          </Tooltip>
        )
      },
      {
        key: identifierColumn.key,
        header: t(identifierColumn.headerKey),
        defaultWidth: identifierColumn.defaultWidth,
        minWidth: identifierColumn.minWidth,
        render: (mapping: TMapping) => (
          <Tooltip
            content={identifierColumn.value(mapping)}
            position="top"
            className="block min-w-0"
          >
            <span className="block truncate font-mono text-xs text-themed-secondary">
              {identifierColumn.value(mapping)}
            </span>
          </Tooltip>
        )
      }
    ];

    if (sourceColumn) {
      baseColumns.push(sourceColumn);
    }

    baseColumns.push(
      {
        key: 'discovered',
        header: t(labels.discovered),
        defaultWidth: dateColumnWidth,
        minWidth: 110,
        render: (mapping: TMapping) => <FormattedDateCell timestamp={mapping.discoveredAtUtc} />
      },
      {
        key: 'lastSeen',
        header: t(labels.lastSeen),
        defaultWidth: dateColumnWidth,
        minWidth: 110,
        render: (mapping: TMapping) => <FormattedDateCell timestamp={mapping.lastSeenAtUtc} />
      }
    );

    return baseColumns;
  }, [
    t,
    nameColumn,
    identifierColumn,
    sourceColumn,
    dateColumnWidth,
    labels.discovered,
    labels.lastSeen
  ]);

  return (
    <AccordionSection
      title={t(labels.title)}
      icon={Gamepad2}
      iconColor={accentColor}
      count={stats?.totalGames}
      isExpanded={expanded}
      onToggle={toggleExpanded}
      surface="well"
    >
      <div className="space-y-3">
        {/* Description */}
        <p className="text-xs text-themed-muted">
          {stats && stats.totalGames > 0
            ? t(labels.gamesInLibrary, { count: stats.totalGames })
            : t(labels.description)}
        </p>

        {/* Error / Info Message */}
        {error && <Alert color="red">{error}</Alert>}

        {/* Empty State */}
        {mappings.length === 0 && !searchQuery && (
          <EmptyState variant="text" title={t(labels.noGames)} />
        )}

        {/* Search and Table */}
        {(mappings.length > 0 || searchQuery) && (
          <>
            {/* Search */}
            <SearchInput
              value={searchQuery}
              onChange={(e: React.ChangeEvent<HTMLInputElement>) => handleSearch(e.target.value)}
              placeholder={t(labels.search)}
              onClear={() => handleSearch('')}
            />

            {/* DataTable */}
            {mappings.length === 0 ? (
              <EmptyState variant="text" title={t(labels.noResults)} />
            ) : (
              <DataTable<TMapping>
                columns={columns}
                data={mappings}
                keyExtractor={identifierColumn.value}
                maxHeight="400px"
                accentColor={() => themeColorVar(accentColor)}
                resizable
                striped
                storageKey={columnWidthStorageKey}
                compact
              />
            )}
          </>
        )}
      </div>
    </AccordionSection>
  );
}

export default GameMappingsCatalog;
