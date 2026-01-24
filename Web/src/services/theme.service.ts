import { API_BASE } from '../utils/constants';
import authService from './auth.service';
import preferencesService from './preferences.service';
import * as TOML from 'toml';
import { storage } from '@utils/storage';


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
  xboxColor?: string;

  // Component colors
  cardBg?: string;
  cardBorder?: string;
  cardOutline?: string;
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

  // Session colors (for Users tab)
  userSessionColor?: string;
  userSessionBg?: string;
  guestSessionColor?: string;
  guestSessionBg?: string;
  activeSessionColor?: string;
  activeSessionBg?: string;

  // Event colors (for Events tab)
  eventColor1?: string;
  eventColor2?: string;
  eventColor3?: string;
  eventColor4?: string;
  eventColor5?: string;
  eventColor6?: string;
  eventColor7?: string;
  eventColor8?: string;

  // Index signature for dynamic color access
  [key: string]: string | undefined;
}

interface ThemeMeta {
  id: string;
  name: string;
  description?: string;
  author?: string;
  version?: string;
  isDark?: boolean;
  sharpCorners?: boolean;
  disableFocusOutlines?: boolean;
  disableTooltips?: boolean;
  isCommunityTheme?: boolean;
  basedOn?: string;
}

interface Theme {
  meta: ThemeMeta;
  colors: ThemeColors;
  custom?: Record<string, string>;
  css?: { content?: string };
}

class ThemeService {
  // Get the best text color for a given background using theme colors
  

  private currentTheme: Theme | null = null;
  private styleElement: HTMLStyleElement | null = null;
  private preferenceListenersSetup = false;
  private isProcessingReset = false;

  /**
   * Setup listeners for live preference updates
   */
  setupPreferenceListeners(): void {
    if (this.preferenceListenersSetup) {
      return; // Already setup
    }

    // console.log('[ThemeService] Setting up preference change listeners');

    window.addEventListener('preference-changed', (event: Event) => {
      const { key, value } = (event as CustomEvent<{ key: string; value: unknown }>).detail;

      // Apply preference changes without showing notifications
      try {
        switch (key) {
          case 'selectedTheme':
            // Handle null/empty value by fetching default guest theme (async, non-blocking)
            if (!value) {
              fetch(`${API_BASE}/themes/preferences/guest`, {
                credentials: 'include',
                headers: authService.getAuthHeaders()
              })
                .then(response => {
                  if (response.ok) {
                    return response.json();
                  }
                  throw new Error('Failed to fetch default guest theme');
                })
                .then(data => {
                  const defaultTheme = data.themeId || 'dark-default';
                  if (defaultTheme !== this.getCurrentThemeId()) {
                    return this.setTheme(defaultTheme);
                  }
                })
                .catch(err => {
                  console.error('[ThemeService] Failed to fetch default guest theme:', err);
                });
            } else if (typeof value === 'string' && value !== this.getCurrentThemeId()) {
              this.setTheme(value);
            }
            break;

          case 'sharpCorners':
            // Re-apply current theme to update border radius
            if (this.currentTheme) {
              this.applyTheme(this.currentTheme);
            } else {
              this.applyDefaultVariables();
            }
            break;

          case 'disableFocusOutlines':
            if (value !== null && value !== undefined) {
              document.documentElement.setAttribute('data-disable-focus-outlines', value.toString());
              window.dispatchEvent(new Event('focusoutlineschange'));
            }
            break;

          case 'disableTooltips':
            if (value !== null && value !== undefined) {
              document.documentElement.setAttribute('data-disable-tooltips', value.toString());
              window.dispatchEvent(new Event('tooltipschange'));
            }
            break;

          case 'picsAlwaysVisible':
            window.dispatchEvent(new Event('notificationvisibilitychange'));
            break;

          case 'disableStickyNotifications':
            window.dispatchEvent(new Event('stickynotificationschange'));
            break;
        }
      } catch (err) {
        console.error(`[ThemeService] Error handling preference change for ${key}:`, err);
      }
    });

    // Listen for preferences reset event
    window.addEventListener('preferences-reset', async () => {
      // Prevent duplicate processing
      if (this.isProcessingReset) {
        // console.log('[ThemeService] Already processing reset, skipping duplicate');
        return;
      }

      this.isProcessingReset = true;
      // console.log('[ThemeService] Preferences reset, applying defaults...');

      try {
        // Clear localStorage theme cache
        storage.removeItem('lancache_selected_theme');
        storage.removeItem('lancache_theme_css');
        storage.removeItem('lancache_theme_dark');

        // Reload preferences (will get defaults from API)
        await preferencesService.loadPreferences();

        // Load and apply default theme
        await this.loadSavedTheme();

        // Show notification with different message for guest vs authenticated users
        const isGuest = authService.authMode === 'guest';
        const message = isGuest
          ? 'Your preferences have been reset to defaults by an administrator'
          : 'Preferences reset to defaults';

        window.dispatchEvent(
          new CustomEvent('show-toast', {
            detail: {
              type: 'info',
              message,
              duration: 5000
            }
          })
        );
      } finally {
        // Reset flag after a delay to allow event to complete
        setTimeout(() => {
          this.isProcessingReset = false;
        }, 1000);
      }
    });

    this.preferenceListenersSetup = true;
  }

  async loadThemes(): Promise<Theme[]> {
    const builtInThemes = this.getBuiltInThemes();

    const apiThemes: Theme[] = [];
    const deletedThemeIds: string[] = [];

    try {
      const response = await fetch(`${API_BASE}/themes`);
      if (response.ok) {
        const themeList = await response.json();

        for (const themeInfo of themeList) {
          if (themeInfo.format === 'toml') {
            try {
              const themeResponse = await fetch(`${API_BASE}/themes/${themeInfo.id}`);

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
          isDark: true,
          sharpCorners: false,
          disableFocusOutlines: true,
          disableTooltips: false
        },
        colors: {
          // Core colors
          primaryColor: '#3b82f6',
          secondaryColor: '#8b5cf6',
          accentColor: '#06b6d4',

          // Backgrounds
          bgPrimary: '#111827',
          bgSecondary: '#283649',
          bgTertiary: '#313e52',
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
          steamColor: '#10b981', // Green
          epicColor: '#8b5cf6', // Purple
          originColor: '#fb923c', // Bright Orange
          blizzardColor: '#3b82f6', // Blue
          wsusColor: '#06b6d4', // Cyan
          riotColor: '#ef4444', // Red
          xboxColor: '#107C10', // Xbox Green

          // Components
          cardBg: '#1e2938',
          cardBorder: '#374151',
          cardOutline: '#3b82f6',
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
          publicAccessBg: 'rgba(16, 185, 129, 0.2)', // green-500 with 20% opacity
          publicAccessText: '#34d399', // green-400
          publicAccessBorder: 'rgba(16, 185, 129, 0.3)', // green-500 with 30% opacity
          securedAccessBg: 'rgba(245, 158, 11, 0.2)', // yellow-500 with 20% opacity
          securedAccessText: '#fbbf24', // yellow-400
          securedAccessBorder: 'rgba(245, 158, 11, 0.3)', // yellow-500 with 30% opacity

          // Session colors
          userSessionColor: '#3b82f6', // Primary blue for authenticated users
          userSessionBg: 'rgba(59, 130, 246, 0.15)', // Primary blue with 15% opacity
          guestSessionColor: '#06b6d4', // Cyan for guest users
          guestSessionBg: 'rgba(6, 182, 212, 0.15)', // Cyan with 15% opacity
          activeSessionColor: '#f97316', // Orange for active sessions
          activeSessionBg: 'rgba(249, 115, 22, 0.15)', // Orange with 15% opacity

          // Event colors (for calendar events)
          eventColor1: '#3b82f6', // Blue
          eventColor2: '#10b981', // Green
          eventColor3: '#f59e0b', // Amber
          eventColor4: '#ef4444', // Red
          eventColor5: '#8b5cf6', // Purple
          eventColor6: '#ec4899', // Pink
          eventColor7: '#06b6d4', // Cyan
          eventColor8: '#f97316' // Orange
        }
      },
      // Modern, clean light theme inspired by Linear/Stripe
      // Uses subtle off-white background with white cards and shadows for depth
      {
        meta: {
          id: 'light-default',
          name: 'Light Default',
          description: 'Clean modern light theme with subtle depth',
          author: 'System',
          version: '3.0.0',
          isDark: false,
          sharpCorners: false,
          disableFocusOutlines: true,
          disableTooltips: false
        },
        colors: {
          // Core colors - Refined blue primary
          primaryColor: '#2563eb',
          secondaryColor: '#7c3aed',
          accentColor: '#0891b2',

          // Backgrounds - Subtle off-white, not heavy grey
          // Key: Very light background, white cards with shadows for separation
          bgPrimary: '#f8fafc', // slate-50 - very subtle off-white
          bgSecondary: '#ffffff', // Pure white for cards
          bgTertiary: '#f1f5f9', // slate-100 - subtle grey for nested elements
          bgHover: '#e2e8f0', // slate-200 - hover state

          // Text - Rich, crisp colors (not washed out)
          textPrimary: '#0f172a', // slate-900 - near black for crisp readability
          textSecondary: '#1e293b', // slate-800 - strong secondary
          textMuted: '#475569', // slate-600 - visible muted text
          textAccent: '#1d4ed8', // blue-700 - rich blue for links
          textPlaceholder: '#64748b', // slate-500 - visible placeholders

          // Drag handle
          dragHandleColor: '#94a3b8',
          dragHandleHover: '#2563eb',

          // Borders - Subtle but defined
          borderPrimary: '#e2e8f0', // slate-200 - subtle border
          borderSecondary: '#cbd5e1', // slate-300 - slightly stronger
          borderFocus: '#2563eb',

          // Navigation - Clean white with subtle border
          navBg: '#ffffff',
          navBorder: '#e2e8f0',
          navTabActive: '#2563eb',
          navTabInactive: '#64748b',
          navTabHover: '#1e293b',
          navTabActiveBorder: '#2563eb',
          navMobileMenuBg: '#ffffff',
          navMobileItemHover: '#f1f5f9',

          // Status colors - Vibrant and accessible
          success: '#059669', // emerald-600
          successBg: '#ecfdf5', // emerald-50
          successText: '#047857', // emerald-700
          warning: '#d97706', // amber-600
          warningBg: '#fffbeb', // amber-50
          warningText: '#b45309', // amber-700
          error: '#dc2626', // red-600
          errorBg: '#fef2f2', // red-50
          errorText: '#b91c1c', // red-700
          info: '#2563eb', // blue-600
          infoBg: '#eff6ff', // blue-50
          infoText: '#1d4ed8', // blue-700

          // Service colors - Vibrant
          steamColor: '#059669',
          epicColor: '#7c3aed',
          originColor: '#ea580c',
          blizzardColor: '#2563eb',
          wsusColor: '#0891b2',
          riotColor: '#dc2626',
          xboxColor: '#107C10', // Xbox Green

          // Components - Clean styling with emphasis on shadows
          cardBg: '#ffffff',
          cardBorder: '#e2e8f0',
          cardOutline: '#2563eb',
          buttonBg: '#2563eb',
          buttonHover: '#1d4ed8',
          buttonText: '#ffffff',
          inputBg: '#ffffff',
          inputBorder: '#e2e8f0',
          inputFocus: '#2563eb',
          checkboxAccent: '#2563eb',
          checkboxBorder: '#cbd5e1',
          checkboxBg: '#ffffff',
          checkboxCheckmark: '#ffffff',
          checkboxShadow: '0 1px 2px rgba(0, 0, 0, 0.05)',
          checkboxHoverShadow: '0 0 0 3px rgba(37, 99, 235, 0.12)',
          checkboxHoverBg: '#f8fafc',
          checkboxFocus: '#2563eb',
          sliderAccent: '#2563eb',
          sliderThumb: '#2563eb',
          sliderTrack: '#e2e8f0',
          progressBg: '#e2e8f0',

          // Hit rate specific - Softer backgrounds
          hitRateHighBg: '#ecfdf5',
          hitRateHighText: '#047857',
          hitRateMediumBg: '#eff6ff',
          hitRateMediumText: '#1d4ed8',
          hitRateLowBg: '#fffbeb',
          hitRateLowText: '#b45309',
          hitRateWarningBg: '#fef2f2',
          hitRateWarningText: '#b91c1c',

          // Action buttons
          actionResetBg: '#d97706',
          actionResetHover: '#b45309',
          actionProcessBg: '#059669',
          actionProcessHover: '#047857',
          actionDeleteBg: '#dc2626',
          actionDeleteHover: '#b91c1c',

          // Icon backgrounds
          iconBgBlue: '#2563eb',
          iconBgGreen: '#059669',
          iconBgEmerald: '#059669',
          iconBgPurple: '#7c3aed',
          iconBgIndigo: '#4f46e5',
          iconBgOrange: '#ea580c',
          iconBgYellow: '#ca8a04',
          iconBgCyan: '#0891b2',
          iconBgRed: '#dc2626',

          // Chart colors
          chartColor1: '#2563eb',
          chartColor2: '#059669',
          chartColor3: '#d97706',
          chartColor4: '#dc2626',
          chartColor5: '#7c3aed',
          chartColor6: '#0891b2',
          chartColor7: '#ea580c',
          chartColor8: '#db2777',
          chartBorderColor: '#f1f5f9',
          chartGridColor: '#e2e8f0',
          chartTextColor: '#475569', // Darker for readability
          chartCacheHitColor: '#059669',
          chartCacheMissColor: '#d97706',

          // Scrollbar colors
          scrollbarTrack: '#f1f5f9',
          scrollbarThumb: '#cbd5e1',
          scrollbarHover: '#94a3b8',

          // Access indicator colors
          publicAccessBg: '#ecfdf5',
          publicAccessText: '#047857',
          publicAccessBorder: '#a7f3d0',
          securedAccessBg: '#fffbeb',
          securedAccessText: '#b45309',
          securedAccessBorder: '#fde68a',

          // Session colors
          userSessionColor: '#2563eb',
          userSessionBg: '#eff6ff',
          guestSessionColor: '#0891b2',
          guestSessionBg: '#ecfeff',
          activeSessionColor: '#ea580c',
          activeSessionBg: '#fff7ed',

          // Event colors (for calendar events)
          eventColor1: '#2563eb', // Blue
          eventColor2: '#059669', // Green
          eventColor3: '#d97706', // Amber
          eventColor4: '#dc2626', // Red
          eventColor5: '#7c3aed', // Purple
          eventColor6: '#db2777', // Pink
          eventColor7: '#0891b2', // Cyan
          eventColor8: '#ea580c' // Orange
        }
      }
    ];
  }

  async getTheme(themeId: string): Promise<Theme | null> {
    const builtIn = this.getBuiltInThemes().find((t) => t.meta.id === themeId);
    if (builtIn) return builtIn;

    try {
      const response = await fetch(`${API_BASE}/themes/${themeId}`);
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

      // Validate required color properties to prevent runtime errors
      const requiredColors = ['primaryColor', 'bgPrimary', 'textPrimary', 'bgSecondary'];
      for (const key of requiredColors) {
        if (!parsed.colors[key]) {
          console.error(`Invalid theme: missing required color ${key}`);
          return null;
        }
      }

      return parsed as Theme;
    } catch (error) {
      console.error('Error parsing TOML theme:', error);
      return null;
    }
  }

  

  

  private applyDefaultVariables(): void {
    const sharpCorners = this.getSharpCornersSync();
    const borderRadius = sharpCorners ? '0px' : '0.5rem';
    const borderRadiusLg = sharpCorners ? '0px' : '0.75rem';
    const borderRadiusXl = sharpCorners ? '0px' : '1rem';

    const defaultStyles = `
      :root {
        /* Core Colors */
        --theme-primary: #3b82f6;
        --theme-primary-rgb: 59, 130, 246;
        --theme-secondary: #8b5cf6;
        --theme-secondary-rgb: 139, 92, 246;
        --theme-accent: #06b6d4;

        /* Backgrounds */
        --theme-bg-primary: #111827;
        --theme-bg-secondary: #283649;
        --theme-bg-tertiary: #313e52;
        --theme-bg-hover: #4b5563;
        --theme-bg-elevated: #283649;

        /* Text */
        --theme-text-primary: #ffffff;
        --theme-text-secondary: #d1d5db;
        --theme-text-muted: #9ca3af;
        --theme-text-accent: #60a5fa;
        --theme-text-placeholder: #6b7280;

        /* Drag Handle */
        --theme-drag-handle: #6b7280;
        --theme-drag-handle-hover: #60a5fa;

        /* Borders */
        --theme-border: #374151;
        --theme-border-primary: #374151;
        --theme-border-secondary: #4b5563;
        --theme-border-focus: var(--theme-primary);
        --theme-border-radius: ${borderRadius};
        --theme-border-radius-lg: ${borderRadiusLg};
        --theme-border-radius-xl: ${borderRadiusXl};

        /* Navigation */
        --theme-nav-bg: #1f2937;
        --theme-nav-border: #374151;
        --theme-nav-tab-active: #3b82f6;
        --theme-nav-tab-inactive: #9ca3af;
        --theme-nav-tab-hover: #ffffff;
        --theme-nav-tab-active-border: #3b82f6;
        --theme-nav-mobile-menu-bg: #1f2937;
        --theme-nav-mobile-item-hover: #374151;

        /* Status Colors */
        --theme-success: #10b981;
        --theme-success-bg: #064e3b;
        --theme-success-text: #34d399;
        --theme-warning: #fb923c;
        --theme-warning-bg: #44403c;
        --theme-warning-text: #fcd34d;
        --theme-error: #ef4444;
        --theme-error-bg: #7f1d1d;
        --theme-error-text: #fca5a5;
        --theme-info: #3b82f6;
        --theme-info-bg: #1e3a8a;
        --theme-info-text: #93c5fd;

        /* Service Colors - Match getBuiltInThemes() */
        --theme-steam: #10b981;
        --theme-epic: #8b5cf6;
        --theme-origin: #fb923c;
        --theme-blizzard: #3b82f6;
        --theme-wsus: #06b6d4;
        --theme-riot: #ef4444;
        --theme-xbox: #107C10;

        /* Card & Components */
        --theme-card-bg: #1e2938;
        --theme-card-border: #374151;
        --theme-card-outline: #3b82f6;
        --theme-card-hover: #4b5563;

        /* Buttons */
        --theme-button-bg: #3b82f6;
        --theme-button-hover: #2563eb;
        --theme-button-text: #ffffff;
        --theme-button-primary: #3b82f6;
        --theme-primary-hover: #2563eb;
        --theme-primary-text: #ffffff;
        --theme-primary-subtle: rgba(59, 130, 246, 0.1);
        --theme-secondary-bg: #283649;

        /* Inputs */
        --theme-input-bg: #374151;
        --theme-input-border: #4b5563;
        --theme-input-focus: var(--theme-primary);

        /* Checkbox */
        --theme-checkbox-accent: #3b82f6;
        --theme-checkbox-border: #4b5563;
        --theme-checkbox-bg: #1f2937;
        --theme-checkbox-checkmark: #ffffff;
        --theme-checkbox-shadow: none;
        --theme-checkbox-hover-shadow: 0 0 0 3px rgba(59, 130, 246, 0.1);
        --theme-checkbox-hover-bg: #374151;
        --theme-checkbox-focus: var(--theme-primary);

        /* Slider */
        --theme-slider-accent: #3b82f6;
        --theme-slider-thumb: #3b82f6;
        --theme-slider-track: #374151;
        --theme-progress-bg: #374151;

        /* Hit Rate Colors - Match getBuiltInThemes() */
        --theme-hit-rate-high-bg: #064e3b;
        --theme-hit-rate-high-text: #34d399;
        --theme-hit-rate-medium-bg: #1e3a8a;
        --theme-hit-rate-medium-text: #93c5fd;
        --theme-hit-rate-low-bg: #44403c;
        --theme-hit-rate-low-text: #fbbf24;
        --theme-hit-rate-warning-bg: #44403c;
        --theme-hit-rate-warning-text: #fcd34d;

        /* Action Buttons */
        --theme-action-reset-bg: #f59e0b;
        --theme-action-reset-hover: #d97706;
        --theme-action-process-bg: #10b981;
        --theme-action-process-hover: #059669;
        --theme-action-delete-bg: #ef4444;
        --theme-action-delete-hover: #dc2626;

        /* Icon Colors */
        --theme-icon-blue: #3b82f6;
        --theme-icon-green: #10b981;
        --theme-icon-emerald: #10b981;
        --theme-icon-purple: #8b5cf6;
        --theme-icon-indigo: #6366f1;
        --theme-icon-orange: #f97316;
        --theme-icon-yellow: #eab308;
        --theme-icon-cyan: #06b6d4;
        --theme-icon-red: #ef4444;
        --theme-icon-gray: #6b7280;

        /* Chart Colors */
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

        /* Scrollbar */
        --theme-scrollbar-track: #374151;
        --theme-scrollbar-thumb: #6B7280;
        --theme-scrollbar-hover: #9CA3AF;

        /* Access Indicators */
        --theme-public-access-bg: rgba(16, 185, 129, 0.2);
        --theme-public-access-text: #34d399;
        --theme-public-access-border: rgba(16, 185, 129, 0.3);
        --theme-secured-access-bg: rgba(245, 158, 11, 0.2);
        --theme-secured-access-text: #fbbf24;
        --theme-secured-access-border: rgba(245, 158, 11, 0.3);

        /* Session Colors */
        --theme-user-session: #3b82f6;
        --theme-user-session-bg: rgba(59, 130, 246, 0.15);
        --theme-guest-session: #06b6d4;
        --theme-guest-session-bg: rgba(6, 182, 212, 0.15);
        --theme-active-session: #f97316;
        --theme-active-session-bg: rgba(249, 115, 22, 0.15);

        /* Event Colors */
        --theme-event-1: #3b82f6;
        --theme-event-2: #10b981;
        --theme-event-3: #f59e0b;
        --theme-event-4: #ef4444;
        --theme-event-5: #8b5cf6;
        --theme-event-6: #ec4899;
        --theme-event-7: #06b6d4;
        --theme-event-8: #f97316;

        /* Muted aliases */
        --theme-muted: #9ca3af;
        --theme-muted-bg: #313e52;
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

    // Apply theme-specific settings
    // Check prefs from database/API, then fall back to theme defaults
    const prefs = preferencesService.getPreferencesSync();

    const sharpCorners = prefs?.sharpCorners ?? (theme.meta.sharpCorners ?? false);
    const disableFocusOutlines = prefs?.disableFocusOutlines ?? (theme.meta.disableFocusOutlines ?? false);
    const disableTooltips = prefs?.disableTooltips ?? (theme.meta.disableTooltips ?? false);

    // Apply focus outlines setting
    document.documentElement.setAttribute(
      'data-disable-focus-outlines',
      disableFocusOutlines.toString()
    );

    // Apply tooltips setting
    document.documentElement.setAttribute('data-disable-tooltips', disableTooltips.toString());

    const colors = theme.colors;

    // Normalize theme: if focus colors aren't defined, use primaryColor
    if (!colors.borderFocus) colors.borderFocus = colors.primaryColor;
    if (!colors.inputFocus) colors.inputFocus = colors.primaryColor;
    if (!colors.checkboxFocus) colors.checkboxFocus = colors.primaryColor;
    if (!colors.cardOutline) colors.cardOutline = colors.primaryColor;

    // Get border radius settings from theme (already set above)
    const borderRadius = sharpCorners ? '0px' : '0.5rem';
    const borderRadiusLg = sharpCorners ? '0px' : '0.75rem';
    const borderRadiusXl = sharpCorners ? '0px' : '1rem';

    // Helper to convert hex to RGB
    const hexToRgb = (hex: string): string => {
      const result = /^#?([a-f\d]{2})([a-f\d]{2})([a-f\d]{2})$/i.exec(hex);
      return result
        ? `${parseInt(result[1], 16)}, ${parseInt(result[2], 16)}, ${parseInt(result[3], 16)}`
        : '0, 0, 0';
    };

    const primaryRgb = colors.primaryColor ? hexToRgb(colors.primaryColor) : '0, 0, 0';
    const secondaryRgb = colors.secondaryColor ? hexToRgb(colors.secondaryColor) : '0, 0, 0';

    // Create clean theme styles with only CSS variables - no Tailwind overrides
    const themeStyles = `
    :root {
      /* Core Colors */
      --theme-primary: ${colors.primaryColor};
      --theme-primary-rgb: ${primaryRgb};
      --theme-secondary: ${colors.secondaryColor};
      --theme-secondary-rgb: ${secondaryRgb};
      --theme-accent: ${colors.accentColor};

      /* Backgrounds */
      --theme-bg-primary: ${colors.bgPrimary};
      --theme-bg-secondary: ${colors.bgSecondary};
      --theme-bg-tertiary: ${colors.bgTertiary};
      --theme-bg-hover: ${colors.bgHover};
      --theme-bg-elevated: ${colors.bgSecondary};

      /* Text */
      --theme-text-primary: ${colors.textPrimary};
      --theme-text-secondary: ${colors.textSecondary};
      --theme-text-muted: ${colors.textMuted};
      --theme-text-accent: ${colors.textAccent};
      --theme-text-placeholder: ${colors.textPlaceholder};

      /* Drag Handle */
      --theme-drag-handle: ${colors.dragHandleColor};
      --theme-drag-handle-hover: ${colors.dragHandleHover};

      /* Borders */
      --theme-border: ${colors.borderPrimary};
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
      --theme-xbox: ${colors.xboxColor};

      /* Card & Component Colors */
      --theme-card-bg: ${colors.cardBg};
      --theme-card-border: ${colors.cardBorder};
      --theme-card-outline: ${colors.cardOutline};
      --theme-card-hover: ${colors.bgHover};

      /* Buttons */
      --theme-button-bg: ${colors.buttonBg};
      --theme-button-hover: ${colors.buttonHover};
      --theme-button-text: ${colors.buttonText};
      --theme-button-primary: ${colors.buttonBg};
      --theme-primary-hover: ${colors.buttonHover};
      --theme-primary-text: ${colors.buttonText};
      --theme-primary-subtle: rgba(${primaryRgb}, 0.1);
      --theme-secondary-bg: ${colors.bgSecondary};

      /* Inputs */
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
      --theme-icon-gray: ${colors.textMuted};
      
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

      /* Session Colors */
      --theme-user-session: ${colors.userSessionColor || colors.primaryColor};
      --theme-user-session-bg: ${colors.userSessionBg || `rgba(${primaryRgb}, 0.15)`};
      --theme-guest-session: ${colors.guestSessionColor || colors.info};
      --theme-guest-session-bg: ${colors.guestSessionBg || colors.infoBg};
      --theme-active-session: ${colors.activeSessionColor || colors.iconBgOrange};
      --theme-active-session-bg: ${colors.activeSessionBg || 'rgba(249, 115, 22, 0.15)'};

      /* Event Colors */
      --theme-event-1: ${colors.eventColor1 || colors.primaryColor};
      --theme-event-2: ${colors.eventColor2 || colors.success};
      --theme-event-3: ${colors.eventColor3 || colors.warning};
      --theme-event-4: ${colors.eventColor4 || colors.error};
      --theme-event-5: ${colors.eventColor5 || colors.secondaryColor};
      --theme-event-6: ${colors.eventColor6 || '#ec4899'};
      --theme-event-7: ${colors.eventColor7 || colors.accentColor};
      --theme-event-8: ${colors.eventColor8 || colors.iconBgOrange};

      /* Alias Variables for Compatibility */
      --theme-muted: ${colors.textMuted};
      --theme-muted-bg: ${colors.bgTertiary};
      --theme-icon-gray: ${colors.textMuted};
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

    // Save theme preferences for all users (authenticated and guests)
    // Save the theme ID and CSS for instant loading on next page load (localStorage for caching)
    storage.setItem('lancache_selected_theme', theme.meta.id);
    storage.setItem('lancache_theme_css', themeStyles);
    storage.setItem('lancache_theme_dark', theme.meta.isDark ? 'true' : 'false');

    // Force re-render
    window.dispatchEvent(new Event('themechange'));
  }

  async loadSavedTheme(selectedThemeFromPrefs?: string | null): Promise<void> {
    // Initialize with default settings (no API calls at startup)
    document.documentElement.setAttribute('data-disable-focus-outlines', 'false');
    document.documentElement.setAttribute('data-disable-tooltips', 'false');

    // Check if we have a preloaded theme from the HTML
    const preloadStyle = document.getElementById('lancache-theme-preload');
    const savedThemeId = storage.getItem('lancache_selected_theme');

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
      storage.removeItem('lancache_selected_theme');
      storage.removeItem('lancache_theme_css');
      storage.removeItem('lancache_theme_dark');
    }

    // Apply default Tailwind dark theme
    this.applyDefaultVariables();

    // Priority 1: Use selectedTheme from API preferences if provided
    if (selectedThemeFromPrefs !== undefined) {
      if (selectedThemeFromPrefs === null) {
        // Null means use default guest theme - fetch it from the API
        try {
          const response = await fetch(`${API_BASE}/themes/preferences/guest`, {
            credentials: 'include',
            headers: authService.getAuthHeaders()
          });
          if (response.ok) {
            const data = await response.json();
            const defaultGuestThemeId = data.themeId || 'dark-default';
            const theme = await this.getTheme(defaultGuestThemeId);
            if (theme) {
              this.applyTheme(theme);
              return;
            }
          }
        } catch (err) {
          console.error('[ThemeService] Failed to fetch default guest theme:', err);
        }
        // Fall through to default if fetching failed
      } else {
        // Use the specified theme from preferences
        const theme = await this.getTheme(selectedThemeFromPrefs);
        if (theme) {
          this.applyTheme(theme);
          return;
        }
      }
    }

    // Priority 2: Fallback to localStorage cache if API didn't have a preference
    if (savedThemeId) {
      const theme = await this.getTheme(savedThemeId);
      if (theme) {
        this.applyTheme(theme);
        return;
      }
    }

    // Priority 3: Default to dark theme if no saved preference or theme not found
    const darkDefault = await this.getTheme('dark-default');
    if (darkDefault) {
      this.applyTheme(darkDefault);
    }
  }

  // Migration removed - preferences are now stored in the database via API

  getCurrentThemeId(): string {
    return this.currentTheme?.meta.id || 'dark-default';
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
    if (theme.meta.sharpCorners !== undefined)
      toml += `sharpCorners = ${theme.meta.sharpCorners}\n`;
    if (theme.meta.disableFocusOutlines !== undefined)
      toml += `disableFocusOutlines = ${theme.meta.disableFocusOutlines}\n`;
    if (theme.meta.disableTooltips !== undefined)
      toml += `disableTooltips = ${theme.meta.disableTooltips}\n`;
    if (theme.meta.isCommunityTheme !== undefined)
      toml += `isCommunityTheme = ${theme.meta.isCommunityTheme}\n`;
    if (theme.meta.basedOn) toml += `basedOn = "${theme.meta.basedOn}"\n`;
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

  async setSharpCorners(enabled: boolean): Promise<void> {
    // Save to API (this will trigger SignalR broadcast to other users)
    await preferencesService.setPreference('sharpCorners', enabled);

    // Apply immediately for current user
    if (this.currentTheme) {
      this.applyTheme(this.currentTheme);
    } else {
      this.applyDefaultVariables();
    }
  }

  

  getSharpCornersSync(): boolean {
    const prefs = preferencesService.getPreferencesSync();
    return prefs?.sharpCorners || false;
  }

  async setDisableFocusOutlines(enabled: boolean): Promise<void> {
    // Save to API
    await preferencesService.setPreference('disableFocusOutlines', enabled);

    // Trigger CSS update
    document.documentElement.setAttribute('data-disable-focus-outlines', enabled.toString());

    // Dispatch event for any components that need to react
    window.dispatchEvent(new Event('focusoutlineschange'));
  }

  

  getDisableFocusOutlinesSync(): boolean {
    const prefs = preferencesService.getPreferencesSync();
    return prefs?.disableFocusOutlines ?? true; // Default to true
  }

  async setDisableTooltips(enabled: boolean): Promise<void> {
    // Save to API
    await preferencesService.setPreference('disableTooltips', enabled);

    // Trigger update
    document.documentElement.setAttribute('data-disable-tooltips', enabled.toString());

    // Dispatch event for any components that need to react
    window.dispatchEvent(new Event('tooltipschange'));
  }

  

  getDisableTooltipsSync(): boolean {
    const prefs = preferencesService.getPreferencesSync();
    return prefs?.disableTooltips || false;
  }

  async setPicsAlwaysVisible(enabled: boolean): Promise<void> {
    // Save to API (this will trigger SignalR broadcast to other users)
    await preferencesService.setPreference('picsAlwaysVisible', enabled);

    // Apply immediately for current user
    window.dispatchEvent(new Event('notificationvisibilitychange'));
  }

  

  getPicsAlwaysVisibleSync(): boolean {
    const prefs = preferencesService.getPreferencesSync();
    return prefs?.picsAlwaysVisible || false;
  }

  async setDisableStickyNotifications(enabled: boolean): Promise<void> {
    // Save to API (this will trigger SignalR broadcast to other users)
    await preferencesService.setPreference('disableStickyNotifications', enabled);

    // Apply immediately for current user
    window.dispatchEvent(new Event('stickynotificationschange'));
  }

  

  getDisableStickyNotificationsSync(): boolean {
    const prefs = preferencesService.getPreferencesSync();
    return prefs?.disableStickyNotifications || false;
  }

  async setTheme(themeId: string): Promise<void> {
    const theme = await this.getTheme(themeId);
    if (theme) {
      this.applyTheme(theme);
    }
  }

  // Removed old API methods - now using preferencesService instead

  // Called after authentication to reload theme from server
  async reloadThemeAfterAuth(): Promise<void> {
    // Reload preferences from API
    preferencesService.clearCache();
    const prefs = await preferencesService.loadPreferences();

    // Load theme from preferences
    if (prefs.selectedTheme) {
      const theme = await this.getTheme(prefs.selectedTheme);
      if (theme) {
        this.applyTheme(theme);
        return;
      }
    }

    // Fallback to localStorage if API didn't have a preference
    const localThemeId = storage.getItem('lancache_selected_theme');
    if (localThemeId) {
      const theme = await this.getTheme(localThemeId);
      if (theme) {
        this.applyTheme(theme);
        // Save to API for future use
        await preferencesService.setPreference('selectedTheme', localThemeId);
        return;
      }
    }
  }

  // Preview theme state management
  setPreviewTheme(themeId: string | null): void {
    if (themeId) {
      storage.setItem('lancache_preview_theme', themeId);
    } else {
      storage.removeItem('lancache_preview_theme');
    }
  }

  getPreviewTheme(): string | null {
    return storage.getItem('lancache_preview_theme');
  }

  clearPreviewTheme(): void {
    storage.removeItem('lancache_preview_theme');
  }

  // Save the original theme before starting preview
  setOriginalThemeBeforePreview(themeId: string): void {
    // Only save if we're not already in preview mode
    if (!this.getPreviewTheme()) {
      storage.setItem('lancache_original_theme_before_preview', themeId);
    }
  }

  getOriginalThemeBeforePreview(): string | null {
    return storage.getItem('lancache_original_theme_before_preview');
  }

  clearOriginalThemeBeforePreview(): void {
    storage.removeItem('lancache_original_theme_before_preview');
  }
}

export default new ThemeService();
