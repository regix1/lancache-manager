import { useTranslation } from 'react-i18next';
import { Card } from '../../ui/Card';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { MultiSelectDropdown } from '@components/ui/MultiSelectDropdown';
import { SectionActionsMenu } from '@components/ui/SectionActionsMenu';
import { ActionMenuDangerItem } from '@components/ui/ActionMenu';
import LoadingSpinner from '@components/common/LoadingSpinner';
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

  const renderCommandTile = (cmd: CommandButton) => {
    const disabled = getCommandDisabled(cmd);
    const label = getCommandLabel(cmd);
    const description = getCommandDescription(cmd);

    return (
      <button
        key={cmd.id}
        className={`focus-ring cmd-tile cmd-tile-enter ${getTileVariantClass(cmd)}`}
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
            <span className="font-medium text-sm text-themed-primary flex items-center gap-2">
              {label}
              {cmd.id === 'prefill' && selectedAppIds.length > 0 && (
                <span className="cmd-badge">{selectedAppIds.length}</span>
              )}
            </span>
            <span className="text-xs text-themed-muted">{description}</span>
          </div>
        </div>
      </button>
    );
  };

  return (
    <Card padding="md" className="cmd-center">
      <div className="space-y-4">
        {/* Top Row: Game Selection + Download Settings side-by-side */}
        <div className="grid grid-cols-1 lg:grid-cols-3 gap-4">
          {/* Game Selection */}
          <div
            className={`cmd-section cmd-section--select p-4 ${isGlobalDisabled ? 'cmd-section--disabled' : ''}`}
          >
            <div className="cmd-section-header mb-3">
              <h3 className="text-xs font-semibold uppercase tracking-wider text-themed-muted">
                {t('prefill.sections.gameSelection')}
              </h3>
            </div>
            <div className="grid grid-cols-1 gap-2">
              {SELECTION_COMMANDS.map(renderCommandTile)}
            </div>
          </div>

          {/* Download Settings - spans 2 columns on large */}
          <div
            className={`cmd-section cmd-section--settings p-4 lg:col-span-2 ${isGlobalDisabled ? 'cmd-section--disabled' : ''}`}
          >
            <div className="cmd-section-header mb-3">
              <h3 className="text-xs font-semibold uppercase tracking-wider text-themed-muted">
                {t('prefill.sections.downloadSettings')}
              </h3>
            </div>
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
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
              <div className={`cmd-settings-field ${hasTargetPlatforms ? '' : 'sm:col-span-2'}`}>
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
        </div>

        {/* Prefill Commands - the hero section */}
        <div
          className={`cmd-section cmd-section--prefill p-4 ${isGlobalDisabled ? 'cmd-section--disabled' : ''}`}
        >
          <div className="cmd-section-header mb-3">
            <h3 className="text-xs font-semibold uppercase tracking-wider text-themed-muted">
              {t('prefill.sections.prefillOptions')}
            </h3>
          </div>
          <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-2">
            {availablePrefillCommands.map(renderCommandTile)}
          </div>
        </div>

        {/* Utilities - compact bottom row */}
        <div
          className={`cmd-section cmd-section--utility p-4 ${isGlobalDisabled ? 'cmd-section--disabled' : ''}`}
        >
          <div className="cmd-section-header mb-3 flex items-center justify-between gap-2">
            <h3 className="text-xs font-semibold uppercase tracking-wider text-themed-muted">
              {t('prefill.sections.utilities')}
            </h3>
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
            {utilityTileCommands.map(renderCommandTile)}
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
