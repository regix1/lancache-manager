import { useTranslation } from 'react-i18next';
import { Card } from '../../ui/Card';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { MultiSelectDropdown } from '@components/ui/MultiSelectDropdown';
import { SectionActionsMenu } from '@components/ui/SectionActionsMenu';
import { ActionMenuDangerItem } from '@components/ui/ActionMenu';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { formatBytes } from '@utils/formatters';
import { Database } from 'lucide-react';
import {
  type CommandButton,
  type CommandType,
  SELECTION_COMMANDS,
  PREFILL_COMMANDS,
  UTILITY_COMMANDS,
  OS_OPTIONS,
  getThreadOptions
} from './types';

interface PrefillCommandButtonsProps {
  isLoggedIn: boolean;
  isExecuting: boolean;
  isPrefillActive: boolean;
  isSessionActive: boolean;
  isUserAuthenticated: boolean;
  /** Localized display name of this service, interpolated into the login-notice copy. */
  serviceName: string;
  selectedAppIds: string[];
  selectedOS: string[];
  maxConcurrency: string;
  maxThreadLimit?: number | null;
  /**
   * Prefill presets this service's daemon actually supports (prefillServiceConfig.prefillCommands).
   * PREFILL_COMMANDS is the full catalog; daemons differ (e.g. Battle.net/Riot are all-only),
   * so unsupported preset tiles are not rendered rather than shown dead.
   */
  supportedCommands: readonly CommandType[];
  /**
   * Target-platform (OS) values this service's daemon actually honours
   * (prefillServiceConfig.supportedOperatingSystems). Only Steam's socket reads an `os` filter;
   * the other daemons silently ignore it, so the "Target platforms" field is hidden entirely
   * (not just emptied) when this is an empty array.
   */
  supportedOperatingSystems: readonly string[];
  /** App IDs already present in cache, used for the "N cached" split-card summary. */
  cachedAppIds: string[];
  /** Prefetched download-size estimate for the current selection, shown on the split card. */
  estimatedSize: { bytes: number; loading: boolean; error?: string };
  onCommandClick: (commandType: CommandType) => void;
  onSelectedOSChange: (values: string[]) => void;
  onMaxConcurrencyChange: (value: string) => void;
}

export function PrefillCommandButtons({
  isLoggedIn,
  isExecuting,
  isPrefillActive,
  isSessionActive,
  isUserAuthenticated,
  serviceName,
  selectedAppIds,
  selectedOS,
  maxConcurrency,
  maxThreadLimit,
  supportedCommands,
  supportedOperatingSystems,
  cachedAppIds,
  estimatedSize,
  onCommandClick,
  onSelectedOSChange,
  onMaxConcurrencyChange
}: PrefillCommandButtonsProps) {
  const { t } = useTranslation();

  const availablePrefillCommands = PREFILL_COMMANDS.filter((cmd: CommandButton) =>
    supportedCommands.includes(cmd.id)
  );

  // Clear Cache DB moves from a tile into the Utilities section menu; the auth gate is the
  // same filter the tile row used (isUserAuthenticated is wired to isAdmin at the call
  // site), so non-admins get neither the tile nor the menu. [27]
  const clearCacheDbCommand = UTILITY_COMMANDS.find(
    (cmd: CommandButton) => cmd.id === 'clear-cache-data'
  );
  const showClearCacheDb =
    !!clearCacheDbCommand && (!clearCacheDbCommand.authOnly || isUserAuthenticated);
  const utilityTileCommands = UTILITY_COMMANDS.filter(
    (cmd: CommandButton) => cmd.id !== 'clear-cache-data'
  );

  const hasTargetPlatforms = supportedOperatingSystems.length > 0;

  // The split card's right half leads with one primary launch tile. Steam uses the
  // per-selection `prefill`; all-only daemons (Battle.net/Riot) have no `prefill`, so the
  // first supported preset (e.g. prefill-all) becomes the primary and drops out of the
  // presets row below.
  const prefillPrimaryCommand =
    availablePrefillCommands.find((cmd: CommandButton) => cmd.id === 'prefill') ??
    availablePrefillCommands[0];
  const presetCommands = availablePrefillCommands.filter(
    (cmd: CommandButton) => cmd.id !== prefillPrimaryCommand?.id
  );

  const cachedIdSet = new Set(cachedAppIds);
  const cachedSelectedCount = selectedAppIds.filter((id) => cachedIdSet.has(id)).length;
  const hasSelection = selectedAppIds.length > 0;

  const isGlobalDisabled = isExecuting || !isSessionActive || !isLoggedIn;

  const getCommandDisabled = (cmd: CommandButton): boolean => {
    const isPrefillSelected = cmd.id === 'prefill';
    const noGamesSelected = selectedAppIds.length === 0;
    const isPrefillCommand = cmd.id.startsWith('prefill');

    return (
      isGlobalDisabled ||
      (isPrefillSelected && noGamesSelected) ||
      (isPrefillCommand && isPrefillActive)
    );
  };

  const getCommandLabel = (cmd: CommandButton): string => {
    return t(`prefill.commands.${cmd.id}.label`);
  };

  const getCommandDescription = (cmd: CommandButton): string => {
    if (cmd.id === 'prefill') {
      return selectedAppIds.length === 0
        ? t('prefill.commands.selectGamesFirst')
        : t('prefill.commands.gamesReady', { count: selectedAppIds.length });
    }
    return t(`prefill.commands.${cmd.id}.description`);
  };

  const getIconBgClass = (cmd: CommandButton): string => {
    if (cmd.color === 'green') return 'icon-bg-green';
    if (cmd.color === 'red') return 'icon-bg-red';
    if (cmd.color === 'blue') return 'icon-bg-blue';
    return 'prefill-command-icon-bg-default';
  };

  const getIconColorClass = (cmd: CommandButton): string => {
    if (cmd.color === 'green') return 'icon-green';
    if (cmd.color === 'red') return 'icon-red';
    if (cmd.color === 'blue') return 'icon-blue';
    return 'text-themed-secondary';
  };

  const getTileVariantClass = (cmd: CommandButton): string => {
    if (cmd.id === 'prefill') return 'cmd-tile--primary';
    if (cmd.color === 'red') return 'cmd-tile--destructive';
    return '';
  };

  const renderCommandTile = (cmd: CommandButton, forcePrimary = false) => {
    const disabled = getCommandDisabled(cmd);
    const label = getCommandLabel(cmd);
    const description = getCommandDescription(cmd);
    const variantClass = forcePrimary ? 'cmd-tile--primary' : getTileVariantClass(cmd);

    return (
      <button
        key={cmd.id}
        className={`focus-ring cmd-tile cmd-tile-enter ${variantClass}`}
        onClick={() => onCommandClick(cmd.id)}
        disabled={disabled}
        type="button"
      >
        <div className="flex items-start gap-3 w-full">
          <span className={`icon-box icon-box--sm cmd-tile-icon ${getIconBgClass(cmd)}`}>
            {isExecuting && cmd.id.startsWith('prefill') ? (
              <LoadingSpinner inline size="sm" className="text-themed-muted" />
            ) : (
              <span className={getIconColorClass(cmd)}>
                <cmd.icon className="h-4 w-4" />
              </span>
            )}
          </span>
          <div className="flex flex-col">
            <span className="font-medium text-sm text-themed-primary">{label}</span>
            <span className="text-xs text-themed-muted">{description}</span>
          </div>
        </div>
      </button>
    );
  };

  return (
    <Card padding="md" className="cmd-center">
      <div className="space-y-5">
        {/* Split card: choose what to download (left), start it (right) */}
        <div className={`well-surface cmd-split p-4 ${isGlobalDisabled ? 'cmd-disabled' : ''}`}>
          <div className="cmd-split-left">
            {SELECTION_COMMANDS.map((cmd) => renderCommandTile(cmd))}
            <div className="cmd-split-summary text-xs">
              {hasSelection ? (
                <span className="flex flex-wrap items-center gap-2 text-themed-secondary">
                  <span className="themed-badge status-badge-neutral badge-count">
                    {selectedAppIds.length}
                  </span>
                  <span>{t('prefill.commands.gamesReady', { count: selectedAppIds.length })}</span>
                  {cachedSelectedCount > 0 && (
                    <span className="text-themed-muted">
                      · {cachedSelectedCount} {t('prefill.gameSelection.cachedBadge')}
                    </span>
                  )}
                </span>
              ) : (
                <span className="text-themed-muted">{t('prefill.commands.selectGamesFirst')}</span>
              )}
            </div>
            <div className="cmd-split-estimate">
              <span className="caps-label">
                {t('prefill.settings.estimatedSize', 'Estimated size')}
              </span>
              <span className="tabular-nums">
                {!hasSelection ? (
                  <span className="text-themed-muted">—</span>
                ) : estimatedSize.loading ? (
                  <LoadingSpinner inline size="sm" />
                ) : estimatedSize.error ? (
                  <span className="text-xs text-warning-text">{estimatedSize.error}</span>
                ) : (
                  <span className="font-medium text-themed-primary">
                    {formatBytes(estimatedSize.bytes)}
                  </span>
                )}
              </span>
            </div>
          </div>

          <div className="cmd-split-right">
            {prefillPrimaryCommand && renderCommandTile(prefillPrimaryCommand, true)}
            {hasTargetPlatforms && (
              <div className="cmd-settings-field">
                <label className="caps-label cmd-settings-label">
                  {t('prefill.settings.targetPlatforms')}
                </label>
                <MultiSelectDropdown
                  options={OS_OPTIONS.filter((opt) =>
                    supportedOperatingSystems.includes(opt.value)
                  ).map((opt) => ({
                    ...opt,
                    label: t(`prefill.settings.os.${opt.value}.label`),
                    description: t(`prefill.settings.os.${opt.value}.description`)
                  }))}
                  values={selectedOS}
                  onChange={onSelectedOSChange}
                  disabled={isGlobalDisabled}
                  minSelections={1}
                  placeholder={t('prefill.placeholders.selectPlatforms')}
                />
              </div>
            )}
            <div className="cmd-settings-field">
              <label className="caps-label cmd-settings-label">
                {t('prefill.settings.downloadThreads')}
              </label>
              <EnhancedDropdown
                options={getThreadOptions(maxThreadLimit).map((opt) => ({
                  ...opt,
                  label: t(`prefill.settings.threads.${opt.value}.label`),
                  description: t(`prefill.settings.threads.${opt.value}.description`)
                }))}
                value={maxConcurrency}
                onChange={onMaxConcurrencyChange}
                disabled={isGlobalDisabled}
              />
            </div>
          </div>
        </div>

        {/* Presets */}
        {presetCommands.length > 0 && (
          <div className={isGlobalDisabled ? 'cmd-disabled' : ''}>
            <h3 className="caps-label mb-3">{t('prefill.sections.prefillOptions')}</h3>
            <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-2">
              {presetCommands.map((cmd) => renderCommandTile(cmd))}
            </div>
          </div>
        )}

        {/* Utilities */}
        <div className={isGlobalDisabled ? 'cmd-disabled' : ''}>
          <div className="flex items-center justify-between gap-2 mb-3">
            <h3 className="caps-label">{t('prefill.sections.utilities')}</h3>
            {showClearCacheDb && !isGlobalDisabled && (
              <SectionActionsMenu
                label={t('prefill.sections.utilitiesActions', 'Utilities actions')}
              >
                {(close) => (
                  <ActionMenuDangerItem
                    onClick={() => {
                      close();
                      onCommandClick('clear-cache-data');
                    }}
                    icon={<Database className="w-4 h-4" />}
                  >
                    {t('prefill.commands.clear-cache-data.label')}
                  </ActionMenuDangerItem>
                )}
              </SectionActionsMenu>
            )}
          </div>
          <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-2">
            {utilityTileCommands.map((cmd) => renderCommandTile(cmd))}
          </div>
        </div>

        {/* Login Required Notice */}
        {!isLoggedIn && (
          <div className="cmd-login-notice p-4">
            <p className="font-medium text-sm text-warning-text">
              {t('prefill.loginNotice.title')}
            </p>
            <p className="text-sm text-themed-muted mt-1">
              {t('prefill.loginNotice.message', { service: serviceName })}
            </p>
          </div>
        )}
      </div>
    </Card>
  );
}
