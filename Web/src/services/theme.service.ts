import { API_BASE } from '../utils/constants';
import authService from './auth.service';
import * as TOML from 'toml';

interface ThemeColors {
  // Core colors
  primaryColor?: string;
  secondaryColor?: string;
  accentColor?: string;
  
  // Background colors
  bgPrimary?: string;
  bgSecondary?: string;
  bgTertiary?: string;
  bgHover?: string;
  
  // Text colors
  textPrimary?: string;
  textSecondary?: string;
  textMuted?: string;
  textAccent?: string;
  
  // Border colors
  borderPrimary?: string;
  borderSecondary?: string;
  borderFocus?: string;
  
  // Status colors
  success?: string;
  successBg?: string;
  successText?: string;
  warning?: string;
  warningBg?: string;
  warningText?: string;
  error?: string;
  errorBg?: string;
  errorText?: string;
  info?: string;
  infoBg?: string;
  infoText?: string;
  
  // Service colors
  steamColor?: string;
  epicColor?: string;
  originColor?: string;
  blizzardColor?: string;
  wsusColor?: string;
  riotColor?: string;
  
  // Component colors
  cardBg?: string;
  cardBorder?: string;
  buttonBg?: string;
  buttonHover?: string;
  buttonText?: string;
  inputBg?: string;
  inputBorder?: string;
  inputFocus?: string;
  badgeBg?: string;
  badgeText?: string;
  progressBar?: string;
  progressBg?: string;
  
  // Icon backgrounds
  iconBgBlue?: string;
  iconBgGreen?: string;
  iconBgEmerald?: string;
  iconBgPurple?: string;
  iconBgIndigo?: string;
  iconBgOrange?: string;
  iconBgYellow?: string;
  iconBgCyan?: string;
  iconBgRed?: string;
}

interface ThemeMeta {
  id: string;
  name: string;
  description?: string;
  author?: string;
  version?: string;
  isDark?: boolean;
}

interface Theme {
  meta: ThemeMeta;
  colors: ThemeColors;
  custom?: Record<string, string>;
  css?: { content?: string };
}

class ThemeService {
  private currentTheme: Theme | null = null;
  private themes: Theme[] = [];
  private styleElement: HTMLStyleElement | null = null;

  async loadThemes(): Promise<Theme[]> {
    const builtInThemes = this.getBuiltInThemes();
    
    let apiThemes: Theme[] = [];
    try {
      const response = await fetch(`${API_BASE}/themes`);
      if (response.ok) {
        const themeList = await response.json();
        // Parse TOML themes from API
        for (const themeInfo of themeList) {
          if (themeInfo.format === 'toml') {
            try {
              const themeResponse = await fetch(`${API_BASE}/theme/${themeInfo.id}`);
              if (themeResponse.ok) {
                const tomlContent = await themeResponse.text();
                const theme = this.parseTomlTheme(tomlContent);
                if (theme) {
                  apiThemes.push(theme);
                }
              }
            } catch (error) {
              console.error(`Failed to load theme ${themeInfo.id}:`, error);
            }
          }
        }
      }
    } catch (error) {
      console.error('Failed to load themes from server:', error);
    }
    
    // Combine built-in and server themes
    const allThemes = [...builtInThemes];
    const themeIds = new Set(allThemes.map(t => t.meta.id));
    
    apiThemes.forEach(theme => {
      if (!themeIds.has(theme.meta.id)) {
        allThemes.push(theme);
        themeIds.add(theme.meta.id);
      }
    });
    
    this.themes = allThemes;
    return this.themes;
  }

  private getBuiltInThemes(): Theme[] {
    return [
      {
        meta: {
          id: 'dark-default',
          name: 'Dark Default',
          description: 'Default dark theme with blue accents',
          author: 'System',
          version: '1.0.0',
          isDark: true
        },
        colors: {
          // Core colors
          primaryColor: '#3b82f6',
          secondaryColor: '#8b5cf6',
          accentColor: '#06b6d4',
          
          // Backgrounds
          bgPrimary: '#111827',
          bgSecondary: '#1f2937',
          bgTertiary: '#374151',
          bgHover: '#4b5563',
          
          // Text
          textPrimary: '#ffffff',
          textSecondary: '#d1d5db',
          textMuted: '#9ca3af',
          textAccent: '#60a5fa',
          
          // Borders
          borderPrimary: '#374151',
          borderSecondary: '#4b5563',
          borderFocus: '#3b82f6',
          
          // Status colors
          success: '#10b981',
          successBg: '#064e3b',
          successText: '#34d399',
          warning: '#f59e0b',
          warningBg: '#78350f',
          warningText: '#fbbf24',
          error: '#ef4444',
          errorBg: '#7f1d1d',
          errorText: '#fca5a5',
          info: '#3b82f6',
          infoBg: '#1e3a8a',
          infoText: '#93c5fd',
          
          // Service colors
          steamColor: '#3b82f6',
          epicColor: '#8b5cf6',
          originColor: '#10b981',
          blizzardColor: '#ef4444',
          wsusColor: '#06b6d4',
          riotColor: '#f59e0b',
          
          // Components
          cardBg: '#1f2937',
          cardBorder: '#374151',
          buttonBg: '#3b82f6',
          buttonHover: '#2563eb',
          buttonText: '#ffffff',
          inputBg: '#374151',
          inputBorder: '#4b5563',
          inputFocus: '#3b82f6',
          badgeBg: '#3b82f6',
          badgeText: '#ffffff',
          progressBar: '#3b82f6',
          progressBg: '#374151',
          
          // Icon backgrounds
          iconBgBlue: '#3b82f6',
          iconBgGreen: '#10b981',
          iconBgEmerald: '#10b981',
          iconBgPurple: '#8b5cf6',
          iconBgIndigo: '#6366f1',
          iconBgOrange: '#f97316',
          iconBgYellow: '#eab308',
          iconBgCyan: '#06b6d4',
          iconBgRed: '#ef4444'
        }
      },
      {
        meta: {
          id: 'light-default',
          name: 'Light Default',
          description: 'Default light theme with blue accents',
          author: 'System',
          version: '1.0.0',
          isDark: false
        },
        colors: {
          primaryColor: '#3b82f6',
          secondaryColor: '#8b5cf6',
          accentColor: '#06b6d4',
          bgPrimary: '#ffffff',
          bgSecondary: '#f9fafb',
          bgTertiary: '#f3f4f6',
          bgHover: '#e5e7eb',
          textPrimary: '#111827',
          textSecondary: '#374151',
          textMuted: '#6b7280',
          textAccent: '#2563eb',
          borderPrimary: '#e5e7eb',
          borderSecondary: '#d1d5db',
          borderFocus: '#3b82f6',
          success: '#10b981',
          successBg: '#d1fae5',
          successText: '#065f46',
          warning: '#f59e0b',
          warningBg: '#fef3c7',
          warningText: '#92400e',
          error: '#ef4444',
          errorBg: '#fee2e2',
          errorText: '#991b1b',
          info: '#3b82f6',
          infoBg: '#dbeafe',
          infoText: '#1e40af',
          steamColor: '#3b82f6',
          epicColor: '#8b5cf6',
          originColor: '#10b981',
          blizzardColor: '#ef4444',
          wsusColor: '#06b6d4',
          riotColor: '#f59e0b',
          cardBg: '#ffffff',
          cardBorder: '#e5e7eb',
          buttonBg: '#3b82f6',
          buttonHover: '#2563eb',
          buttonText: '#ffffff',
          inputBg: '#ffffff',
          inputBorder: '#d1d5db',
          inputFocus: '#3b82f6',
          badgeBg: '#3b82f6',
          badgeText: '#ffffff',
          progressBar: '#3b82f6',
          progressBg: '#e5e7eb',
          iconBgBlue: '#3b82f6',
          iconBgGreen: '#10b981',
          iconBgEmerald: '#10b981',
          iconBgPurple: '#8b5cf6',
          iconBgIndigo: '#6366f1',
          iconBgOrange: '#f97316',
          iconBgYellow: '#eab308',
          iconBgCyan: '#06b6d4',
          iconBgRed: '#ef4444'
        }
      }
    ];
  }

  async getTheme(themeId: string): Promise<Theme | null> {
    const builtIn = this.getBuiltInThemes().find(t => t.meta.id === themeId);
    if (builtIn) return builtIn;

    const loaded = this.themes.find(t => t.meta.id === themeId);
    if (loaded) return loaded;

    try {
      const response = await fetch(`${API_BASE}/theme/${themeId}`);
      if (!response.ok) return null;
      
      const tomlText = await response.text();
      return this.parseTomlTheme(tomlText);
    } catch (error) {
      console.error('Error loading theme:', error);
      return null;
    }
  }

  parseTomlTheme(tomlText: string): Theme | null {
    try {
      const parsed = TOML.parse(tomlText);
      
      if (!parsed.meta || !parsed.meta.id || !parsed.meta.name) {
        console.error('Invalid theme: missing meta.id or meta.name');
        return null;
      }

      if (!parsed.colors) {
        console.error('Invalid theme: missing colors section');
        return null;
      }

      return parsed as Theme;
    } catch (error) {
      console.error('Error parsing TOML theme:', error);
      return null;
    }
  }

  async uploadTheme(file: File): Promise<Theme> {
    const text = await file.text();
    const theme = this.parseTomlTheme(text);
    
    if (!theme) {
      throw new Error('Invalid TOML theme format');
    }

    // Upload to server
    const formData = new FormData();
    formData.append('file', file);
    
    const response = await fetch(`${API_BASE}/theme/upload`, {
      method: 'POST',
      headers: authService.getAuthHeaders(),
      body: formData
    });

    if (!response.ok) {
      const error = await response.json().catch(() => ({ error: 'Failed to upload theme' }));
      throw new Error(error.error || 'Failed to upload theme');
    }

    // Reload themes from server
    await this.loadThemes();
    
    return theme;
  }

  async deleteTheme(themeId: string): Promise<void> {
    const response = await fetch(`${API_BASE}/theme/${themeId}`, {
      method: 'DELETE',
      headers: authService.getAuthHeaders()
    });

    if (!response.ok) {
      const error = await response.json().catch(() => ({ error: 'Failed to delete theme' }));
      throw new Error(error.error || 'Failed to delete theme');
    }

    // Reload themes from server
    await this.loadThemes();
  }

  private applyDefaultVariables(): void {
    // Create default CSS variables that match Tailwind dark theme
    const defaultStyles = `
      /* Default CSS Variables for Tailwind Dark Theme */
      :root {
        --theme-primary: #3b82f6;
        --theme-secondary: #8b5cf6;
        --theme-accent: #06b6d4;
        --theme-bg-primary: #111827;
        --theme-bg-secondary: #1f2937;
        --theme-bg-tertiary: #374151;
        --theme-bg-hover: #4b5563;
        --theme-text-primary: #ffffff;
        --theme-text-secondary: #d1d5db;
        --theme-text-muted: #9ca3af;
        --theme-text-accent: #60a5fa;
        --theme-border-primary: #374151;
        --theme-border-secondary: #4b5563;
        --theme-border-focus: #3b82f6;
        --theme-success: #10b981;
        --theme-success-bg: #064e3b;
        --theme-success-text: #34d399;
        --theme-warning: #f59e0b;
        --theme-warning-bg: #78350f;
        --theme-warning-text: #fbbf24;
        --theme-error: #ef4444;
        --theme-error-bg: #7f1d1d;
        --theme-error-text: #fca5a5;
        --theme-info: #3b82f6;
        --theme-info-bg: #1e3a8a;
        --theme-info-text: #93c5fd;
        --theme-steam: #3b82f6;
        --theme-epic: #8b5cf6;
        --theme-origin: #10b981;
        --theme-blizzard: #ef4444;
        --theme-wsus: #06b6d4;
        --theme-riot: #f59e0b;
        --theme-card-bg: #1f2937;
        --theme-card-border: #374151;
        --theme-button-bg: #3b82f6;
        --theme-button-hover: #2563eb;
        --theme-button-text: #ffffff;
        --theme-input-bg: #374151;
        --theme-input-border: #4b5563;
        --theme-input-focus: #3b82f6;
        --theme-badge-bg: #3b82f6;
        --theme-badge-text: #ffffff;
        --theme-progress-bar: #3b82f6;
        --theme-progress-bg: #374151;
        --theme-icon-blue: #3b82f6;
        --theme-icon-green: #10b981;
        --theme-icon-emerald: #10b981;
        --theme-icon-purple: #8b5cf6;
        --theme-icon-indigo: #6366f1;
        --theme-icon-orange: #f97316;
        --theme-icon-yellow: #eab308;
        --theme-icon-cyan: #06b6d4;
        --theme-icon-red: #ef4444;
      }
    `;

    // Check if default variables style element exists
    let defaultStyleElement = document.getElementById('lancache-default-vars');
    if (!defaultStyleElement) {
      defaultStyleElement = document.createElement('style');
      defaultStyleElement.id = 'lancache-default-vars';
      document.head.appendChild(defaultStyleElement);
    }
    defaultStyleElement.textContent = defaultStyles;
  }

  clearTheme(): void {
    if (this.styleElement) {
      this.styleElement.remove();
      this.styleElement = null;
    }
    
    const root = document.documentElement;
    root.removeAttribute('data-theme');
    root.removeAttribute('data-theme-id');
    
    localStorage.removeItem('lancache_theme');
    localStorage.removeItem('lancache_theme_applied');
    this.currentTheme = null;
    
    // Reapply default variables
    this.applyDefaultVariables();
  }

  applyTheme(theme: Theme): void {
    if (!theme || !theme.colors) return;

    if (this.styleElement) {
      this.styleElement.remove();
      this.styleElement = null;
    }

    const colors = theme.colors;

    // Create comprehensive theme styles
    const themeStyles = `
      /* CSS Variables */
      :root {
        --theme-primary: ${colors.primaryColor || '#3b82f6'};
        --theme-secondary: ${colors.secondaryColor || '#8b5cf6'};
        --theme-accent: ${colors.accentColor || '#06b6d4'};
        --theme-bg-primary: ${colors.bgPrimary || '#111827'};
        --theme-bg-secondary: ${colors.bgSecondary || '#1f2937'};
        --theme-bg-tertiary: ${colors.bgTertiary || '#374151'};
        --theme-bg-hover: ${colors.bgHover || '#4b5563'};
        --theme-text-primary: ${colors.textPrimary || '#ffffff'};
        --theme-text-secondary: ${colors.textSecondary || '#d1d5db'};
        --theme-text-muted: ${colors.textMuted || '#9ca3af'};
        --theme-text-accent: ${colors.textAccent || '#60a5fa'};
        --theme-border-primary: ${colors.borderPrimary || '#374151'};
        --theme-border-secondary: ${colors.borderSecondary || '#4b5563'};
        --theme-border-focus: ${colors.borderFocus || '#3b82f6'};
        --theme-success: ${colors.success || '#10b981'};
        --theme-success-bg: ${colors.successBg || '#064e3b'};
        --theme-success-text: ${colors.successText || '#34d399'};
        --theme-warning: ${colors.warning || '#f59e0b'};
        --theme-warning-bg: ${colors.warningBg || '#78350f'};
        --theme-warning-text: ${colors.warningText || '#fbbf24'};
        --theme-error: ${colors.error || '#ef4444'};
        --theme-error-bg: ${colors.errorBg || '#7f1d1d'};
        --theme-error-text: ${colors.errorText || '#fca5a5'};
        --theme-info: ${colors.info || '#3b82f6'};
        --theme-info-bg: ${colors.infoBg || '#1e3a8a'};
        --theme-info-text: ${colors.infoText || '#93c5fd'};
        --theme-steam: ${colors.steamColor || '#3b82f6'};
        --theme-epic: ${colors.epicColor || '#8b5cf6'};
        --theme-origin: ${colors.originColor || '#10b981'};
        --theme-blizzard: ${colors.blizzardColor || '#ef4444'};
        --theme-wsus: ${colors.wsusColor || '#06b6d4'};
        --theme-riot: ${colors.riotColor || '#f59e0b'};
        --theme-card-bg: ${colors.cardBg || colors.bgSecondary || '#1f2937'};
        --theme-card-border: ${colors.cardBorder || colors.borderPrimary || '#374151'};
        --theme-button-bg: ${colors.buttonBg || colors.primaryColor || '#3b82f6'};
        --theme-button-hover: ${colors.buttonHover || '#2563eb'};
        --theme-button-text: ${colors.buttonText || '#ffffff'};
        --theme-input-bg: ${colors.inputBg || colors.bgTertiary || '#374151'};
        --theme-input-border: ${colors.inputBorder || colors.borderSecondary || '#4b5563'};
        --theme-input-focus: ${colors.inputFocus || colors.primaryColor || '#3b82f6'};
        --theme-badge-bg: ${colors.badgeBg || colors.primaryColor || '#3b82f6'};
        --theme-badge-text: ${colors.badgeText || '#ffffff'};
        --theme-progress-bar: ${colors.progressBar || colors.primaryColor || '#3b82f6'};
        --theme-progress-bg: ${colors.progressBg || colors.bgTertiary || '#374151'};
        --theme-icon-blue: ${colors.iconBgBlue || '#3b82f6'};
        --theme-icon-green: ${colors.iconBgGreen || '#10b981'};
        --theme-icon-emerald: ${colors.iconBgEmerald || '#10b981'};
        --theme-icon-purple: ${colors.iconBgPurple || '#8b5cf6'};
        --theme-icon-indigo: ${colors.iconBgIndigo || '#6366f1'};
        --theme-icon-orange: ${colors.iconBgOrange || '#f97316'};
        --theme-icon-yellow: ${colors.iconBgYellow || '#eab308'};
        --theme-icon-cyan: ${colors.iconBgCyan || '#06b6d4'};
        --theme-icon-red: ${colors.iconBgRed || '#ef4444'};
      }

      /* Apply theme styles with proper specificity */
      body * {
        transition: background-color 0.2s ease, color 0.2s ease, border-color 0.2s ease;
      }

      /* Global body styles */
      body {
        background-color: var(--theme-bg-primary) !important;
        color: var(--theme-text-primary) !important;
      }

      /* Background overrides */
      .bg-gray-900, body { background-color: var(--theme-bg-primary) !important; }
      .bg-gray-800 { background-color: var(--theme-bg-secondary) !important; }
      .bg-gray-700 { background-color: var(--theme-bg-tertiary) !important; }
      .bg-gray-600 { background-color: var(--theme-bg-hover) !important; }
      .hover\\:bg-gray-700:hover { background-color: var(--theme-bg-hover) !important; }
      .hover\\:bg-gray-600:hover { background-color: var(--theme-bg-hover) !important; opacity: 0.8; }

      /* Text color overrides */
      .text-white, .text-gray-100, .text-gray-200 { color: var(--theme-text-primary) !important; }
      .text-gray-300, .text-gray-400 { color: var(--theme-text-secondary) !important; }
      .text-gray-500, .text-gray-600 { color: var(--theme-text-muted) !important; }

      /* Border overrides */
      .border-gray-700 { border-color: var(--theme-border-primary) !important; }
      .border-gray-600 { border-color: var(--theme-border-secondary) !important; }

      /* Status colors */
      .text-green-400, .text-green-500 { color: var(--theme-success-text) !important; }
      .text-yellow-400, .text-yellow-500 { color: var(--theme-warning-text) !important; }
      .text-red-400, .text-red-500 { color: var(--theme-error-text) !important; }
      .text-blue-400, .text-blue-500 { color: var(--theme-info-text) !important; }

      /* Buttons */
      .bg-blue-600:not([role="progressbar"]) {
        background-color: var(--theme-button-bg) !important;
      }
      .hover\\:bg-blue-700:hover {
        background-color: var(--theme-button-hover) !important;
      }

      /* Cards */
      .bg-gray-800.rounded-lg {
        background-color: var(--theme-card-bg) !important;
      }
      .border.border-gray-700 {
        border-color: var(--theme-card-border) !important;
      }

      /* Inputs */
      input, select, textarea {
        background-color: var(--theme-input-bg) !important;
        border-color: var(--theme-input-border) !important;
        color: var(--theme-text-primary) !important;
      }
      input:focus, select:focus, textarea:focus {
        border-color: var(--theme-input-focus) !important;
        outline-color: var(--theme-input-focus) !important;
      }

      /* Custom CSS if provided */
      ${theme.css?.content || ''}
    `;

    // Create and inject the style element
    this.styleElement = document.createElement('style');
    this.styleElement.id = 'lancache-theme';
    this.styleElement.textContent = themeStyles;
    document.head.appendChild(this.styleElement);

    // Set data attributes
    const root = document.documentElement;
    root.setAttribute('data-theme', theme.meta.isDark ? 'dark' : 'light');
    root.setAttribute('data-theme-id', theme.meta.id);

    // Save to localStorage
    localStorage.setItem('lancache_theme', theme.meta.id);
    localStorage.setItem('lancache_theme_applied', 'true');
    this.currentTheme = theme;

    // Force re-render
    window.dispatchEvent(new Event('themechange'));
  }

  async loadSavedTheme(): Promise<void> {
    // Always apply default CSS variables first
    this.applyDefaultVariables();
    
    // Load themes from server
    await this.loadThemes();
    
    // Check if user has a saved theme preference
    const themeApplied = localStorage.getItem('lancache_theme_applied') === 'true';
    const savedThemeId = localStorage.getItem('lancache_theme');
    
    if (themeApplied && savedThemeId) {
      // User has a saved theme - apply it
      const theme = await this.getTheme(savedThemeId);
      if (theme) {
        this.applyTheme(theme);
      }
    } else if (!themeApplied) {
      // First time user - apply dark-default theme automatically
      const darkDefault = await this.getTheme('dark-default');
      if (darkDefault) {
        this.applyTheme(darkDefault);
      }
    }
  }

  getCurrentThemeId(): string {
    const applied = localStorage.getItem('lancache_theme_applied') === 'true';
    if (!applied) return 'dark-default';
    return localStorage.getItem('lancache_theme') || 'dark-default';
  }

  getCurrentTheme(): Theme | null {
    const applied = localStorage.getItem('lancache_theme_applied') === 'true';
    if (!applied) {
      return this.getBuiltInThemes().find(t => t.meta.id === 'dark-default') || null;
    }
    return this.currentTheme;
  }

  isThemeApplied(): boolean {
    return localStorage.getItem('lancache_theme_applied') === 'true';
  }

  exportTheme(theme: Theme): string {
    let toml = '';
    
    toml += '[meta]\n';
    toml += `name = "${theme.meta.name}"\n`;
    toml += `id = "${theme.meta.id}"\n`;
    if (theme.meta.description) toml += `description = "${theme.meta.description}"\n`;
    if (theme.meta.author) toml += `author = "${theme.meta.author}"\n`;
    if (theme.meta.version) toml += `version = "${theme.meta.version}"\n`;
    if (theme.meta.isDark !== undefined) toml += `isDark = ${theme.meta.isDark}\n`;
    toml += '\n';
    
    toml += '[colors]\n';
    if (theme.colors) {
      Object.entries(theme.colors).forEach(([key, value]) => {
        toml += `${key} = "${value}"\n`;
      });
    }
    toml += '\n';
    
    if (theme.custom && Object.keys(theme.custom).length > 0) {
      toml += '[custom]\n';
      Object.entries(theme.custom).forEach(([key, value]) => {
        toml += `"${key}" = "${value}"\n`;
      });
      toml += '\n';
    }
    
    if (theme.css?.content) {
      toml += '[css]\n';
      toml += `content = """\n${theme.css.content}\n"""\n`;
    }
    
    return toml;
  }
}

export default new ThemeService();