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
  
  // Drag handle colors
  dragHandleColor?: string;
  dragHandleHover?: string;

  // Border colors
  borderPrimary?: string;
  borderSecondary?: string;
  borderFocus?: string;

  // Navigation specific colors
  navBg?: string;
  navBorder?: string;
  navTabActive?: string;
  navTabInactive?: string;
  navTabHover?: string;
  navTabActiveBorder?: string;
  navMobileMenuBg?: string;
  navMobileItemHover?: string;

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

  // Hit rate specific colors
  hitRateHighBg?: string;
  hitRateHighText?: string;
  hitRateMediumBg?: string;
  hitRateMediumText?: string;
  hitRateLowBg?: string;
  hitRateLowText?: string;
  hitRateWarningBg?: string;
  hitRateWarningText?: string;

  // Action button colors
  actionResetBg?: string;
  actionResetHover?: string;
  actionProcessBg?: string;
  actionProcessHover?: string;
  actionDeleteBg?: string;
  actionDeleteHover?: string;

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

  // Chart colors
  chartColor1?: string;
  chartColor2?: string;
  chartColor3?: string;
  chartColor4?: string;
  chartColor5?: string;
  chartColor6?: string;
  chartColor7?: string;
  chartColor8?: string;
  chartBorderColor?: string;
  chartGridColor?: string;
  chartTextColor?: string;
  chartCacheHitColor?: string;
  chartCacheMissColor?: string;

  // Scrollbar colors
  scrollbarTrack?: string;
  scrollbarThumb?: string;
  scrollbarHover?: string;
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
  private styleElement: HTMLStyleElement | null = null;

  async loadThemes(): Promise<Theme[]> {
    const builtInThemes = this.getBuiltInThemes();

    const apiThemes: Theme[] = [];
    const deletedThemeIds: string[] = [];

    try {
      const response = await fetch(`${API_BASE}/theme`);
      if (response.ok) {
        const themeList = await response.json();

        for (const themeInfo of themeList) {
          if (themeInfo.format === 'toml') {
            try {
              const themeResponse = await fetch(`${API_BASE}/theme/${themeInfo.id}`);

              if (themeResponse.status === 404) {
                deletedThemeIds.push(themeInfo.id);
                console.log(`Theme ${themeInfo.id} no longer exists on server`);
                continue;
              }

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

        if (deletedThemeIds.length > 0) {
          // If current theme was deleted, reset to default
          if (this.currentTheme && deletedThemeIds.includes(this.currentTheme.meta.id)) {
            console.log(`Current theme ${this.currentTheme.meta.id} was deleted, resetting to default`);
            const darkDefault = builtInThemes.find((t) => t.meta.id === 'dark-default');
            if (darkDefault) {
              this.applyTheme(darkDefault);
            }
          }
        }
      }
    } catch (error) {
      console.error('Failed to load themes from server:', error);
    }

    const allThemes = [...builtInThemes];
    const themeIds = new Set(allThemes.map((t) => t.meta.id));

    apiThemes.forEach((theme) => {
      if (!themeIds.has(theme.meta.id)) {
        allThemes.push(theme);
        themeIds.add(theme.meta.id);
      }
    });

    return allThemes;
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
          
          // Drag handle
          dragHandleColor: '#6b7280',
          dragHandleHover: '#60a5fa',

          // Borders
          borderPrimary: '#374151',
          borderSecondary: '#4b5563',
          borderFocus: '#3b82f6',

          // Navigation
          navBg: '#1f2937',
          navBorder: '#374151',
          navTabActive: '#3b82f6',
          navTabInactive: '#9ca3af',
          navTabHover: '#ffffff',
          navTabActiveBorder: '#3b82f6',
          navMobileMenuBg: '#1f2937',
          navMobileItemHover: '#374151',

          // Status colors
          success: '#10b981',
          successBg: '#064e3b',
          successText: '#34d399',
          warning: '#fb923c',
          warningBg: '#44403c', // Softer warm grey-brown
          warningText: '#fcd34d', // Bright golden yellow
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

          // Hit rate specific - MUCH PRETTIER COLORS
          hitRateHighBg: '#064e3b',
          hitRateHighText: '#34d399',
          hitRateMediumBg: '#1e3a8a',
          hitRateMediumText: '#93c5fd',
          hitRateLowBg: '#44403c', // Warm neutral grey-brown
          hitRateLowText: '#fbbf24', // Bright amber
          hitRateWarningBg: '#44403c', // Same warm neutral background
          hitRateWarningText: '#fcd34d', // Golden yellow (prettier!)

          // Action buttons
          actionResetBg: '#f59e0b',
          actionResetHover: '#d97706',
          actionProcessBg: '#10b981',
          actionProcessHover: '#059669',
          actionDeleteBg: '#ef4444',
          actionDeleteHover: '#dc2626',

          // Icon backgrounds
          iconBgBlue: '#3b82f6',
          iconBgGreen: '#10b981',
          iconBgEmerald: '#10b981',
          iconBgPurple: '#8b5cf6',
          iconBgIndigo: '#6366f1',
          iconBgOrange: '#f97316',
          iconBgYellow: '#eab308',
          iconBgCyan: '#06b6d4',
          iconBgRed: '#ef4444',

          // Chart colors
          chartColor1: '#3b82f6',
          chartColor2: '#10b981',
          chartColor3: '#f59e0b',
          chartColor4: '#ef4444',
          chartColor5: '#8b5cf6',
          chartColor6: '#06b6d4',
          chartColor7: '#f97316',
          chartColor8: '#ec4899',
          chartBorderColor: '#1f2937',
          chartGridColor: '#374151',
          chartTextColor: '#9ca3af',
          chartCacheHitColor: '#10b981',
          chartCacheMissColor: '#f59e0b',

          // Scrollbar colors
          scrollbarTrack: '#374151',
          scrollbarThumb: '#6B7280',
          scrollbarHover: '#9CA3AF'
        }
      },
      // In theme.service.ts, update the light-default theme:

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
          // Core colors
          primaryColor: '#3b82f6',
          secondaryColor: '#8b5cf6',
          accentColor: '#06b6d4',

          // Backgrounds - LIGHT GREY INSTEAD OF WHITE
          bgPrimary: '#f8f9fa',
          bgSecondary: '#ffffff',
          bgTertiary: '#f3f4f6',
          bgHover: '#e5e7eb',

          // Text
          textPrimary: '#111827',
          textSecondary: '#374151',
          textMuted: '#6b7280',
          textAccent: '#2563eb',
          
          // Drag handle
          dragHandleColor: '#9ca3af',
          dragHandleHover: '#2563eb',

          // Borders
          borderPrimary: '#e5e7eb',
          borderSecondary: '#d1d5db',
          borderFocus: '#3b82f6',

          // Navigation
          navBg: '#ffffff',
          navBorder: '#e5e7eb',
          navTabActive: '#3b82f6',
          navTabInactive: '#6b7280',
          navTabHover: '#111827',
          navTabActiveBorder: '#3b82f6',
          navMobileMenuBg: '#ffffff',
          navMobileItemHover: '#f3f4f6',

          // Status colors - FIXED FOR BETTER CONTRAST ON LIGHT BACKGROUNDS
          success: '#10b981',
          successBg: '#d1fae5',
          successText: '#047857', // Changed from '#065f46' - darker green for better contrast
          warning: '#f97316',
          warningBg: '#fef3c7',
          warningText: '#b45309', // Changed from '#92400e' - darker amber for better contrast
          error: '#ef4444',
          errorBg: '#fee2e2',
          errorText: '#991b1b',
          info: '#3b82f6',
          infoBg: '#dbeafe',
          infoText: '#1e40af',

          // Service colors
          steamColor: '#3b82f6',
          epicColor: '#8b5cf6',
          originColor: '#10b981',
          blizzardColor: '#ef4444',
          wsusColor: '#06b6d4',
          riotColor: '#f59e0b',

          // Components
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

          // Hit rate specific - MUCH PRETTIER COLORS
          hitRateHighBg: '#d1fae5',
          hitRateHighText: '#047857', // Changed to match successText for consistency
          hitRateMediumBg: '#dbeafe',
          hitRateMediumText: '#1e40af',
          hitRateLowBg: '#fef3c7',
          hitRateLowText: '#92400e',
          hitRateWarningBg: '#fef3c7',
          hitRateWarningText: '#92400e',

          // Action buttons
          actionResetBg: '#f59e0b',
          actionResetHover: '#d97706',
          actionProcessBg: '#10b981',
          actionProcessHover: '#059669',
          actionDeleteBg: '#ef4444',
          actionDeleteHover: '#dc2626',

          // Icon backgrounds
          iconBgBlue: '#3b82f6',
          iconBgGreen: '#10b981',
          iconBgEmerald: '#10b981',
          iconBgPurple: '#8b5cf6',
          iconBgIndigo: '#6366f1',
          iconBgOrange: '#f97316',
          iconBgYellow: '#eab308',
          iconBgCyan: '#06b6d4',
          iconBgRed: '#ef4444',

          // Chart colors
          chartColor1: '#3b82f6',
          chartColor2: '#10b981',
          chartColor3: '#f59e0b',
          chartColor4: '#ef4444',
          chartColor5: '#8b5cf6',
          chartColor6: '#06b6d4',
          chartColor7: '#f97316',
          chartColor8: '#ec4899',
          chartBorderColor: '#e5e7eb',
          chartGridColor: '#d1d5db',
          chartTextColor: '#6b7280',
          chartCacheHitColor: '#047857', // Changed to darker green for better contrast
          chartCacheMissColor: '#b45309', // Changed to darker amber for better contrast

          // Scrollbar colors - Light theme appropriate
          scrollbarTrack: '#e5e7eb',
          scrollbarThumb: '#9ca3af',
          scrollbarHover: '#6b7280'
        }
      }
    ];
  }

  async getTheme(themeId: string): Promise<Theme | null> {
    const builtIn = this.getBuiltInThemes().find((t) => t.meta.id === themeId);
    if (builtIn) return builtIn;

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

    const formData = new FormData();
    formData.append('file', file);

    try {
      const response = await fetch(`${API_BASE}/theme/upload`, {
        method: 'POST',
        headers: authService.getAuthHeaders(),
        body: formData
      });

      if (!response.ok) {
        const error = await response.json().catch(() => ({ error: 'Failed to upload theme' }));
        throw new Error(error.error || 'Failed to upload theme');
      }

      return theme;
    } catch (error: any) {
      if (error.message.includes('Failed to fetch') || error.message.includes('NetworkError')) {
        throw new Error('Cannot save theme: API server is not running. Please start the LANCache Manager API service.');
      }
      throw error;
    }
  }

  async deleteTheme(themeId: string): Promise<void> {
    const response = await fetch(`${API_BASE}/theme/${themeId}`, {
      method: 'DELETE',
      headers: authService.getAuthHeaders()
    });

    if (!response.ok && response.status !== 404) {
      const error = await response.json().catch(() => ({ error: 'Failed to delete theme' }));
      throw new Error(error.error || 'Failed to delete theme');
    }
  }

  private applyDefaultVariables(): void {
    const defaultStyles = `
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
        --theme-drag-handle: #6b7280;
        --theme-drag-handle-hover: #60a5fa;
        --theme-border-primary: #374151;
        --theme-border-secondary: #4b5563;
        --theme-border-focus: #3b82f6;
        --theme-nav-bg: #1f2937;
        --theme-nav-border: #374151;
        --theme-nav-tab-active: #3b82f6;
        --theme-nav-tab-inactive: #9ca3af;
        --theme-nav-tab-hover: #ffffff;
        --theme-nav-tab-active-border: #3b82f6;
        --theme-nav-mobile-menu-bg: #1f2937;
        --theme-nav-mobile-item-hover: #374151;
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
        --theme-hit-rate-high-bg: #064e3b;
        --theme-hit-rate-high-text: #34d399;
        --theme-hit-rate-medium-bg: #1e3a8a;
        --theme-hit-rate-medium-text: #93c5fd;
        --theme-hit-rate-low-bg: #ea580c;
        --theme-hit-rate-low-text: #fb923c;
        --theme-hit-rate-warning-bg: #78350f;
        --theme-hit-rate-warning-text: #fbbf24;
        --theme-action-reset-bg: #f59e0b;
        --theme-action-reset-hover: #d97706;
        --theme-action-process-bg: #10b981;
        --theme-action-process-hover: #059669;
        --theme-action-delete-bg: #ef4444;
        --theme-action-delete-hover: #dc2626;
        --theme-icon-blue: #3b82f6;
        --theme-icon-green: #10b981;
        --theme-icon-emerald: #10b981;
        --theme-icon-purple: #8b5cf6;
        --theme-icon-indigo: #6366f1;
        --theme-icon-orange: #f97316;
        --theme-icon-yellow: #eab308;
        --theme-icon-cyan: #06b6d4;
        --theme-icon-red: #ef4444;
        --theme-chart-1: #3b82f6;
        --theme-chart-2: #10b981;
        --theme-chart-3: #f59e0b;
        --theme-chart-4: #ef4444;
        --theme-chart-5: #8b5cf6;
        --theme-chart-6: #06b6d4;
        --theme-chart-7: #f97316;
        --theme-chart-8: #ec4899;
        --theme-chart-border: #1f2937;
        --theme-chart-grid: #374151;
        --theme-chart-text: #9ca3af;
        --theme-chart-cache-hit: #10b981;
        --theme-chart-cache-miss: #f59e0b;
      }
    `;

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
    this.currentTheme = null;

    this.applyDefaultVariables();
  }

  applyTheme(theme: Theme): void {
    if (!theme || !theme.colors) return;

    // Remove any existing theme styles
    if (this.styleElement) {
      this.styleElement.remove();
      this.styleElement = null;
    }
    
    // Also remove default preload if it exists
    const defaultPreload = document.getElementById('lancache-default-preload');
    if (defaultPreload) {
      defaultPreload.remove();
    }

    const colors = theme.colors;

    // Create clean theme styles with only CSS variables - no Tailwind overrides
    const themeStyles = `
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
      --theme-drag-handle: ${colors.dragHandleColor || colors.textMuted || '#6b7280'};
      --theme-drag-handle-hover: ${colors.dragHandleHover || colors.textAccent || '#60a5fa'};
      --theme-border-primary: ${colors.borderPrimary || '#374151'};
      --theme-border-secondary: ${colors.borderSecondary || '#4b5563'};
      --theme-border-focus: ${colors.borderFocus || '#3b82f6'};
      
      /* Navigation Variables */
      --theme-nav-bg: ${colors.navBg || colors.bgSecondary || '#1f2937'};
      --theme-nav-border: ${colors.navBorder || colors.borderPrimary || '#374151'};
      --theme-nav-tab-active: ${colors.navTabActive || colors.primaryColor || '#3b82f6'};
      --theme-nav-tab-inactive: ${colors.navTabInactive || colors.textMuted || '#9ca3af'};
      --theme-nav-tab-hover: ${colors.navTabHover || colors.textPrimary || '#ffffff'};
      --theme-nav-tab-active-border: ${colors.navTabActiveBorder || colors.primaryColor || '#3b82f6'};
      --theme-nav-mobile-menu-bg: ${colors.navMobileMenuBg || colors.bgSecondary || '#1f2937'};
      --theme-nav-mobile-item-hover: ${colors.navMobileItemHover || colors.bgTertiary || '#374151'};
      
      /* Status Colors */
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
      
      /* Service Colors */
      --theme-steam: ${colors.steamColor || '#3b82f6'};
      --theme-epic: ${colors.epicColor || '#8b5cf6'};
      --theme-origin: ${colors.originColor || '#10b981'};
      --theme-blizzard: ${colors.blizzardColor || '#ef4444'};
      --theme-wsus: ${colors.wsusColor || '#06b6d4'};
      --theme-riot: ${colors.riotColor || '#f59e0b'};
      
      /* Component Colors */
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
      
      /* Hit Rate Colors - FIXED WITH PRETTIER COLORS */
      --theme-hit-rate-high-bg: ${colors.hitRateHighBg || '#064e3b'};
      --theme-hit-rate-high-text: ${colors.hitRateHighText || '#34d399'};
      --theme-hit-rate-medium-bg: ${colors.hitRateMediumBg || '#1e3a8a'};
      --theme-hit-rate-medium-text: ${colors.hitRateMediumText || '#93c5fd'};
      --theme-hit-rate-low-bg: ${colors.hitRateLowBg || '#44403c'};
      --theme-hit-rate-low-text: ${colors.hitRateLowText || '#fbbf24'};
      --theme-hit-rate-warning-bg: ${colors.hitRateWarningBg || '#44403c'};
      --theme-hit-rate-warning-text: ${colors.hitRateWarningText || '#fcd34d'};
      
      /* Action Button Colors */
      --theme-action-reset-bg: ${colors.actionResetBg || '#f59e0b'};
      --theme-action-reset-hover: ${colors.actionResetHover || '#d97706'};
      --theme-action-process-bg: ${colors.actionProcessBg || '#10b981'};
      --theme-action-process-hover: ${colors.actionProcessHover || '#059669'};
      --theme-action-delete-bg: ${colors.actionDeleteBg || '#ef4444'};
      --theme-action-delete-hover: ${colors.actionDeleteHover || '#dc2626'};
      
      /* Icon Colors */
      --theme-icon-blue: ${colors.iconBgBlue || '#3b82f6'};
      --theme-icon-green: ${colors.iconBgGreen || '#10b981'};
      --theme-icon-emerald: ${colors.iconBgEmerald || '#10b981'};
      --theme-icon-purple: ${colors.iconBgPurple || '#8b5cf6'};
      --theme-icon-indigo: ${colors.iconBgIndigo || '#6366f1'};
      --theme-icon-orange: ${colors.iconBgOrange || '#f97316'};
      --theme-icon-yellow: ${colors.iconBgYellow || '#eab308'};
      --theme-icon-cyan: ${colors.iconBgCyan || '#06b6d4'};
      --theme-icon-red: ${colors.iconBgRed || '#ef4444'};
      
      /* Chart Colors */
      --theme-chart-1: ${colors.chartColor1 || '#3b82f6'};
      --theme-chart-2: ${colors.chartColor2 || '#10b981'};
      --theme-chart-3: ${colors.chartColor3 || '#f59e0b'};
      --theme-chart-4: ${colors.chartColor4 || '#ef4444'};
      --theme-chart-5: ${colors.chartColor5 || '#8b5cf6'};
      --theme-chart-6: ${colors.chartColor6 || '#06b6d4'};
      --theme-chart-7: ${colors.chartColor7 || '#f97316'};
      --theme-chart-8: ${colors.chartColor8 || '#ec4899'};
      --theme-chart-border: ${colors.chartBorderColor || '#1f2937'};
      --theme-chart-grid: ${colors.chartGridColor || '#374151'};
      --theme-chart-text: ${colors.chartTextColor || '#9ca3af'};
      --theme-chart-cache-hit: ${colors.chartCacheHitColor || '#10b981'};
      --theme-chart-cache-miss: ${colors.chartCacheMissColor || '#f59e0b'};
      
      /* Scrollbar Colors */
      --theme-scrollbar-track: ${colors.scrollbarTrack || colors.bgTertiary || '#374151'};
      --theme-scrollbar-thumb: ${colors.scrollbarThumb || colors.textMuted || '#6B7280'};
      --theme-scrollbar-hover: ${colors.scrollbarHover || colors.textSecondary || '#9CA3AF'};
    }

    /* Global Transitions */
    body * {
      transition: background-color 0.2s ease, color 0.2s ease, border-color 0.2s ease;
    }

    /* Global Body Style */
    body {
      background-color: var(--theme-bg-primary) !important;
      color: var(--theme-text-primary) !important;
    }

    /* Custom CSS from theme */
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
    this.currentTheme = theme;

    // Force re-render
    window.dispatchEvent(new Event('themechange'));
  }

  async loadSavedTheme(): Promise<void> {
    // Always start with default variables
    this.applyDefaultVariables();
    
    // Always load and apply dark-default theme on startup
    // User can change it after loading
    const darkDefault = await this.getTheme('dark-default');
    if (darkDefault) {
      this.applyTheme(darkDefault);
    }
  }

  getCurrentThemeId(): string {
    return this.currentTheme?.meta.id || 'dark-default';
  }

  getCurrentTheme(): Theme | null {
    return this.currentTheme;
  }

  isThemeApplied(): boolean {
    return this.currentTheme !== null;
  }

  exportTheme(theme: Theme): string {
    console.log('Exporting theme to TOML:', theme);
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
      console.log('Exporting colors:', theme.colors);
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
