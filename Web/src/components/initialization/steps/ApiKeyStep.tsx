import React from 'react';
import { Key, Eye, Loader } from 'lucide-react';
import { Button } from '@components/ui/Button';

interface ApiKeyStepProps {
  apiKey: string;
  setApiKey: (key: string) => void;
  authenticating: boolean;
  authError: string | null;
  dataAvailable: boolean;
  checkingDataAvailability: boolean;
  apiKeyOnlyMode?: boolean;
  onAuthenticate: () => void;
  onStartGuestMode: () => void;
}

export const ApiKeyStep: React.FC<ApiKeyStepProps> = ({
  apiKey,
  setApiKey,
  authenticating,
  authError,
  dataAvailable,
  checkingDataAvailability,
  apiKeyOnlyMode = false,
  onAuthenticate,
  onStartGuestMode
}) => {
  return (
    <>
      <p className="text-themed-secondary text-center mb-6">
        {apiKeyOnlyMode
          ? 'Your API key has been regenerated. Enter the new API key for full access, or continue as guest to view data only:'
          : 'Enter your API key for full management access, or continue as guest to view data for 6 hours:'
        }
      </p>

      {/* API Key Form */}
      <div className="space-y-4">
        <div>
          <label className="block text-sm font-medium text-themed-primary mb-2">
            API Key
          </label>
          <input
            type="text"
            value={apiKey}
            onChange={(e) => setApiKey(e.target.value)}
            placeholder="Enter your API key here..."
            className="w-full p-3 text-sm themed-input"
            disabled={authenticating}
          />
        </div>

        <div className="flex flex-col gap-3">
          <Button
            variant="filled"
            color="blue"
            leftSection={authenticating ? <Loader className="w-4 h-4 animate-spin" /> : <Key className="w-4 h-4" />}
            onClick={onAuthenticate}
            disabled={authenticating || !apiKey.trim()}
            fullWidth
          >
            {authenticating ? 'Authenticating...' : 'Authenticate'}
          </Button>

          <div className="flex items-center gap-4">
            <div className="flex-1 h-px bg-themed-border"></div>
            <span className="text-xs text-themed-muted">OR</span>
            <div className="flex-1 h-px bg-themed-border"></div>
          </div>

          <Button
            variant="default"
            leftSection={<Eye className="w-4 h-4" />}
            onClick={onStartGuestMode}
            disabled={authenticating || checkingDataAvailability || !dataAvailable}
            fullWidth
            title={!dataAvailable ? 'No data available. Please authenticate first.' : 'View data for 6 hours'}
          >
            {!dataAvailable ? 'Guest Mode (No Data Available)' : 'Continue as Guest (6 hours)'}
          </Button>
        </div>
      </div>

      {/* API Key Help */}
      <div className="mt-6 p-4 rounded-lg"
           style={{
             backgroundColor: 'var(--theme-info-bg)',
             borderColor: 'var(--theme-info)',
             color: 'var(--theme-info-text)'
           }}>
        <p className="text-sm">
          <strong>Where to find your API key:</strong><br/>
          The API key was displayed when you first started the server. Check your server logs for "API Key:" or look in the <code>data/api_key.txt</code> file.
        </p>
      </div>

      {authError && (
        <div className="mt-4 p-4 rounded-lg"
             style={{
               backgroundColor: 'var(--theme-error-bg)',
               borderColor: 'var(--theme-error)',
               color: 'var(--theme-error-text)'
             }}>
          <p className="text-sm">{authError}</p>
        </div>
      )}
    </>
  );
};
