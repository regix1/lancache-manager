import ReactDOM from 'react-dom/client';
import App from './App';
import './index.css';
import './i18n';
import themeService from './services/theme.service';
import preferencesService from './services/preferences.service';
import { initializeFavicon } from './utils/favicon';

// Load preferences first, then theme based on preference
// Migration will happen after authentication in App.tsx
preferencesService.loadPreferences()
  .then((preferences) => {
    // Load theme based on the selectedTheme preference from the API
    return themeService.loadSavedTheme(preferences?.selectedTheme);
  })
  .catch((err) => {
    console.warn('[Init] Failed to load preferences:', err);
    // Still load theme with no preference (will use localStorage/default)
    return themeService.loadSavedTheme();
  })
  .then(() => {
    // Initialize dynamic favicon after theme is loaded
    initializeFavicon();

    // Setup preference listeners for live updates
    themeService.setupPreferenceListeners();

    // Listen for guest session creation to reload preferences and reapply theme
    window.addEventListener('guest-session-created', () => {
      console.log('[Init] Guest session created, reloading preferences...');
      preferencesService.clearCache();
      preferencesService.loadPreferences()
        .then((preferences) => {
          console.log('[Init] Preferences reloaded after guest session creation');
          // Reapply theme based on new preferences
          return themeService.loadSavedTheme(preferences?.selectedTheme);
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
  <App />
);
