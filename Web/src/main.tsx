import React from 'react';
import ReactDOM from 'react-dom/client';
import App from './App';
import './index.css';
import themeService from './services/theme.service';

// Load saved theme ONLY if user has explicitly applied one
// Otherwise use default Tailwind dark theme
themeService.loadSavedTheme();

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>
);
