import React, { useState, useEffect } from 'react';
import { Database, SkipForward, Loader2, CheckCircle, XCircle } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { Checkbox } from '@components/ui/Checkbox';
import ApiService from '@services/api.service';
import FileBrowser from '@components/management/FileBrowser';
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
    // Restore from localStorage
    return storage.getItem('importConnectionString') || '';
  });
  const [batchSize, setBatchSize] = useState(() => {
    // Restore from localStorage
    const stored = storage.getItem('importBatchSize');
    return stored ? parseInt(stored) : 1000;
  });
  const [overwriteExisting, setOverwriteExisting] = useState(() => {
    // Restore from localStorage
    return storage.getItem('importOverwriteExisting') === 'true';
  });
  const [validating, setValidating] = useState(false);
  const [importing, setImporting] = useState(false);
  const [validationResult, setValidationResult] = useState<ValidationResult | null>(null);
  const [importResult, setImportResult] = useState<ImportResult | null>(null);
  const [useBrowser, setUseBrowser] = useState(true);

  // Persist state to localStorage
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
      setValidationResult({
        valid: false,
        message: 'Please enter a database path'
      });
      return;
    }

    setValidating(true);
    setValidationResult(null);

    try {
      const res = await fetch(
        `/api/migration/validate-connection?connectionString=${encodeURIComponent(connectionString)}`,
        ApiService.getFetchOptions({
          method: 'GET'
        })
      );

      const result = await ApiService.handleResponse<ValidationResult>(res);
      setValidationResult(result);
    } catch (error: any) {
      setValidationResult({
        valid: false,
        message: error.message || 'Failed to validate connection'
      });
    } finally {
      setValidating(false);
    }
  };

  const handleImport = async () => {
    if (!validationResult?.valid) {
      return;
    }

    setImporting(true);
    setImportResult(null);

    try {
      const res = await fetch(
        '/api/migration/import-develancache',
        ApiService.getFetchOptions({
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            connectionString,
            batchSize,
            overwriteExisting
          })
        })
      );

      const result = await ApiService.handleResponse<ImportResult>(res);
      setImportResult(result);

      // After successful import, wait 2 seconds then move to next step
      setTimeout(() => {
        onComplete();
      }, 2000);
    } catch (error: any) {
      setValidationResult({
        valid: false,
        message: 'Import failed: ' + error.message
      });
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
    <>
      <p className="text-themed-secondary text-center mb-6">
        Do you have historical data from DeveLanCacheUI_Backend or another compatible system you'd
        like to import?
      </p>

      {/* Info Box */}
      <div
        className="mb-6 p-4 rounded-lg"
        style={{
          backgroundColor: 'var(--theme-info-bg)',
          borderColor: 'var(--theme-info)',
          color: 'var(--theme-info-text)'
        }}
      >
        <p className="text-sm mb-2">
          <strong>What is this?</strong> Import download history from external sources to maintain
          all your historical data in one place.
        </p>
        <p className="text-sm">
          <strong>Supported:</strong> SQLite databases from DeveLanCacheUI_Backend and other
          compatible lancache management tools.
        </p>
      </div>

      {/* Success Message */}
      {importResult && (
        <div
          className="mb-6 p-4 rounded-lg"
          style={{
            backgroundColor: 'var(--theme-success-bg)',
            color: 'var(--theme-success-text)'
          }}
        >
          <p className="font-medium mb-3 flex items-center gap-2">
            <CheckCircle className="w-4 h-4" />
            {importResult.message}
          </p>
          <div className="grid grid-cols-2 gap-3 text-sm">
            <div>
              <span className="opacity-80">Total:</span>{' '}
              <span className="font-medium">{importResult.totalRecords.toLocaleString()}</span>
            </div>
            <div>
              <span className="opacity-80">Imported:</span>{' '}
              <span className="font-medium">{importResult.imported.toLocaleString()}</span>
            </div>
            <div>
              <span className="opacity-80">Skipped:</span>{' '}
              <span className="font-medium">{importResult.skipped.toLocaleString()}</span>
            </div>
            <div>
              <span className="opacity-80">Errors:</span>{' '}
              <span className="font-medium">{importResult.errors.toLocaleString()}</span>
            </div>
          </div>
        </div>
      )}

      {/* Browse/Manual Toggle */}
      <div className="flex items-center gap-4 mb-4">
        <div className="flex-1 h-px bg-themed-border" />
        <div className="flex items-center gap-2">
          <Button
            onClick={() => setUseBrowser(true)}
            size="xs"
            variant={useBrowser ? 'filled' : 'default'}
            color="blue"
            disabled={importing || !!importResult}
          >
            Browse
          </Button>
          <Button
            onClick={() => setUseBrowser(false)}
            size="xs"
            variant={!useBrowser ? 'filled' : 'default'}
            color="blue"
            disabled={importing || !!importResult}
          >
            Manual
          </Button>
        </div>
        <div className="flex-1 h-px bg-themed-border" />
      </div>

      {/* File Browser or Manual Input */}
      {useBrowser ? (
        <div className="mb-4">
          <FileBrowser
            onSelectFile={handleFileSelect}
            isAuthenticated={true}
            mockMode={false}
          />
        </div>
      ) : (
        <div className="mb-4">
          <label className="block text-sm font-medium text-themed-primary mb-2">
            Database File Path
          </label>
          <input
            type="text"
            value={connectionString}
            onChange={(e) => {
              setConnectionString(e.target.value);
              setValidationResult(null);
            }}
            placeholder="/path/to/lancache.db or C:\path\to\lancache.db"
            className="w-full px-3 py-2 rounded-lg themed-input"
            disabled={importing || !!importResult}
          />
          <p className="text-xs text-themed-muted mt-1">
            Enter the full path to your DeveLanCacheUI_Backend database file
          </p>
        </div>
      )}

      {/* Advanced Options */}
      <div
        className="mb-4 p-4 rounded-lg space-y-4"
        style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
      >
        <div>
          <label className="block text-sm font-medium text-themed-primary mb-2">Batch Size</label>
          <input
            type="number"
            value={batchSize}
            onChange={(e) => setBatchSize(parseInt(e.target.value) || 1000)}
            min="100"
            max="10000"
            step="100"
            className="w-full px-3 py-2 rounded-lg themed-input"
            disabled={importing || !!importResult}
          />
          <p className="text-xs text-themed-muted mt-1">
            Number of records to process at once (100-10000)
          </p>
        </div>

        <div>
          <Checkbox
            checked={overwriteExisting}
            onChange={(e) => setOverwriteExisting(e.target.checked)}
            label="Update existing records (merge mode)"
            disabled={importing || !!importResult}
          />
          <p className="text-xs text-themed-muted mt-1 ml-6">
            {overwriteExisting
              ? 'Existing records will be updated with new data. New records will be added (merge/sync mode).'
              : 'Only new records will be added. Existing records will be skipped (append-only mode).'}
          </p>
        </div>
      </div>

      {/* Validation Result */}
      {validationResult && !validationResult.valid && (
        <div
          className="mb-4 p-3 rounded-lg flex items-start gap-3"
          style={{
            backgroundColor: 'var(--theme-error-bg)',
            borderColor: 'var(--theme-error)'
          }}
        >
          <XCircle className="w-5 h-5 flex-shrink-0 mt-0.5" style={{ color: 'var(--theme-error)' }} />
          <p className="text-sm" style={{ color: 'var(--theme-error-text)' }}>
            {validationResult.message}
          </p>
        </div>
      )}

      {validationResult && validationResult.valid && (
        <div
          className="mb-4 p-3 rounded-lg flex items-start gap-3"
          style={{
            backgroundColor: 'var(--theme-success-bg)',
            borderColor: 'var(--theme-success)'
          }}
        >
          <CheckCircle
            className="w-5 h-5 flex-shrink-0 mt-0.5"
            style={{ color: 'var(--theme-success)' }}
          />
          <p className="text-sm" style={{ color: 'var(--theme-success-text)' }}>
            {validationResult.message}
            {validationResult.recordCount !== undefined &&
              ` Found ${validationResult.recordCount.toLocaleString()} records.`}
          </p>
        </div>
      )}

      {/* Action Buttons */}
      {!importResult ? (
        <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
          <Button
            onClick={handleValidate}
            disabled={validating || !connectionString.trim() || importing}
            leftSection={validating ? <Loader2 className="w-4 h-4 animate-spin" /> : undefined}
            variant="default"
            fullWidth
          >
            {validating ? 'Validating...' : 'Validate'}
          </Button>

          <Button
            onClick={handleImport}
            disabled={!validationResult?.valid || importing}
            leftSection={
              importing ? (
                <Loader2 className="w-4 h-4 animate-spin" />
              ) : (
                <Database className="w-4 h-4" />
              )
            }
            variant="filled"
            color="green"
            fullWidth
          >
            {importing ? 'Importing...' : 'Import Data'}
          </Button>

          <Button
            onClick={onSkip}
            disabled={importing}
            leftSection={<SkipForward className="w-4 h-4" />}
            variant="default"
            fullWidth
          >
            Skip
          </Button>
        </div>
      ) : null}
    </>
  );
};
