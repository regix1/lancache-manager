import { API_BASE } from '../utils/constants';
import authService from './auth.service';
import * as TOML from 'toml';
import packageJson from '../../package.json';

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
  textPlaceholder?: string;

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
  checkboxAccent?: string;
  checkboxBorder?: string;
  checkboxBg?: string;
  checkboxCheckmark?: string;
  checkboxShadow?: string;
  checkboxHoverShadow?: string;
  checkboxHoverBg?: string;
  checkboxFocus?: string;
  sliderAccent?: string;
  sliderThumb?: string;
  sliderTrack?: string;
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

  // Access indicator colors
  publicAccessBg?: string;
  publicAccessText?: string;
  publicAccessBorder?: string;
  securedAccessBg?: string;
  securedAccessText?: string;
  securedAccessBorder?: string;
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


  // Get the best text color for a given background using theme colors
  public getContrastText(background: string): string {
    if (!background || background === 'transparent') {
      return 'var(--theme-text-primary)';
    }

    // Get the actual theme button text color
    const buttonText = getComputedStyle(document.documentElement)
      .getPropertyValue('--theme-button-text').trim() || '#ffffff';

    // For primary color backgrounds, always use the theme's button text
    return buttonText;
  }

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
          textPlaceholder: '#6b7280',
          
          // Drag handle
          dragHandleColor: '#6b7280',
          dragHandleHover: '#60a5fa',

          // Borders
          borderPrimary: '#374151',
          borderSecondary: '#4b5563',
          borderFocus: '#3b82f6', // Uses primaryColor

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
          checkboxAccent: '#3b82f6',
          checkboxBorder: '#4b5563',
          checkboxBg: '#1f2937',
          checkboxCheckmark: '#ffffff',
          checkboxShadow: 'none',
          checkboxHoverShadow: '0 0 0 3px rgba(59, 130, 246, 0.1)',
          checkboxHoverBg: '#374151',
          checkboxFocus: '#3b82f6',
          sliderAccent: '#3b82f6',
          sliderThumb: '#3b82f6',
          sliderTrack: '#374151',
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
          scrollbarHover: '#9CA3AF',

          // Access indicator colors
          publicAccessBg: 'rgba(16, 185, 129, 0.2)',  // green-500 with 20% opacity
          publicAccessText: '#34d399',                 // green-400
          publicAccessBorder: 'rgba(16, 185, 129, 0.3)', // green-500 with 30% opacity
          securedAccessBg: 'rgba(245, 158, 11, 0.2)',    // yellow-500 with 20% opacity
          securedAccessText: '#fbbf24',                   // yellow-400
          securedAccessBorder: 'rgba(245, 158, 11, 0.3)'  // yellow-500 with 30% opacity
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
          textPlaceholder: '#9ca3af',
          
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
          checkboxAccent: '#3b82f6',
          checkboxBorder: '#d1d5db',
          checkboxBg: '#ffffff',
          checkboxCheckmark: '#ffffff',
          checkboxShadow: '0 1px 2px rgba(0, 0, 0, 0.05)',
          checkboxHoverShadow: '0 0 0 3px rgba(59, 130, 246, 0.1)',
          checkboxHoverBg: '#f9fafb',
          checkboxFocus: '#3b82f6',
          sliderAccent: '#3b82f6',
          sliderThumb: '#3b82f6',
          sliderTrack: '#e5e7eb',
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
          scrollbarHover: '#6b7280',

          // Access indicator colors
          publicAccessBg: '#d1fae5',      // green-100
          publicAccessText: '#047857',    // green-800
          publicAccessBorder: '#86efac',  // green-300
          securedAccessBg: '#fef3c7',     // yellow-100
          securedAccessText: '#92400e',   // yellow-800
          securedAccessBorder: '#fde047'  // yellow-300
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
    const sharpCorners = localStorage.getItem('lancache_sharp_corners') === 'true';
    const borderRadius = sharpCorners ? '0px' : '0.5rem';
    const borderRadiusLg = sharpCorners ? '0px' : '0.75rem';
    const borderRadiusXl = sharpCorners ? '0px' : '1rem';

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
        --theme-border-focus: var(--theme-primary);
        --theme-border-radius: ${borderRadius};
        --theme-border-radius-lg: ${borderRadiusLg};
        --theme-border-radius-xl: ${borderRadiusXl};
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
        --theme-input-focus: var(--theme-primary);
        --theme-checkbox-accent: #3b82f6;
        --theme-checkbox-border: #4b5563;
        --theme-checkbox-bg: #1f2937;
        --theme-checkbox-checkmark: #ffffff;
        --theme-checkbox-shadow: none;
        --theme-checkbox-hover-shadow: 0 0 0 3px rgba(59, 130, 246, 0.1);
        --theme-checkbox-hover-bg: #374151;
        --theme-checkbox-focus: var(--theme-primary);
        --theme-slider-accent: #3b82f6;
        --theme-slider-thumb: #3b82f6;
        --theme-slider-track: #374151;
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
    
    // Clear all saved theme data
    localStorage.removeItem('lancache_selected_theme');
    localStorage.removeItem('lancache_theme_css');
    localStorage.removeItem('lancache_theme_dark');

    this.applyDefaultVariables();
  }

  applyTheme(theme: Theme): void {
    if (!theme || !theme.colors) return;

    // Remove any existing theme styles
    if (this.styleElement) {
      this.styleElement.remove();
      this.styleElement = null;
    }

    // Remove preload styles since we're applying the real theme
    const preloadStyle = document.getElementById('lancache-theme-preload');
    if (preloadStyle) {
      preloadStyle.remove();
    }

    const defaultPreload = document.getElementById('lancache-default-preload');
    if (defaultPreload) {
      defaultPreload.remove();
    }

    const colors = theme.colors;

    // Normalize theme: if focus colors aren't defined, use primaryColor
    if (!colors.borderFocus) colors.borderFocus = colors.primaryColor;
    if (!colors.inputFocus) colors.inputFocus = colors.primaryColor;
    if (!colors.checkboxFocus) colors.checkboxFocus = colors.primaryColor;

    // Get border radius settings
    const sharpCorners = localStorage.getItem('lancache_sharp_corners') === 'true';
    const borderRadius = sharpCorners ? '0px' : '0.5rem';
    const borderRadiusLg = sharpCorners ? '0px' : '0.75rem';
    const borderRadiusXl = sharpCorners ? '0px' : '1rem';

    // Create clean theme styles with only CSS variables - no Tailwind overrides
    const themeStyles = `
    :root {
      --theme-primary: ${colors.primaryColor};
      --theme-secondary: ${colors.secondaryColor};
      --theme-accent: ${colors.accentColor};
      --theme-bg-primary: ${colors.bgPrimary};
      --theme-bg-secondary: ${colors.bgSecondary};
      --theme-bg-tertiary: ${colors.bgTertiary};
      --theme-bg-hover: ${colors.bgHover};
      --theme-text-primary: ${colors.textPrimary};
      --theme-text-secondary: ${colors.textSecondary};
      --theme-text-muted: ${colors.textMuted};
      --theme-text-accent: ${colors.textAccent};
      --theme-text-placeholder: ${colors.textPlaceholder};
      --theme-drag-handle: ${colors.dragHandleColor};
      --theme-drag-handle-hover: ${colors.dragHandleHover};
      --theme-border-primary: ${colors.borderPrimary};
      --theme-border-secondary: ${colors.borderSecondary};
      --theme-border-focus: ${colors.borderFocus};
      --theme-border-radius: ${borderRadius};
      --theme-border-radius-lg: ${borderRadiusLg};
      --theme-border-radius-xl: ${borderRadiusXl};
      
      /* Navigation Variables */
      --theme-nav-bg: ${colors.navBg};
      --theme-nav-border: ${colors.navBorder};
      --theme-nav-tab-active: ${colors.navTabActive};
      --theme-nav-tab-inactive: ${colors.navTabInactive};
      --theme-nav-tab-hover: ${colors.navTabHover};
      --theme-nav-tab-active-border: ${colors.navTabActiveBorder};
      --theme-nav-mobile-menu-bg: ${colors.navMobileMenuBg};
      --theme-nav-mobile-item-hover: ${colors.navMobileItemHover};
      
      /* Status Colors */
      --theme-success: ${colors.success};
      --theme-success-bg: ${colors.successBg};
      --theme-success-text: ${colors.successText};
      --theme-warning: ${colors.warning};
      --theme-warning-bg: ${colors.warningBg};
      --theme-warning-text: ${colors.warningText};
      --theme-error: ${colors.error};
      --theme-error-bg: ${colors.errorBg};
      --theme-error-text: ${colors.errorText};
      --theme-info: ${colors.info};
      --theme-info-bg: ${colors.infoBg};
      --theme-info-text: ${colors.infoText};
      
      /* Service Colors */
      --theme-steam: ${colors.steamColor};
      --theme-epic: ${colors.epicColor};
      --theme-origin: ${colors.originColor};
      --theme-blizzard: ${colors.blizzardColor};
      --theme-wsus: ${colors.wsusColor};
      --theme-riot: ${colors.riotColor};
      
      /* Component Colors */
      --theme-card-bg: ${colors.cardBg};
      --theme-card-border: ${colors.cardBorder};
      --theme-button-bg: ${colors.buttonBg};
      --theme-button-hover: ${colors.buttonHover};
      --theme-button-text: ${colors.buttonText};
      --theme-input-bg: ${colors.inputBg};
      --theme-input-border: ${colors.inputBorder};
      --theme-input-focus: ${colors.inputFocus};
      --theme-checkbox-accent: ${colors.checkboxAccent};
      --theme-checkbox-border: ${colors.checkboxBorder};
      --theme-checkbox-bg: ${colors.checkboxBg};
      --theme-checkbox-checkmark: ${colors.checkboxCheckmark};
      --theme-checkbox-shadow: ${colors.checkboxShadow};
      --theme-checkbox-hover-shadow: ${colors.checkboxHoverShadow};
      --theme-checkbox-hover-bg: ${colors.checkboxHoverBg};
      --theme-checkbox-focus: ${colors.checkboxFocus};
      --theme-slider-accent: ${colors.sliderAccent};
      --theme-slider-thumb: ${colors.sliderThumb};
      --theme-slider-track: ${colors.sliderTrack};
      --theme-badge-bg: ${colors.badgeBg};
      --theme-badge-text: ${colors.badgeText};
      --theme-progress-bar: ${colors.progressBar};
      --theme-progress-bg: ${colors.progressBg};
      
      /* Hit Rate Colors - FIXED WITH PRETTIER COLORS */
      --theme-hit-rate-high-bg: ${colors.hitRateHighBg};
      --theme-hit-rate-high-text: ${colors.hitRateHighText};
      --theme-hit-rate-medium-bg: ${colors.hitRateMediumBg};
      --theme-hit-rate-medium-text: ${colors.hitRateMediumText};
      --theme-hit-rate-low-bg: ${colors.hitRateLowBg};
      --theme-hit-rate-low-text: ${colors.hitRateLowText};
      --theme-hit-rate-warning-bg: ${colors.hitRateWarningBg};
      --theme-hit-rate-warning-text: ${colors.hitRateWarningText};
      
      /* Action Button Colors */
      --theme-action-reset-bg: ${colors.actionResetBg};
      --theme-action-reset-hover: ${colors.actionResetHover};
      --theme-action-process-bg: ${colors.actionProcessBg};
      --theme-action-process-hover: ${colors.actionProcessHover};
      --theme-action-delete-bg: ${colors.actionDeleteBg};
      --theme-action-delete-hover: ${colors.actionDeleteHover};
      
      /* Icon Colors */
      --theme-icon-blue: ${colors.iconBgBlue};
      --theme-icon-green: ${colors.iconBgGreen};
      --theme-icon-emerald: ${colors.iconBgEmerald};
      --theme-icon-purple: ${colors.iconBgPurple};
      --theme-icon-indigo: ${colors.iconBgIndigo};
      --theme-icon-orange: ${colors.iconBgOrange};
      --theme-icon-yellow: ${colors.iconBgYellow};
      --theme-icon-cyan: ${colors.iconBgCyan};
      --theme-icon-red: ${colors.iconBgRed};
      
      /* Chart Colors */
      --theme-chart-1: ${colors.chartColor1};
      --theme-chart-2: ${colors.chartColor2};
      --theme-chart-3: ${colors.chartColor3};
      --theme-chart-4: ${colors.chartColor4};
      --theme-chart-5: ${colors.chartColor5};
      --theme-chart-6: ${colors.chartColor6};
      --theme-chart-7: ${colors.chartColor7};
      --theme-chart-8: ${colors.chartColor8};
      --theme-chart-border: ${colors.chartBorderColor};
      --theme-chart-grid: ${colors.chartGridColor};
      --theme-chart-text: ${colors.chartTextColor};
      --theme-chart-cache-hit: ${colors.chartCacheHitColor};
      --theme-chart-cache-miss: ${colors.chartCacheMissColor};
      
      /* Scrollbar Colors */
      --theme-scrollbar-track: ${colors.scrollbarTrack || colors.bgTertiary};
      --theme-scrollbar-thumb: ${colors.scrollbarThumb || colors.textMuted};
      --theme-scrollbar-hover: ${colors.scrollbarHover || colors.textSecondary};

      /* Access Indicator Colors */
      --theme-public-access-bg: ${colors.publicAccessBg || colors.warningBg};
      --theme-public-access-text: ${colors.publicAccessText || colors.errorText};
      --theme-public-access-border: ${colors.publicAccessBorder || colors.error};
      --theme-secured-access-bg: ${colors.securedAccessBg};
      --theme-secured-access-text: ${colors.securedAccessText};
      --theme-secured-access-border: ${colors.securedAccessBorder};
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
    
    // Save the theme ID and CSS for instant loading on next page load
    localStorage.setItem('lancache_selected_theme', theme.meta.id);
    localStorage.setItem('lancache_theme_css', themeStyles);
    localStorage.setItem('lancache_theme_dark', theme.meta.isDark ? 'true' : 'false');

    // Force re-render
    window.dispatchEvent(new Event('themechange'));
  }

  async loadSavedTheme(): Promise<void> {
    // Check for feature migrations
    this.migrateLocalStorageFeatures();

    // Check if we have a preloaded theme from the HTML
    const preloadStyle = document.getElementById('lancache-theme-preload');
    const savedThemeId = localStorage.getItem('lancache_selected_theme');

    if (preloadStyle && savedThemeId) {
      // We have a preloaded theme, load the fresh version from server
      const theme = await this.getTheme(savedThemeId);
      if (theme) {
        // Apply the fresh theme (this will remove the preload and apply the real theme)
        this.applyTheme(theme);
        this.currentTheme = theme;
        return;
      }
      // If saved theme not found on server, clear everything
      localStorage.removeItem('lancache_selected_theme');
      localStorage.removeItem('lancache_theme_css');
      localStorage.removeItem('lancache_theme_dark');
    }

    // No preload or theme not found, apply defaults
    this.applyDefaultVariables();

    // Check if user has a saved theme preference without preload
    if (savedThemeId) {
      const theme = await this.getTheme(savedThemeId);
      if (theme) {
        this.applyTheme(theme);
        return;
      }
    }

    // Default to dark theme if no saved preference or theme not found
    const darkDefault = await this.getTheme('dark-default');
    if (darkDefault) {
      this.applyTheme(darkDefault);
    }
  }

  private migrateLocalStorageFeatures(): void {
    const migrationVersion = localStorage.getItem('lancache_migration_version');
    const currentVersion = packageJson.version;

    if (migrationVersion !== currentVersion) {
      // Migration for sharp corners feature
      if (!localStorage.getItem('lancache_sharp_corners')) {
        localStorage.setItem('lancache_sharp_corners', 'false'); // Default to rounded
      }

      // Set migration version to prevent future runs
      localStorage.setItem('lancache_migration_version', currentVersion);
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

  setSharpCorners(enabled: boolean): void {
    localStorage.setItem('lancache_sharp_corners', enabled.toString());

    // Re-apply current theme to update border radius variables
    if (this.currentTheme) {
      this.applyTheme(this.currentTheme);
    } else {
      this.applyDefaultVariables();
    }
  }

  getSharpCorners(): boolean {
    return localStorage.getItem('lancache_sharp_corners') === 'true';
  }
}

export default new ThemeService();
