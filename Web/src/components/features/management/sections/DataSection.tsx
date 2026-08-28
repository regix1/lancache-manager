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
import { APP_EVENTS } from '@utils/constants';
import { type ManagementSection } from '../ManagementNav';
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
  { table: 'XboxCdnPatterns', key: 'xboxCdnPatterns' },
  { table: 'IdentityAuditEntries', key: 'identityAuditEntries' }
] as const;

/**
 * Where each table's data is actually visible, as somewhere the reader can be sent rather than a
 * sentence naming a screen. Labels come from the navigation's own translations, so a tab or section
 * that gets renamed carries its new name here automatically. Hand-written screen names were wrong
 * twice: they named an Analytics tab and a Charts tab that never existed, and they sent the theme
 * preferences to Theme when half of them live under Settings.
 *
 * A table whose loss shows up nowhere on screen has no entry. Its details line says what is lost.
 */
type TableDestination = { tab: string } | { section: ManagementSection };

const TABLE_DESTINATIONS: Record<string, readonly TableDestination[]> = {
  LogEntries: [{ tab: 'dashboard' }, { tab: 'downloads' }, { tab: 'clients' }],
  Downloads: [{ tab: 'dashboard' }, { tab: 'downloads' }, { tab: 'clients' }, { tab: 'events' }],
  SteamDepotMappings: [{ tab: 'dashboard' }, { tab: 'downloads' }],
  GameImages: [{ tab: 'downloads' }],
  CachedGameDetections: [{ section: 'storage' }],
  CachedServiceDetections: [{ section: 'storage' }],
  CachedDetectionSummaries: [{ section: 'storage' }],
  CachedCorruptionDetections: [{ section: 'storage' }],
  ClientGroups: [{ tab: 'clients' }, { tab: 'downloads' }, { section: 'clients' }],
  Events: [{ tab: 'events' }, { tab: 'downloads' }],
  EventDownloads: [{ tab: 'events' }, { tab: 'downloads' }],
  PrefillSessions: [{ section: 'prefill-sessions' }],
  PrefillHistoryEntries: [{ section: 'prefill-sessions' }],
  PrefillCachedDepots: [{ tab: 'prefill' }],
  BannedPrefillUsers: [{ section: 'prefill-sessions' }],
  UserPreferences: [{ section: 'settings' }, { section: 'preferences' }],
  CacheSnapshots: [{ tab: 'dashboard' }],
  EpicGameMappings: [{ tab: 'dashboard' }, { tab: 'downloads' }],
  EpicCdnPatterns: [{ tab: 'downloads' }, { section: 'storage' }],
  XboxGameMappings: [{ tab: 'dashboard' }, { tab: 'downloads' }],
  XboxCdnPatterns: [{ tab: 'downloads' }, { section: 'storage' }]
};

/**
 * Section ids are kebab-case; their translations are camelCase. Only the two multi-word sections
 * differ, but the map covers every section this file links to so a new destination cannot silently
 * render a raw key.
 */
const MANAGEMENT_SECTION_KEYS: Record<ManagementSection, string> = {
  settings: 'settings',
  integrations: 'integrations',
  storage: 'storage',
  data: 'data',
  schedules: 'schedules',
  preferences: 'preferences',
  clients: 'clients',
  'prefill-sessions': 'prefillSessions',
  'status-check': 'statusCheck'
};

/**
 * Tables holding something a person entered or a record of something that happened. Clearing these
 * is one way: nothing regenerates them. Every other table is derived from the logs, the cache on
 * disk, or a store catalog, so it refills on the next scan, reprocess or refresh.
 */
const PERMANENT_TABLES = new Set([
  'ClientGroups',
  'Events',
  'EventDownloads',
  'PrefillSessions',
  'PrefillHistoryEntries',
  'BannedPrefillUsers',
  'IdentityAuditEntries',
  'CacheSnapshots'
]);

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
  // Jump to another Management section from a table's destination buttons. Other tabs go through
  // the NAVIGATE_TO_TAB event instead, which App already listens for.
  onNavigateToSection?: (section: ManagementSection) => void;
}

const DataSection: React.FC<DataSectionProps> = ({
  isAdmin,
  authMode,
  mockMode,
  onError,
  onSuccess,
  onDataRefresh,
  onNavigateToSection
}) => {
  const { t } = useTranslation();

  // A destination is either another top-level tab or another Management section. Both routes
  // already exist: App listens for NAVIGATE_TO_TAB, and ManagementTab owns the section state.
  const goToDestination = (destination: TableDestination) => {
    if ('tab' in destination) {
      window.dispatchEvent(
        new CustomEvent(APP_EVENTS.NAVIGATE_TO_TAB, { detail: { tab: destination.tab } })
      );
      return;
    }
    onNavigateToSection?.(destination.section);
  };

  const destinationLabel = (destination: TableDestination) =>
    'tab' in destination
      ? t(`nav.${destination.tab}`)
      : t('management.sections.data.inManagement', {
          section: t(`management.nav.${MANAGEMENT_SECTION_KEYS[destination.section]}`)
        });

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
      details: t('management.sections.data.tables.logEntries.details')
    },
    {
      name: 'Downloads',
      label: t('management.sections.data.tables.downloads.label'),
      description: t('management.sections.data.tables.downloads.description'),
      details: t('management.sections.data.tables.downloads.details')
    },
    {
      name: 'SteamDepotMappings',
      label: t('management.sections.data.tables.steamDepotMappings.label'),
      description: t('management.sections.data.tables.steamDepotMappings.description'),
      details: t('management.sections.data.tables.steamDepotMappings.details')
    },
    {
      name: 'GameImages',
      label: t('management.sections.data.tables.gameImages.label'),
      description: t('management.sections.data.tables.gameImages.description'),
      details: t('management.sections.data.tables.gameImages.details')
    },
    {
      name: 'CachedGameDetections',
      label: t('management.sections.data.tables.cachedGameDetections.label'),
      description: t('management.sections.data.tables.cachedGameDetections.description'),
      details: t('management.sections.data.tables.cachedGameDetections.details')
    },
    {
      name: 'CachedServiceDetections',
      label: t('management.sections.data.tables.cachedServiceDetections.label'),
      description: t('management.sections.data.tables.cachedServiceDetections.description'),
      details: t('management.sections.data.tables.cachedServiceDetections.details')
    },
    {
      name: 'CachedDetectionSummaries',
      label: t('management.sections.data.tables.cachedDetectionSummaries.label'),
      description: t('management.sections.data.tables.cachedDetectionSummaries.description'),
      details: t('management.sections.data.tables.cachedDetectionSummaries.details')
    },
    {
      name: 'CachedCorruptionDetections',
      label: t('management.sections.data.tables.cachedCorruptionDetections.label'),
      description: t('management.sections.data.tables.cachedCorruptionDetections.description'),
      details: t('management.sections.data.tables.cachedCorruptionDetections.details')
    },
    {
      name: 'ClientGroups',
      label: t('management.sections.data.tables.clientGroups.label'),
      description: t('management.sections.data.tables.clientGroups.description'),
      details: t('management.sections.data.tables.clientGroups.details')
    },
    {
      name: 'Events',
      label: t('management.sections.data.tables.events.label'),
      description: t('management.sections.data.tables.events.description'),
      details: t('management.sections.data.tables.events.details')
    },
    {
      name: 'EventDownloads',
      label: t('management.sections.data.tables.eventDownloads.label'),
      description: t('management.sections.data.tables.eventDownloads.description'),
      details: t('management.sections.data.tables.eventDownloads.details')
    },
    {
      name: 'PrefillSessions',
      label: t('management.sections.data.tables.prefillSessions.label'),
      description: t('management.sections.data.tables.prefillSessions.description'),
      details: t('management.sections.data.tables.prefillSessions.details')
    },
    {
      name: 'PrefillHistoryEntries',
      label: t('management.sections.data.tables.prefillHistoryEntries.label'),
      description: t('management.sections.data.tables.prefillHistoryEntries.description'),
      details: t('management.sections.data.tables.prefillHistoryEntries.details')
    },
    {
      name: 'PrefillCachedDepots',
      label: t('management.sections.data.tables.prefillCachedDepots.label'),
      description: t('management.sections.data.tables.prefillCachedDepots.description'),
      details: t('management.sections.data.tables.prefillCachedDepots.details')
    },
    {
      name: 'BannedPrefillUsers',
      label: t('management.sections.data.tables.bannedPrefillUsers.label'),
      description: t('management.sections.data.tables.bannedPrefillUsers.description'),
      details: t('management.sections.data.tables.bannedPrefillUsers.details')
    },
    {
      name: 'UserSessions',
      label: t('management.sections.data.tables.userSessions.label'),
      description: t('management.sections.data.tables.userSessions.description'),
      details: t('management.sections.data.tables.userSessions.details')
    },
    {
      name: 'UserPreferences',
      label: t('management.sections.data.tables.userPreferences.label'),
      description: t('management.sections.data.tables.userPreferences.description'),
      details: t('management.sections.data.tables.userPreferences.details')
    },
    {
      name: 'IdentityAuditEntries',
      label: t('management.sections.data.tables.identityAuditEntries.label'),
      description: t('management.sections.data.tables.identityAuditEntries.description'),
      details: t('management.sections.data.tables.identityAuditEntries.details')
    },
    {
      name: 'CacheSnapshots',
      label: t('management.sections.data.tables.cacheSnapshots.label'),
      description: t('management.sections.data.tables.cacheSnapshots.description'),
      details: t('management.sections.data.tables.cacheSnapshots.details')
    },
    {
      name: 'EpicGameMappings',
      label: t('management.sections.data.tables.epicGameMappings.label'),
      description: t('management.sections.data.tables.epicGameMappings.description'),
      details: t('management.sections.data.tables.epicGameMappings.details')
    },
    {
      name: 'EpicCdnPatterns',
      label: t('management.sections.data.tables.epicCdnPatterns.label'),
      description: t('management.sections.data.tables.epicCdnPatterns.description'),
      details: t('management.sections.data.tables.epicCdnPatterns.details')
    },
    {
      name: 'XboxGameMappings',
      label: t('management.sections.data.tables.xboxGameMappings.label'),
      description: t('management.sections.data.tables.xboxGameMappings.description'),
      details: t('management.sections.data.tables.xboxGameMappings.details')
    },
    {
      name: 'XboxCdnPatterns',
      label: t('management.sections.data.tables.xboxCdnPatterns.label'),
      description: t('management.sections.data.tables.xboxCdnPatterns.description'),
      details: t('management.sections.data.tables.xboxCdnPatterns.details')
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
                    <div className="db-table-item-head">
                      <span className="font-medium text-themed-primary">{table.label}</span>
                      <span
                        className={`db-table-recovery themed-border-radius-sm${PERMANENT_TABLES.has(table.name) ? ' db-table-recovery-permanent' : ''}`}
                      >
                        {PERMANENT_TABLES.has(table.name)
                          ? t('management.sections.data.recovery.permanent')
                          : t('management.sections.data.recovery.rebuilds')}
                      </span>
                    </div>
                    <div className="text-sm text-themed-secondary mt-1">{table.description}</div>
                    <div className="text-xs text-themed-muted mt-1.5">{table.details}</div>
                    {TABLE_DESTINATIONS[table.name]?.length ? (
                      <div className="db-table-destinations">
                        <span className="db-table-destinations-label">
                          {t('management.sections.data.affects')}
                        </span>
                        {TABLE_DESTINATIONS[table.name].map((destination) => {
                          const label = destinationLabel(destination);
                          return (
                            <button
                              key={label}
                              type="button"
                              className="db-table-destination themed-border-radius-sm"
                              // The card is a label wrapping a checkbox, so a click here would
                              // otherwise also toggle the table's selection.
                              onClick={(event) => {
                                event.preventDefault();
                                event.stopPropagation();
                                goToDestination(destination);
                              }}
                            >
                              {label}
                            </button>
                          );
                        })}
                      </div>
                    ) : null}
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
              {TABLE_DESTINATIONS[table.name]?.length ? (
                <div className="text-xs mt-2 flex items-start gap-1.5">
                  <span className="text-themed-muted shrink-0">
                    {t('management.sections.data.affects')}
                  </span>
                  {/* Read-only here on purpose: leaving the page mid-confirmation would drop the
                      selection the reader is about to act on. */}
                  <span className="text-themed-warning font-medium">
                    {TABLE_DESTINATIONS[table.name].map(destinationLabel).join(' • ')}
                  </span>
                </div>
              ) : null}
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
