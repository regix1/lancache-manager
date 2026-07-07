import { useState } from 'react';
import { CheckCircle, XCircle, ChevronDown, ChevronUp } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import { Checkbox } from '@components/ui/Checkbox';
import { Alert } from '@components/ui/Alert';
import { CollapsibleRegion } from '@components/ui/CollapsibleRegion';
import ApiService from '@services/api.service';
import { formatCount } from '@utils/formatters';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { getErrorMessage } from '@utils/error';
import type { ImportResult, ValidationResult, PostgresConnectionConfig } from '@/types/migration';
import './DatabaseImportForm.css';

interface DatabaseImportFormProps {
  onImportComplete: (result: ImportResult) => void;
  onSkip?: () => void;
  showSkipButton?: boolean;
  className?: string;
}

export function DatabaseImportForm({
  onImportComplete,
  onSkip,
  showSkipButton = false,
  className = ''
}: DatabaseImportFormProps) {
  const { t } = useTranslation();

  const [pgConfig, setPgConfig] = useState<PostgresConnectionConfig>({
    host: 'localhost',
    port: 5432,
    database: 'lancache',
    username: 'lancache',
    password: ''
  });
  const [showRawConnectionString, setShowRawConnectionString] = useState(false);
  const [rawConnectionString, setRawConnectionString] = useState('');
  const [batchSize, setBatchSize] = useState(1000);
  const [overwriteExisting, setOverwriteExisting] = useState(false);
  const [showAdvanced, setShowAdvanced] = useState(false);
  const [validating, setValidating] = useState(false);
  const [importing, setImporting] = useState(false);
  const [validationResult, setValidationResult] = useState<ValidationResult | null>(null);
  const [importResult, setImportResult] = useState<ImportResult | null>(null);

  const getEffectiveConnectionString = (): string => {
    if (showRawConnectionString) return rawConnectionString;
    const { host, port, database, username, password } = pgConfig;
    return `Host=${host};Port=${port};Database=${database};Username=${username};Password=${password}`;
  };

  const handleValidate = async () => {
    const cs = getEffectiveConnectionString().trim();
    if (!cs) {
      setValidationResult({
        valid: false,
        message: t('initialization.importHistorical.pleaseEnter')
      });
      return;
    }

    setValidating(true);
    setValidationResult(null);

    try {
      const result = await ApiService.validateMigrationConnection(cs);
      setValidationResult(result);
    } catch (error: unknown) {
      setValidationResult({
        valid: false,
        message: getErrorMessage(error) || t('initialization.importHistorical.failedToValidate')
      });
    } finally {
      setValidating(false);
    }
  };

  const handleImport = async () => {
    if (!validationResult?.valid) return;

    setImporting(true);
    setImportResult(null);

    const cs = getEffectiveConnectionString();

    try {
      const result = await ApiService.importFromLancacheManager(cs, batchSize, overwriteExisting);
      setImportResult(result);
      onImportComplete(result);
    } catch (error: unknown) {
      setValidationResult({
        valid: false,
        message: t('initialization.importHistorical.importFailed', {
          error: getErrorMessage(error)
        })
      });
    } finally {
      setImporting(false);
    }
  };

  const isDisabled = importing || !!importResult;
  const hasValidInput = getEffectiveConnectionString().trim().length > 0;

  return (
    <div className={`database-import-form ${className}`}>
      {/* Lancache Manager form */}
      <div className="database-import-form__postgres-fields">
        {!showRawConnectionString && (
          <>
            <div className="database-import-form__field-row">
              <div className="database-import-form__field">
                <label className="block text-sm font-medium text-themed-secondary">
                  {t('initialization.importHistorical.host')}
                </label>
                <input
                  type="text"
                  value={pgConfig.host}
                  onChange={(e) => {
                    setPgConfig((prev) => ({ ...prev, host: e.target.value }));
                    setValidationResult(null);
                  }}
                  placeholder="localhost"
                  className="w-full px-3 py-2.5 themed-input"
                  disabled={isDisabled}
                />
              </div>
              <div className="database-import-form__field">
                <label className="block text-sm font-medium text-themed-secondary">
                  {t('initialization.importHistorical.port')}
                </label>
                <input
                  type="number"
                  value={pgConfig.port}
                  onChange={(e) => {
                    setPgConfig((prev) => ({ ...prev, port: parseInt(e.target.value) || 5432 }));
                    setValidationResult(null);
                  }}
                  placeholder="5432"
                  className="w-full px-3 py-2.5 themed-input"
                  disabled={isDisabled}
                />
              </div>
            </div>

            <div className="database-import-form__field">
              <label className="block text-sm font-medium text-themed-secondary">
                {t('initialization.importHistorical.database')}
              </label>
              <input
                type="text"
                value={pgConfig.database}
                onChange={(e) => {
                  setPgConfig((prev) => ({ ...prev, database: e.target.value }));
                  setValidationResult(null);
                }}
                placeholder="lancache"
                className="w-full px-3 py-2.5 themed-input"
                disabled={isDisabled}
              />
            </div>

            <div className="database-import-form__field-row">
              <div className="database-import-form__field">
                <label className="block text-sm font-medium text-themed-secondary">
                  {t('initialization.importHistorical.username')}
                </label>
                <input
                  type="text"
                  value={pgConfig.username}
                  onChange={(e) => {
                    setPgConfig((prev) => ({ ...prev, username: e.target.value }));
                    setValidationResult(null);
                  }}
                  placeholder="postgres"
                  className="w-full px-3 py-2.5 themed-input"
                  disabled={isDisabled}
                />
              </div>
              <div className="database-import-form__field">
                <label className="block text-sm font-medium text-themed-secondary">
                  {t('initialization.importHistorical.password')}
                </label>
                <input
                  type="password"
                  value={pgConfig.password}
                  onChange={(e) => {
                    setPgConfig((prev) => ({ ...prev, password: e.target.value }));
                    setValidationResult(null);
                  }}
                  placeholder="••••••••"
                  className="w-full px-3 py-2.5 themed-input"
                  disabled={isDisabled}
                />
              </div>
            </div>
          </>
        )}

        {showRawConnectionString && (
          <div className="database-import-form__field">
            <label className="block text-sm font-medium text-themed-secondary">
              {t('initialization.importHistorical.connectionString')}
            </label>
            <input
              type="text"
              value={rawConnectionString}
              onChange={(e) => {
                setRawConnectionString(e.target.value);
                setValidationResult(null);
              }}
              placeholder="Host=localhost;Port=5432;Database=lancache;Username=postgres;Password=..."
              className="w-full px-3 py-2.5 themed-input"
              disabled={isDisabled}
            />
          </div>
        )}

        <button
          type="button"
          onClick={() => setShowRawConnectionString((prev) => !prev)}
          className="text-xs text-themed-muted hover:text-themed-secondary transition-colors text-left"
          disabled={isDisabled}
        >
          {showRawConnectionString
            ? t('initialization.importHistorical.useFields')
            : t('initialization.importHistorical.useConnectionString')}
        </button>
      </div>

      {/* Advanced Options */}
      <div className="database-import-form__advanced-toggle border border-themed-secondary">
        <button
          onClick={() => setShowAdvanced(!showAdvanced)}
          className="database-import-form__advanced-toggle-btn text-themed-secondary bg-themed-tertiary hover:bg-themed-hover"
        >
          <span>{t('initialization.importHistorical.advancedOptions')}</span>
          {showAdvanced ? <ChevronUp className="w-4 h-4" /> : <ChevronDown className="w-4 h-4" />}
        </button>
        <CollapsibleRegion
          open={showAdvanced}
          contentClassName="database-import-form__advanced-content bg-themed-secondary"
        >
          <div className="flex items-center gap-2">
            <label className="text-sm text-themed-secondary whitespace-nowrap">
              {t('initialization.importHistorical.batchSize')}:
            </label>
            <input
              type="number"
              value={batchSize}
              onChange={(e) => setBatchSize(parseInt(e.target.value) || 1000)}
              min="100"
              max="10000"
              step="100"
              className="w-24 px-2 py-1 themed-input text-sm"
              disabled={isDisabled}
            />
          </div>
          <Checkbox
            checked={overwriteExisting}
            onChange={(e) => setOverwriteExisting(e.target.checked)}
            label={t('initialization.importHistorical.updateExisting')}
            disabled={isDisabled}
          />
        </CollapsibleRegion>
      </div>

      {/* Validation Result */}
      {validationResult && (
        <div
          className={`p-3 rounded-lg flex items-start gap-3 ${validationResult.valid ? 'bg-themed-success' : 'bg-themed-error'}`}
        >
          {validationResult.valid ? (
            <CheckCircle className="w-5 h-5 flex-shrink-0 icon-success" />
          ) : (
            <XCircle className="w-5 h-5 flex-shrink-0 icon-error" />
          )}
          <div
            className={`text-sm ${validationResult.valid ? 'text-themed-success' : 'text-themed-error'}`}
          >
            <p>
              {validationResult.message}
              {validationResult.recordCount != null &&
                ` ${t('initialization.importHistorical.foundRecords', {
                  count: validationResult.recordCount ?? 0,
                  formattedCount: formatCount(validationResult.recordCount ?? 0)
                })}`}
            </p>
          </div>
        </div>
      )}

      {/* Import in Progress */}
      {importing && (
        <Alert color="blue">
          <div className="flex items-center gap-2">
            <LoadingSpinner inline size="sm" />
            <span>
              {t('initialization.importHistorical.importing')} -{' '}
              {t('initialization.importHistorical.checkNotifications')}
            </span>
          </div>
        </Alert>
      )}

      {/* Import Result */}
      {importResult && (
        <div
          className={`p-4 rounded-lg border ${
            importResult.errors > 0
              ? 'bg-themed-warning border-warning'
              : 'bg-themed-success border-success'
          }`}
        >
          <p
            className={`font-medium mb-3 ${importResult.errors > 0 ? 'text-themed-warning' : 'text-themed-success'}`}
          >
            {importResult.message}
          </p>
          <div className="database-import-form__import-result-grid">
            <div>
              <span className="text-themed-muted">
                {t('initialization.importHistorical.total', {
                  count: importResult.totalRecords,
                  formattedCount: formatCount(importResult.totalRecords)
                })}
              </span>
            </div>
            <div>
              <span className="text-themed-muted">
                {t('initialization.importHistorical.imported', {
                  count: importResult.imported,
                  formattedCount: formatCount(importResult.imported)
                })}
              </span>
            </div>
            <div>
              <span className="text-themed-muted">
                {t('initialization.importHistorical.skipped', {
                  count: importResult.skipped,
                  formattedCount: formatCount(importResult.skipped)
                })}
              </span>
            </div>
            <div>
              <span className="text-themed-muted">
                {t('initialization.importHistorical.errors', {
                  count: importResult.errors,
                  formattedCount: formatCount(importResult.errors)
                })}
              </span>
            </div>
          </div>
          {importResult.backupPath && !importResult.backupPath.includes('(no backup') && (
            <div
              className={`pt-3 border-t ${importResult.errors > 0 ? 'border-warning' : 'border-success'}`}
            >
              <p className="text-xs text-themed-muted mb-1">
                {t('management.dataImporter.result.backupCreated')}:
              </p>
              <p className="text-xs font-mono text-themed-secondary bg-themed-tertiary px-2 py-1 rounded break-all">
                {importResult.backupPath}
              </p>
            </div>
          )}
        </div>
      )}

      {/* Action Buttons */}
      {!importResult && (
        <div
          className={`database-import-form__action-buttons ${showSkipButton ? 'database-import-form__action-buttons--with-skip' : ''}`}
        >
          <Button
            variant="default"
            onClick={handleValidate}
            loading={validating}
            disabled={validating || !hasValidInput || importing}
          >
            {validating
              ? t('initialization.importHistorical.validating')
              : t('initialization.importHistorical.validate')}
          </Button>

          <Button
            variant="filled"
            color="green"
            onClick={handleImport}
            loading={importing}
            disabled={!validationResult?.valid || importing}
          >
            {importing
              ? t('initialization.importHistorical.importing')
              : t('management.dataImporter.buttons.importRecords', {
                  count: validationResult?.recordCount ?? 0,
                  formattedCount: formatCount(validationResult?.recordCount ?? 0)
                })}
          </Button>

          {showSkipButton && onSkip && (
            <Button variant="default" onClick={onSkip} disabled={importing}>
              {t('initialization.importHistorical.skip')}
            </Button>
          )}
        </div>
      )}

      {showSkipButton && !importResult && (
        <p className="text-xs text-themed-muted text-center">
          {t('initialization.importHistorical.skipNotice')}
        </p>
      )}
    </div>
  );
}
