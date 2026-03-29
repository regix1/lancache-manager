import React, { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Cloud, Database, CheckCircle, Gamepad2 } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { EpicIcon } from '@components/ui/EpicIcon';
import type { CompletedPlatforms } from '@hooks/useInitializationFlow';

type SelectedPlatform = 'github' | 'steam' | 'epic' | null;

interface PlatformSetupStepProps {
  onSelectPlatform: (platform: 'github' | 'steam' | 'epic') => void;
  onContinue: () => void;
  onSkip: () => void;
  completedPlatforms: CompletedPlatforms;
}

interface PlatformCardProps {
  variant: 'github' | 'steam';
  selected: SelectedPlatform;
  completedPlatforms: CompletedPlatforms;
  onSelect: (platform: SelectedPlatform) => void;
}

interface EpicCardProps {
  selected: SelectedPlatform;
  completedPlatforms: CompletedPlatforms;
  onSelect: (platform: SelectedPlatform) => void;
}

function getCardClassName(isSelected: boolean): string {
  const base = 'p-4 rounded-lg border-2 cursor-pointer transition-all';
  if (isSelected) {
    return `${base} border-[var(--theme-primary)] bg-themed-primary-subtle`;
  }
  return `${base} border-themed-primary bg-themed-tertiary hover:border-themed-secondary`;
}

const GithubCard: React.FC<PlatformCardProps> = ({ selected, completedPlatforms, onSelect }) => {
  const { t } = useTranslation();
  const isSelected = selected === 'github';
  const isCompleted = completedPlatforms.steam === 'github';

  const handleClick = (): void => {
    onSelect(isSelected ? null : 'github');
  };

  return (
    <div className={getCardClassName(isSelected)} onClick={handleClick}>
      <div className="flex items-start justify-between">
        <div className="flex items-center gap-2 mb-2">
          <Cloud className="w-5 h-5 icon-info flex-shrink-0" />
          <h4 className="font-semibold text-themed-primary">
            {t('initialization.platformSetup.github.label')}
          </h4>
        </div>
        {isCompleted && (
          <div className="flex items-center gap-1 text-xs font-medium text-success flex-shrink-0 ml-2">
            <CheckCircle className="w-4 h-4" />
            {t('initialization.platformSetup.completed')}
          </div>
        )}
      </div>
      <p className="text-sm text-themed-secondary mb-1">
        {t('initialization.platformSetup.github.description')}
      </p>
      <p className="text-xs text-themed-muted">{t('initialization.platformSetup.github.note')}</p>
    </div>
  );
};

const SteamPicsCard: React.FC<PlatformCardProps> = ({ selected, completedPlatforms, onSelect }) => {
  const { t } = useTranslation();
  const isSelected = selected === 'steam';
  const isCompleted = completedPlatforms.steam === 'steam';

  const handleClick = (): void => {
    onSelect(isSelected ? null : 'steam');
  };

  return (
    <div className={getCardClassName(isSelected)} onClick={handleClick}>
      <div className="flex items-start justify-between">
        <div className="flex items-center gap-2 mb-2">
          <Database className="w-5 h-5 icon-success flex-shrink-0" />
          <h4 className="font-semibold text-themed-primary">
            {t('initialization.platformSetup.steam.label')}
          </h4>
        </div>
        {isCompleted && (
          <div className="flex items-center gap-1 text-xs font-medium text-success flex-shrink-0 ml-2">
            <CheckCircle className="w-4 h-4" />
            {t('initialization.platformSetup.completed')}
          </div>
        )}
      </div>
      <p className="text-sm text-themed-secondary mb-1">
        {t('initialization.platformSetup.steam.description')}
      </p>
      <p className="text-xs text-themed-muted">{t('initialization.platformSetup.steam.note')}</p>
    </div>
  );
};

const EpicCard: React.FC<EpicCardProps> = ({ selected, completedPlatforms, onSelect }) => {
  const { t } = useTranslation();
  const isSelected = selected === 'epic';
  const isCompleted = completedPlatforms.epic;

  const handleClick = (): void => {
    onSelect(isSelected ? null : 'epic');
  };

  return (
    <div className={getCardClassName(isSelected)} onClick={handleClick}>
      <div className="flex items-start justify-between">
        <div className="flex items-center gap-2 mb-2">
          <EpicIcon size={20} className="icon-primary flex-shrink-0" />
          <h4 className="font-semibold text-themed-primary">
            {t('initialization.platformSetup.epic.label')}
          </h4>
        </div>
        {isCompleted && (
          <div className="flex items-center gap-1 text-xs font-medium text-success flex-shrink-0 ml-2">
            <CheckCircle className="w-4 h-4" />
            {t('initialization.platformSetup.completed')}
          </div>
        )}
      </div>
      <p className="text-sm text-themed-secondary mb-1">
        {t('initialization.platformSetup.epic.description')}
      </p>
      <p className="text-xs text-themed-muted">{t('initialization.platformSetup.epic.note')}</p>
    </div>
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
  return completedPlatforms.steam !== null || completedPlatforms.epic;
}

export const PlatformSetupStep: React.FC<PlatformSetupStepProps> = ({
  onSelectPlatform,
  onContinue,
  onSkip,
  completedPlatforms
}) => {
  const { t } = useTranslation();
  const [selected, setSelected] = useState<SelectedPlatform>(null);

  const handleSelectCard = (platform: SelectedPlatform): void => {
    setSelected(platform);
  };

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
      {/* Header */}
      <div className="flex flex-col items-center text-center">
        <div className="w-14 h-14 rounded-full flex items-center justify-center mb-3 bg-themed-primary-subtle">
          <Gamepad2 className="w-7 h-7 icon-primary" />
        </div>
        <h3 className="text-lg font-semibold text-themed-primary mb-1">
          {t('initialization.platformSetup.title')}
        </h3>
        <p className="text-sm text-themed-secondary max-w-md">
          {t('initialization.platformSetup.subtitle')}
        </p>
      </div>

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
      <div className="space-y-2">
        <div className="flex items-center justify-between">
          <p className="text-sm font-medium text-themed-secondary">
            {t('initialization.platformSetup.steamGroup')}
          </p>
          <p className="text-xs text-themed-muted">
            {t('initialization.platformSetup.steamGroupNote')}
          </p>
        </div>
        <div className="grid grid-cols-2 gap-3">
          <GithubCard
            variant="github"
            selected={selected}
            completedPlatforms={completedPlatforms}
            onSelect={handleSelectCard}
          />
          <SteamPicsCard
            variant="steam"
            selected={selected}
            completedPlatforms={completedPlatforms}
            onSelect={handleSelectCard}
          />
        </div>
      </div>

      {/* Epic Group */}
      <div className="space-y-2">
        <p className="text-sm font-medium text-themed-secondary">
          {t('initialization.platformSetup.epicGroup')}
        </p>
        <EpicCard
          selected={selected}
          completedPlatforms={completedPlatforms}
          onSelect={handleSelectCard}
        />
      </div>

      {/* Actions */}
      <div className="flex gap-3 pt-2">
        <Button variant="outline" onClick={onSkip}>
          {t('initialization.platformSetup.skip.label')}
        </Button>
        <Button
          variant="filled"
          color="blue"
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
