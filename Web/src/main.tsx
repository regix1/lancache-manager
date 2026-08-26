import ReactDOM from 'react-dom/client';
import App from './App';
import './index.css';
import '@/i18n';
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

// The downloads list used to mirror its page and page size into the query string. It owns both in
// component state now, so a bookmark or a shared link still carrying them would show stale text that
// nothing reads. Drop just those two and leave every other param alone.
const dropRetiredListParams = (): void => {
  const url = new URL(window.location.href);
  if (!url.searchParams.has('page') && !url.searchParams.has('pageSize')) {
    return;
  }
  url.searchParams.delete('page');
  url.searchParams.delete('pageSize');
  window.history.replaceState(window.history.state, '', `${url.pathname}${url.search}${url.hash}`);
};

const renderApp = (): void => {
  dropRetiredListParams();
  const rootEl = document.getElementById('root');
  if (rootEl === null) {
    console.error('[Fatal] Missing root element');
    return;
  }

  ReactDOM.createRoot(rootEl).render(<App />);
};

renderApp();
