import React, { useState, useEffect } from 'react';
import { Key, Lock, Unlock, Loader, AlertCircle } from 'lucide-react';
import authService from '../../services/auth.service';

const AuthenticationManager = ({ onAuthChange, onError, onSuccess }) => {
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
        } catch (error) {
            console.error('Authentication error:', error);
            setAuthError(error.message || 'Authentication failed');
        } finally {
            setAuthLoading(false);
        }
    };

    const handleRegenerateKey = async () => {
        const message = 'WARNING: This will:\n\n' +
            '1. Generate a NEW API key on the server\n' +
            '2. Revoke ALL existing device registrations\n' +
            '3. Require ALL users to re-authenticate\n' +
            '4. You must check the container logs for the new key\n\n' +
            'This cannot be undone. Continue?';
        
        if (!confirm(message)) return;
        
        setAuthLoading(true);
        
        try {
            const result = await authService.regenerateApiKey();
            
            if (result.success) {
                setIsAuthenticated(false);
                onAuthChange?.(false);
                setShowAuthModal(false);
                
                onError?.('API KEY REGENERATED - Check container logs for new key!');
                onSuccess?.(result.message);
                
                setTimeout(() => {
                    setShowAuthModal(true);
                }, 3000);
            } else {
                onError?.(result.message || 'Failed to regenerate API key');
            }
        } catch (error) {
            console.error('Error regenerating key:', error);
            onError?.('Failed to regenerate API key: ' + error.message);
        } finally {
            setAuthLoading(false);
        }
    };

    const AuthModal = () => (
        <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50">
            <div className="bg-gray-800 rounded-lg p-6 max-w-md w-full mx-4 border border-gray-700">
                <div className="flex items-center space-x-3 mb-4">
                    <Key className="w-6 h-6 text-yellow-500" />
                    <h3 className="text-lg font-semibold text-white">Authentication Required</h3>
                </div>
                
                <p className="text-gray-300 mb-4">
                    Management operations require authentication. Please enter your API key to continue.
                </p>
                
                <div className="space-y-4">
                    <div>
                        <label className="block text-sm font-medium text-gray-300 mb-2">
                            API Key
                        </label>
                        <input
                            type="password"
                            value={apiKey}
                            onChange={(e) => setApiKey(e.target.value)}
                            onKeyPress={(e) => e.key === 'Enter' && handleAuthenticate()}
                            placeholder="lm_xxxxxxxxxxxxxxxxxxxxx"
                            className="w-full px-3 py-2 bg-gray-700 border border-gray-600 rounded text-white placeholder-gray-400 focus:outline-none focus:border-blue-500"
                            disabled={authLoading}
                        />
                    </div>
                    
                    {authError && (
                        <div className="p-3 bg-red-900 bg-opacity-30 rounded border border-red-700">
                            <p className="text-sm text-red-400">{authError}</p>
                        </div>
                    )}
                    
                    <div className="text-xs text-gray-400">
                        <p>To find your API key:</p>
                        <ol className="list-decimal list-inside mt-1 space-y-1">
                            <li>SSH into your server</li>
                            <li>Check the file: <code className="bg-gray-700 px-1 rounded">/data/api_key.txt</code></li>
                            <li>Or check the API container logs on startup</li>
                        </ol>
                    </div>
                    
                    <div className="flex justify-end space-x-3 pt-4 border-t border-gray-700">
                        <button
                            onClick={() => {
                                setShowAuthModal(false);
                                setApiKey('');
                                setAuthError('');
                            }}
                            disabled={authLoading}
                            className="px-4 py-2 bg-gray-600 hover:bg-gray-700 rounded text-white disabled:opacity-50"
                        >
                            Cancel
                        </button>
                        <button
                            onClick={handleAuthenticate}
                            disabled={authLoading || !apiKey.trim()}
                            className="px-4 py-2 bg-blue-600 hover:bg-blue-700 rounded text-white disabled:opacity-50 flex items-center space-x-2"
                        >
                            {authLoading ? (
                                <>
                                    <Loader className="w-4 h-4 animate-spin" />
                                    <span>Authenticating...</span>
                                </>
                            ) : (
                                <>
                                    <Lock className="w-4 h-4" />
                                    <span>Authenticate</span>
                                </>
                            )}
                        </button>
                    </div>
                </div>
            </div>
        </div>
    );

    return (
        <>
            {!authChecking && (
                <div className={`rounded-lg p-4 border ${
                    isAuthenticated 
                        ? 'bg-green-900 bg-opacity-30 border-green-700' 
                        : 'bg-yellow-900 bg-opacity-30 border-yellow-700'
                }`}>
                    <div className="flex items-center justify-between">
                        <div className="flex items-center space-x-3">
                            {isAuthenticated ? (
                                <>
                                    <Unlock className="w-5 h-5 text-green-500" />
                                    <span className="text-green-400">Authenticated - Management features enabled</span>
                                </>
                            ) : (
                                <>
                                    <Lock className="w-5 h-5 text-yellow-500" />
                                    <span className="text-yellow-400">Not authenticated - Management features require API key</span>
                                </>
                            )}
                        </div>
                        <div className="flex items-center space-x-2">
                            {isAuthenticated ? (
                                <button
                                    onClick={handleRegenerateKey}
                                    disabled={authLoading}
                                    className="px-3 py-1.5 bg-red-600 hover:bg-red-700 disabled:opacity-50 rounded text-white text-sm flex items-center space-x-2"
                                    title="Regenerate API key and revoke all devices"
                                >
                                    {authLoading ? (
                                        <Loader className="w-3 h-3 animate-spin" />
                                    ) : (
                                        <AlertCircle className="w-3 h-3" />
                                    )}
                                    <span>Regenerate Key</span>
                                </button>
                            ) : (
                                <button
                                    onClick={() => setShowAuthModal(true)}
                                    className="px-4 py-2 bg-yellow-600 hover:bg-yellow-700 rounded text-white flex items-center space-x-2"
                                >
                                    <Key className="w-4 h-4" />
                                    <span>Authenticate</span>
                                </button>
                            )}
                        </div>
                    </div>
                </div>
            )}

            {showAuthModal && <AuthModal />}
        </>
    );
};

export default AuthenticationManager;