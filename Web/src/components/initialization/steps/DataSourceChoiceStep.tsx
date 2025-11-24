import React from 'react';
import { Cloud, Database } from 'lucide-react';
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
    <>
      <p className="text-themed-secondary text-center mb-6">
        Choose how you want to obtain depot mapping data for identifying Steam games in your cache logs:
      </p>

      {/* What is depot mapping info box */}
      <div
        className="mb-6 p-4 rounded-lg border"
        style={{
          backgroundColor: 'var(--theme-info-bg)',
          borderColor: 'var(--theme-info)'
        }}
      >
        <p className="text-sm" style={{ color: 'var(--theme-info-text)' }}>
          <strong>What is depot mapping?</strong>
          <br />
          Depot mapping links cache files to games. This data is essential for identifying which games are being cached.
        </p>
      </div>

      {/* Options */}
      <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
        {/* GitHub Pre-created Data Option */}
        <div
          className="p-5 rounded-lg border-2 transition-all flex flex-col"
          style={{
            backgroundColor: 'var(--theme-bg-tertiary)',
            borderColor: 'var(--theme-border-primary)',
            minHeight: '260px'
          }}
        >
          <div className="flex items-center gap-2 mb-3">
            <Cloud size={20} style={{ color: 'var(--theme-info)' }} />
            <h3 className="text-base font-semibold text-themed-primary">GitHub Pre-created Data</h3>
          </div>
          <p className="text-sm text-themed-secondary mb-3">
            Download community-maintained depot mappings from GitHub. No Steam API key required.
          </p>
          <ul className="text-xs text-themed-muted space-y-1 mb-4 flex-grow">
            <li>✓ Quick setup (~30 seconds)</li>
            <li>✓ 290,000+ mappings ready</li>
            <li>✓ Regularly updated</li>
            <li>✓ No Steam API key needed</li>
            <li>✓ Access to public games only</li>
          </ul>
          <Button
            variant="filled"
            color="blue"
            size="sm"
            leftSection={<Cloud className="w-3 h-3" />}
            onClick={onChooseGithub}
            fullWidth
            className="mt-auto"
          >
            Use GitHub Data
          </Button>
        </div>

        {/* Steam Data Option */}
        <div
          className="p-5 rounded-lg border-2 transition-all flex flex-col"
          style={{
            backgroundColor: 'var(--theme-bg-tertiary)',
            borderColor: 'var(--theme-border-primary)',
            minHeight: '260px'
          }}
        >
          <div className="flex items-center gap-2 mb-3">
            <Database size={20} style={{ color: 'var(--theme-success)' }} />
            <h3 className="text-base font-semibold text-themed-primary">Steam Data</h3>
          </div>
          <p className="text-sm text-themed-secondary mb-3">
            Generate depot mappings directly from Steam. Requires Steam Web API v1 key.
          </p>
          <ul className="text-xs text-themed-muted space-y-1 mb-4 flex-grow">
            <li>✓ Latest data from Steam</li>
            <li>✓ Access to playtest/beta games</li>
            <li>✓ Customizable auth options</li>
            <li>✓ Can update incrementally</li>
            <li>○ Requires Steam API key setup</li>
            <li>○ Takes 10-30 minutes for full scan</li>
          </ul>
          <Button
            variant="filled"
            color="green"
            size="sm"
            leftSection={<Database className="w-3 h-3" />}
            onClick={onChooseSteam}
            fullWidth
            className="mt-auto"
          >
            Use Steam Data
          </Button>
        </div>
      </div>
    </>
  );
};
