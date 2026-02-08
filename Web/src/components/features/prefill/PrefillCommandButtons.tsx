import { useTranslation } from 'react-i18next';
import { Card } from '../../ui/Card';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { MultiSelectDropdown } from '@components/ui/MultiSelectDropdown';
import { Shield, Monitor, Cpu, Loader2 } from 'lucide-react';
import {
  CommandButton,
  CommandType,
  SELECTION_COMMANDS,
  PREFILL_COMMANDS,
  UTILITY_COMMANDS,
  OS_OPTIONS,
  THREAD_OPTIONS
} from './types';

interface PrefillCommandButtonsProps {
  isLoggedIn: boolean;
  isExecuting: boolean;
  isPrefillActive: boolean;
  isSessionActive: boolean;
  isUserAuthenticated: boolean;
  selectedAppIds: number[];
  selectedOS: string[];
  maxConcurrency: string;
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
  selectedAppIds,
  selectedOS,
  maxConcurrency,
  onCommandClick,
  onSelectedOSChange,
  onMaxConcurrencyChange
}: PrefillCommandButtonsProps) {
  const { t } = useTranslation();

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
        className={`cmd-tile cmd-tile-enter ${getTileVariantClass(cmd)}`}
        onClick={() => onCommandClick(cmd.id)}
        disabled={disabled}
        type="button"
      >
        <div className="flex items-start gap-3 w-full">
          <span className={`cmd-tile-icon ${getIconBgClass(cmd)}`}>
            {isExecuting && cmd.id.startsWith('prefill') ? (
              <Loader2 className="h-4 w-4 animate-spin text-themed-muted" />
            ) : (
              <span className={getIconColorClass(cmd)}>{cmd.icon}</span>
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
          <div className="cmd-section cmd-section--select p-4">
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
          <div className="cmd-section cmd-section--settings p-4 lg:col-span-2">
            <div className="cmd-section-header mb-3">
              <h3 className="text-xs font-semibold uppercase tracking-wider text-themed-muted">
                {t('prefill.sections.downloadSettings')}
              </h3>
            </div>
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
              <div className="cmd-settings-field">
                <label className="cmd-settings-label flex items-center gap-1.5">
                  <Monitor className="h-3 w-3" />
                  {t('prefill.settings.targetPlatforms')}
                </label>
                <MultiSelectDropdown
                  options={OS_OPTIONS.map((opt) => ({
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
              <div className="cmd-settings-field">
                <label className="cmd-settings-label flex items-center gap-1.5">
                  <Cpu className="h-3 w-3" />
                  {t('prefill.settings.downloadThreads')}
                </label>
                <EnhancedDropdown
                  options={THREAD_OPTIONS.map((opt) => ({
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
        <div className="cmd-section cmd-section--prefill p-4">
          <div className="cmd-section-header mb-3">
            <h3 className="text-xs font-semibold uppercase tracking-wider text-themed-muted">
              {t('prefill.sections.prefillOptions')}
            </h3>
          </div>
          <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-2">
            {PREFILL_COMMANDS.map(renderCommandTile)}
          </div>
        </div>

        {/* Utilities - compact bottom row */}
        <div className="cmd-section cmd-section--utility p-4">
          <div className="cmd-section-header mb-3">
            <h3 className="text-xs font-semibold uppercase tracking-wider text-themed-muted">
              {t('prefill.sections.utilities')}
            </h3>
          </div>
          <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-2">
            {UTILITY_COMMANDS.filter(
              (cmd: CommandButton) => !cmd.authOnly || isUserAuthenticated
            ).map(renderCommandTile)}
          </div>
        </div>

        {/* Login Required Notice */}
        {!isLoggedIn && (
          <div className="cmd-login-notice p-4 flex items-start gap-3">
            <Shield className="h-5 w-5 flex-shrink-0 mt-0.5 text-warning" />
            <div>
              <p className="font-medium text-sm text-warning-text">
                {t('prefill.loginNotice.title')}
              </p>
              <p className="text-sm text-themed-muted mt-1">
                {t('prefill.loginNotice.message')}
              </p>
            </div>
          </div>
        )}
      </div>
    </Card>
  );
}
