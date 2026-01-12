import { Card } from '../../ui/Card';
import { Button } from '../../ui/Button';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { MultiSelectDropdown } from '@components/ui/MultiSelectDropdown';
import { List, Settings, Download, Zap, Shield, Monitor, Cpu } from 'lucide-react';
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
  isUserAuthenticated,
  selectedAppIds,
  selectedOS,
  maxConcurrency,
  onCommandClick,
  onSelectedOSChange,
  onMaxConcurrencyChange
}: PrefillCommandButtonsProps) {
  const renderCommandButton = (cmd: CommandButton) => {
    // Special handling for "Prefill Selected" - disable if no games selected
    const isPrefillSelected = cmd.id === 'prefill';
    const noGamesSelected = selectedAppIds.length === 0;
    // Disable prefill buttons while a prefill is in progress
    const isPrefillCommand = cmd.id.startsWith('prefill');
    const isDisabled =
      isExecuting || !isLoggedIn || (isPrefillSelected && noGamesSelected) || (isPrefillCommand && isPrefillActive);

    // Dynamic label for prefill selected
    const label =
      isPrefillSelected && selectedAppIds.length > 0
        ? `Prefill Selected (${selectedAppIds.length})`
        : cmd.label;

    // Dynamic description for prefill selected
    const description = isPrefillSelected
      ? noGamesSelected
        ? 'Select games first'
        : `${selectedAppIds.length} game${selectedAppIds.length !== 1 ? 's' : ''} ready`
      : cmd.description;

    return (
      <Button
        key={cmd.id}
        variant={cmd.variant || 'default'}
        color={cmd.color}
        onClick={() => onCommandClick(cmd.id)}
        disabled={isDisabled}
        className="h-auto py-3 px-4 flex-col items-start gap-1"
        size="sm"
      >
        <div className="flex items-center gap-2 w-full">
          <span
            className="p-1.5 rounded-md"
            style={{
              backgroundColor:
                cmd.variant === 'filled'
                  ? 'rgba(255,255,255,0.15)'
                  : 'color-mix(in srgb, var(--theme-primary) 15%, transparent)'
            }}
          >
            {cmd.icon}
          </span>
          <span className="font-medium text-sm">{label}</span>
        </div>
        <span className="text-xs opacity-70 pl-8">{description}</span>
      </Button>
    );
  };

  return (
    <Card padding="md">
      <div className="space-y-6">
        {/* Selection Commands */}
        <div>
          <h3 className="text-xs font-semibold uppercase tracking-wider text-themed-muted mb-3 flex items-center gap-2">
            <List className="h-3.5 w-3.5" />
            Game Selection
          </h3>
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-2">
            {SELECTION_COMMANDS.map(renderCommandButton)}
          </div>
        </div>

        {/* Download Settings */}
        <div>
          <h3 className="text-xs font-semibold uppercase tracking-wider text-themed-muted mb-3 flex items-center gap-2">
            <Settings className="h-3.5 w-3.5" />
            Download Settings
          </h3>
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
            {/* OS Selection */}
            <div>
              <label className="text-sm font-medium text-themed-secondary mb-1.5 flex items-center gap-2">
                <Monitor className="h-3.5 w-3.5" />
                Target Platforms
              </label>
              <MultiSelectDropdown
                options={OS_OPTIONS}
                values={selectedOS}
                onChange={onSelectedOSChange}
                disabled={isExecuting || !isLoggedIn}
                minSelections={1}
                placeholder="Select platforms"
              />
            </div>
            {/* Thread/Concurrency Selection */}
            <div>
              <label className="text-sm font-medium text-themed-secondary mb-1.5 flex items-center gap-2">
                <Cpu className="h-3.5 w-3.5" />
                Download Threads
              </label>
              <EnhancedDropdown
                options={THREAD_OPTIONS}
                value={maxConcurrency}
                onChange={onMaxConcurrencyChange}
                disabled={isExecuting || !isLoggedIn}
              />
            </div>
          </div>
        </div>

        {/* Prefill Commands */}
        <div>
          <h3 className="text-xs font-semibold uppercase tracking-wider text-themed-muted mb-3 flex items-center gap-2">
            <Download className="h-3.5 w-3.5" />
            Prefill Options
          </h3>
          <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-2">
            {PREFILL_COMMANDS.map(renderCommandButton)}
          </div>
        </div>

        {/* Utility Commands */}
        <div>
          <h3 className="text-xs font-semibold uppercase tracking-wider text-themed-muted mb-3 flex items-center gap-2">
            <Zap className="h-3.5 w-3.5" />
            Utilities
          </h3>
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-2">
            {UTILITY_COMMANDS.filter((cmd) => !cmd.authOnly || isUserAuthenticated).map(
              renderCommandButton
            )}
          </div>
        </div>

        {/* Login Required Notice */}
        {!isLoggedIn && (
          <div className="p-4 rounded-lg flex items-start gap-3 bg-[color-mix(in_srgb,var(--theme-warning)_10%,transparent)] border border-[color-mix(in_srgb,var(--theme-warning)_25%,transparent)]">
            <Shield className="h-5 w-5 flex-shrink-0 mt-0.5 text-[var(--theme-warning)]" />
            <div>
              <p className="font-medium text-sm text-[var(--theme-warning-text)]">
                Login Required to Use Commands
              </p>
              <p className="text-sm text-themed-muted mt-1">
                All prefill commands require Steam authentication. Click "Login to Steam" above to
                enable commands. Your credentials are sent directly to the container and never stored
                by this application.
              </p>
            </div>
          </div>
        )}
      </div>
    </Card>
  );
}
