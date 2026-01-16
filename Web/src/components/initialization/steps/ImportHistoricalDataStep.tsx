import React, { useState, useEffect, useCallback } from 'react';
import { Database, Loader2, CheckCircle, XCircle, FolderOpen, RefreshCw, Search } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import { Checkbox } from '@components/ui/Checkbox';
import { EnhancedDropdown, type DropdownOption } from '@components/ui/EnhancedDropdown';
import ApiService from '@services/api.service';
import FileBrowser from '@components/features/management/file-browser/FileBrowser';
import { storage } from '@utils/storage';

type ImportType = 'develancache' | 'lancache-manager';

interface ImportHistoricalDataStepProps {
  onComplete: () => void;
  onSkip: () => void;
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

export const ImportHistoricalDataStep: React.FC<ImportHistoricalDataStepProps> = ({
  onComplete,
  onSkip
}) => {
  const { t } = useTranslation();
  
  const importTypeOptions: DropdownOption[] = [
    {
      value: 'develancache',
      label: 'DeveLanCacheUI_Backend',
      description: t('initialization.importHistorical.deveLanCacheDesc')
    },
    {
      value: 'lancache-manager',
      label: 'LancacheManager',
      description: t('initialization.importHistorical.lancacheManagerDesc')
    }
  ];
  
  const [importType, setImportType] = useState<ImportType>(() => {
    const stored = storage.getItem('importType');
    return (stored === 'lancache-manager' ? stored : 'develancache') as ImportType;
  });
  const [connectionString, setConnectionString] = useState(() => {
    return storage.getItem('importConnectionString') || '';
  });
  const [batchSize, setBatchSize] = useState(() => {
    const stored = storage.getItem('importBatchSize');
    return stored ? parseInt(stored) : 1000;
  });
  const [overwriteExisting, setOverwriteExisting] = useState(() => {
    return storage.getItem('importOverwriteExisting') === 'true';
  });
  const [validating, setValidating] = useState(false);
  const [importing, setImporting] = useState(false);
  const [validationResult, setValidationResult] = useState<ValidationResult | null>(null);
  const [importResult, setImportResult] = useState<ImportResult | null>(null);
  const [inputMode, setInputMode] = useState<InputMode>('auto');
  const [autoSearching, setAutoSearching] = useState(false);
  const [foundDatabases, setFoundDatabases] = useState<FileSystemItem[]>([]);

  useEffect(() => {
    storage.setItem('importType', importType);
  }, [importType]);

  useEffect(() => {
    if (connectionString) {
      storage.setItem('importConnectionString', connectionString);
    } else {
      storage.removeItem('importConnectionString');
    }
  }, [connectionString]);

  useEffect(() => {
    storage.setItem('importBatchSize', batchSize.toString());
  }, [batchSize]);

  useEffect(() => {
    storage.setItem('importOverwriteExisting', overwriteExisting.toString());
  }, [overwriteExisting]);

  // Auto-search for databases when in auto mode
  const searchForDatabases = useCallback(async () => {
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
  }, []);

  // Trigger search when switching to auto mode
  useEffect(() => {
    if (inputMode === 'auto' && foundDatabases.length === 0) {
      searchForDatabases();
    }
  }, [inputMode, foundDatabases.length, searchForDatabases]);

  const handleValidate = async () => {
    if (!connectionString.trim()) {
      setValidationResult({ valid: false, message: t('initialization.importHistorical.pleaseEnter') });
      return;
    }

    setValidating(true);
    setValidationResult(null);

    try {
      const res = await fetch(
        `/api/migration/validate-connection?connectionString=${encodeURIComponent(connectionString)}&importType=${importType}`,
        ApiService.getFetchOptions({ method: 'GET' })
      );
      const result = await ApiService.handleResponse<ValidationResult>(res);
      setValidationResult(result);
    } catch (error: unknown) {
      setValidationResult({ valid: false, message: (error instanceof Error ? error.message : String(error)) || t('initialization.importHistorical.failedToValidate') });
    } finally {
      setValidating(false);
    }
  };

  const handleImport = async () => {
    if (!validationResult?.valid) return;

    setImporting(true);
    setImportResult(null);

    const endpoint = importType === 'lancache-manager'
      ? '/api/migration/import-lancache-manager'
      : '/api/migration/import-develancache';

    try {
      const res = await fetch(
        endpoint,
        ApiService.getFetchOptions({
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ connectionString, batchSize, overwriteExisting })
        })
      );
      const result = await ApiService.handleResponse<ImportResult>(res);
      setImportResult(result);
      setTimeout(() => onComplete(), 2000);
    } catch (error: unknown) {
      setValidationResult({ valid: false, message: t('initialization.importHistorical.importFailed', { error: error instanceof Error ? error.message : String(error) }) });
    } finally {
      setImporting(false);
    }
  };

  const handleFileSelect = (path: string) => {
    setConnectionString(path);
    setValidationResult(null);
    setInputMode('manual');
  };

  const handleAutoSelect = (item: FileSystemItem) => {
    setConnectionString(item.path);
    setValidationResult(null);
  };

  const formatSize = (bytes: number): string => {
    if (bytes === 0) return '-';
    const units = ['B', 'KB', 'MB', 'GB'];
    const i = Math.floor(Math.log(bytes) / Math.log(1024));
    return `${(bytes / Math.pow(1024, i)).toFixed(1)} ${units[i]}`;
  };

  return (
    <div className="space-y-5">
      {/* Header */}
      <div className="flex flex-col items-center text-center">
        <div
          className={`w-14 h-14 rounded-full flex items-center justify-center mb-3 ${
            importType === 'develancache' ? 'bg-themed-info' : 'bg-themed-primary-subtle'
          }`}
        >
          <Database className={`w-7 h-7 ${importType === 'develancache' ? 'icon-info' : 'icon-primary'}`} />
        </div>
        <h3 className="text-lg font-semibold text-themed-primary mb-1">{t('initialization.importHistorical.title')}</h3>
        <p className="text-sm text-themed-secondary max-w-md">
          {t('initialization.importHistorical.subtitle')}
        </p>
      </div>

      {/* Skip Notice */}
      <div className="p-3 rounded-lg text-center text-sm bg-themed-info text-themed-info">
        {t('initialization.importHistorical.skipNotice')}
      </div>

      {/* Success Message */}
      {importResult && (
        <div className="p-4 rounded-lg bg-themed-success">
          <p className="font-medium mb-2 flex items-center gap-2 text-themed-success">
            <CheckCircle className="w-4 h-4" />
            {importResult.message}
          </p>
          <div className="grid grid-cols-2 gap-2 text-sm text-themed-success">
            <div>{t('initialization.importHistorical.total', {
              count: importResult.totalRecords,
              formattedCount: importResult.totalRecords.toLocaleString()
            })}</div>
            <div>{t('initialization.importHistorical.imported', {
              count: importResult.imported,
              formattedCount: importResult.imported.toLocaleString()
            })}</div>
            <div>{t('initialization.importHistorical.skipped', {
              count: importResult.skipped,
              formattedCount: importResult.skipped.toLocaleString()
            })}</div>
            <div>{t('initialization.importHistorical.errors', {
              count: importResult.errors,
              formattedCount: importResult.errors.toLocaleString()
            })}</div>
          </div>
        </div>
      )}

      {/* Import Type Dropdown */}
      <div>
        <label className="block text-sm font-medium text-themed-secondary mb-1.5">
          {t('initialization.importHistorical.databaseType')}
        </label>
        <EnhancedDropdown
          options={importTypeOptions}
          value={importType}
          onChange={(value) => {
            setImportType(value as ImportType);
            setValidationResult(null);
          }}
          disabled={importing || !!importResult}
        />
      </div>

      {/* Mode Toggle */}
      <div className="flex items-center justify-center gap-2">
        <Button
          onClick={() => setInputMode('auto')}
          size="xs"
          variant={inputMode === 'auto' ? 'filled' : 'default'}
          color="blue"
          disabled={importing || !!importResult}
        >
          <Search className="w-3 h-3 mr-1" />
          {t('initialization.importHistorical.auto')}
        </Button>
        <Button
          onClick={() => setInputMode('browse')}
          size="xs"
          variant={inputMode === 'browse' ? 'filled' : 'default'}
          color="blue"
          disabled={importing || !!importResult}
        >
          <FolderOpen className="w-3 h-3 mr-1" />
          {t('initialization.importHistorical.browse')}
        </Button>
        <Button
          onClick={() => setInputMode('manual')}
          size="xs"
          variant={inputMode === 'manual' ? 'filled' : 'default'}
          color="blue"
          disabled={importing || !!importResult}
        >
          {t('initialization.importHistorical.manualPath')}
        </Button>
      </div>

      {/* Auto Mode - Found Databases */}
      {inputMode === 'auto' && (
        <div className="space-y-3">
          <div className="flex items-center justify-between">
            <p className="text-sm text-themed-secondary">
              {autoSearching ? t('initialization.importHistorical.searching') : t('initialization.importHistorical.foundDatabases', { count: foundDatabases.length })}
            </p>
            <Button
              onClick={searchForDatabases}
              disabled={autoSearching || importing || !!importResult}
              variant="subtle"
              size="xs"
            >
              {autoSearching ? (
                <Loader2 className="w-3 h-3 animate-spin" />
              ) : (
                <RefreshCw className="w-3 h-3" />
              )}
            </Button>
          </div>

          {autoSearching ? (
            <div className="flex items-center justify-center py-8 rounded-lg bg-themed-tertiary">
              <Loader2 className="w-5 h-5 animate-spin text-themed-secondary mr-2" />
              <span className="text-sm text-themed-secondary">{t('initialization.importHistorical.searchingStatus')}</span>
            </div>
          ) : foundDatabases.length > 0 ? (
            <div className="rounded-lg border overflow-hidden border-themed-secondary">
              <div className="max-h-[180px] overflow-y-auto custom-scrollbar">
                {foundDatabases.map((item, index) => (
                  <button
                    key={index}
                    onClick={() => handleAutoSelect(item)}
                    disabled={importing || !!importResult}
                    className={`w-full px-3 py-2.5 flex items-center gap-3 transition-all text-left border-b last:border-b-0
                      hover:bg-themed-hover cursor-pointer disabled:opacity-50 disabled:cursor-not-allowed border-themed-secondary
                      ${connectionString === item.path ? 'bg-themed-accent-subtle ring-1 ring-inset ring-themed-accent' : ''}
                    `}
                  >
                    <div className="flex-shrink-0 w-8 h-8 rounded-lg flex items-center justify-center icon-bg-green">
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
            <div className="flex flex-col items-center justify-center py-6 rounded-lg bg-themed-tertiary">
              <Search className="w-8 h-8 text-themed-muted mb-2" />
              <p className="text-sm text-themed-secondary font-medium">{t('initialization.importHistorical.noDatabasesFound')}</p>
              <p className="text-xs text-themed-muted mt-1">{t('initialization.importHistorical.tryOtherModes')}</p>
            </div>
          )}
        </div>
      )}

      {/* Browse Mode */}
      {inputMode === 'browse' && (
        <FileBrowser onSelectFile={handleFileSelect} isAuthenticated={true} mockMode={false} />
      )}

      {/* Manual Mode */}
      {inputMode === 'manual' && (
        <div>
          <label className="block text-sm font-medium text-themed-secondary mb-1.5">
            {t('initialization.importHistorical.databasePath')}
          </label>
          <input
            type="text"
            value={connectionString}
            onChange={(e) => {
              setConnectionString(e.target.value);
              setValidationResult(null);
            }}
            placeholder="/path/to/lancache.db"
            className="w-full px-3 py-2.5 themed-input"
            disabled={importing || !!importResult}
          />
        </div>
      )}

      {/* Selected Database Display (for auto mode) */}
      {inputMode === 'auto' && connectionString && (
        <div className="p-3 rounded-lg flex items-center gap-2 bg-themed-tertiary">
          <Database className="w-4 h-4 text-themed-secondary" />
          <span className="text-sm text-themed-primary font-medium truncate flex-1">
            {connectionString}
          </span>
        </div>
      )}

      {/* Advanced Options */}
      <div className="p-4 rounded-lg space-y-3 bg-themed-tertiary">
        <div>
          <label className="block text-sm font-medium text-themed-secondary mb-1.5">{t('initialization.importHistorical.batchSize')}</label>
          <input
            type="number"
            value={batchSize}
            onChange={(e) => setBatchSize(parseInt(e.target.value) || 1000)}
            min="100"
            max="10000"
            step="100"
            className="w-full px-3 py-2 themed-input"
            disabled={importing || !!importResult}
          />
        </div>
        <div>
          <Checkbox
            checked={overwriteExisting}
            onChange={(e) => setOverwriteExisting(e.target.checked)}
            label={t('initialization.importHistorical.updateExisting')}
            disabled={importing || !!importResult}
          />
          <p className="text-xs text-themed-muted mt-1 ml-6">
            {overwriteExisting ? t('initialization.importHistorical.updateExistingNote') : t('initialization.importHistorical.addNewOnly')}
          </p>
        </div>
      </div>

      {/* Validation Result */}
      {validationResult && (
        <div
          className={`p-3 rounded-lg flex items-start gap-3 ${
            validationResult.valid ? 'bg-themed-success' : 'bg-themed-error'
          }`}
        >
          {validationResult.valid ? (
            <CheckCircle className="w-5 h-5 flex-shrink-0 icon-success" />
          ) : (
            <XCircle className="w-5 h-5 flex-shrink-0 icon-error" />
          )}
          <div className={`text-sm ${validationResult.valid ? 'text-themed-success' : 'text-themed-error'}`}>
            <p>
              {validationResult.message}
              {validationResult.recordCount != null && ` ${t('initialization.importHistorical.foundRecords', {
                count: validationResult.recordCount ?? 0,
                formattedCount: (validationResult.recordCount ?? 0).toLocaleString()
              })}`}
            </p>
            {!validationResult.valid && validationResult.message.includes('DownloadEvents') && (
              <p className="mt-1 text-xs opacity-80">
                {t('initialization.importHistorical.checkDeveLanCache')}
              </p>
            )}
            {!validationResult.valid && validationResult.message.includes('Downloads') && (
              <p className="mt-1 text-xs opacity-80">
                {t('initialization.importHistorical.checkLancacheManager')}
              </p>
            )}
          </div>
        </div>
      )}

      {/* Action Buttons */}
      {!importResult && (
        <div className="flex gap-3">
          <Button
            variant="default"
            onClick={handleValidate}
            disabled={validating || !connectionString.trim() || importing}
            className="flex-1"
          >
            {validating && <Loader2 className="w-4 h-4 animate-spin mr-2" />}
            {validating ? t('initialization.importHistorical.validating') : t('initialization.importHistorical.validate')}
          </Button>

          <Button
            variant="filled"
            color="green"
            onClick={handleImport}
            disabled={!validationResult?.valid || importing}
            className="flex-1"
          >
            {importing && <Loader2 className="w-4 h-4 animate-spin mr-2" />}
            {importing ? t('initialization.importHistorical.importing') : t('initialization.importHistorical.import')}
          </Button>

          <Button variant="default" onClick={onSkip} disabled={importing} className="flex-1">
            {t('initialization.importHistorical.skip')}
          </Button>
        </div>
      )}
    </div>
  );
};
