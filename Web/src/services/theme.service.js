import { API_BASE } from '../utils/constants';
import authService from './auth.service';

class ThemeService {
  constructor() {
    this.currentTheme = null;
    this.themes = [];
  }

  async loadThemes() {
    try {
      const response = await fetch(`${API_BASE}/theme`);
      if (!response.ok) throw new Error('Failed to load themes');
      this.themes = await response.json();
      return this.themes;
    } catch (error) {
      console.error('Error loading themes:', error);
      return [];
    }
  }

  async getTheme(themeId) {
    try {
      const response = await fetch(`${API_BASE}/theme/${themeId}`);
      if (!response.ok) throw new Error('Failed to load theme');
      return await response.json();
    } catch (error) {
      console.error('Error loading theme:', error);
      return null;
    }
  }

  async uploadTheme(file) {
    const formData = new FormData();
    formData.append('file', file);

    // Use the auth service to get proper headers
    const headers = authService.getAuthHeaders();

    const response = await fetch(`${API_BASE}/theme/upload`, {
      method: 'POST',
      headers,
      body: formData
    });

    if (!response.ok) {
      const error = await response.json();
      throw new Error(error.error || 'Failed to upload theme');
    }

    return await response.json();
  }

  async deleteTheme(themeId) {
    // Use the auth service to get proper headers
    const headers = authService.getAuthHeaders();

    const response = await fetch(`${API_BASE}/theme/${themeId}`, {
      method: 'DELETE',
      headers
    });

    if (!response.ok) {
      const error = await response.json();
      throw new Error(error.error || 'Failed to delete theme');
    }

    return await response.json();
  }

  applyTheme(theme) {
    if (!theme || !theme.colors) return;

    const root = document.documentElement;
    
    // Set theme attribute for CSS targeting (light or dark)
    const isLight = theme.id?.includes('light') || 
                    theme.name?.toLowerCase().includes('light');
    root.setAttribute('data-theme', isLight ? 'light' : 'dark');
    
    // Apply ALL color variables - ensure none are missed
    const defaultColors = {
      '--bg-primary': isLight ? '#ffffff' : '#111827',
      '--bg-secondary': isLight ? '#f9fafb' : '#1f2937',
      '--bg-tertiary': isLight ? '#f3f4f6' : '#374151',
      '--bg-hover': isLight ? '#e5e7eb' : '#4b5563',
      '--bg-input': isLight ? '#ffffff' : '#374151',
      '--bg-dropdown': isLight ? '#ffffff' : '#1f2937',
      '--bg-dropdown-hover': isLight ? '#e5e7eb' : '#374151',
      '--bg-nav': isLight ? '#ffffff' : '#1f2937',
      '--border-primary': isLight ? '#e5e7eb' : '#374151',
      '--border-secondary': isLight ? '#d1d5db' : '#4b5563',
      '--border-input': isLight ? '#d1d5db' : '#4b5563',
      '--border-nav': isLight ? '#e5e7eb' : '#374151',
      '--border-dropdown': isLight ? '#9ca3af' : '#374151',
      '--text-primary': isLight ? '#111827' : '#ffffff',
      '--text-secondary': isLight ? '#374151' : '#d1d5db',
      '--text-muted': isLight ? '#6b7280' : '#9ca3af',
      '--text-disabled': isLight ? '#9ca3af' : '#6b7280',
      '--text-button': '#ffffff',
      '--text-dropdown': isLight ? '#111827' : '#ffffff',
      '--text-dropdown-item': isLight ? '#111827' : '#ffffff',
      '--text-input': isLight ? '#111827' : '#ffffff',
      '--text-placeholder': '#9ca3af',
      '--text-nav': isLight ? '#374151' : '#d1d5db',
      '--text-nav-active': isLight ? '#1d4ed8' : '#3b82f6',
      '--icon-primary': isLight ? '#6b7280' : '#d1d5db',
      '--icon-button': '#ffffff',
      '--icon-muted': '#9ca3af'
    };
    
    // First apply defaults, then override with theme colors
    Object.entries(defaultColors).forEach(([key, value]) => {
      if (!theme.colors[key]) {
        root.style.setProperty(key, value);
      }
    });
    
    // Apply theme colors
    Object.entries(theme.colors).forEach(([key, value]) => {
      root.style.setProperty(key, value);
    });

    // Apply custom properties if any
    if (theme.custom) {
      Object.entries(theme.custom).forEach(([key, value]) => {
        root.style.setProperty(key, value);
      });
    }

    // Force a repaint to ensure all styles are applied
    root.style.display = 'none';
    root.offsetHeight; // Trigger reflow
    root.style.display = '';

    // Save to localStorage
    localStorage.setItem('lancache_theme', theme.id || 'dark-default');
    this.currentTheme = theme;
  }

  async loadSavedTheme() {
    const savedThemeId = localStorage.getItem('lancache_theme') || 'dark-default';
    const theme = await this.getTheme(savedThemeId);
    if (theme) {
      this.applyTheme(theme);
    }
  }

  getCurrentThemeId() {
    return localStorage.getItem('lancache_theme') || 'dark-default';
  }
}

export default new ThemeService();