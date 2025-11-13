import React from 'react';
import ReactDOM from 'react-dom/client';
import App from './App';
import './index.css';
import themeService from './services/theme.service';
import preferencesService from './services/preferences.service';
import { initializeFavicon } from './utils/favicon';

// Load saved theme, preferences, and initialize favicon at startup
// Migration will happen after authentication in App.tsx
Promise.all([
  preferencesService.loadPreferences().catch((err) => {
    console.warn('[Init] Failed to load preferences:', err);
    return null;
  }),
  themeService.loadSavedTheme()
])
  .then(() => {
    // Initialize dynamic favicon after theme is loaded
    initializeFavicon();

    // Setup preference listeners for live updates
    themeService.setupPreferenceListeners();

    // Listen for guest session creation to reload preferences
    window.addEventListener('guest-session-created', () => {
      console.log('[Init] Guest session created, reloading preferences...');
      preferencesService.clearCache();
      preferencesService.loadPreferences()
        .then(() => {
          console.log('[Init] Preferences reloaded after guest session creation');
        })
        .catch((err) => {
          console.warn('[Init] Failed to reload preferences after guest session creation:', err);
        });
    });

    // Note: SignalR listener for preferences is setup in App.tsx after SignalR connection is established
    console.log('[Init] Initialization complete');
  })
  .catch((error) => {
    console.error('[Init] Error during initialization:', error);
    // Continue even if loading fails
    initializeFavicon();

    // Setup listeners even if theme loading fails
    themeService.setupPreferenceListeners();
  });

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>
);
