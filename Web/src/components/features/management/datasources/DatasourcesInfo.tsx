import React, { use } from 'react';
import { Database, FolderOpen, FileText, CheckCircle, XCircle } from 'lucide-react';
import ApiService from '@services/api.service';
import { Card } from '@components/ui/Card';
import { HelpPopover, HelpSection, HelpNote, HelpDefinition } from '@components/ui/HelpPopover';
import type { Config } from '../../../../types';

// Fetch datasource configuration
const fetchConfig = async (): Promise<Config> => {
  return await ApiService.getConfig();
};

// Cache the promise to avoid refetching
let configPromise: Promise<Config> | null = null;

const getConfigPromise = () => {
  if (!configPromise) {
    configPromise = fetchConfig();
  }
  return configPromise;
};

// Export function to invalidate cache when needed
export const invalidateDatasourcesCache = () => {
  configPromise = null;
};

const DatasourcesInfo: React.FC = () => {
  const config = use(getConfigPromise());

  // Only show if multiple datasources are configured
  if (!config.dataSources || config.dataSources.length <= 1) {
    return null;
  }

  return (
    <Card>
      <div className="flex items-center gap-3 mb-4">
        <div className="w-10 h-10 rounded-lg flex items-center justify-center icon-bg-purple">
          <Database className="w-5 h-5 icon-purple" />
        </div>
        <h3 className="text-lg font-semibold text-themed-primary">Datasources</h3>
        <HelpPopover position="left" width={300}>
          <HelpSection title="Multi-Datasource Mode">
            <div className="space-y-1.5">
              <HelpDefinition term="Datasource" termColor="purple">
                A named cache/logs directory pair for segmenting cached content
              </HelpDefinition>
              <HelpDefinition term="Writable" termColor="green">
                Indicates if the directory can be modified by the application
              </HelpDefinition>
            </div>
          </HelpSection>

          <HelpNote type="info">
            Multiple datasources allow you to manage separate LANCache instances
            from a single dashboard.
          </HelpNote>
        </HelpPopover>
      </div>

      <p className="text-themed-secondary mb-4">
        {config.dataSources.length} datasources configured for multi-cache management.
      </p>

      <div className="space-y-3">
        {config.dataSources.map((ds) => (
          <div
            key={ds.name}
            className="p-3 rounded-lg border"
            style={{
              backgroundColor: 'var(--theme-bg-secondary)',
              borderColor: ds.enabled ? 'var(--theme-border-primary)' : 'var(--theme-border-secondary)',
              opacity: ds.enabled ? 1 : 0.6
            }}
          >
            {/* Header with name and status */}
            <div className="flex items-center justify-between mb-2">
              <div className="flex items-center gap-2">
                <span className="font-semibold text-themed-primary">{ds.name}</span>
                {!ds.enabled && (
                  <span
                    className="px-2 py-0.5 text-xs rounded font-medium"
                    style={{
                      backgroundColor: 'var(--theme-bg-tertiary)',
                      color: 'var(--theme-text-muted)'
                    }}
                  >
                    Disabled
                  </span>
                )}
              </div>
              <div className="flex items-center gap-2">
                {/* Cache writable status */}
                <span
                  className="flex items-center gap-1 text-xs"
                  title={ds.cacheWritable ? 'Cache is writable' : 'Cache is read-only'}
                >
                  {ds.cacheWritable ? (
                    <CheckCircle className="w-3.5 h-3.5" style={{ color: 'var(--theme-success-text)' }} />
                  ) : (
                    <XCircle className="w-3.5 h-3.5" style={{ color: 'var(--theme-warning)' }} />
                  )}
                  <span className="hidden sm:inline text-themed-muted">Cache</span>
                </span>
                {/* Logs writable status */}
                <span
                  className="flex items-center gap-1 text-xs"
                  title={ds.logsWritable ? 'Logs are writable' : 'Logs are read-only'}
                >
                  {ds.logsWritable ? (
                    <CheckCircle className="w-3.5 h-3.5" style={{ color: 'var(--theme-success-text)' }} />
                  ) : (
                    <XCircle className="w-3.5 h-3.5" style={{ color: 'var(--theme-warning)' }} />
                  )}
                  <span className="hidden sm:inline text-themed-muted">Logs</span>
                </span>
              </div>
            </div>

            {/* Paths */}
            <div className="space-y-1">
              <div className="flex items-center gap-2 text-xs">
                <FolderOpen className="w-3.5 h-3.5 text-themed-muted flex-shrink-0" />
                <span className="text-themed-muted">Cache:</span>
                <code className="bg-themed-tertiary px-1.5 py-0.5 rounded text-themed-secondary truncate">
                  {ds.cachePath}
                </code>
              </div>
              <div className="flex items-center gap-2 text-xs">
                <FileText className="w-3.5 h-3.5 text-themed-muted flex-shrink-0" />
                <span className="text-themed-muted">Logs:</span>
                <code className="bg-themed-tertiary px-1.5 py-0.5 rounded text-themed-secondary truncate">
                  {ds.logsPath}
                </code>
              </div>
            </div>
          </div>
        ))}
      </div>
    </Card>
  );
};

export default DatasourcesInfo;
