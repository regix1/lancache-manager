import React, { useState, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { Database } from 'lucide-react';
import { AccordionSection } from '@components/ui/AccordionSection';
import { useAccordionGroupItem } from '@contexts/AccordionGroupContext';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { ConfirmationModal } from '@components/common/ConfirmationModal';
import { Checkbox } from '@components/ui/Checkbox';
import { HelpPopover, HelpSection, HelpNote, HelpDefinition } from '@components/ui/HelpPopover';
import { AccordionGroupToggle } from '@components/ui/AccordionGroupToggle';
import { GroupHeading } from '@components/ui/GroupHeading';
import { TabPanel } from '@components/features/management/TabPanel';
import { type AuthMode } from '@services/auth.service';
import ApiService from '@services/api.service';
import { getErrorMessage } from '@utils/error';
import DataImporter from '../data/DataImporter';

/**
 * Tables whose loss costs the reader something beyond the rows themselves, and the sentence that
 * says so. Tables absent from this list clear without a consequence worth spelling out.
 */
const TABLE_CLEAR_CONSEQUENCES = [
  { table: 'SteamDepotMappings', key: 'depotMappings' },
  { table: 'Events', key: 'events' },
  { table: 'UserSessions', key: 'userSessions' },
  { table: 'ClientGroups', key: 'clientGroups' },
  { table: 'PrefillSessions', key: 'prefillSessions' },
  { table: 'BannedPrefillUsers', key: 'bannedPrefillUsers' },
  { table: 'EpicGameMappings', key: 'epicGameMappings' },
  { table: 'EpicCdnPatterns', key: 'epicCdnPatterns' },
  { table: 'XboxGameMappings', key: 'xboxGameMappings' },
  { table: 'XboxCdnPatterns', key: 'xboxCdnPatterns' }
] as const;

interface DataSectionProps {
  isAdmin: boolean;
  authMode: AuthMode;
  mockMode: boolean;
  onError: (message: string) => void;
  onSuccess: (message: string) => void;
  onDataRefresh: () => void;
  // Battle.net is anonymous (no login); this navigates to / highlights the
  // Battle.net daemon status card in the Integrations section.
  onNavigateToBattleNetLogin?: () => void;
}

const DataSection: React.FC<DataSectionProps> = ({
  isAdmin,
  authMode,
  mockMode,
  onError,
  onSuccess,
  onDataRefresh
}) => {
  const { t } = useTranslation();

  // Database Manager State
  const [loading, setLoading] = useState(false);
  const [showClearModal, setShowClearModal] = useState(false);
  const [selectedTables, setSelectedTables] = useState<string[]>([]);
  const [databaseManagementExpanded, setDatabaseManagementExpanded] = useState(false);
  useAccordionGroupItem('data-database-management', databaseManagementExpanded, () =>
    setDatabaseManagementExpanded((prev) => !prev)
  );
  const clearInProgressRef = useRef(false);

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
      name: 'BannedPrefillUsers',
      label: t('management.sections.data.tables.bannedPrefillUsers.label'),
      description: t('management.sections.data.tables.bannedPrefillUsers.description'),
      details: t('management.sections.data.tables.bannedPrefillUsers.details'),
      affectedPages: t('management.sections.data.tables.bannedPrefillUsers.affectedPages')
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
    },
    {
      name: 'XboxGameMappings',
      label: t('management.sections.data.tables.xboxGameMappings.label'),
      description: t('management.sections.data.tables.xboxGameMappings.description'),
      details: t('management.sections.data.tables.xboxGameMappings.details'),
      affectedPages: t('management.sections.data.tables.xboxGameMappings.affectedPages')
    },
    {
      name: 'XboxCdnPatterns',
      label: t('management.sections.data.tables.xboxCdnPatterns.label'),
      description: t('management.sections.data.tables.xboxCdnPatterns.description'),
      details: t('management.sections.data.tables.xboxCdnPatterns.details'),
      affectedPages: t('management.sections.data.tables.xboxCdnPatterns.affectedPages')
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
        // Wait-queue model: a queued/deduplicated response means this click didn't start a new
        // reset - say so instead of the generic "reset started" message.
        if (result.alreadyRunning) {
          onSuccess(t('management.database.success.resetAlreadyRunning'));
        } else if (result.queued) {
          onSuccess(t('common.notifications.willQueueBehindCurrent'));
        } else {
          onSuccess(
            result.message ||
              t('management.database.success.resetStarted', { count: selectedTables.length })
          );
        }
        setSelectedTables([]);
        if (!selectedTables.includes('UserSessions')) {
          onDataRefresh();
        }
      }
    } catch (err: unknown) {
      onError(getErrorMessage(err) || t('management.database.errors.failedToClear'));
    } finally {
      setLoading(false);
      clearInProgressRef.current = false;
    }
  };

  const getSelectedTableInfo = () => tables.filter((t) => selectedTables.includes(t.name));

  const databaseHelp = (
    <HelpPopover position="left" width={320}>
      <HelpSection title={t('management.database.help.aboutTitle')}>
        {t('management.database.description')}
      </HelpSection>

      <HelpSection title={t('management.database.help.whatGetsCleared.title')} variant="subtle">
        <HelpDefinition
          items={[
            {
              term: t('management.database.help.whatGetsCleared.logEntries.term'),
              description: t('management.database.help.whatGetsCleared.logEntries.description')
            },
            {
              term: t('management.database.help.whatGetsCleared.downloads.term'),
              description: t('management.database.help.whatGetsCleared.downloads.description')
            },
            {
              term: t('management.database.help.whatGetsCleared.depotMappings.term'),
              description: t('management.database.help.whatGetsCleared.depotMappings.description')
            }
          ]}
        />
      </HelpSection>

      <HelpNote type="info">{t('management.database.help.note')}</HelpNote>
    </HelpPopover>
  );

  return (
    <TabPanel tabId="data">
      {/* Subsection: Data Import */}
      <div className="mb-6 sm:mb-8">
        <GroupHeading
          label={t('management.sections.data.dataImport')}
          actions={<AccordionGroupToggle />}
        />

        <div className="space-y-4">
          <DataImporter
            isAdmin={isAdmin}
            mockMode={mockMode}
            onError={onError}
            onSuccess={onSuccess}
            onDataRefresh={onDataRefresh}
          />
        </div>
      </div>

      {/* Subsection: Database Management */}
      <div>
        <GroupHeading label={t('management.sections.data.databaseManagement')} />

        <div className="space-y-4">
          <AccordionSection
            title={t('management.sections.data.databaseManagement')}
            titleAccessory={databaseHelp}
            icon={Database}
            isExpanded={databaseManagementExpanded}
            onToggle={() => setDatabaseManagementExpanded((prev) => !prev)}
          >
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
                  className={`db-table-item p-3 rounded-lg cursor-pointer flex items-start gap-3 transition duration-150 bg-themed-tertiary${selectedTables.includes(table.name) ? ' db-table-item-selected' : ''}`}
                >
                  <Checkbox
                    checked={selectedTables.includes(table.name)}
                    onChange={() => handleTableToggle(table.name)}
                    variant="rounded"
                    className="mt-1"
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
                color="destructive"
                className="w-full sm:w-auto"
              >
                <span className="hidden sm:inline">
                  {t('management.sections.data.clearSelected')}
                </span>
                <span className="sm:hidden">
                  {t('management.sections.data.clearSelectedShort')}
                </span>
              </Button>
            </div>
          </AccordionSection>
        </div>
      </div>

      {/* Confirmation Modal */}
      <ConfirmationModal
        opened={showClearModal}
        onClose={() => setShowClearModal(false)}
        onConfirm={confirmClear}
        title={t('management.sections.data.confirmClearTitle')}
        confirmLabel={t('management.sections.data.clearTables', { count: selectedTables.length })}
        loading={loading}
        size="lg"
      >
        <p className="text-themed-secondary">{t('management.sections.data.confirmClearMessage')}</p>

        <div className="space-y-2 max-h-[40vh] overflow-y-auto custom-scrollbar pr-2">
          {getSelectedTableInfo().map((table) => (
            <div
              key={table.name}
              className="p-3 rounded-lg bg-themed-tertiary border border-[var(--theme-border-well)] [background-clip:padding-box]"
            >
              <div className="font-medium text-themed-primary">{table.label}</div>
              <div className="text-sm text-themed-secondary mt-1">{table.description}</div>
              <div className="text-xs mt-2 flex items-center gap-1.5">
                <span className="text-themed-muted">{t('management.sections.data.affects')}</span>
                <span className="text-themed-warning font-medium">{table.affectedPages}</span>
              </div>
            </div>
          ))}
        </div>

        {/* The generic cautions are one lead sentence; what follows is only the consequences the
            selected tables actually carry, run together as prose rather than a bullet per table. */}
        <Alert color="yellow">
          <p className="text-sm">
            {[
              t('management.sections.data.confirmClearWarnings.clearSummary'),
              ...TABLE_CLEAR_CONSEQUENCES.filter(({ table }) => selectedTables.includes(table)).map(
                ({ key }) => t(`management.sections.data.confirmClearWarnings.${key}`)
              )
            ].join(' ')}
          </p>
        </Alert>
      </ConfirmationModal>
    </TabPanel>
  );
};

export default DataSection;
