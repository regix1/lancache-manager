import React from 'react';
import ReactDOM from 'react-dom/client';
import App from './App';
import './index.css';
import themeService from './services/theme.service';
import { initializeFavicon } from './utils/favicon';

// Load saved theme ONLY if user has explicitly applied one
// Otherwise use default Tailwind dark theme
themeService.loadSavedTheme().then(() => {
  // Initialize dynamic favicon after theme is loaded
  initializeFavicon();
});

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>
);
