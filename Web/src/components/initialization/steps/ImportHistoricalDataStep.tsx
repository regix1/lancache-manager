import React, { useState, useEffect } from 'react';
import { Database, Loader2, CheckCircle, XCircle, FolderOpen } from 'lucide-react';
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
  const [useBrowser, setUseBrowser] = useState(true);

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
    setUseBrowser(false);
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
          onClick={() => setUseBrowser(true)}
          size="xs"
          variant={useBrowser ? 'filled' : 'default'}
          color="blue"
          disabled={importing || !!importResult}
        >
          <FolderOpen className="w-3 h-3 mr-1" />
          Browse
        </Button>
        <Button
          onClick={() => setUseBrowser(false)}
          size="xs"
          variant={!useBrowser ? 'filled' : 'default'}
          color="blue"
          disabled={importing || !!importResult}
        >
          Manual Path
        </Button>
      </div>

      {/* File Selection */}
      {useBrowser ? (
        <FileBrowser onSelectFile={handleFileSelect} isAuthenticated={true} mockMode={false} />
      ) : (
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
          <p
            className="text-sm"
            style={{ color: validationResult.valid ? 'var(--theme-success-text)' : 'var(--theme-error-text)' }}
          >
            {validationResult.message}
            {validationResult.recordCount !== undefined && ` Found ${validationResult.recordCount.toLocaleString()} records.`}
          </p>
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
