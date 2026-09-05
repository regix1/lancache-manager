import React, { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Cloud, Database, CheckCircle, Gamepad2 } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { EpicIcon } from '@components/ui/EpicIcon';
import { XboxIcon } from '@components/ui/XboxIcon';
import { SelectableCard } from '@components/ui/SelectableCard';
import { StepHeader } from '@components/initialization/StepHeader';
import type { CompletedPlatforms } from '@hooks/useInitializationFlow';

type Platform = 'github' | 'steam' | 'epic' | 'xbox';
type SelectedPlatform = Platform | null;

interface PlatformSetupStepProps {
  onSelectPlatform: (platform: Platform) => void;
  onContinue: () => void;
  onSkip: () => void;
  completedPlatforms: CompletedPlatforms;
}

interface PlatformCardProps {
  platform: Platform;
  icon: React.ReactNode;
  selected: SelectedPlatform;
  completed: boolean;
  onSelect: (platform: SelectedPlatform) => void;
}

const PlatformCard: React.FC<PlatformCardProps> = ({
  platform,
  icon,
  selected,
  completed,
  onSelect
}) => {
  const { t } = useTranslation();
  const isSelected = selected === platform;

  return (
    <SelectableCard
      name="platform"
      value={platform}
      checked={isSelected}
      onChange={() => onSelect(platform)}
      onDeselect={() => onSelect(null)}
      icon={icon}
      title={t(`initialization.platformSetup.${platform}.label`)}
      description={t(`initialization.platformSetup.${platform}.description`)}
      note={t(`initialization.platformSetup.${platform}.note`)}
      badge={
        completed ? (
          <span className="flex items-center gap-1 text-xs font-medium text-success">
            <CheckCircle className="w-4 h-4" />
            {t('initialization.platformSetup.completed')}
          </span>
        ) : undefined
      }
    />
  );
};

function getPrimaryButtonLabel(selected: SelectedPlatform, t: (key: string) => string): string {
  if (selected === null) {
    return t('initialization.platformSetup.finishSetup');
  }
  const platformLabel = t(`initialization.platformSetup.${selected}.label`);
  return `${t('initialization.platformSetup.setUp')} ${platformLabel}`;
}

function hasAnyCompletion(completedPlatforms: CompletedPlatforms): boolean {
  return completedPlatforms.steam !== null || completedPlatforms.epic || completedPlatforms.xbox;
}

export const PlatformSetupStep: React.FC<PlatformSetupStepProps> = ({
  onSelectPlatform,
  onContinue,
  onSkip,
  completedPlatforms
}) => {
  const { t } = useTranslation();
  const [selected, setSelected] = useState<SelectedPlatform>(null);

  const handlePrimaryAction = (): void => {
    if (selected !== null) {
      onSelectPlatform(selected);
    } else {
      onContinue();
    }
  };

  const isPrimaryDisabled = selected === null && !hasAnyCompletion(completedPlatforms);

  return (
    <div className="space-y-5">
      <StepHeader
        icon={<Gamepad2 className="w-7 h-7 icon-primary" />}
        iconBackground="bg-themed-primary-subtle"
        title={t('initialization.platformSetup.title')}
        description={t('initialization.platformSetup.subtitle')}
      />

      {/* Info Banner */}
      <div className="p-3 rounded-lg text-sm bg-themed-tertiary">
        <p className="text-themed-secondary">
          <strong className="text-themed-primary">
            {t('initialization.platformSetup.requirementsInfo.label')}
          </strong>{' '}
          {t('initialization.platformSetup.requirementsInfo.body')}
        </p>
        <p className="text-themed-muted mt-1.5">
          {t('initialization.platformSetup.softRequirement')}
        </p>
      </div>

      {/* Steam Group */}
      <fieldset className="space-y-2">
        <legend className="text-sm font-medium text-themed-secondary">
          {t('initialization.platformSetup.steamGroup')}
        </legend>
        <p className="text-xs text-themed-muted">
          {t('initialization.platformSetup.steamGroupNote')}
        </p>
        <div className="grid gap-3 sm:grid-cols-2">
          <PlatformCard
            platform="github"
            icon={<Cloud className="icon-info" />}
            selected={selected}
            completed={completedPlatforms.steam === 'github'}
            onSelect={setSelected}
          />
          <PlatformCard
            platform="steam"
            icon={<Database className="icon-success" />}
            selected={selected}
            completed={completedPlatforms.steam === 'steam'}
            onSelect={setSelected}
          />
        </div>
      </fieldset>

      {/* Epic Group */}
      <fieldset className="space-y-2">
        <legend className="text-sm font-medium text-themed-secondary">
          {t('initialization.platformSetup.epicGroup')}
        </legend>
        <PlatformCard
          platform="epic"
          icon={<EpicIcon className="icon-primary" />}
          selected={selected}
          completed={completedPlatforms.epic}
          onSelect={setSelected}
        />
      </fieldset>

      {/* Xbox Group */}
      <fieldset className="space-y-2">
        <legend className="text-sm font-medium text-themed-secondary">
          {t('initialization.platformSetup.xboxGroup')}
        </legend>
        <PlatformCard
          platform="xbox"
          icon={<XboxIcon className="icon-primary" />}
          selected={selected}
          completed={completedPlatforms.xbox}
          onSelect={setSelected}
        />
      </fieldset>

      {/* Actions */}
      <div className="setup-actions pt-2">
        <Button variant="filled" color="secondary" onClick={onSkip}>
          {t('initialization.platformSetup.skip.label')}
        </Button>
        <Button
          variant="filled"
          color="secondary"
          onClick={handlePrimaryAction}
          disabled={isPrimaryDisabled}
          className="flex-1"
        >
          {getPrimaryButtonLabel(selected, t)}
        </Button>
      </div>
    </div>
  );
};
