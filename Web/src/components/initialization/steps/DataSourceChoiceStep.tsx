import React from 'react';
import { Cloud, Database, CheckCircle } from 'lucide-react';
import { Button } from '@components/ui/Button';

interface DataSourceChoiceStepProps {
  onChooseGithub: () => void;
  onChooseSteam: () => void;
}

export const DataSourceChoiceStep: React.FC<DataSourceChoiceStepProps> = ({
  onChooseGithub,
  onChooseSteam
}) => {
  return (
    <div className="space-y-5">
      {/* Header */}
      <div className="flex flex-col items-center text-center">
        <div className="w-14 h-14 rounded-full flex items-center justify-center mb-3 bg-themed-info">
          <Database className="w-7 h-7 icon-info" />
        </div>
        <h3 className="text-lg font-semibold text-themed-primary mb-1">Choose Data Source</h3>
        <p className="text-sm text-themed-secondary max-w-md">
          Select how to obtain depot mapping data for identifying Steam games
        </p>
      </div>

      {/* Info Box */}
      <div className="p-3 rounded-lg text-sm bg-themed-tertiary">
        <p className="text-themed-secondary">
          <strong className="text-themed-primary">What is depot mapping?</strong>{' '}
          Links cache files to games. Essential for identifying which games are being cached.
        </p>
      </div>

      {/* Options Grid */}
      <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
        {/* GitHub Option */}
        <div className="p-4 rounded-lg border-2 flex flex-col bg-themed-tertiary border-themed-primary">
          <div className="flex items-center gap-2 mb-2">
            <Cloud className="w-5 h-5 icon-info" />
            <h4 className="font-semibold text-themed-primary">GitHub Data</h4>
          </div>
          <p className="text-sm text-themed-secondary mb-3">
            Community-maintained depot mappings. Quick and easy.
          </p>
          <ul className="text-xs text-themed-muted space-y-1 mb-4 flex-grow">
            <li className="flex items-center gap-1.5">
              <CheckCircle className="w-3 h-3 icon-success" />
              Quick setup (~30 seconds)
            </li>
            <li className="flex items-center gap-1.5">
              <CheckCircle className="w-3 h-3 icon-success" />
              290,000+ mappings ready
            </li>
            <li className="flex items-center gap-1.5">
              <CheckCircle className="w-3 h-3 icon-success" />
              No API key needed
            </li>
          </ul>
          <Button
            variant="filled"
            color="blue"
            size="sm"
            onClick={onChooseGithub}
            fullWidth
          >
            Use GitHub Data
          </Button>
        </div>

        {/* Steam Option */}
        <div className="p-4 rounded-lg border-2 flex flex-col bg-themed-tertiary border-themed-primary">
          <div className="flex items-center gap-2 mb-2">
            <Database className="w-5 h-5 icon-success" />
            <h4 className="font-semibold text-themed-primary">Steam Data</h4>
          </div>
          <p className="text-sm text-themed-secondary mb-3">
            Generate directly from Steam. Access all games.
          </p>
          <ul className="text-xs text-themed-muted space-y-1 mb-4 flex-grow">
            <li className="flex items-center gap-1.5">
              <CheckCircle className="w-3 h-3 icon-success" />
              Latest data from Steam
            </li>
            <li className="flex items-center gap-1.5">
              <CheckCircle className="w-3 h-3 icon-success" />
              Playtest/beta games access
            </li>
            <li className="flex items-center gap-1.5">
              <span className="w-3 h-3 rounded-full border border-themed-secondary" />
              Requires Steam API key
            </li>
          </ul>
          <Button
            variant="filled"
            color="green"
            size="sm"
            onClick={onChooseSteam}
            fullWidth
          >
            Use Steam Data
          </Button>
        </div>
      </div>
    </div>
  );
};
