import React, { useState, useRef } from 'react';
import {
  Database,
  AlertTriangle
} from 'lucide-react';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { Modal } from '@components/ui/Modal';
import { Checkbox } from '@components/ui/Checkbox';
import { HelpPopover, HelpSection, HelpNote, HelpDefinition } from '@components/ui/HelpPopover';
import { type AuthMode } from '@services/auth.service';
import ApiService from '@services/api.service';
import DepotMappingManager from '../depot/DepotMappingManager';
import DataImporter from '../data/DataImporter';

interface DataSectionProps {
  isAuthenticated: boolean;
  authMode: AuthMode;
  steamAuthMode: 'anonymous' | 'authenticated';
  mockMode: boolean;
  isProcessingLogs: boolean;
  onError: (message: string) => void;
  onSuccess: (message: string) => void;
  onDataRefresh: () => void;
}

// Table definitions with descriptions
const tables = [
  {
    name: 'LogEntries',
    label: 'Log Entries',
    description: 'Raw access log entries from nginx cache logs',
    details: 'Individual log line records used for analytics and reporting'
  },
  {
    name: 'Downloads',
    label: 'Downloads',
    description: 'Download records with game associations and statistics',
    details: 'Tracked downloads with game names, sizes, and timestamps'
  },
  {
    name: 'ClientStats',
    label: 'Client Statistics',
    description: 'Per-client download statistics and metrics',
    details: 'Bandwidth and download counts grouped by IP address'
  },
  {
    name: 'ServiceStats',
    label: 'Service Statistics',
    description: 'Per-service (Steam, Epic, etc.) download statistics',
    details: 'Total downloads and bandwidth usage by CDN service'
  },
  {
    name: 'SteamDepotMappings',
    label: 'Steam Depot Mappings',
    description: 'Depot ID to game name associations from SteamKit',
    details: 'Mappings used to identify which game a depot belongs to. Also clears game names from existing downloads.'
  },
  {
    name: 'CachedGameDetections',
    label: 'Game Cache Detection',
    description: 'Cached results from game and service detection scans',
    details: 'Pre-computed game and service detections from cache files to speed up dashboard loading'
  },
  {
    name: 'Events',
    label: 'Events',
    description: 'Custom events for tracking LAN parties and gaming sessions',
    details: 'User-created events with date ranges. Clears associated download links via cascade.'
  },
  {
    name: 'EventDownloads',
    label: 'Event Download Links',
    description: 'Associations between events and downloads',
    details: 'Links connecting downloads to events (both auto-tagged and manual)'
  },
  {
    name: 'UserSessions',
    label: 'User Sessions',
    description: 'Active and historical user session records',
    details: 'Session tracking with device info, IP addresses, and authentication status'
  },
  {
    name: 'UserPreferences',
    label: 'User Preferences',
    description: 'Per-session user interface preferences',
    details: 'Theme selections and UI customization settings linked to sessions'
  }
];

const DataSection: React.FC<DataSectionProps> = ({
  isAuthenticated,
  authMode,
  steamAuthMode,
  mockMode,
  isProcessingLogs,
  onError,
  onSuccess,
  onDataRefresh
}) => {
  // Database Manager State
  const [loading, setLoading] = useState(false);
  const [showClearModal, setShowClearModal] = useState(false);
  const [selectedTables, setSelectedTables] = useState<string[]>([]);
  const clearInProgressRef = useRef(false);

  // Depot Manager State
  const [depotActionLoading, setDepotActionLoading] = useState(false);

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
      onError('Full authentication required for management operations');
      return;
    }

    if (selectedTables.length === 0) {
      onError('Please select at least one table to clear');
      return;
    }

    setShowClearModal(true);
  };

  const confirmClear = async () => {
    if (clearInProgressRef.current) return;
    clearInProgressRef.current = true;

    if (authMode !== 'authenticated') {
      onError('Full authentication required for management operations');
      clearInProgressRef.current = false;
      return;
    }

    setLoading(true);
    setShowClearModal(false);

    try {
      const result = await ApiService.resetSelectedTables(selectedTables);
      if (result) {
        onSuccess(result.message || `Database reset started for ${selectedTables.length} table(s)`);
        setSelectedTables([]);
        if (!selectedTables.includes('UserSessions')) {
          onDataRefresh();
        }
      }
    } catch (err: unknown) {
      onError((err instanceof Error ? err.message : String(err)) || 'Failed to clear selected tables');
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
          Data Configuration
        </h2>
        <p className="text-themed-secondary text-sm">
          Manage depot mappings, import data, and control database tables
        </p>
      </div>

      {/* Subsection: Depot Mapping */}
      <div className="mb-8">
        <div className="flex items-center gap-2 mb-4">
          <div
            className="w-1 h-5 rounded-full"
            style={{ backgroundColor: 'var(--theme-steam)' }}
          />
          <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
            Depot Mapping
          </h3>
        </div>

        <DepotMappingManager
          isAuthenticated={isAuthenticated}
          mockMode={mockMode}
          steamAuthMode={steamAuthMode}
          actionLoading={depotActionLoading}
          setActionLoading={setDepotActionLoading}
          isProcessingLogs={isProcessingLogs}
          onError={onError}
          onSuccess={onSuccess}
          onDataRefresh={onDataRefresh}
        />
      </div>

      {/* Subsection: Data Import */}
      <div className="mb-8">
        <div className="flex items-center gap-2 mb-4">
          <div
            className="w-1 h-5 rounded-full"
            style={{ backgroundColor: 'var(--theme-icon-green)' }}
          />
          <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
            Data Import
          </h3>
        </div>

        <DataImporter
          isAuthenticated={isAuthenticated}
          mockMode={mockMode}
          onError={onError}
          onSuccess={onSuccess}
          onDataRefresh={onDataRefresh}
        />
      </div>

      {/* Subsection: Database Management */}
      <div>
        <div className="flex items-center gap-2 mb-4">
          <div
            className="w-1 h-5 rounded-full"
            style={{ backgroundColor: 'var(--theme-icon-cyan)' }}
          />
          <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
            Database Management
          </h3>
        </div>

        <Card>
          <div className="flex items-center gap-3 mb-4">
            <div className="w-10 h-10 rounded-lg flex items-center justify-center icon-bg-cyan">
              <Database className="w-5 h-5 icon-cyan" />
            </div>
            <h3 className="text-lg font-semibold text-themed-primary">Database Management</h3>
            <HelpPopover position="left" width={320}>
              <HelpSection title="What Gets Cleared">
                <div className="space-y-1.5">
                  <HelpDefinition term="Log Entries" termColor="blue">
                    Raw nginx access log records used for analytics
                  </HelpDefinition>
                  <HelpDefinition term="Downloads" termColor="green">
                    Game download records with statistics and timestamps
                  </HelpDefinition>
                  <HelpDefinition term="Depot Mappings" termColor="purple">
                    Steam depot-to-game associations (also clears game names)
                  </HelpDefinition>
                </div>
              </HelpSection>

              <HelpNote type="info">
                Cached files on disk are never touched — only database records are cleared.
              </HelpNote>
            </HelpPopover>
          </div>

          <p className="text-themed-secondary mb-4">
            Select which database tables you want to clear. Cached files on disk will remain
            untouched.
          </p>

          {/* Select All / Deselect All */}
          <div className="mb-4 pb-4 border-b" style={{ borderColor: 'var(--theme-border-primary)' }}>
            <Checkbox
              checked={selectedTables.length === tables.length}
              onChange={handleSelectAll}
              label={
                selectedTables.length === tables.length ? 'Deselect All Tables' : 'Select All Tables'
              }
              variant="rounded"
            />
          </div>

          {/* Table Selection - Grid on larger screens */}
          <div className="grid grid-cols-1 lg:grid-cols-2 gap-3 mb-4">
            {tables.map((table) => (
              <label
                key={table.name}
                className="p-3 rounded-lg cursor-pointer flex items-start gap-3 transition-all duration-150"
                style={{
                  backgroundColor: 'var(--theme-bg-secondary)',
                  border: `1px solid ${selectedTables.includes(table.name) ? 'var(--theme-primary)' : 'var(--theme-border-primary)'}`
                }}
              >
                <input
                  type="checkbox"
                  checked={selectedTables.includes(table.name)}
                  onChange={() => handleTableToggle(table.name)}
                  className="rounded mt-1"
                />
                <div className="flex-1 min-w-0">
                  <div className="font-medium text-themed-primary">{table.label}</div>
                  <div className="text-sm text-themed-secondary mt-1 line-clamp-1">{table.description}</div>
                </div>
              </label>
            ))}
          </div>

          {/* Action Button */}
          <div
            className="flex items-center justify-between pt-4 border-t"
            style={{ borderColor: 'var(--theme-border-primary)' }}
          >
            <div className="text-sm text-themed-secondary">
              {selectedTables.length > 0
                ? `${selectedTables.length} table(s) selected`
                : 'No tables selected'}
            </div>
            <Button
              onClick={handleClearSelected}
              disabled={
                loading || mockMode || authMode !== 'authenticated' || selectedTables.length === 0
              }
              loading={loading}
              variant="filled"
              color="red"
            >
              <span className="hidden sm:inline">Clear Selected Tables</span>
              <span className="sm:hidden">Clear Selected</span>
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
        title={
          <div className="flex items-center space-x-3">
            <AlertTriangle className="w-6 h-6 text-themed-warning" />
            <span>Clear Selected Tables</span>
          </div>
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            You are about to permanently delete data from the following table(s):
          </p>

          <div className="space-y-2 max-h-[40vh] overflow-y-auto custom-scrollbar pr-2">
            {getSelectedTableInfo().map((table) => (
              <div
                key={table.name}
                className="p-3 rounded-lg"
                style={{ backgroundColor: 'var(--theme-bg-secondary)' }}
              >
                <div className="font-medium text-themed-primary">{table.label}</div>
                <div className="text-sm text-themed-secondary mt-1">{table.description}</div>
              </div>
            ))}
          </div>

          <Alert color="yellow">
            <div>
              <p className="text-sm font-medium mb-2">Important:</p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li>This action cannot be undone</li>
                <li>Export any data you need before continuing</li>
                <li>Historical reports may be affected</li>
                {selectedTables.includes('SteamDepotMappings') && (
                  <li>Games will show as "Unknown" until mappings are rebuilt</li>
                )}
                {selectedTables.includes('Events') && (
                  <li>All events and their download associations will be permanently deleted</li>
                )}
                {selectedTables.includes('UserSessions') && (
                  <li className="font-semibold">All devices will be logged out and the application will reload</li>
                )}
                {selectedTables.includes('UserSessions') && (
                  <li className="font-semibold">All registered devices will be cleared - you will need to re-authenticate</li>
                )}
              </ul>
            </div>
          </Alert>

          <div className="flex justify-end space-x-3 pt-2">
            <Button variant="default" onClick={() => setShowClearModal(false)} disabled={loading}>
              Cancel
            </Button>
            <Button
              variant="filled"
              color="red"
              onClick={confirmClear}
              loading={loading}
            >
              Clear {selectedTables.length} Table{selectedTables.length !== 1 ? 's' : ''}
            </Button>
          </div>
        </div>
      </Modal>
    </div>
  );
};

export default DataSection;
