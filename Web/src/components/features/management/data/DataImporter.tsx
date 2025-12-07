import React, { useState } from 'react';
import { Database, AlertTriangle, ChevronUp, ChevronDown } from 'lucide-react';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { Checkbox } from '@components/ui/Checkbox';
import { Modal } from '@components/ui/Modal';
import { HelpPopover, HelpCode, HelpSection, HelpNote, HelpKeyword } from '@components/ui/HelpPopover';
import ApiService from '@services/api.service';
import FileBrowser from '../file-browser/FileBrowser';

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

const DataImporter: React.FC<DataImporterProps> = ({
  isAuthenticated,
  mockMode,
  onError,
  onSuccess,
  onDataRefresh
}) => {
  const [connectionString, setConnectionString] = useState('');
  const [batchSize, setBatchSize] = useState(1000);
  const [overwriteExisting, setOverwriteExisting] = useState(false);
  const [validating, setValidating] = useState(false);
  const [importing, setImporting] = useState(false);
  const [validationResult, setValidationResult] = useState<ValidationResult | null>(null);
  const [importResult, setImportResult] = useState<ImportResult | null>(null);
  const [useBrowser, setUseBrowser] = useState(true);
  const [showConfirmModal, setShowConfirmModal] = useState(false);

  const handleValidate = async () => {
    if (!connectionString.trim()) {
      onError?.('Please enter a connection string');
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

      if (result.valid) {
        onSuccess?.(`Connection validated! Found ${result.recordCount} records.`);
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

    try {
      const res = await fetch('/api/migration/import-develancache', ApiService.getFetchOptions({
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
    // Pass raw path - controller now accepts both formats
    setConnectionString(path);
    setValidationResult(null);
    setUseBrowser(false);
    onSuccess?.(`Selected database: ${path}`);
  };

  return (
    <Card>
      <div className="flex items-center justify-between mb-4">
        <div className="flex items-center gap-3">
          <div className="w-10 h-10 rounded-lg flex items-center justify-center icon-bg-cyan">
            <Database className="w-5 h-5 icon-cyan" />
          </div>
          <div className="flex items-center gap-2">
            <h3 className="text-lg font-semibold text-themed-primary">
              Import Historical Data
            </h3>
            <HelpPopover position="left" width={380} maxHeight="420px">
              <HelpSection title="Compatibility">
                Supports <HelpKeyword color="cyan">SQLite</HelpKeyword> databases from{' '}
                <HelpKeyword color="purple">DeveLanCacheUI_Backend</HelpKeyword> and other compatible tools.
              </HelpSection>

              <HelpSection title="Docker Setup" variant="subtle">
                <p className="mb-2">Mount your external database as a volume:</p>
                <HelpCode block>
                  volumes:<br />
                  &nbsp;&nbsp;- ./data:/data<br />
                  &nbsp;&nbsp;- /path/to/data:/mnt/import:ro
                </HelpCode>
                <p className="mt-2">
                  <HelpKeyword color="green">:ro</HelpKeyword> = read-only mount for safety.
                  Browse to <HelpCode>/mnt/import/lancache.db</HelpCode>
                </p>
              </HelpSection>

              <HelpSection title="Manual Input">
                Paste the file path directly or use a connection string format.
              </HelpSection>

              <HelpNote type="warning">
                Stop the external application before importing to avoid database locks.
              </HelpNote>
            </HelpPopover>
          </div>
        </div>
      </div>

      <p className="text-themed-muted text-sm mb-4">
        Import download history from external sources to maintain all your historical data in one place
      </p>

      {mockMode && (
        <Alert color="yellow" className="mb-4">
          Mock mode is enabled - import functionality is disabled
        </Alert>
      )}

      <div className="space-y-4">
        {/* Browse/Manual Toggle */}
        <div className="flex items-center gap-4">
          <div
            className="flex-1 h-[2px] rounded-full"
            style={{
              background: 'repeating-linear-gradient(90deg, var(--theme-border-secondary) 0px, var(--theme-border-secondary) 4px, transparent 4px, transparent 8px)'
            }}
          />
          <div className="flex items-center gap-2">
            <Button
              onClick={() => setUseBrowser(true)}
              size="xs"
              variant={useBrowser ? 'filled' : 'default'}
              color="blue"
              disabled={mockMode || !isAuthenticated}
            >
              Browse
            </Button>
            <Button
              onClick={() => setUseBrowser(false)}
              size="xs"
              variant={!useBrowser ? 'filled' : 'default'}
              color="blue"
              disabled={mockMode || !isAuthenticated}
            >
              Manual
            </Button>
          </div>
        </div>

        {/* File Browser or Manual Input */}
        {useBrowser ? (
          <FileBrowser
            onSelectFile={handleFileSelect}
            isAuthenticated={isAuthenticated}
            mockMode={mockMode}
          />
        ) : (
          <div>
            <label className="block text-sm font-medium text-themed-primary mb-2">
              Database File Path
            </label>
            <input
              type="text"
              value={connectionString}
              onChange={(e) => setConnectionString(e.target.value)}
              placeholder="/mnt/import/lancache.db"
              className="w-full px-3 py-2 rounded-lg transition-colors
                       bg-themed-secondary text-themed-primary
                       border border-themed-secondary focus:border-themed-focus
                       placeholder:text-themed-muted"
              disabled={mockMode || !isAuthenticated}
            />
            <p className="text-xs text-themed-muted mt-1">
              Accepts file path or connection string:{' '}
              <code className="bg-themed-tertiary px-1 py-0.5 rounded">/path/to/database.db</code>
            </p>
          </div>
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
              {overwriteExisting
                ? 'Existing records will be updated with new data. New records will be added (merge/sync mode).'
                : 'Only new records will be added. Existing records will be skipped (append-only mode).'}
            </p>
          </div>
        </div>

        {/* Import Result */}
        {importResult && (
          <div className="p-4 rounded-lg" style={{ backgroundColor: 'var(--theme-success-bg)', borderWidth: '1px', borderColor: 'var(--theme-success)' }}>
            <p className="font-medium text-themed-success mb-3">{importResult.message}</p>
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
              <div className="pt-3 border-t" style={{ borderColor: 'var(--theme-success)' }}>
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
            {validating ? 'Validating...' : 'Validate Connection'}
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
            {importing ? 'Importing...' : 'Import Data'}
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
              variant="default"
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
