import React, { useState, useEffect } from 'react';
import { Key, Lock, Unlock, AlertCircle } from 'lucide-react';
import authService from '../../services/auth.service';
import { Button } from '../ui/Button';
import { Alert } from '../ui/Alert';
import { Modal } from '../ui/Modal';

interface AuthenticationManagerProps {
  onAuthChange?: (isAuthenticated: boolean) => void;
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
  onApiKeyRegenerated?: () => void;
}

const AuthenticationManager: React.FC<AuthenticationManagerProps> = ({
  onAuthChange,
  onError,
  onSuccess,
  onApiKeyRegenerated
}) => {
  const [isAuthenticated, setIsAuthenticated] = useState(false);
  const [authChecking, setAuthChecking] = useState(true);
  const [showAuthModal, setShowAuthModal] = useState(false);
  const [apiKey, setApiKey] = useState('');
  const [authError, setAuthError] = useState('');
  const [authLoading, setAuthLoading] = useState(false);

  useEffect(() => {
    checkAuth();
  }, []);

  const checkAuth = async () => {
    setAuthChecking(true);
    try {
      const result = await authService.checkAuth();
      setIsAuthenticated(result.isAuthenticated);
      onAuthChange?.(result.isAuthenticated);

      if (!result.isAuthenticated && authService.isRegistered()) {
        authService.clearAuth();
      }
    } catch (error) {
      console.error('Auth check failed:', error);
      setIsAuthenticated(false);
      onAuthChange?.(false);
    } finally {
      setAuthChecking(false);
    }
  };

  const handleAuthenticate = async () => {
    if (!apiKey.trim()) {
      setAuthError('Please enter an API key');
      return;
    }

    setAuthLoading(true);
    setAuthError('');

    try {
      const result = await authService.register(apiKey);

      if (result.success) {
        setIsAuthenticated(true);
        onAuthChange?.(true);
        setShowAuthModal(false);
        setApiKey('');
        onSuccess?.('Authentication successful! You can now use management features.');
      } else {
        setAuthError(result.message || 'Authentication failed');
      }
    } catch (error: any) {
      console.error('Authentication error:', error);
      setAuthError(error.message || 'Authentication failed');
    } finally {
      setAuthLoading(false);
    }
  };

  const handleRegenerateKey = async () => {
    const message =
      'WARNING: This will:\n\n' +
      '1. Generate a NEW API key on the server\n' +
      '2. Revoke ALL existing device registrations\n' +
      '3. Require ALL users to re-authenticate\n' +
      '4. You must check the container logs for the new key\n\n' +
      'This cannot be undone. Continue?';

    if (!window.confirm(message)) return;

    setAuthLoading(true);

    try {
      const result = await authService.regenerateApiKey();

      if (result.success) {
        setIsAuthenticated(false);
        onAuthChange?.(false);
        setShowAuthModal(false);

        onSuccess?.(result.message);

        // Trigger the API key regeneration modal from App.tsx
        setTimeout(() => {
          onApiKeyRegenerated?.();
        }, 1000);
      } else {
        onError?.(result.message || 'Failed to regenerate API key');
      }
    } catch (error: any) {
      console.error('Error regenerating key:', error);
      onError?.('Failed to regenerate API key: ' + error.message);
    } finally {
      setAuthLoading(false);
    }
  };

  if (authChecking) {
    return null;
  }

  return (
    <>
      <Alert
        color={isAuthenticated ? 'green' : 'yellow'}
        icon={isAuthenticated ? <Unlock className="w-5 h-5" /> : <Lock className="w-5 h-5" />}
      >
        <div className="flex items-center justify-between">
          <div>
            <span className="font-medium">
              {isAuthenticated ? 'Authenticated' : 'Not Authenticated'}
            </span>
            <p className="text-xs mt-1 opacity-75">
              {isAuthenticated
                ? 'Management features enabled'
                : 'Management features require API key'}
            </p>
          </div>

          {isAuthenticated ? (
            <Button
              variant="filled"
              color="red"
              size="sm"
              leftSection={<AlertCircle className="w-3 h-3" />}
              onClick={handleRegenerateKey}
              loading={authLoading}
            >
              Regenerate Key
            </Button>
          ) : (
            <Button
              variant="filled"
              color="yellow"
              leftSection={<Key className="w-4 h-4" />}
              onClick={() => setShowAuthModal(true)}
            >
              Authenticate
            </Button>
          )}
        </div>
      </Alert>

      <Modal
        opened={showAuthModal}
        onClose={() => {
          setShowAuthModal(false);
          setApiKey('');
          setAuthError('');
        }}
        title={
          <div className="flex items-center space-x-3">
            <Key className="w-6 h-6 text-themed-warning" />
            <span>Authentication Required</span>
          </div>
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            Management operations require authentication. Please enter your API key to continue.
          </p>

          <div>
            <label className="block text-sm font-medium text-themed-secondary mb-2">API Key</label>
            <input
              type="password"
              value={apiKey}
              onChange={(e) => setApiKey(e.target.value)}
              onKeyPress={(e) => e.key === 'Enter' && handleAuthenticate()}
              placeholder="lm_xxxxxxxxxxxxxxxxxxxxx"
              className="w-full px-3 py-2 themed-input text-themed-primary placeholder-themed-muted focus:outline-none"
              disabled={authLoading}
            />
          </div>

          {authError && <Alert color="red">{authError}</Alert>}

          <Alert color="blue">
            <div>
              <p className="font-medium mb-2">To find your API key:</p>
              <ol className="list-decimal list-inside text-xs space-y-1">
                <li>SSH into your server</li>
                <li>
                  Check the file:{' '}
                  <code className="bg-themed-tertiary px-1 rounded">/data/api_key.txt</code>
                </li>
                <li>Or check the API container logs on startup</li>
              </ol>
            </div>
          </Alert>

          <div className="flex justify-end space-x-3 pt-4 border-t border-themed-secondary">
            <Button
              variant="default"
              onClick={() => {
                setShowAuthModal(false);
                setApiKey('');
                setAuthError('');
              }}
              disabled={authLoading}
            >
              Cancel
            </Button>
            <Button
              variant="filled"
              color="blue"
              leftSection={<Lock className="w-4 h-4" />}
              onClick={handleAuthenticate}
              loading={authLoading}
              disabled={!apiKey.trim()}
            >
              Authenticate
            </Button>
          </div>
        </div>
      </Modal>
    </>
  );
};

export default AuthenticationManager;
