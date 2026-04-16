import ReactDOM from 'react-dom/client';
import { NuqsAdapter } from 'nuqs/adapters/react';
import App from './App';
import './index.css';
import './i18n';
import themeService from './services/theme.service';
import { initializeFavicon } from './utils/favicon';

// Bootstrap the UI with cached/default theme values only.
// Authenticated preference hydration happens after auth settles inside the app.
themeService
  .loadSavedTheme()
  .catch((err) => {
    console.warn('[Init] Failed to load saved theme:', err);
    return themeService.loadSavedTheme();
  })
  .then(() => {
    // Initialize dynamic favicon after theme is loaded
    initializeFavicon();

    // Setup preference listeners for live updates
    themeService.setupPreferenceListeners();

    // Note: SignalR listener for preferences is setup in App.tsx after SignalR connection is established
  })
  .catch((error) => {
    console.error('[Init] Error during initialization:', error);
    // Continue even if loading fails
    initializeFavicon();

    // Setup listeners even if theme loading fails
    themeService.setupPreferenceListeners();
  });

const renderApp = (): void => {
  const rootEl = document.getElementById('root');
  if (rootEl === null) {
    console.error('[Fatal] Missing root element');
    return;
  }

  ReactDOM.createRoot(rootEl).render(
    <NuqsAdapter>
      <App />
    </NuqsAdapter>
  );
};

renderApp();
