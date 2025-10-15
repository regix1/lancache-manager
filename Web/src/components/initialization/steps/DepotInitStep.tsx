import React from 'react';
import { Cloud, Database, Loader, AlertTriangle, ArrowLeft } from 'lucide-react';
import { Button } from '@components/ui/Button';

interface DepotInitStepProps {
  picsData: any;
  initializing: boolean;
  selectedMethod: 'cloud' | 'generate' | 'continue' | null;
  downloadStatus?: string | null;
  usingSteamAuth?: boolean;
  onDownloadPrecreated: () => void;
  onGenerateOwn: () => void;
  onContinue: () => void;
  onBackToSteamAuth?: () => void;
}

export const DepotInitStep: React.FC<DepotInitStepProps> = ({
  picsData,
  initializing,
  selectedMethod,
  downloadStatus,
  usingSteamAuth = false,
  onDownloadPrecreated,
  onGenerateOwn,
  onContinue,
  onBackToSteamAuth
}) => {
  const shouldShowContinueOption = () => {
    if (!picsData) return false;
    return picsData.jsonFile?.exists === true;
  };

  return (
    <>
      <p className="text-themed-secondary text-center mb-6">
        To identify Steam games from your cache logs, depot mapping data is required.
        Choose how you'd like to initialize this data:
      </p>

      {/* Steam Auth Warning Banner */}
      {usingSteamAuth && (
        <div className="mb-6 p-4 rounded-lg border-2"
             style={{
               backgroundColor: 'var(--theme-warning-bg)',
               borderColor: 'var(--theme-warning)',
               color: 'var(--theme-warning-text)'
             }}>
          <div className="flex items-start gap-3">
            <AlertTriangle className="w-5 h-5 flex-shrink-0 mt-0.5" />
            <div className="flex-1">
              <p className="text-sm font-semibold mb-1">Steam Account Authenticated</p>
              <p className="text-sm mb-3">
                Since you logged in with your Steam account, the GitHub download option is unavailable.
                Your personalized depot data will be generated using your Steam login, which provides
                access to all games including playtests and restricted content.
              </p>
              {onBackToSteamAuth && (
                <Button
                  size="sm"
                  variant="default"
                  leftSection={<ArrowLeft className="w-3 h-3" />}
                  onClick={onBackToSteamAuth}
                >
                  Change Authentication Method
                </Button>
              )}
            </div>
          </div>
        </div>
      )}

      {/* Download Status Display */}
      {downloadStatus && (
        <div className="mb-6 p-4 rounded-lg"
             style={{
               backgroundColor: 'var(--theme-info-bg)',
               borderColor: 'var(--theme-info)',
               color: 'var(--theme-info-text)'
             }}>
          <div className="flex items-center gap-3">
            <Loader className="w-5 h-5 animate-spin flex-shrink-0" />
            <div className="flex-1">
              <p className="text-sm font-semibold">{downloadStatus}</p>
              {selectedMethod === 'cloud' && downloadStatus.includes('Downloading') && (
                <p className="text-xs mt-1 opacity-80">
                  This typically takes 30-60 seconds depending on your connection
                </p>
              )}
            </div>
          </div>
        </div>
      )}

      {/* Show PICS data status if available */}
      {picsData && (
        <div className="mb-6 p-4 rounded-lg"
             style={{
               backgroundColor: 'var(--theme-info-bg)',
               borderColor: 'var(--theme-info)',
               color: 'var(--theme-info-text)'
             }}>
          <p className="text-sm">
            <strong>Current PICS Data Status:</strong><br/>
            {picsData.jsonFile?.exists && (
              <>JSON File: {picsData.jsonFile?.totalMappings?.toLocaleString() ?? 0} mappings<br/></>
            )}
            Database: {picsData.database?.totalMappings?.toLocaleString() ?? 0} mappings<br/>
            SteamKit2: {picsData.steamKit2?.depotCount?.toLocaleString() ?? 0} depots
            {picsData.steamKit2?.isReady ? ' (Ready)' : ' (Not Ready)'}
          </p>
        </div>
      )}

      {/* Options */}
      <div className={`grid grid-cols-1 gap-4 ${shouldShowContinueOption() ? 'md:grid-cols-3' : 'md:grid-cols-2'}`}>
        {/* Cloud Download Option */}
        <div className="p-5 rounded-lg border-2 transition-all flex flex-col"
             style={{
               backgroundColor: selectedMethod === 'cloud' ? 'var(--theme-primary)/10' : 'var(--theme-bg-tertiary)',
               borderColor: selectedMethod === 'cloud' ? 'var(--theme-primary)' : 'var(--theme-border-primary)',
               minHeight: '280px'
             }}>
          <div className="flex items-center gap-2 mb-3">
            <Cloud size={20} style={{ color: 'var(--theme-info)' }} />
            <h3 className="text-base font-semibold text-themed-primary">Pre-created Data</h3>
          </div>
          <p className="text-sm text-themed-secondary mb-3 min-h-[40px]">
            Download community-maintained depot mappings from GitHub.
            {picsData && (picsData.database?.totalMappings > 0 || picsData.steamKit2?.depotCount > 0) &&
              <span className="text-themed-success"> Will update existing data.</span>
            }
          </p>
          <ul className="text-xs text-themed-muted space-y-1 mb-4 flex-grow">
            <li>✓ Quick setup (~30 seconds)</li>
            <li>✓ 290,000+ mappings ready</li>
            <li>✓ Regularly updated</li>
            <li>✓ Won't delete existing data</li>
          </ul>
          <Button
            variant="filled"
            color="blue"
            size="sm"
            leftSection={initializing && selectedMethod === 'cloud' ? <Loader className="w-3 h-3 animate-spin" /> : <Cloud className="w-3 h-3" />}
            onClick={onDownloadPrecreated}
            disabled={initializing || usingSteamAuth}
            fullWidth
            className="mt-auto"
          >
            {usingSteamAuth ? 'Unavailable (Steam Login)' : initializing && selectedMethod === 'cloud' ? 'Downloading...' : 'Download Pre-created'}
          </Button>
        </div>

        {/* Generate Own Option */}
        <div className="p-5 rounded-lg border-2 transition-all flex flex-col"
             style={{
               backgroundColor: selectedMethod === 'generate' ? 'var(--theme-primary)/10' : 'var(--theme-bg-tertiary)',
               borderColor: selectedMethod === 'generate' ? 'var(--theme-primary)' : 'var(--theme-border-primary)',
               minHeight: '280px'
             }}>
          <div className="flex items-center gap-2 mb-3">
            <Database size={20} style={{ color: 'var(--theme-success)' }} />
            <h3 className="text-base font-semibold text-themed-primary">Generate Fresh</h3>
          </div>
          <p className="text-sm text-themed-secondary mb-3 min-h-[40px]">
            Build your own depot mappings directly from Steam.
            <span className="text-themed-warning"> Always starts fresh - overwrites existing data.</span>
          </p>
          <ul className="text-xs text-themed-muted space-y-1 mb-4 flex-grow">
            <li>✓ Latest data from Steam</li>
            <li>✓ Complete fresh rebuild</li>
            <li>✓ Overwrites any existing data</li>
            <li>○ Takes 10-30 minutes for full scan</li>
          </ul>
          <Button
            variant="filled"
            color="green"
            size="sm"
            leftSection={initializing && selectedMethod === 'generate' ? <Loader className="w-3 h-3 animate-spin" /> : <Database className="w-3 h-3" />}
            onClick={onGenerateOwn}
            disabled={initializing}
            fullWidth
            className="mt-auto"
          >
            {initializing && selectedMethod === 'generate' ? 'Processing...' : 'Generate Fresh Data'}
          </Button>
        </div>

        {/* Continue Option - Show only when JSON data exists */}
        {shouldShowContinueOption() && (
          <div className="p-5 rounded-lg border-2 transition-all flex flex-col"
               style={{
                 backgroundColor: selectedMethod === 'continue' ? 'var(--theme-primary)/10' : 'var(--theme-bg-tertiary)',
                 borderColor: selectedMethod === 'continue' ? 'var(--theme-primary)' : 'var(--theme-border-primary)',
                 minHeight: '280px'
               }}>
            <div className="flex items-center gap-2 mb-3">
              <Database size={20} style={{ color: 'var(--theme-warning)' }} />
              <h3 className="text-base font-semibold text-themed-primary">Continue</h3>
            </div>
            <p className="text-sm text-themed-secondary mb-3 min-h-[40px]">
              Update existing depot mappings with latest changes from Steam.
              <span className="text-themed-success"> Incremental update only.</span>
            </p>
            <ul className="text-xs text-themed-muted space-y-1 mb-4 flex-grow">
              <li>✓ Fast incremental update (~1-2 minutes)</li>
              <li>✓ Uses existing {picsData?.jsonFile?.totalMappings?.toLocaleString()} mappings</li>
              <li>✓ Fetches only new/changed data</li>
              <li>✓ Perfect for regular updates</li>
            </ul>
            <Button
              variant="filled"
              color="orange"
              size="sm"
              leftSection={initializing && selectedMethod === 'continue' ? <Loader className="w-3 h-3 animate-spin" /> : <Database className="w-3 h-3" />}
              onClick={onContinue}
              disabled={initializing}
              fullWidth
              className="mt-auto"
            >
              {initializing && selectedMethod === 'continue' ? 'Updating...' : 'Continue with Update'}
            </Button>
          </div>
        )}
      </div>
    </>
  );
};
