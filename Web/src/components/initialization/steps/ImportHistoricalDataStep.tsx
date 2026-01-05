import React, { useState, useEffect, useCallback } from 'react';
import { Database, Loader2, CheckCircle, XCircle, FolderOpen, RefreshCw, Search } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { Checkbox } from '@components/ui/Checkbox';
import ApiService from '@services/api.service';
import FileBrowser from '@components/features/management/file-browser/FileBrowser';
import { storage } from '@utils/storage';

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
      setValidationResult({ valid: false, message: 'Please enter a database path' });
      return;
    }

    setValidating(true);
    setValidationResult(null);

    try {
      const res = await fetch(
        `/api/migration/validate-connection?connectionString=${encodeURIComponent(connectionString)}`,
        ApiService.getFetchOptions({ method: 'GET' })
      );
      const result = await ApiService.handleResponse<ValidationResult>(res);
      setValidationResult(result);
    } catch (error: unknown) {
      setValidationResult({ valid: false, message: (error instanceof Error ? error.message : String(error)) || 'Failed to validate connection' });
    } finally {
      setValidating(false);
    }
  };

  const handleImport = async () => {
    if (!validationResult?.valid) return;

    setImporting(true);
    setImportResult(null);

    try {
      const res = await fetch(
        '/api/migration/import-develancache',
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
      setValidationResult({ valid: false, message: 'Import failed: ' + (error instanceof Error ? error.message : String(error)) });
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
          className="w-14 h-14 rounded-full flex items-center justify-center mb-3"
          style={{ backgroundColor: 'var(--theme-info-bg)' }}
        >
          <Database className="w-7 h-7" style={{ color: 'var(--theme-info)' }} />
        </div>
        <h3 className="text-lg font-semibold text-themed-primary mb-1">Import Historical Data</h3>
        <p className="text-sm text-themed-secondary max-w-md">
          Import download history from DeveLanCacheUI_Backend or compatible systems
        </p>
      </div>

      {/* Success Message */}
      {importResult && (
        <div
          className="p-4 rounded-lg"
          style={{ backgroundColor: 'var(--theme-success-bg)' }}
        >
          <p className="font-medium mb-2 flex items-center gap-2" style={{ color: 'var(--theme-success-text)' }}>
            <CheckCircle className="w-4 h-4" />
            {importResult.message}
          </p>
          <div className="grid grid-cols-2 gap-2 text-sm" style={{ color: 'var(--theme-success-text)' }}>
            <div>Total: <strong>{importResult.totalRecords.toLocaleString()}</strong></div>
            <div>Imported: <strong>{importResult.imported.toLocaleString()}</strong></div>
            <div>Skipped: <strong>{importResult.skipped.toLocaleString()}</strong></div>
            <div>Errors: <strong>{importResult.errors.toLocaleString()}</strong></div>
          </div>
        </div>
      )}

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
          Auto
        </Button>
        <Button
          onClick={() => setInputMode('browse')}
          size="xs"
          variant={inputMode === 'browse' ? 'filled' : 'default'}
          color="blue"
          disabled={importing || !!importResult}
        >
          <FolderOpen className="w-3 h-3 mr-1" />
          Browse
        </Button>
        <Button
          onClick={() => setInputMode('manual')}
          size="xs"
          variant={inputMode === 'manual' ? 'filled' : 'default'}
          color="blue"
          disabled={importing || !!importResult}
        >
          Manual Path
        </Button>
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
            <div
              className="flex items-center justify-center py-8 rounded-lg"
              style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
            >
              <Loader2 className="w-5 h-5 animate-spin text-themed-secondary mr-2" />
              <span className="text-sm text-themed-secondary">Searching for databases...</span>
            </div>
          ) : foundDatabases.length > 0 ? (
            <div
              className="rounded-lg border overflow-hidden"
              style={{ borderColor: 'var(--theme-border-secondary)' }}
            >
              <div className="max-h-[180px] overflow-y-auto custom-scrollbar">
                {foundDatabases.map((item, index) => (
                  <button
                    key={index}
                    onClick={() => handleAutoSelect(item)}
                    disabled={importing || !!importResult}
                    className={`w-full px-3 py-2.5 flex items-center gap-3 transition-all text-left border-b last:border-b-0
                      hover:bg-themed-hover cursor-pointer disabled:opacity-50 disabled:cursor-not-allowed
                      ${connectionString === item.path ? 'bg-themed-accent-subtle ring-1 ring-inset ring-themed-accent' : ''}
                    `}
                    style={{ borderColor: 'var(--theme-border-secondary)' }}
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
            <div
              className="flex flex-col items-center justify-center py-6 rounded-lg"
              style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
            >
              <Search className="w-8 h-8 text-themed-muted mb-2" />
              <p className="text-sm text-themed-secondary font-medium">No database files found</p>
              <p className="text-xs text-themed-muted mt-1">Try using Browse or Manual mode</p>
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
            Database File Path
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
        <div
          className="p-3 rounded-lg flex items-center gap-2"
          style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
        >
          <Database className="w-4 h-4 text-themed-secondary" />
          <span className="text-sm text-themed-primary font-medium truncate flex-1">
            {connectionString}
          </span>
        </div>
      )}

      {/* Advanced Options */}
      <div
        className="p-4 rounded-lg space-y-3"
        style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
      >
        <div>
          <label className="block text-sm font-medium text-themed-secondary mb-1.5">Batch Size</label>
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
            label="Update existing records (merge mode)"
            disabled={importing || !!importResult}
          />
          <p className="text-xs text-themed-muted mt-1 ml-6">
            {overwriteExisting ? 'Existing records will be updated.' : 'Only new records will be added.'}
          </p>
        </div>
      </div>

      {/* Validation Result */}
      {validationResult && (
        <div
          className="p-3 rounded-lg flex items-start gap-3"
          style={{
            backgroundColor: validationResult.valid ? 'var(--theme-success-bg)' : 'var(--theme-error-bg)'
          }}
        >
          {validationResult.valid ? (
            <CheckCircle className="w-5 h-5 flex-shrink-0" style={{ color: 'var(--theme-success)' }} />
          ) : (
            <XCircle className="w-5 h-5 flex-shrink-0" style={{ color: 'var(--theme-error)' }} />
          )}
          <div
            className="text-sm"
            style={{ color: validationResult.valid ? 'var(--theme-success-text)' : 'var(--theme-error-text)' }}
          >
            <p>
              {validationResult.message}
              {validationResult.recordCount != null && ` Found ${validationResult.recordCount.toLocaleString()} records.`}
            </p>
            {!validationResult.valid && validationResult.message.includes('DownloadEvents') && (
              <p className="mt-1 text-xs opacity-80">
                Make sure to select a database from DeveLanCacheUI_Backend.
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
            {validating ? 'Validating...' : 'Validate'}
          </Button>

          <Button
            variant="filled"
            color="green"
            onClick={handleImport}
            disabled={!validationResult?.valid || importing}
            className="flex-1"
          >
            {importing && <Loader2 className="w-4 h-4 animate-spin mr-2" />}
            {importing ? 'Importing...' : 'Import'}
          </Button>

          <Button variant="default" onClick={onSkip} disabled={importing} className="flex-1">
            Skip
          </Button>
        </div>
      )}
    </div>
  );
};
