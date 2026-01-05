import React, { useState, useEffect, useCallback } from 'react';
import {
  Database,
  AlertTriangle,
  ChevronUp,
  ChevronDown,
  Upload,
  CheckCircle2,
  Loader2,
  Search,
  RefreshCw
} from 'lucide-react';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { Checkbox } from '@components/ui/Checkbox';
import { Modal } from '@components/ui/Modal';
import { HelpPopover, HelpSection, HelpNote, HelpDefinition } from '@components/ui/HelpPopover';
import { EnhancedDropdown, type DropdownOption } from '@components/ui/EnhancedDropdown';
import {
  ManagerCardHeader,
  LoadingState,
  EmptyState
} from '@components/ui/ManagerCard';
import ApiService from '@services/api.service';
import FileBrowser from '../file-browser/FileBrowser';

type ImportType = 'develancache' | 'lancache-manager';

const importTypeOptions: DropdownOption[] = [
  {
    value: 'develancache',
    label: 'DeveLanCacheUI_Backend',
    description: 'Import from DeveLanCacheUI_Backend SQLite database'
  },
  {
    value: 'lancache-manager',
    label: 'LancacheManager',
    description: 'Import from LancacheManager database backup'
  }
];

interface DataImporterProps {
  isAuthenticated: boolean;
  mockMode: boolean;
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
  onDataRefresh?: () => void;
}

interface ValidationResult {
  valid: boolean;
  message: string;
  recordCount?: number;
}

interface ImportResult {
  message: string;
  totalRecords: number;
  imported: number;
  skipped: number;
  errors: number;
  backupPath?: string;
}

interface FileSystemItem {
  name: string;
  path: string;
  isDirectory: boolean;
  size: number;
  lastModified: string;
  isAccessible: boolean;
}

type InputMode = 'auto' | 'browse' | 'manual';

const DataImporter: React.FC<DataImporterProps> = ({
  isAuthenticated,
  mockMode,
  onError,
  onSuccess,
  onDataRefresh
}) => {
  const [importType, setImportType] = useState<ImportType>('develancache');
  const [connectionString, setConnectionString] = useState('');
  const [batchSize, setBatchSize] = useState(1000);
  const [overwriteExisting, setOverwriteExisting] = useState(false);
  const [validating, setValidating] = useState(false);
  const [importing, setImporting] = useState(false);
  const [validationResult, setValidationResult] = useState<ValidationResult | null>(null);
  const [importResult, setImportResult] = useState<ImportResult | null>(null);
  const [inputMode, setInputMode] = useState<InputMode>('auto');
  const [showConfirmModal, setShowConfirmModal] = useState(false);
  const [importProgress, setImportProgress] = useState(0);
  const [autoSearching, setAutoSearching] = useState(false);
  const [foundDatabases, setFoundDatabases] = useState<FileSystemItem[]>([]);

  // Simulate progress during import
  useEffect(() => {
    if (importing) {
      setImportProgress(0);
      const interval = setInterval(() => {
        setImportProgress(prev => {
          if (prev >= 90) return prev;
          return prev + Math.random() * 15;
        });
      }, 500);
      return () => clearInterval(interval);
    } else if (importResult) {
      setImportProgress(100);
    }
  }, [importing, importResult]);

  // Auto-search for databases when in auto mode
  const searchForDatabases = useCallback(async () => {
    if (!isAuthenticated || mockMode) return;

    setAutoSearching(true);
    try {
      const res = await fetch(
        '/api/filebrowser/search?searchPath=/',
        ApiService.getFetchOptions({ method: 'GET' })
      );
      const result = await ApiService.handleResponse<{ results: FileSystemItem[] }>(res);
      setFoundDatabases(result.results);
    } catch (error) {
      console.error('Failed to search for databases:', error);
      setFoundDatabases([]);
    } finally {
      setAutoSearching(false);
    }
  }, [isAuthenticated, mockMode]);

  // Trigger search when switching to auto mode
  useEffect(() => {
    if (inputMode === 'auto' && isAuthenticated && !mockMode && foundDatabases.length === 0) {
      searchForDatabases();
    }
  }, [inputMode, isAuthenticated, mockMode, foundDatabases.length, searchForDatabases]);

  const handleValidate = async () => {
    if (!connectionString.trim()) {
      onError?.('Please enter a connection string');
      return;
    }

    setValidating(true);
    setValidationResult(null);
    setImportResult(null);

    try {
      const res = await fetch(
        `/api/migration/validate-connection?connectionString=${encodeURIComponent(connectionString)}&importType=${importType}`,
        ApiService.getFetchOptions({
          method: 'GET'
        })
      );

      const result = await ApiService.handleResponse<ValidationResult>(res);
      setValidationResult(result);

      if (result.valid) {
        onSuccess?.(`Connection validated! Found ${result.recordCount?.toLocaleString() ?? 0} records.`);
      } else {
        onError?.(result.message);
      }
    } catch (error: unknown) {
      const errorMsg = error instanceof Error ? error.message : String(error);
      onError?.('Failed to validate connection: ' + errorMsg);
      setValidationResult({
        valid: false,
        message: errorMsg
      });
    } finally {
      setValidating(false);
    }
  };

  const handleImportClick = () => {
    if (!validationResult?.valid) {
      onError?.('Please validate the connection first');
      return;
    }
    setShowConfirmModal(true);
  };

  const handleConfirmImport = async () => {
    setShowConfirmModal(false);
    setImporting(true);
    setImportResult(null);

    const endpoint = importType === 'lancache-manager'
      ? '/api/migration/import-lancache-manager'
      : '/api/migration/import-develancache';

    try {
      const res = await fetch(endpoint, ApiService.getFetchOptions({
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          connectionString,
          batchSize,
          overwriteExisting
        })
      }));

      const result = await ApiService.handleResponse<ImportResult>(res);
      setImportResult(result);
      onSuccess?.(
        `Import completed! ${result.imported} imported, ${result.skipped} skipped, ${result.errors} errors`
      );

      if (onDataRefresh) {
        setTimeout(() => onDataRefresh(), 1000);
      }
    } catch (error: unknown) {
      onError?.('Import failed: ' + (error instanceof Error ? error.message : String(error)));
    } finally {
      setImporting(false);
    }
  };

  const handleFileSelect = (path: string) => {
    setConnectionString(path);
    setValidationResult(null);
    setImportResult(null);
    onSuccess?.(`Selected database: ${path}`);
  };

  const handleAutoSelect = (item: FileSystemItem) => {
    setConnectionString(item.path);
    setValidationResult(null);
    setImportResult(null);
    onSuccess?.(`Selected database: ${item.path}`);
  };

  const formatSize = (bytes: number): string => {
    if (bytes === 0) return '-';
    const units = ['B', 'KB', 'MB', 'GB'];
    const i = Math.floor(Math.log(bytes) / Math.log(1024));
    return `${(bytes / Math.pow(1024, i)).toFixed(1)} ${units[i]}`;
  };

  // Help content
  const helpContent = (
    <HelpPopover position="left" width={340}>
      <HelpSection title="Import Types">
        <div className="space-y-1.5">
          <HelpDefinition term="DeveLanCacheUI_Backend" termColor="purple">
            DeveLanCache monitoring system database
          </HelpDefinition>
          <HelpDefinition term="LancacheManager" termColor="blue">
            LancacheManager database backup
          </HelpDefinition>
        </div>
      </HelpSection>

      <HelpSection title="Input Methods">
        <div className="space-y-1.5">
          <HelpDefinition term="Browse" termColor="blue">
            Navigate to select your SQLite database file
          </HelpDefinition>
          <HelpDefinition term="Manual" termColor="green">
            Paste the file path directly
          </HelpDefinition>
        </div>
      </HelpSection>

      <HelpSection title="Compatibility" variant="subtle">
        Select the correct import type for your database.
        Mount external databases as Docker volumes.
      </HelpSection>

      <HelpNote type="warning">
        Stop the external application before importing to avoid database locks.
      </HelpNote>
    </HelpPopover>
  );

  // Get the selected import type label for display
  const selectedImportTypeLabel = importTypeOptions.find(o => o.value === importType)?.label || 'Unknown';

  // Header actions - compatibility badge
  const headerActions = (
    <div
      className="hidden sm:flex items-center gap-2 px-3 py-1.5 rounded-lg text-sm"
      style={{
        backgroundColor: 'var(--theme-bg-tertiary)',
        border: '1px solid var(--theme-border-secondary)'
      }}
    >
      <Database className={`w-4 h-4 ${importType === 'develancache' ? 'icon-purple' : 'icon-blue'}`} />
      <span className="text-themed-secondary font-medium">{selectedImportTypeLabel}</span>
    </div>
  );

  return (
    <Card>
      <ManagerCardHeader
        icon={Upload}
        iconColor={importType === 'develancache' ? 'purple' : 'blue'}
        title="Import Historical Data"
        subtitle="Import from external SQLite databases"
        helpContent={helpContent}
        actions={headerActions}
      />

      {mockMode && (
        <Alert color="yellow" className="mb-4">
          Mock mode is enabled - import functionality is disabled
        </Alert>
      )}

      <div className="space-y-4">
        {/* Import Type Dropdown */}
        <div>
          <label className="block text-sm font-medium text-themed-primary mb-2">
            Database Type
          </label>
          <EnhancedDropdown
            options={importTypeOptions}
            value={importType}
            onChange={(value) => {
              setImportType(value as ImportType);
              setValidationResult(null);
              setImportResult(null);
            }}
            disabled={mockMode || !isAuthenticated || importing}
          />
        </div>

        {/* Mode Toggle */}
        <div className="flex items-center gap-4">
          <div
            className="flex-1 h-[2px] rounded-full"
            style={{
              background: 'repeating-linear-gradient(90deg, var(--theme-border-secondary) 0px, var(--theme-border-secondary) 4px, transparent 4px, transparent 8px)'
            }}
          />
          <div
            className="inline-flex items-center gap-1 p-1 rounded-lg"
            style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
          >
            <button
              onClick={() => setInputMode('auto')}
              disabled={mockMode || !isAuthenticated}
              className="px-4 py-1.5 rounded-md text-sm font-medium transition-all duration-200 disabled:opacity-50 disabled:cursor-not-allowed"
              style={{
                backgroundColor: inputMode === 'auto' ? 'var(--theme-primary)' : 'transparent',
                color: inputMode === 'auto' ? 'var(--theme-button-text)' : 'var(--theme-text-secondary)'
              }}
            >
              Auto
            </button>
            <button
              onClick={() => setInputMode('browse')}
              disabled={mockMode || !isAuthenticated}
              className="px-4 py-1.5 rounded-md text-sm font-medium transition-all duration-200 disabled:opacity-50 disabled:cursor-not-allowed"
              style={{
                backgroundColor: inputMode === 'browse' ? 'var(--theme-primary)' : 'transparent',
                color: inputMode === 'browse' ? 'var(--theme-button-text)' : 'var(--theme-text-secondary)'
              }}
            >
              Browse
            </button>
            <button
              onClick={() => setInputMode('manual')}
              disabled={mockMode || !isAuthenticated}
              className="px-4 py-1.5 rounded-md text-sm font-medium transition-all duration-200 disabled:opacity-50 disabled:cursor-not-allowed"
              style={{
                backgroundColor: inputMode === 'manual' ? 'var(--theme-primary)' : 'transparent',
                color: inputMode === 'manual' ? 'var(--theme-button-text)' : 'var(--theme-text-secondary)'
              }}
            >
              Manual
            </button>
          </div>
          <div
            className="flex-1 h-[2px] rounded-full"
            style={{
              background: 'repeating-linear-gradient(90deg, var(--theme-border-secondary) 0px, var(--theme-border-secondary) 4px, transparent 4px, transparent 8px)'
            }}
          />
        </div>

        {/* Auto Mode - Found Databases */}
        {inputMode === 'auto' && (
          <div className="space-y-3">
            <div className="flex items-center justify-between">
              <p className="text-sm text-themed-secondary">
                {autoSearching ? 'Searching for databases...' : `Found ${foundDatabases.length} database(s)`}
              </p>
              <Button
                onClick={searchForDatabases}
                disabled={autoSearching || mockMode || !isAuthenticated}
                variant="subtle"
                size="sm"
              >
                {autoSearching ? (
                  <Loader2 className="w-4 h-4 animate-spin" />
                ) : (
                  <RefreshCw className="w-4 h-4" />
                )}
              </Button>
            </div>

            {autoSearching ? (
              <LoadingState message="Searching for databases..." />
            ) : foundDatabases.length > 0 ? (
              <div
                className="rounded-lg border overflow-hidden"
                style={{ borderColor: 'var(--theme-border-secondary)' }}
              >
                <div className="max-h-[200px] overflow-y-auto custom-scrollbar">
                  {foundDatabases.map((item, index) => (
                    <button
                      key={index}
                      onClick={() => handleAutoSelect(item)}
                      className={`w-full px-3 py-2.5 flex items-center gap-3 transition-all text-left border-b last:border-b-0
                        hover:bg-themed-hover cursor-pointer
                        ${connectionString === item.path ? 'bg-themed-accent-subtle ring-1 ring-inset ring-themed-accent' : ''}
                      `}
                      style={{ borderColor: 'var(--theme-border-secondary)' }}
                    >
                      <div className="flex-shrink-0 w-9 h-9 rounded-lg flex items-center justify-center icon-bg-green">
                        <Database className="w-4 h-4 icon-green" />
                      </div>
                      <div className="flex-1 min-w-0">
                        <div className="font-medium text-themed-primary truncate text-sm">{item.name}</div>
                        <div className="text-xs text-themed-muted mt-0.5 truncate">{item.path}</div>
                      </div>
                      <div className="text-xs text-themed-muted flex-shrink-0">
                        {formatSize(item.size)}
                      </div>
                    </button>
                  ))}
                </div>
              </div>
            ) : (
              <EmptyState
                icon={Search}
                title="No database files found"
                subtitle="Try using Browse or Manual mode"
              />
            )}
          </div>
        )}

        {/* Browse Mode */}
        {inputMode === 'browse' && (
          <FileBrowser
            onSelectFile={handleFileSelect}
            isAuthenticated={isAuthenticated}
            mockMode={mockMode}
          />
        )}

        {/* Manual Mode */}
        {inputMode === 'manual' && (
          <div>
            <label className="block text-sm font-medium text-themed-primary mb-2">
              Database File Path
            </label>
            <input
              type="text"
              value={connectionString}
              onChange={(e) => {
                setConnectionString(e.target.value);
                setValidationResult(null);
                setImportResult(null);
              }}
              placeholder="/mnt/import/lancache.db"
              className="w-full px-3 py-2 rounded-lg transition-colors
                       bg-themed-secondary text-themed-primary
                       border border-themed-secondary focus:border-themed-focus
                       placeholder:text-themed-muted"
              disabled={mockMode || !isAuthenticated}
            />
            <p className="text-xs text-themed-muted mt-1">
              Example: <code className="bg-themed-tertiary px-1 py-0.5 rounded">/path/to/database.db</code>
            </p>
          </div>
        )}

        {/* Validation Success */}
        {validationResult?.valid && (
          <div
            className="flex items-center gap-3 p-3 rounded-lg"
            style={{
              backgroundColor: 'var(--theme-success-bg)',
              border: '1px solid var(--theme-success)'
            }}
          >
            <CheckCircle2 className="w-5 h-5 flex-shrink-0" style={{ color: 'var(--theme-success)' }} />
            <div>
              <p className="font-medium text-themed-success">Connection Valid</p>
              <p className="text-sm text-themed-secondary">
                Found {validationResult.recordCount?.toLocaleString()} records ready to import
              </p>
            </div>
          </div>
        )}

        {/* Validation Error */}
        {validationResult && !validationResult.valid && (
          <Alert color="red">
            <div>
              <p className="font-medium">Validation Failed</p>
              <p className="text-sm mt-1">{validationResult.message}</p>
              {validationResult.message.includes('DownloadEvents') && (
                <p className="text-xs mt-2 opacity-80">
                  Make sure to select a DeveLanCacheUI_Backend database for this import type.
                </p>
              )}
              {validationResult.message.includes('Downloads') && (
                <p className="text-xs mt-2 opacity-80">
                  Make sure to select a LancacheManager database for this import type.
                </p>
              )}
            </div>
          </Alert>
        )}

        {/* Advanced Options */}
        <div className="p-4 bg-themed-tertiary/30 rounded-lg space-y-4">
          <div>
            <label className="block text-sm font-medium text-themed-primary mb-2">
              Batch Size
            </label>
            <div className="number-input-wrapper">
              <input
                type="number"
                value={batchSize}
                onChange={(e) => setBatchSize(parseInt(e.target.value) || 1000)}
                min="100"
                max="10000"
                step="100"
                className="w-full px-3 py-2 rounded-lg transition-colors
                         bg-themed-secondary text-themed-primary
                         border border-themed-secondary focus:border-themed-focus"
                disabled={mockMode || !isAuthenticated}
              />
              <div className="spinner-buttons">
                <button
                  type="button"
                  className="spinner-btn up"
                  onClick={() => setBatchSize(Math.min(10000, batchSize + 100))}
                  disabled={mockMode || !isAuthenticated}
                  aria-label="Increase batch size"
                >
                  <ChevronUp />
                </button>
                <button
                  type="button"
                  className="spinner-btn down"
                  onClick={() => setBatchSize(Math.max(100, batchSize - 100))}
                  disabled={mockMode || !isAuthenticated}
                  aria-label="Decrease batch size"
                >
                  <ChevronDown />
                </button>
              </div>
            </div>
            <p className="text-xs text-themed-muted mt-1">
              Number of records to process at once (100-10000)
            </p>
          </div>

          <div>
            <Checkbox
              checked={overwriteExisting}
              onChange={(e) => setOverwriteExisting(e.target.checked)}
              label="Update existing records (merge mode)"
              disabled={mockMode || !isAuthenticated}
            />
            <p className="text-xs text-themed-muted mt-1 ml-6">
              {overwriteExisting ? 'Sync mode: update existing + add new' : 'Append only: skip duplicates'}
            </p>
          </div>
        </div>

        {/* Import Progress */}
        {importing && (
          <div className="space-y-2">
            <div className="flex items-center justify-between text-sm">
              <span className="text-themed-secondary">Importing...</span>
              <span className="text-themed-muted">{Math.round(importProgress)}%</span>
            </div>
            <div
              className="h-2 rounded-full overflow-hidden"
              style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
            >
              <div
                className="h-full rounded-full transition-all duration-300"
                style={{
                  width: `${importProgress}%`,
                  backgroundColor: 'var(--theme-icon-green)'
                }}
              />
            </div>
          </div>
        )}

        {/* Import Result */}
        {importResult && (
          <div
            className="p-4 rounded-lg"
            style={{
              backgroundColor: importResult.errors > 0 ? 'var(--theme-warning-bg)' : 'var(--theme-success-bg)',
              border: `1px solid ${importResult.errors > 0 ? 'var(--theme-warning)' : 'var(--theme-success)'}`
            }}
          >
            <p
              className="font-medium mb-3"
              style={{ color: importResult.errors > 0 ? 'var(--theme-warning-text)' : 'var(--theme-success-text)' }}
            >
              {importResult.message}
            </p>
            <div className="grid grid-cols-2 md:grid-cols-4 gap-3 text-sm mb-3">
              <div>
                <span className="text-themed-muted">Total:</span>{' '}
                <span className="font-medium text-themed-primary">{importResult.totalRecords.toLocaleString()}</span>
              </div>
              <div>
                <span className="text-themed-muted">Imported:</span>{' '}
                <span className="font-medium text-themed-success">
                  {importResult.imported.toLocaleString()}
                </span>
              </div>
              <div>
                <span className="text-themed-muted">Skipped:</span>{' '}
                <span className="font-medium text-themed-warning">
                  {importResult.skipped.toLocaleString()}
                </span>
              </div>
              <div>
                <span className="text-themed-muted">Errors:</span>{' '}
                <span className="font-medium text-themed-error">
                  {importResult.errors.toLocaleString()}
                </span>
              </div>
            </div>
            {importResult.backupPath && !importResult.backupPath.includes('(no backup') && (
              <div
                className="pt-3 border-t"
                style={{ borderColor: importResult.errors > 0 ? 'var(--theme-warning)' : 'var(--theme-success)' }}
              >
                <p className="text-xs text-themed-muted mb-1">Database backup created:</p>
                <p className="text-xs font-mono text-themed-secondary bg-themed-tertiary px-2 py-1 rounded break-all">
                  {importResult.backupPath}
                </p>
              </div>
            )}
          </div>
        )}

        {/* Action Buttons */}
        <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
          <Button
            onClick={handleValidate}
            disabled={mockMode || !isAuthenticated || validating || !connectionString.trim()}
            loading={validating}
            variant="default"
            fullWidth
          >
            {validating ? 'Validating...' : validationResult?.valid ? 'Re-validate' : 'Validate Connection'}
          </Button>

          <Button
            onClick={handleImportClick}
            disabled={
              mockMode ||
              !isAuthenticated ||
              importing ||
              !validationResult?.valid
            }
            loading={importing}
            variant="filled"
            color="green"
            fullWidth
          >
            {importing ? (
              <>
                <Loader2 className="w-4 h-4 animate-spin" />
                Importing...
              </>
            ) : (
              `Import ${validationResult?.recordCount?.toLocaleString() || ''} Records`
            )}
          </Button>
        </div>

        {!isAuthenticated && (
          <Alert color="yellow">
            Authentication required to import data
          </Alert>
        )}
      </div>

      {/* Confirmation Modal */}
      <Modal
        opened={showConfirmModal}
        onClose={() => setShowConfirmModal(false)}
        title="Confirm Import"
        size="md"
      >
        <div className="space-y-4">
          <div className="flex items-start space-x-3">
            <AlertTriangle className="w-5 h-5 icon-yellow flex-shrink-0 mt-0.5" />
            <div className="flex-1">
              <p className="text-themed-primary font-medium mb-2">
                Import {validationResult?.recordCount?.toLocaleString()} records from external database?
              </p>
              <div className="text-sm text-themed-muted space-y-1">
                {overwriteExisting ? (
                  <>
                    <p className="font-medium text-themed-warning">Merge/Sync Mode:</p>
                    <ul className="list-disc list-inside space-y-0.5 ml-2">
                      <li>New records will be added</li>
                      <li>Existing records will be updated with new data</li>
                    </ul>
                  </>
                ) : (
                  <>
                    <p className="font-medium text-themed-success">Append-Only Mode:</p>
                    <ul className="list-disc list-inside space-y-0.5 ml-2">
                      <li>New records will be added</li>
                      <li>Existing records will be skipped (no changes)</li>
                    </ul>
                  </>
                )}
                <p className="text-xs mt-2 italic">
                  Duplicates detected by: Client IP + Start Time (UTC)
                </p>
              </div>
            </div>
          </div>

          <div className="flex justify-end space-x-3 pt-4 border-t border-themed-secondary">
            <Button
              onClick={() => setShowConfirmModal(false)}
              variant="outline"
            >
              Cancel
            </Button>
            <Button
              onClick={handleConfirmImport}
              variant="filled"
              color="green"
            >
              Import
            </Button>
          </div>
        </div>
      </Modal>
    </Card>
  );
};

export default DataImporter;
