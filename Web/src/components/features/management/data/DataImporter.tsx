import React, { useState, useEffect, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
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
  const { t } = useTranslation();
  const [importType, setImportType] = useState<ImportType>('develancache');

  const importTypeOptions: DropdownOption[] = [
    {
      value: 'develancache',
      label: 'DeveLanCacheUI_Backend',
      description: t('management.dataImporter.importTypes.deveLanCache.description')
    },
    {
      value: 'lancache-manager',
      label: 'LancacheManager',
      description: t('management.dataImporter.importTypes.lancacheManager.description')
    }
  ];
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
      onError?.(t('management.dataImporter.errors.enterConnectionString'));
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
        const recordCount = result.recordCount ?? 0;
        onSuccess?.(t('management.dataImporter.messages.connectionValidated', {
          count: recordCount,
          formattedCount: recordCount.toLocaleString()
        }));
      } else {
        onError?.(result.message);
      }
    } catch (error: unknown) {
      const errorMsg = error instanceof Error ? error.message : String(error);
      onError?.(t('management.dataImporter.errors.validateFailed') + errorMsg);
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
      onError?.(t('management.dataImporter.errors.validateFirst'));
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
        t('management.dataImporter.messages.importCompleted', {
          imported: result.imported,
          skipped: result.skipped,
          errors: result.errors
        })
      );

      if (onDataRefresh) {
        setTimeout(() => onDataRefresh(), 1000);
      }
    } catch (error: unknown) {
      onError?.(t('management.dataImporter.errors.importFailed') + (error instanceof Error ? error.message : String(error)));
    } finally {
      setImporting(false);
    }
  };

  const handleFileSelect = (path: string) => {
    setConnectionString(path);
    setValidationResult(null);
    setImportResult(null);
    onSuccess?.(t('management.dataImporter.messages.selectedDatabase', { path }));
  };

  const handleAutoSelect = (item: FileSystemItem) => {
    setConnectionString(item.path);
    setValidationResult(null);
    setImportResult(null);
    onSuccess?.(t('management.dataImporter.messages.selectedDatabase', { path: item.path }));
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
      <HelpSection title={t('management.dataImporter.help.importTypes.title')}>
        <div className="space-y-1.5">
          <HelpDefinition term={t('management.dataImporter.help.importTypes.deveLanCache.term')} termColor="purple">
            {t('management.dataImporter.help.importTypes.deveLanCache.description')}
          </HelpDefinition>
          <HelpDefinition term={t('management.dataImporter.help.importTypes.lancacheManager.term')} termColor="blue">
            {t('management.dataImporter.help.importTypes.lancacheManager.description')}
          </HelpDefinition>
        </div>
      </HelpSection>

      <HelpSection title={t('management.dataImporter.help.inputMethods.title')}>
        <div className="space-y-1.5">
          <HelpDefinition term={t('management.dataImporter.help.inputMethods.browse.term')} termColor="blue">
            {t('management.dataImporter.help.inputMethods.browse.description')}
          </HelpDefinition>
          <HelpDefinition term={t('management.dataImporter.help.inputMethods.manual.term')} termColor="green">
            {t('management.dataImporter.help.inputMethods.manual.description')}
          </HelpDefinition>
        </div>
      </HelpSection>

      <HelpSection title={t('management.dataImporter.help.compatibility.title')} variant="subtle">
        {t('management.dataImporter.help.compatibility.description')}
      </HelpSection>

      <HelpNote type="warning">
        {t('management.dataImporter.help.warning')}
      </HelpNote>
    </HelpPopover>
  );

  // Get the selected import type label for display
  const selectedImportTypeLabel = importTypeOptions.find(o => o.value === importType)?.label || t('management.dataImporter.importTypes.unknown');

  // Header actions - compatibility badge
  const headerActions = (
    <div className="hidden sm:flex items-center gap-2 px-3 py-1.5 rounded-lg text-sm bg-themed-tertiary border border-themed-secondary">
      <Database className={`w-4 h-4 ${importType === 'develancache' ? 'icon-purple' : 'icon-blue'}`} />
      <span className="text-themed-secondary font-medium">{selectedImportTypeLabel}</span>
    </div>
  );

  return (
    <Card>
      <ManagerCardHeader
        icon={Upload}
        iconColor={importType === 'develancache' ? 'purple' : 'blue'}
        title={t('management.dataImporter.title')}
        subtitle={t('management.dataImporter.subtitle')}
        helpContent={helpContent}
        actions={headerActions}
      />

      {mockMode && (
        <Alert color="yellow" className="mb-4">
          {t('management.dataImporter.alerts.mockMode')}
        </Alert>
      )}

      <Alert color="blue" className="mb-4">
        {t('management.dataImporter.alerts.skipInfo')}
      </Alert>

      <div className="space-y-4">
        {/* Import Type Dropdown */}
        <div>
          <label className="block text-sm font-medium text-themed-primary mb-2">
            {t('management.dataImporter.databaseType')}
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
          <div className="flex-1 h-[2px] rounded-full divider-dashed" />
          <div className="inline-flex items-center gap-1 p-1 rounded-lg bg-themed-tertiary">
            <button
              onClick={() => setInputMode('auto')}
              disabled={mockMode || !isAuthenticated}
              className={`px-4 py-1.5 rounded-md text-sm font-medium transition-all duration-200 disabled:opacity-50 disabled:cursor-not-allowed ${
                inputMode === 'auto' ? 'toggle-btn-active' : 'toggle-btn-inactive'
              }`}
            >
              {t('management.dataImporter.modes.auto')}
            </button>
            <button
              onClick={() => setInputMode('browse')}
              disabled={mockMode || !isAuthenticated}
              className={`px-4 py-1.5 rounded-md text-sm font-medium transition-all duration-200 disabled:opacity-50 disabled:cursor-not-allowed ${
                inputMode === 'browse' ? 'toggle-btn-active' : 'toggle-btn-inactive'
              }`}
            >
              {t('management.dataImporter.modes.browse')}
            </button>
            <button
              onClick={() => setInputMode('manual')}
              disabled={mockMode || !isAuthenticated}
              className={`px-4 py-1.5 rounded-md text-sm font-medium transition-all duration-200 disabled:opacity-50 disabled:cursor-not-allowed ${
                inputMode === 'manual' ? 'toggle-btn-active' : 'toggle-btn-inactive'
              }`}
            >
              {t('management.dataImporter.modes.manual')}
            </button>
          </div>
          <div className="flex-1 h-[2px] rounded-full divider-dashed" />
        </div>

        {/* Auto Mode - Found Databases */}
        {inputMode === 'auto' && (
          <div className="space-y-3">
            <div className="flex items-center justify-between">
              <p className="text-sm text-themed-secondary">
                {autoSearching ? t('management.dataImporter.auto.searching') : t('management.dataImporter.auto.found', { count: foundDatabases.length })}
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
              <LoadingState message={t('management.dataImporter.auto.searching')} />
            ) : foundDatabases.length > 0 ? (
              <div className="rounded-lg border overflow-hidden border-themed-secondary">
                <div className="max-h-[200px] overflow-y-auto custom-scrollbar">
                  {foundDatabases.map((item, index) => (
                    <button
                      key={index}
                      onClick={() => handleAutoSelect(item)}
                      className={`w-full px-3 py-2.5 flex items-center gap-3 transition-all text-left border-b last:border-b-0
                        hover:bg-themed-hover cursor-pointer border-themed-secondary
                        ${connectionString === item.path ? 'bg-themed-accent-subtle ring-1 ring-inset ring-themed-accent' : ''}
                      `}
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
                title={t('management.dataImporter.emptyState.title')}
                subtitle={t('management.dataImporter.emptyState.subtitle')}
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
              {t('management.dataImporter.manual.label')}
            </label>
            <input
              type="text"
              value={connectionString}
              onChange={(e) => {
                setConnectionString(e.target.value);
                setValidationResult(null);
                setImportResult(null);
              }}
              placeholder={t('management.dataImporter.placeholders.manualPath')}
              className="w-full px-3 py-2 rounded-lg transition-colors
                       bg-themed-secondary text-themed-primary
                       border border-themed-secondary focus:border-themed-focus
                       placeholder:text-themed-muted"
              disabled={mockMode || !isAuthenticated}
            />
            <p className="text-xs text-themed-muted mt-1">
              {t('management.dataImporter.manual.example')} <code className="bg-themed-tertiary px-1 py-0.5 rounded">/path/to/database.db</code>
            </p>
          </div>
        )}

        {/* Validation Success */}
        {validationResult?.valid && (
          <div className="flex items-center gap-3 p-3 rounded-lg bg-themed-success border border-success">
            <CheckCircle2 className="w-5 h-5 flex-shrink-0 icon-success" />
            <div>
              <p className="font-medium text-themed-success">{t('management.dataImporter.validation.valid')}</p>
              <p className="text-sm text-themed-secondary">
                {t('management.dataImporter.validation.foundRecords', {
                  count: validationResult.recordCount ?? 0,
                  formattedCount: (validationResult.recordCount ?? 0).toLocaleString()
                })}
              </p>
            </div>
          </div>
        )}

        {/* Validation Error */}
        {validationResult && !validationResult.valid && (
          <Alert color="red">
            <div>
              <p className="font-medium">{t('management.dataImporter.validation.failed')}</p>
              <p className="text-sm mt-1">{validationResult.message}</p>
              {validationResult.message.includes('DownloadEvents') && (
                <p className="text-xs mt-2 opacity-80">
                  {t('management.dataImporter.validation.hintDeveLanCache')}
                </p>
              )}
              {validationResult.message.includes('Downloads') && (
                <p className="text-xs mt-2 opacity-80">
                  {t('management.dataImporter.validation.hintLancacheManager')}
                </p>
              )}
            </div>
          </Alert>
        )}

        {/* Advanced Options */}
        <div className="p-4 bg-themed-tertiary/30 rounded-lg space-y-4">
          <div>
            <label className="block text-sm font-medium text-themed-primary mb-2">
              {t('management.dataImporter.options.batchSize')}
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
                  aria-label={t('management.dataImporter.aria.increaseBatchSize')}
                >
                  <ChevronUp />
                </button>
                <button
                  type="button"
                  className="spinner-btn down"
                  onClick={() => setBatchSize(Math.max(100, batchSize - 100))}
                  disabled={mockMode || !isAuthenticated}
                  aria-label={t('management.dataImporter.aria.decreaseBatchSize')}
                >
                  <ChevronDown />
                </button>
              </div>
            </div>
            <p className="text-xs text-themed-muted mt-1">
              {t('management.dataImporter.options.batchSizeHint')}
            </p>
          </div>

          <div>
            <Checkbox
              checked={overwriteExisting}
              onChange={(e) => setOverwriteExisting(e.target.checked)}
              label={t('management.dataImporter.options.overwriteExisting')}
              disabled={mockMode || !isAuthenticated}
            />
            <p className="text-xs text-themed-muted mt-1 ml-6">
              {overwriteExisting ? t('management.dataImporter.options.syncMode') : t('management.dataImporter.options.appendMode')}
            </p>
          </div>
        </div>

        {/* Import Progress */}
        {importing && (
          <div className="space-y-2">
            <div className="flex items-center justify-between text-sm">
              <span className="text-themed-secondary">{t('management.dataImporter.progress.importing')}</span>
              <span className="text-themed-muted">{Math.round(importProgress)}%</span>
            </div>
            <div className="h-2 rounded-full overflow-hidden bg-themed-tertiary">
              <div
                className="h-full rounded-full transition-all duration-300 progress-bar-green"
                style={{ width: `${importProgress}%` }}
              />
            </div>
          </div>
        )}

        {/* Import Result */}
        {importResult && (
          <div
            className={`p-4 rounded-lg border ${
              importResult.errors > 0 ? 'bg-themed-warning border-warning' : 'bg-themed-success border-success'
            }`}
          >
            <p
              className={`font-medium mb-3 ${importResult.errors > 0 ? 'text-themed-warning' : 'text-themed-success'}`}
            >
              {importResult.message}
            </p>
            <div className="grid grid-cols-2 md:grid-cols-4 gap-3 text-sm mb-3">
              <div>
                <span className="text-themed-muted">{t('management.dataImporter.result.total')}:</span>{' '}
                <span className="font-medium text-themed-primary">{importResult.totalRecords.toLocaleString()}</span>
              </div>
              <div>
                <span className="text-themed-muted">{t('management.dataImporter.result.imported')}:</span>{' '}
                <span className="font-medium text-themed-success">
                  {importResult.imported.toLocaleString()}
                </span>
              </div>
              <div>
                <span className="text-themed-muted">{t('management.dataImporter.result.skipped')}:</span>{' '}
                <span className="font-medium text-themed-warning">
                  {importResult.skipped.toLocaleString()}
                </span>
              </div>
              <div>
                <span className="text-themed-muted">{t('management.dataImporter.result.errors')}:</span>{' '}
                <span className="font-medium text-themed-error">
                  {importResult.errors.toLocaleString()}
                </span>
              </div>
            </div>
            {importResult.backupPath && !importResult.backupPath.includes('(no backup') && (
              <div
                className={`pt-3 border-t ${importResult.errors > 0 ? 'border-warning' : 'border-success'}`}
              >
                <p className="text-xs text-themed-muted mb-1">{t('management.dataImporter.result.backupCreated')}:</p>
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
            {validating ? t('management.dataImporter.buttons.validating') : validationResult?.valid ? t('management.dataImporter.buttons.revalidate') : t('management.dataImporter.buttons.validate')}
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
                {t('management.dataImporter.buttons.importing')}
              </>
            ) : (
              t('management.dataImporter.buttons.importRecords', {
                count: validationResult?.recordCount ?? 0,
                formattedCount: (validationResult?.recordCount ?? 0).toLocaleString()
              })
            )}
          </Button>
        </div>

        {!isAuthenticated && (
          <Alert color="yellow">
            {t('management.dataImporter.alerts.authRequired')}
          </Alert>
        )}
      </div>

      {/* Confirmation Modal */}
      <Modal
        opened={showConfirmModal}
        onClose={() => setShowConfirmModal(false)}
        title={t('management.dataImporter.confirmImport')}
        size="md"
      >
        <div className="space-y-4">
          <div className="flex items-start space-x-3">
            <AlertTriangle className="w-5 h-5 icon-yellow flex-shrink-0 mt-0.5" />
            <div className="flex-1">
              <p className="text-themed-primary font-medium mb-2">
                {t('management.dataImporter.confirm.importQuestion', {
                  count: validationResult?.recordCount ?? 0,
                  formattedCount: (validationResult?.recordCount ?? 0).toLocaleString()
                })}
              </p>
              <div className="text-sm text-themed-muted space-y-1">
                {overwriteExisting ? (
                  <>
                    <p className="font-medium text-themed-warning">{t('management.dataImporter.confirm.mergeMode')}:</p>
                    <ul className="list-disc list-inside space-y-0.5 ml-2">
                      <li>{t('management.dataImporter.confirm.newRecordsAdded')}</li>
                      <li>{t('management.dataImporter.confirm.existingUpdated')}</li>
                    </ul>
                  </>
                ) : (
                  <>
                    <p className="font-medium text-themed-success">{t('management.dataImporter.confirm.appendMode')}:</p>
                    <ul className="list-disc list-inside space-y-0.5 ml-2">
                      <li>{t('management.dataImporter.confirm.newRecordsAdded')}</li>
                      <li>{t('management.dataImporter.confirm.existingSkipped')}</li>
                    </ul>
                  </>
                )}
                <p className="text-xs mt-2 italic">
                  {t('management.dataImporter.confirm.duplicateDetection')}
                </p>
              </div>
            </div>
          </div>

          <div className="flex justify-end space-x-3 pt-4 border-t border-themed-secondary">
            <Button
              onClick={() => setShowConfirmModal(false)}
              variant="outline"
            >
              {t('common.cancel')}
            </Button>
            <Button
              onClick={handleConfirmImport}
              variant="filled"
              color="green"
            >
              {t('management.dataImporter.buttons.import')}
            </Button>
          </div>
        </div>
      </Modal>
    </Card>
  );
};

export default DataImporter;
