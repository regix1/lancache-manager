import React, { useState, useEffect } from 'react';
import { FileText, Loader2, CheckCircle, FolderOpen, ChevronDown, ChevronUp, PlayCircle, XCircle } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import { Tooltip } from '@components/ui/Tooltip';
import { useSignalR } from '@contexts/SignalRContext';
import type {
  ProcessingProgressEvent,
  LogProcessingCompleteEvent
} from '@contexts/SignalRContext/types';
import ApiService from '@services/api.service';
import type { Config, DatasourceInfo } from '../../../types';

interface LogProcessingStepProps {
  onComplete: () => void;
  onSkip: () => void;
  onProcessingStateChange?: (isProcessing: boolean) => void;
}

interface ProcessingProgress {
  isProcessing: boolean;
  progress: number;
  status: string;
  linesProcessed?: number;
  totalLines?: number;
  entriesProcessed?: number;
  mbProcessed?: number;
  mbTotal?: number;
}

export const LogProcessingStep: React.FC<LogProcessingStepProps> = ({
  onComplete,
  onSkip,
  onProcessingStateChange
}) => {
  const { t } = useTranslation();
  const signalR = useSignalR();
  const [processing, setProcessing] = useState(false);
  const [progress, setProgress] = useState<ProcessingProgress | null>(null);
  const [complete, setComplete] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Multi-datasource state
  const [config, setConfig] = useState<Config | null>(null);
  const [loading, setLoading] = useState(true);
  const [expandedDatasources, setExpandedDatasources] = useState<Set<string>>(new Set());
  const [actionLoading, setActionLoading] = useState<string | null>(null);

  // Load datasource config
  useEffect(() => {
    const loadData = async () => {
      try {
        const configData = await ApiService.getConfig();
        setConfig(configData);
      } catch (err) {
        console.error('Failed to load datasource data:', err);
      } finally {
        setLoading(false);
      }
    };
    loadData();
  }, []);

  useEffect(() => {
    onProcessingStateChange?.(processing);
  }, [processing, onProcessingStateChange]);

  useEffect(() => {
    const checkActiveProcessing = async () => {
      try {
        const status = await ApiService.getProcessingStatus();
        if (status.isProcessing) {
          setProcessing(true);
          setProgress({
            ...status,
            progress: status.progress ?? 0,
            status: status.status ?? 'processing'
          });
        }
      } catch (error) {
        console.error('[LogProcessing] Failed to check processing status:', error);
      }
    };
    checkActiveProcessing();
  }, []);

  useEffect(() => {
    const handleProcessingProgress = (progress: ProcessingProgressEvent) => {
      const currentProgress = progress.percentComplete || progress.progress || 0;
      const status = progress.status || 'processing';

      if (status.toLowerCase() === 'completed') {
        setProgress({
          isProcessing: false,
          progress: 100,
          status: 'completed',
          entriesProcessed: progress.entriesProcessed,
          linesProcessed: progress.linesProcessed || progress.totalLines,
          totalLines: progress.totalLines,
          mbProcessed: progress.mbTotal,
          mbTotal: progress.mbTotal
        });
        setComplete(true);
        setProcessing(false);
        return;
      }

      setProgress({
        isProcessing: true,
        progress: Math.min(99.9, currentProgress),
        status: status,
        mbProcessed: progress.mbProcessed,
        mbTotal: progress.mbTotal,
        entriesProcessed: progress.entriesProcessed,
        totalLines: progress.totalLines,
        linesProcessed: progress.linesProcessed
      });
    };

    const handleLogProcessingComplete = (data: LogProcessingCompleteEvent) => {
      // Check if processing failed
      if (data.success === false) {
        setError(data.message || t('initialization.logProcessing.failedToProcess'));
        setProgress({
          isProcessing: false,
          progress: 0,
          status: 'error'
        });
        setComplete(false);
        setProcessing(false);
        return;
      }

      // Success case
      setProgress({
        isProcessing: false,
        progress: 100,
        status: 'completed',
        entriesProcessed: data.entriesProcessed,
        linesProcessed: data.linesProcessed,
        totalLines: data.linesProcessed
      });
      setComplete(true);
      setProcessing(false);
    };

    signalR.on('LogProcessingProgress', handleProcessingProgress);
    signalR.on('LogProcessingComplete', handleLogProcessingComplete);

    return () => {
      signalR.off('LogProcessingProgress', handleProcessingProgress);
      signalR.off('LogProcessingComplete', handleLogProcessingComplete);
    };
  }, [signalR, t]);

  const toggleExpanded = (name: string) => {
    setExpandedDatasources(prev => {
      const next = new Set(prev);
      if (next.has(name)) {
        next.delete(name);
      } else {
        next.add(name);
      }
      return next;
    });
  };

  // Always reset to beginning (position 0) and process all logs
  const handleProcessAll = async () => {
    if (processing) return;

    setProcessing(true);
    setError(null);
    setComplete(false);
    setActionLoading('all');

    try {
      // Always start from the beginning for initialization
      await ApiService.resetLogPosition('top');
      await ApiService.processAllLogs();
    } catch (err: unknown) {
      setError((err instanceof Error ? err.message : String(err)) || t('initialization.logProcessing.failedToProcess'));
      setProcessing(false);
    } finally {
      setActionLoading(null);
    }
  };

  // Always reset to beginning (position 0) and process single datasource
  const handleProcessDatasource = async (datasourceName: string) => {
    if (processing) return;

    setProcessing(true);
    setError(null);
    setComplete(false);
    setActionLoading(datasourceName);

    try {
      // Always start from the beginning for initialization
      await ApiService.resetDatasourceLogPosition(datasourceName, 'top');
      await ApiService.processDatasourceLogs(datasourceName);
    } catch (err: unknown) {
      setError((err instanceof Error ? err.message : String(err)) || t('initialization.logProcessing.failedToProcessDatasource'));
      setProcessing(false);
    } finally {
      setActionLoading(null);
    }
  };

  // Get datasources - ensure at least one exists
  const datasources = config?.dataSources && config.dataSources.length > 0
    ? config.dataSources
    : config ? [{
        name: 'default',
        cachePath: config.cachePath || '/cache',
        logsPath: config.logsPath || '/logs',
        cacheWritable: config.cacheWritable ?? false,
        logsWritable: config.logsWritable ?? false,
        enabled: true
      } as DatasourceInfo] : [];

  const hasMultiple = datasources.length > 1;
  const progressPercent = progress?.progress || 0;

  if (loading) {
    return (
      <div className="flex flex-col items-center justify-center py-8">
        <Loader2 className="w-8 h-8 animate-spin text-themed-muted mb-3" />
        <p className="text-themed-muted">{t('initialization.logProcessing.loadingDatasources')}</p>
      </div>
    );
  }

  return (
    <div className="space-y-5">
      {/* Header */}
      <div className="flex flex-col items-center text-center">
        <div
          className={`w-16 h-16 rounded-full flex items-center justify-center mb-4 ${
            complete
              ? 'bg-themed-success'
              : processing
                ? 'bg-themed-primary-subtle'
                : 'bg-themed-info'
          }`}
        >
          {complete ? (
            <CheckCircle className="w-8 h-8 icon-success" />
          ) : processing ? (
            <Loader2 className="w-8 h-8 animate-spin icon-primary" />
          ) : (
            <FileText className="w-8 h-8 icon-info" />
          )}
        </div>
        <h3 className="text-xl font-semibold text-themed-primary mb-1">
          {complete ? t('initialization.logProcessing.titleComplete') : t('initialization.logProcessing.title')}
        </h3>
        <p className="text-sm text-themed-secondary max-w-md">
          {complete
            ? t('initialization.logProcessing.subtitleComplete')
            : hasMultiple
              ? t('initialization.logProcessing.subtitleMultiple', { count: datasources.length })
              : t('initialization.logProcessing.subtitle')}
        </p>
      </div>

      {/* Progress Display (when processing) */}
      {processing && progress && !complete && (
        <div className="space-y-4">
          <div>
            <div className="w-full rounded-full h-2.5 overflow-hidden bg-themed-tertiary">
              <div
                className="h-full transition-all duration-500 ease-out rounded-full bg-primary"
                style={{ width: `${progressPercent}%` }}
              />
            </div>
            <p className="text-sm text-themed-secondary text-center mt-2">
              {t('initialization.logProcessing.percentComplete', { percent: progressPercent.toFixed(1) })}
            </p>
          </div>

          <div className="grid grid-cols-2 gap-3 p-4 rounded-lg bg-themed-tertiary">
            <div>
              <p className="text-xs text-themed-muted">{t('initialization.logProcessing.status')}</p>
              <p className="text-sm font-medium text-themed-primary">{progress.status || t('initialization.logProcessing.processing')}</p>
            </div>
            {progress.linesProcessed !== undefined && (
              <div>
                <p className="text-xs text-themed-muted">{t('initialization.logProcessing.lines')}</p>
                <p className="text-sm font-medium text-themed-primary">{progress.linesProcessed.toLocaleString()}</p>
              </div>
            )}
            {progress.entriesProcessed !== undefined && (
              <div>
                <p className="text-xs text-themed-muted">{t('initialization.logProcessing.entries')}</p>
                <p className="text-sm font-medium text-themed-primary">{progress.entriesProcessed.toLocaleString()}</p>
              </div>
            )}
            {progress.mbProcessed !== undefined && progress.mbTotal !== undefined && (
              <div>
                <p className="text-xs text-themed-muted">{t('initialization.logProcessing.data')}</p>
                <p className="text-sm font-medium text-themed-primary">
                  {t('initialization.logProcessing.dataMbProgress', { processed: progress.mbProcessed.toFixed(1), total: progress.mbTotal.toFixed(1) })}
                </p>
              </div>
            )}
          </div>
        </div>
      )}

      {/* Datasource List (when not processing and not complete) */}
      {!processing && !complete && (
        <>
          {/* Info text */}
          <div className="p-4 rounded-lg bg-themed-tertiary">
            <p className="text-sm text-themed-secondary mb-2">
              {t('initialization.logProcessing.description')}
            </p>
            <p className="text-sm text-themed-muted">
              {t('initialization.logProcessing.canSkip')}
            </p>
          </div>

          {/* Process All button */}
          <Button
            variant="filled"
            color="green"
            leftSection={<PlayCircle className="w-4 h-4" />}
            onClick={handleProcessAll}
            disabled={actionLoading !== null || processing}
            loading={actionLoading === 'all'}
            fullWidth
          >
            {t('initialization.logProcessing.processAllLogs')}
          </Button>

          {/* Datasource list */}
          <div className="space-y-2">
            {datasources.map((ds) => {
              const isExpanded = expandedDatasources.has(ds.name);

              return (
                <div
                  key={ds.name}
                  className={`rounded-lg border ${
                    ds.enabled
                      ? 'bg-themed-secondary border-themed-primary'
                      : 'bg-themed-secondary border-themed-secondary opacity-60'
                  }`}
                >
                  {/* Header - clickable to expand */}
                  <div
                    className="p-3 cursor-pointer"
                    onClick={() => toggleExpanded(ds.name)}
                  >
                    <div className="flex items-center justify-between">
                      <div className="flex items-center gap-2">
                        <span className="font-semibold text-themed-primary">{ds.name}</span>
                        {!ds.enabled && (
                          <span className="px-2 py-0.5 text-xs rounded font-medium bg-themed-tertiary text-themed-muted">
                            {t('initialization.logProcessing.disabled')}
                          </span>
                        )}
                      </div>
                      <div className="flex items-center gap-3">
                        <div className="flex items-center gap-2">
                          <Tooltip content={ds.cacheWritable ? t('initialization.logProcessing.cacheWritable') : t('initialization.logProcessing.cacheReadOnly')} position="top">
                            <span className="flex items-center gap-1 text-xs">
                              {ds.cacheWritable ? (
                                <CheckCircle className="w-3.5 h-3.5 text-themed-success" />
                              ) : (
                                <XCircle className="w-3.5 h-3.5 icon-warning" />
                              )}
                            </span>
                          </Tooltip>
                          <Tooltip content={ds.logsWritable ? t('initialization.logProcessing.logsWritable') : t('initialization.logProcessing.logsReadOnly')} position="top">
                            <span className="flex items-center gap-1 text-xs">
                              {ds.logsWritable ? (
                                <CheckCircle className="w-3.5 h-3.5 text-themed-success" />
                              ) : (
                                <XCircle className="w-3.5 h-3.5 icon-warning" />
                              )}
                            </span>
                          </Tooltip>
                        </div>
                        {isExpanded ? (
                          <ChevronUp className="w-4 h-4 text-themed-muted" />
                        ) : (
                          <ChevronDown className="w-4 h-4 text-themed-muted" />
                        )}
                      </div>
                    </div>
                  </div>

                  {/* Expanded details */}
                  {isExpanded && (
                    <div className="px-3 pb-3 border-t border-themed-secondary">
                      <div className="py-2 space-y-1">
                        <div className="flex items-center gap-2 text-xs">
                          <FolderOpen className="w-3.5 h-3.5 text-themed-muted flex-shrink-0" />
                          <span className="text-themed-muted">{t('initialization.logProcessing.cache')}</span>
                          <code className="bg-themed-tertiary px-1.5 py-0.5 rounded text-themed-secondary truncate">
                            {ds.cachePath}
                          </code>
                        </div>
                        <div className="flex items-center gap-2 text-xs">
                          <FileText className="w-3.5 h-3.5 text-themed-muted flex-shrink-0" />
                          <span className="text-themed-muted">{t('initialization.logProcessing.logs')}</span>
                          <code className="bg-themed-tertiary px-1.5 py-0.5 rounded text-themed-secondary truncate">
                            {ds.logsPath}
                          </code>
                        </div>
                      </div>

                      {/* Process single datasource */}
                      <div className="pt-2">
                        <Button
                          variant="filled"
                          color="green"
                          size="sm"
                          leftSection={<PlayCircle className="w-3.5 h-3.5" />}
                          onClick={(e) => {
                            e.stopPropagation();
                            handleProcessDatasource(ds.name);
                          }}
                          disabled={actionLoading !== null || processing || !ds.enabled}
                          loading={actionLoading === ds.name}
                          fullWidth
                        >
                          {t('initialization.logProcessing.processThisDatasource')}
                        </Button>
                      </div>
                    </div>
                  )}
                </div>
              );
            })}
          </div>
        </>
      )}

      {/* Success */}
      {complete && (
        <div className="p-4 rounded-lg text-center bg-themed-success">
          <p className="text-sm text-themed-success">
            {t('initialization.logProcessing.complete')}
          </p>
        </div>
      )}

      {/* Error */}
      {error && (
        <div className="p-3 rounded-lg bg-themed-error">
          <p className="text-sm text-themed-error">{error}</p>
        </div>
      )}

      {/* Action Buttons */}
      <div className="pt-2">
        {!processing && !complete && (
          <Button variant="default" onClick={onSkip} fullWidth>
            {t('initialization.logProcessing.skipForNow')}
          </Button>
        )}

        {complete && (
          <Button variant="filled" color="green" onClick={onComplete} fullWidth>
            {t('initialization.logProcessing.continue')}
          </Button>
        )}
      </div>
    </div>
  );
};
