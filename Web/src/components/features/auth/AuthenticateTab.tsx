import React, { useState } from 'react';
import { Key, Lock, AlertCircle } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
import { Alert } from '@components/ui/Alert';
import authService from '@services/auth.service';
import { useAuth } from '@contexts/AuthContext';

const AuthenticateTab: React.FC = () => {
  const { refreshAuth } = useAuth();
  const [apiKey, setApiKey] = useState('');
  const [loading, setLoading] = useState(false);

  // Helper to show toast notifications
  const showToast = (type: 'success' | 'error' | 'info', message: string) => {
    window.dispatchEvent(new CustomEvent('show-toast', {
      detail: { type, message, duration: 4000 }
    }));
  };

  const handleAuthenticate = async () => {
    if (!apiKey.trim()) {
      showToast('error', 'Please enter an API key');
      return;
    }

    setLoading(true);

    try {
      const result = await authService.register(apiKey);

      if (result.success) {
        showToast('success', 'Authentication successful! Upgrading to full access...');
        // Refresh auth context
        await refreshAuth();
        // Clear input
        setApiKey('');

        // Give user a moment to see success message before redirect happens
        setTimeout(() => {
          window.location.reload();
        }, 1500);
      } else {
        showToast('error', result.message || 'Authentication failed');
      }
    } catch (err: unknown) {
      console.error('Authentication error:', err);
      showToast('error', (err instanceof Error ? err.message : String(err)) || 'Authentication failed');
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="container mx-auto px-3 sm:px-4 py-4 sm:py-6 space-y-4 sm:space-y-6">
      {/* Header */}
      <div className="flex items-center gap-3">
        <div className="p-2 rounded-lg flex-shrink-0 bg-themed-accent-subtle">
          <Key className="w-6 h-6 text-themed-accent" />
        </div>
        <div className="min-w-0">
          <h1 className="text-xl sm:text-2xl font-bold text-themed-primary">
            Upgrade to Full Access
          </h1>
          <p className="text-xs sm:text-sm text-themed-secondary">
            Enter your API key to unlock management features
          </p>
        </div>
      </div>

      {/* Authentication Card */}
      <Card>
        <div className="p-6 space-y-6">
          <div>
            <h2 className="text-lg font-semibold mb-4 text-themed-primary">
              Enter API Key
            </h2>
            <p className="text-sm mb-4 text-themed-secondary">
              You are currently in <strong>Guest Mode</strong> with view-only access. Enter your
              API key to gain full management capabilities.
            </p>

            <div className="space-y-4">
              <div>
                <label className="block text-sm font-medium mb-2 text-themed-secondary">
                  API Key
                </label>
                <input
                  type="password"
                  value={apiKey}
                  onChange={(e) => setApiKey(e.target.value)}
                  onKeyPress={(e) => e.key === 'Enter' && handleAuthenticate()}
                  placeholder="lm_xxxxxxxxxxxxxxxxxxxxx"
                  className="w-full px-3 py-2 themed-input text-themed-primary placeholder-themed-muted focus:outline-none bg-themed-secondary border border-themed rounded-lg"
                  disabled={loading}
                />
              </div>

              <Button
                variant="filled"
                color="green"
                size="md"
                leftSection={<Lock className="w-4 h-4" />}
                onClick={handleAuthenticate}
                loading={loading}
                disabled={!apiKey.trim() || loading}
                className="w-full sm:w-auto"
              >
                Authenticate
              </Button>
            </div>
          </div>

          <Alert color="blue">
            <div>
              <p className="font-medium mb-2">To find your API key:</p>
              <ol className="list-decimal list-inside text-sm space-y-1 ml-2">
                <li>SSH into your LANCache Manager server</li>
                <li>
                  Check <code className="bg-themed-tertiary px-1 rounded">/data/api_key.txt</code>
                </li>
                <li>
                  Or check container logs:{' '}
                  <code className="bg-themed-tertiary px-1 rounded">
                    docker logs lancache-manager-api
                  </code>
                </li>
              </ol>
            </div>
          </Alert>
        </div>
      </Card>

      {/* Features Card */}
      <Card>
        <div className="p-6">
          <h2 className="text-lg font-semibold mb-4 text-themed-primary">
            What You'll Get with Full Access
          </h2>
          <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
            <div className="flex items-start gap-3">
              <div className="p-2 rounded-lg flex-shrink-0 bg-themed-success">
                <AlertCircle className="w-5 h-5 text-themed-success" />
              </div>
              <div>
                <h3 className="font-semibold mb-1 text-themed-primary">
                  User Management
                </h3>
                <p className="text-sm text-themed-secondary">
                  View and manage all authenticated users and guest sessions
                </p>
              </div>
            </div>

            <div className="flex items-start gap-3">
              <div className="p-2 rounded-lg flex-shrink-0 bg-themed-success">
                <AlertCircle className="w-5 h-5 text-themed-success" />
              </div>
              <div>
                <h3 className="font-semibold mb-1 text-themed-primary">
                  Cache Management
                </h3>
                <p className="text-sm text-themed-secondary">
                  Clear cache, remove games, and manage cache operations
                </p>
              </div>
            </div>

            <div className="flex items-start gap-3">
              <div className="p-2 rounded-lg flex-shrink-0 bg-themed-success">
                <AlertCircle className="w-5 h-5 text-themed-success" />
              </div>
              <div>
                <h3 className="font-semibold mb-1 text-themed-primary">
                  Database Operations
                </h3>
                <p className="text-sm text-themed-secondary">
                  Reset database, process logs, and manage data
                </p>
              </div>
            </div>

            <div className="flex items-start gap-3">
              <div className="p-2 rounded-lg flex-shrink-0 bg-themed-success">
                <AlertCircle className="w-5 h-5 text-themed-success" />
              </div>
              <div>
                <h3 className="font-semibold mb-1 text-themed-primary">
                  Theme Customization
                </h3>
                <p className="text-sm text-themed-secondary">
                  Create and apply custom themes to personalize your experience
                </p>
              </div>
            </div>
          </div>
        </div>
      </Card>
    </div>
  );
};

export default AuthenticateTab;
