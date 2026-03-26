import React, { useState, useRef, useEffect, useCallback } from 'react';
import './DataSection.css';
import { useTranslation } from 'react-i18next';
import { Database, AlertTriangle, Loader2 } from 'lucide-react';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { Modal } from '@components/ui/Modal';
import { Checkbox } from '@components/ui/Checkbox';
import { HelpPopover, HelpSection, HelpNote, HelpDefinition } from '@components/ui/HelpPopover';
import { type AuthMode } from '@services/auth.service';
import ApiService from '@services/api.service';
import DepotMappingManager from '../depot/DepotMappingManager';
import EpicMappingManager from '../epic/EpicMappingManager';
import DataImporter from '../data/DataImporter';
interface DataSectionProps {
  isAdmin: boolean;
  authMode: AuthMode;
  steamAuthMode: 'anonymous' | 'authenticated';
  mockMode: boolean;
  isProcessingLogs: boolean;
  onError: (message: string) => void;
  onSuccess: (message: string) => void;
  onDataRefresh: () => void;
  onNavigateToSteamApi?: () => void;
  onNavigateToEpicLogin?: () => void;
}

const DataSection: React.FC<DataSectionProps> = ({
  isAdmin,
  authMode,
  steamAuthMode,
  mockMode,
  isProcessingLogs,
  onError,
  onSuccess,
  onDataRefresh,
  onNavigateToSteamApi,
  onNavigateToEpicLogin
}) => {
  const { t } = useTranslation();

  // Database Manager State
  const [loading, setLoading] = useState(false);
  const [showClearModal, setShowClearModal] = useState(false);
  const [selectedTables, setSelectedTables] = useState<string[]>([]);
  const clearInProgressRef = useRef(false);

  // Depot Manager State
  const [depotActionLoading, setDepotActionLoading] = useState(false);

  // Eviction Settings State
  const [evictionMode, setEvictionMode] = useState<string>('show');
  const [savedEvictionMode, setSavedEvictionMode] = useState<string>('show');
  const [evictionLoading, setEvictionLoading] = useState(false);
  const [evictionSaving, setEvictionSaving] = useState(false);
  const [reconciling, setReconciling] = useState(false);
  const [reconcileResult, setReconcileResult] = useState<{
    processed: number;
    evicted: number;
    unEvicted: number;
  } | null>(null);
  const isEvictionDirty = evictionMode !== savedEvictionMode;

  const loadEvictionSettings = useCallback(
    async (signal?: AbortSignal) => {
      setEvictionLoading(true);
      try {
        const response = await ApiService.getEvictionSettings(signal);
        setEvictionMode(response.evictedDataMode);
        setSavedEvictionMode(response.evictedDataMode);
      } catch (err: unknown) {
        if (err instanceof DOMException && err.name === 'AbortError') return;
        onError(t('management.sections.data.evictionLoadError'));
      } finally {
        setEvictionLoading(false);
      }
    },
    [onError, t]
  );

  useEffect(() => {
    const controller = new AbortController();
    loadEvictionSettings(controller.signal);
    return () => controller.abort();
  }, [loadEvictionSettings]);

  const handleSaveEviction = async () => {
    setEvictionSaving(true);
    try {
      const response = await ApiService.updateEvictionSettings(evictionMode);
      setEvictionMode(response.evictedDataMode);
      setSavedEvictionMode(response.evictedDataMode);
      onSuccess(t('management.sections.data.evictionSaveSuccess'));
      onDataRefresh();
    } catch (err: unknown) {
      onError(
        (err instanceof Error ? err.message : String(err)) ||
          t('management.sections.data.evictionSaveError')
      );
    } finally {
      setEvictionSaving(false);
    }
  };

  const handleRunReconciliation = async () => {
    setReconciling(true);
    setReconcileResult(null);
    try {
      const result = await ApiService.runReconciliation();
      setReconcileResult(result);
      onSuccess(
        t('management.sections.data.reconcileSuccess', {
          processed: result.processed,
          evicted: result.evicted,
          unEvicted: result.unEvicted
        })
      );
      onDataRefresh();
    } catch (err: unknown) {
      onError(
        (err instanceof Error ? err.message : String(err)) ||
          t('management.sections.data.reconcileError')
      );
    } finally {
      setReconciling(false);
    }
  };

  // Get table definitions from translations
  const tables = [
    {
      name: 'LogEntries',
      label: t('management.sections.data.tables.logEntries.label'),
      description: t('management.sections.data.tables.logEntries.description'),
      details: t('management.sections.data.tables.logEntries.details'),
      affectedPages: t('management.sections.data.tables.logEntries.affectedPages')
    },
    {
      name: 'Downloads',
      label: t('management.sections.data.tables.downloads.label'),
      description: t('management.sections.data.tables.downloads.description'),
      details: t('management.sections.data.tables.downloads.details'),
      affectedPages: t('management.sections.data.tables.downloads.affectedPages')
    },
    {
      name: 'ClientStats',
      label: t('management.sections.data.tables.clientStats.label'),
      description: t('management.sections.data.tables.clientStats.description'),
      details: t('management.sections.data.tables.clientStats.details'),
      affectedPages: t('management.sections.data.tables.clientStats.affectedPages')
    },
    {
      name: 'ServiceStats',
      label: t('management.sections.data.tables.serviceStats.label'),
      description: t('management.sections.data.tables.serviceStats.description'),
      details: t('management.sections.data.tables.serviceStats.details'),
      affectedPages: t('management.sections.data.tables.serviceStats.affectedPages')
    },
    {
      name: 'SteamDepotMappings',
      label: t('management.sections.data.tables.steamDepotMappings.label'),
      description: t('management.sections.data.tables.steamDepotMappings.description'),
      details: t('management.sections.data.tables.steamDepotMappings.details'),
      affectedPages: t('management.sections.data.tables.steamDepotMappings.affectedPages')
    },
    {
      name: 'CachedGameDetections',
      label: t('management.sections.data.tables.cachedGameDetections.label'),
      description: t('management.sections.data.tables.cachedGameDetections.description'),
      details: t('management.sections.data.tables.cachedGameDetections.details'),
      affectedPages: t('management.sections.data.tables.cachedGameDetections.affectedPages')
    },
    {
      name: 'CachedServiceDetections',
      label: t('management.sections.data.tables.cachedServiceDetections.label'),
      description: t('management.sections.data.tables.cachedServiceDetections.description'),
      details: t('management.sections.data.tables.cachedServiceDetections.details'),
      affectedPages: t('management.sections.data.tables.cachedServiceDetections.affectedPages')
    },
    {
      name: 'CachedCorruptionDetections',
      label: t('management.sections.data.tables.cachedCorruptionDetections.label'),
      description: t('management.sections.data.tables.cachedCorruptionDetections.description'),
      details: t('management.sections.data.tables.cachedCorruptionDetections.details'),
      affectedPages: t('management.sections.data.tables.cachedCorruptionDetections.affectedPages')
    },
    {
      name: 'ClientGroups',
      label: t('management.sections.data.tables.clientGroups.label'),
      description: t('management.sections.data.tables.clientGroups.description'),
      details: t('management.sections.data.tables.clientGroups.details'),
      affectedPages: t('management.sections.data.tables.clientGroups.affectedPages')
    },
    {
      name: 'Events',
      label: t('management.sections.data.tables.events.label'),
      description: t('management.sections.data.tables.events.description'),
      details: t('management.sections.data.tables.events.details'),
      affectedPages: t('management.sections.data.tables.events.affectedPages')
    },
    {
      name: 'EventDownloads',
      label: t('management.sections.data.tables.eventDownloads.label'),
      description: t('management.sections.data.tables.eventDownloads.description'),
      details: t('management.sections.data.tables.eventDownloads.details'),
      affectedPages: t('management.sections.data.tables.eventDownloads.affectedPages')
    },
    {
      name: 'PrefillSessions',
      label: t('management.sections.data.tables.prefillSessions.label'),
      description: t('management.sections.data.tables.prefillSessions.description'),
      details: t('management.sections.data.tables.prefillSessions.details'),
      affectedPages: t('management.sections.data.tables.prefillSessions.affectedPages')
    },
    {
      name: 'PrefillHistoryEntries',
      label: t('management.sections.data.tables.prefillHistoryEntries.label'),
      description: t('management.sections.data.tables.prefillHistoryEntries.description'),
      details: t('management.sections.data.tables.prefillHistoryEntries.details'),
      affectedPages: t('management.sections.data.tables.prefillHistoryEntries.affectedPages')
    },
    {
      name: 'PrefillCachedDepots',
      label: t('management.sections.data.tables.prefillCachedDepots.label'),
      description: t('management.sections.data.tables.prefillCachedDepots.description'),
      details: t('management.sections.data.tables.prefillCachedDepots.details'),
      affectedPages: t('management.sections.data.tables.prefillCachedDepots.affectedPages')
    },
    {
      name: 'BannedSteamUsers',
      label: t('management.sections.data.tables.bannedSteamUsers.label'),
      description: t('management.sections.data.tables.bannedSteamUsers.description'),
      details: t('management.sections.data.tables.bannedSteamUsers.details'),
      affectedPages: t('management.sections.data.tables.bannedSteamUsers.affectedPages')
    },
    {
      name: 'UserSessions',
      label: t('management.sections.data.tables.userSessions.label'),
      description: t('management.sections.data.tables.userSessions.description'),
      details: t('management.sections.data.tables.userSessions.details'),
      affectedPages: t('management.sections.data.tables.userSessions.affectedPages')
    },
    {
      name: 'UserPreferences',
      label: t('management.sections.data.tables.userPreferences.label'),
      description: t('management.sections.data.tables.userPreferences.description'),
      details: t('management.sections.data.tables.userPreferences.details'),
      affectedPages: t('management.sections.data.tables.userPreferences.affectedPages')
    },
    {
      name: 'CacheSnapshots',
      label: t('management.sections.data.tables.cacheSnapshots.label'),
      description: t('management.sections.data.tables.cacheSnapshots.description'),
      details: t('management.sections.data.tables.cacheSnapshots.details'),
      affectedPages: t('management.sections.data.tables.cacheSnapshots.affectedPages')
    },
    {
      name: 'EpicGameMappings',
      label: t('management.sections.data.tables.epicGameMappings.label'),
      description: t('management.sections.data.tables.epicGameMappings.description'),
      details: t('management.sections.data.tables.epicGameMappings.details'),
      affectedPages: t('management.sections.data.tables.epicGameMappings.affectedPages')
    },
    {
      name: 'EpicCdnPatterns',
      label: t('management.sections.data.tables.epicCdnPatterns.label'),
      description: t('management.sections.data.tables.epicCdnPatterns.description'),
      details: t('management.sections.data.tables.epicCdnPatterns.details'),
      affectedPages: t('management.sections.data.tables.epicCdnPatterns.affectedPages')
    }
  ];

  const handleTableToggle = (tableName: string) => {
    setSelectedTables((prev) =>
      prev.includes(tableName) ? prev.filter((t) => t !== tableName) : [...prev, tableName]
    );
  };

  const handleSelectAll = () => {
    if (selectedTables.length === tables.length) {
      setSelectedTables([]);
    } else {
      setSelectedTables(tables.map((t) => t.name));
    }
  };

  const handleClearSelected = () => {
    if (authMode !== 'authenticated') {
      onError(t('management.database.errors.authRequired'));
      return;
    }

    if (selectedTables.length === 0) {
      onError(t('management.database.errors.selectAtLeastOne'));
      return;
    }

    setShowClearModal(true);
  };

  const confirmClear = async () => {
    if (clearInProgressRef.current) return;
    clearInProgressRef.current = true;

    if (authMode !== 'authenticated') {
      onError(t('management.database.errors.authRequired'));
      clearInProgressRef.current = false;
      return;
    }

    setLoading(true);
    setShowClearModal(false);

    try {
      const result = await ApiService.resetSelectedTables(selectedTables);
      if (result) {
        onSuccess(
          result.message ||
            t('management.database.success.resetStarted', { count: selectedTables.length })
        );
        setSelectedTables([]);
        if (!selectedTables.includes('UserSessions')) {
          onDataRefresh();
        }
      }
    } catch (err: unknown) {
      onError(
        (err instanceof Error ? err.message : String(err)) ||
          t('management.database.errors.failedToClear')
      );
    } finally {
      setLoading(false);
      clearInProgressRef.current = false;
    }
  };

  const getSelectedTableInfo = () => tables.filter((t) => selectedTables.includes(t.name));

  return (
    <div
      className="management-section animate-fade-in"
      role="tabpanel"
      id="panel-data"
      aria-labelledby="tab-data"
    >
      {/* Section Header */}
      <div className="mb-6">
        <h2 className="text-xl font-semibold text-themed-primary mb-1">
          {t('management.sections.data.title')}
        </h2>
        <p className="text-themed-secondary text-sm">{t('management.sections.data.subtitle')}</p>
      </div>

      {/* Subsection: Steam Depot Mapping */}
      <div className="mb-8">
        <div className="flex items-center gap-2 mb-4">
          <div className="w-1 h-5 rounded-full bg-[var(--theme-steam)]" />
          <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
            {t('management.sections.data.steamDepotMapping')}
          </h3>
        </div>

        <DepotMappingManager
          isAdmin={isAdmin}
          mockMode={mockMode}
          steamAuthMode={steamAuthMode}
          actionLoading={depotActionLoading}
          setActionLoading={setDepotActionLoading}
          isProcessingLogs={isProcessingLogs}
          onError={onError}
          onSuccess={onSuccess}
          onDataRefresh={onDataRefresh}
          onNavigateToSteamApi={onNavigateToSteamApi}
        />
      </div>

      {/* Subsection: Epic Game Mapping */}
      <div className="mb-8">
        <div className="flex items-center gap-2 mb-4">
          <div className="w-1 h-5 rounded-full bg-[var(--theme-epic)]" />
          <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
            {t('management.sections.data.epicGameMapping')}
          </h3>
        </div>

        <EpicMappingManager
          isAdmin={isAdmin}
          mockMode={mockMode}
          onError={onError}
          onSuccess={onSuccess}
          onDataRefresh={onDataRefresh}
          onNavigateToEpicLogin={onNavigateToEpicLogin}
        />
      </div>

      {/* Subsection: Data Import */}
      <div className="mb-8">
        <div className="flex items-center gap-2 mb-4">
          <div className="w-1 h-5 rounded-full bg-[var(--theme-icon-green)]" />
          <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
            {t('management.sections.data.dataImport')}
          </h3>
        </div>

        <DataImporter
          isAdmin={isAdmin}
          mockMode={mockMode}
          onError={onError}
          onSuccess={onSuccess}
          onDataRefresh={onDataRefresh}
        />
      </div>

      {/* Subsection: Evicted Cache Data */}
      <div className="mb-8">
        <div className="flex items-center gap-2 mb-4">
          <div className="w-1 h-5 rounded-full bg-[var(--theme-icon-orange)]" />
          <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
            {t('management.sections.data.evictedCacheData')}
          </h3>
        </div>

        <Card>
          <div className="mb-4">
            <h3 className="text-lg font-semibold text-themed-primary mb-2">
              {t('management.sections.data.evictedCacheData')}
            </h3>
            <p className="text-themed-secondary text-sm">
              {t('management.sections.data.evictedCacheDescription')}
            </p>
          </div>

          {evictionLoading ? (
            <div className="flex items-center justify-center py-8">
              <Loader2 className="w-4 h-4 animate-spin" />
            </div>
          ) : (
            <>
              <div className="space-y-2 mb-4">
                {(['show', 'hide', 'remove'] as const).map((mode) => (
                  <label
                    key={mode}
                    className={`eviction-mode-option p-3 rounded-lg cursor-pointer flex items-start gap-3 transition-all duration-150 bg-themed-secondary${evictionMode === mode ? ' eviction-mode-option-selected' : ''}`}
                  >
                    <input
                      type="radio"
                      name="evictionMode"
                      value={mode}
                      checked={evictionMode === mode}
                      onChange={() => setEvictionMode(mode)}
                      className="eviction-radio mt-1"
                    />
                    <div className="flex-1 min-w-0">
                      <div className="font-medium text-themed-primary">
                        {t(`management.sections.data.evictionModes.${mode}`)}
                      </div>
                      <div className="text-sm text-themed-secondary mt-1">
                        {t(`management.sections.data.evictionModes.${mode}Description`)}
                      </div>
                    </div>
                  </label>
                ))}
              </div>

              <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3 pt-4 border-t border-themed-primary">
                <div className="flex items-center gap-3">
                  <Button
                    onClick={handleRunReconciliation}
                    disabled={reconciling}
                    loading={reconciling}
                    variant="subtle"
                    className="sm:w-48"
                  >
                    {t('management.sections.data.runReconciliation')}
                  </Button>
                  {reconcileResult && (
                    <span className="text-xs text-themed-muted">
                      {t('management.sections.data.reconcileResult', {
                        processed: reconcileResult.processed,
                        evicted: reconcileResult.evicted,
                        unEvicted: reconcileResult.unEvicted
                      })}
                    </span>
                  )}
                </div>
                <Button
                  onClick={handleSaveEviction}
                  disabled={!isEvictionDirty || evictionSaving}
                  loading={evictionSaving}
                  className="sm:w-40"
                >
                  {t('management.sections.clients.saveChanges')}
                </Button>
              </div>
            </>
          )}
        </Card>
      </div>

      {/* Subsection: Database Management */}
      <div>
        <div className="flex items-center gap-2 mb-4">
          <div className="w-1 h-5 rounded-full bg-[var(--theme-icon-cyan)]" />
          <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
            {t('management.sections.data.databaseManagement')}
          </h3>
        </div>

        <Card>
          <div className="flex items-center gap-3 mb-4">
            <div className="w-10 h-10 rounded-lg flex items-center justify-center icon-bg-cyan">
              <Database className="w-5 h-5 icon-cyan" />
            </div>
            <h3 className="text-lg font-semibold text-themed-primary">
              {t('management.sections.data.databaseManagement')}
            </h3>
            <HelpPopover position="left" width={320}>
              <HelpSection
                title={t('management.database.help.whatGetsCleared.title')}
                variant="subtle"
              >
                <HelpDefinition
                  items={[
                    {
                      term: t('management.database.help.whatGetsCleared.logEntries.term'),
                      description: t(
                        'management.database.help.whatGetsCleared.logEntries.description'
                      )
                    },
                    {
                      term: t('management.database.help.whatGetsCleared.downloads.term'),
                      description: t(
                        'management.database.help.whatGetsCleared.downloads.description'
                      )
                    },
                    {
                      term: t('management.database.help.whatGetsCleared.depotMappings.term'),
                      description: t(
                        'management.database.help.whatGetsCleared.depotMappings.description'
                      )
                    }
                  ]}
                />
              </HelpSection>

              <HelpNote type="info">{t('management.database.help.note')}</HelpNote>
            </HelpPopover>
          </div>

          <p className="text-themed-secondary mb-4">{t('management.database.description')}</p>

          {/* Select All / Deselect All */}
          <div className="mb-4 pb-4 border-b border-themed-primary">
            <Checkbox
              checked={selectedTables.length === tables.length}
              onChange={handleSelectAll}
              label={
                selectedTables.length === tables.length
                  ? t('management.sections.data.deselectAllTables')
                  : t('management.sections.data.selectAllTables')
              }
              variant="rounded"
            />
          </div>

          {/* Table Selection - Grid on larger screens */}
          <div className="grid grid-cols-1 lg:grid-cols-2 gap-3 mb-4">
            {tables.map((table) => (
              <label
                key={table.name}
                className={`db-table-item p-3 rounded-lg cursor-pointer flex items-start gap-3 transition-all duration-150 bg-themed-secondary${selectedTables.includes(table.name) ? ' db-table-item-selected' : ''}`}
              >
                <input
                  type="checkbox"
                  checked={selectedTables.includes(table.name)}
                  onChange={() => handleTableToggle(table.name)}
                  className="rounded mt-1"
                />
                <div className="flex-1 min-w-0">
                  <div className="font-medium text-themed-primary">{table.label}</div>
                  <div className="text-sm text-themed-secondary mt-1 line-clamp-1">
                    {table.description}
                  </div>
                  <div className="text-xs text-themed-muted mt-1.5 flex items-center gap-1">
                    <span className="opacity-70">{t('management.sections.data.affects')}</span>
                    <span className="text-themed-warning">{table.affectedPages}</span>
                  </div>
                </div>
              </label>
            ))}
          </div>

          {/* Action Button */}
          <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3 pt-4 border-t border-themed-primary">
            <div className="text-sm text-themed-secondary">
              {selectedTables.length > 0
                ? t('management.sections.data.selectedTables', { count: selectedTables.length })
                : t('management.sections.data.noTablesSelected')}
            </div>
            <Button
              onClick={handleClearSelected}
              disabled={
                loading || mockMode || authMode !== 'authenticated' || selectedTables.length === 0
              }
              loading={loading}
              variant="filled"
              color="red"
              className="w-full sm:w-auto"
            >
              <span className="hidden sm:inline">
                {t('management.sections.data.clearSelected')}
              </span>
              <span className="sm:hidden">{t('management.sections.data.clearSelectedShort')}</span>
            </Button>
          </div>
        </Card>
      </div>

      {/* Confirmation Modal */}
      <Modal
        opened={showClearModal}
        onClose={() => {
          if (!loading) {
            setShowClearModal(false);
          }
        }}
        size="lg"
        title={
          <div className="flex items-center space-x-3">
            <AlertTriangle className="w-6 h-6 text-themed-warning" />
            <span>{t('management.sections.data.confirmClearTitle')}</span>
          </div>
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            {t('management.sections.data.confirmClearMessage')}
          </p>

          <div className="space-y-2 max-h-[40vh] overflow-y-auto custom-scrollbar pr-2">
            {getSelectedTableInfo().map((table) => (
              <div key={table.name} className="p-3 rounded-lg bg-themed-secondary">
                <div className="font-medium text-themed-primary">{table.label}</div>
                <div className="text-sm text-themed-secondary mt-1">{table.description}</div>
                <div className="text-xs mt-2 flex items-center gap-1.5">
                  <span className="text-themed-muted">{t('management.sections.data.affects')}</span>
                  <span className="text-themed-warning font-medium">{table.affectedPages}</span>
                </div>
              </div>
            ))}
          </div>

          <Alert color="yellow">
            <div>
              <p className="text-sm font-medium mb-2">
                {t('management.sections.data.confirmClearImportant')}
              </p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li>{t('management.sections.data.confirmClearWarnings.noUndo')}</li>
                <li>{t('management.sections.data.confirmClearWarnings.exportFirst')}</li>
                <li>{t('management.sections.data.confirmClearWarnings.reportsAffected')}</li>
                {selectedTables.includes('SteamDepotMappings') && (
                  <li>{t('management.sections.data.confirmClearWarnings.depotMappings')}</li>
                )}
                {selectedTables.includes('Events') && (
                  <li>{t('management.sections.data.confirmClearWarnings.events')}</li>
                )}
                {selectedTables.includes('UserSessions') && (
                  <li className="font-semibold">
                    {t('management.sections.data.confirmClearWarnings.userSessionsLogout')}
                  </li>
                )}
                {selectedTables.includes('UserSessions') && (
                  <li className="font-semibold">
                    {t('management.sections.data.confirmClearWarnings.userSessionsCleared')}
                  </li>
                )}
                {selectedTables.includes('ClientGroups') && (
                  <li>{t('management.sections.data.confirmClearWarnings.clientGroups')}</li>
                )}
                {selectedTables.includes('PrefillSessions') && (
                  <li>{t('management.sections.data.confirmClearWarnings.prefillSessions')}</li>
                )}
                {selectedTables.includes('BannedSteamUsers') && (
                  <li>{t('management.sections.data.confirmClearWarnings.bannedSteamUsers')}</li>
                )}
                {selectedTables.includes('EpicGameMappings') && (
                  <li>{t('management.sections.data.confirmClearWarnings.epicGameMappings')}</li>
                )}
                {selectedTables.includes('EpicCdnPatterns') && (
                  <li>{t('management.sections.data.confirmClearWarnings.epicCdnPatterns')}</li>
                )}
              </ul>
            </div>
          </Alert>

          <div className="flex justify-end space-x-3 pt-2">
            <Button variant="default" onClick={() => setShowClearModal(false)} disabled={loading}>
              {t('common.cancel')}
            </Button>
            <Button variant="filled" color="red" onClick={confirmClear} loading={loading}>
              {t('management.sections.data.clearTables', { count: selectedTables.length })}
            </Button>
          </div>
        </div>
      </Modal>
    </div>
  );
};

export default DataSection;
