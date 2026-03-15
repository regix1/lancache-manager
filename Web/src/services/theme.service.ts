import { API_BASE } from '../utils/constants';
import authService from './auth.service';
import preferencesService from './preferences.service';
import * as TOML from 'toml';
import { storage } from '@utils/storage';
import { parseThemeColors, hexToRgba as schemaHexToRgba } from './themeSchema';

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
  colors: Record<string, string | undefined>;
  custom?: Record<string, string>;
  css?: { content?: string };
}

class ThemeService {
  // Get the best text color for a given background using theme colors

  private currentTheme: Theme | null = null;
  private styleElement: HTMLStyleElement | null = null;
  private preferenceListenersSetup = false;
  private isProcessingReset = false;

  // Store preference values locally - updated via preference-changed events
  // This ensures themeService doesn't depend on preferencesService cache being in sync
  private _sharpCorners = false;
  private _disableFocusOutlines = true;
  private _disableTooltips = false;
  private _picsAlwaysVisible = false;
  private _disableStickyNotifications = false;
  private _preferencesInitialized = false;

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
                credentials: 'include'
              })
                .then((response) => {
                  if (response.ok) {
                    return response.json();
                  }
                  throw new Error('Failed to fetch default guest theme');
                })
                .then((data) => {
                  const defaultTheme = data.themeId || 'dark-default';
                  if (defaultTheme !== this.getCurrentThemeId()) {
                    return this.setTheme(defaultTheme);
                  }
                })
                .catch((err) => {
                  console.error('[ThemeService] Failed to fetch default guest theme:', err);
                });
            } else if (typeof value === 'string' && value !== this.getCurrentThemeId()) {
              this.setTheme(value);
            }
            break;

          case 'sharpCorners':
            // Store the value locally before applying
            this._sharpCorners = value as boolean;
            // Re-apply current theme to update border radius
            if (this.currentTheme) {
              this.applyTheme(this.currentTheme);
            } else {
              this.applyDefaultVariables();
            }
            break;

          case 'disableFocusOutlines':
            if (value !== null && value !== undefined) {
              this._disableFocusOutlines = value as boolean;
              document.documentElement.setAttribute(
                'data-disable-focus-outlines',
                value.toString()
              );
              window.dispatchEvent(new Event('focusoutlineschange'));
            }
            break;

          case 'disableTooltips':
            if (value !== null && value !== undefined) {
              this._disableTooltips = value as boolean;
              document.documentElement.setAttribute('data-disable-tooltips', value.toString());
              window.dispatchEvent(new Event('tooltipschange'));
            }
            break;

          case 'picsAlwaysVisible':
            this._picsAlwaysVisible = value as boolean;
            window.dispatchEvent(new Event('notificationvisibilitychange'));
            break;

          case 'disableStickyNotifications':
            this._disableStickyNotifications = value as boolean;
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
        return;
      }

      this.isProcessingReset = true;

      try {
        // Clear localStorage theme cache
        storage.removeItem('lancache_selected_theme');
        storage.removeItem('lancache_theme_css');
        storage.removeItem('lancache_theme_dark');

        // Reload preferences from API and reinitialize local state
        const prefs = await preferencesService.loadPreferences();
        this.initializePreferences(prefs);

        // Load and apply default theme
        await this.loadSavedTheme(prefs);

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

  private _builtInThemesCache: Theme[] | null = null;

  getBuiltInThemes(): Theme[] {
    if (this._builtInThemesCache) return this._builtInThemesCache;

    // Run colors through schema to ensure all keys are present (including derived colors)
    const complete = (
      colors: Record<string, string | undefined>
    ): Record<string, string | undefined> =>
      parseThemeColors(colors as Record<string, unknown>) as Record<string, string | undefined>;

    this._builtInThemesCache = [
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
        colors: complete({})
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
        colors: complete({
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
          steamFaint: 'rgba(5, 150, 105, 0.1)',
          steamOnBorder: 'rgba(5, 150, 105, 0.5)',
          steamStrong: 'rgba(5, 150, 105, 0.3)',
          epicColor: '#7c3aed',
          epicFaint: 'rgba(124, 58, 237, 0.1)',
          epicOnBorder: 'rgba(124, 58, 237, 0.5)',
          epicStrong: 'rgba(124, 58, 237, 0.3)',
          originColor: '#ea580c',
          blizzardColor: '#2563eb',
          wsusColor: '#0891b2',
          riotColor: '#dc2626',
          xboxColor: '#107C10', // Xbox Green
          ubisoftColor: '#db2777', // Pink

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
          eventColor8: '#ea580c', // Orange

          // Firework/celebration colors - Light blue theme (vibrant blues with purple accents)
          fireworkColor1: '#2563eb', // Primary blue
          fireworkColor2: '#3b82f6', // Medium blue
          fireworkColor3: '#0891b2', // Cyan
          fireworkColor4: '#7c3aed', // Purple
          fireworkColor5: '#06b6d4', // Bright cyan
          fireworkColor6: '#8b5cf6', // Light purple
          fireworkColor7: '#0ea5e9', // Sky blue
          fireworkColor8: '#ffffff', // White
          fireworkRocketColor: '#2563eb', // Blue (uses primaryColor)
          fireworkGlowColor: '#3b82f6' // Blue glow
        })
      }
    ];
    return this._builtInThemesCache;
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

      const theme = parsed as Theme;
      theme.colors = parseThemeColors(theme.colors as Record<string, unknown>);
      return theme;
    } catch (error) {
      console.error('Error parsing TOML theme:', error);
      return null;
    }
  }

  /**
   * Generate computed color tier CSS variables from base theme colors.
   * Uses hexToRgba for opacity tiers — simple "base color + transparency".
   * Blend variables use explicit defaults — theme creators override directly.
   */
  private generateComputedColorVars(colors: Record<string, string | undefined>): string {
    const rgba = (hex: string, opacity: number): string => schemaHexToRgba(hex, opacity);

    // Resolve base colors — guaranteed present by schema
    const primary = colors.primaryColor!;
    const accent = colors.accentColor!;
    const success = colors.success!;
    const warning = colors.warning!;
    const error = colors.error!;
    const info = colors.info!;
    const steam = colors.steamColor!;
    const epic = colors.epicColor!;
    const buttonBg = colors.buttonBg!;
    const actionDelete = colors.actionDeleteBg!;
    const actionProcess = colors.actionProcessBg!;
    const actionReset = colors.actionResetBg!;
    const bgPrimary = colors.bgPrimary!;
    const bgSecondary = colors.bgSecondary!;
    const bgTertiary = colors.bgTertiary!;
    const cardBg = colors.cardBg!;
    const textPrimary = colors.textPrimary!;
    const textSecondary = colors.textSecondary!;
    const textMuted = colors.textMuted!;
    const iconBlue = colors.iconBgBlue!;
    const iconGreen = colors.iconBgGreen!;
    const iconEmerald = colors.iconBgEmerald!;
    const iconPurple = colors.iconBgPurple!;
    const iconIndigo = colors.iconBgIndigo!;
    const iconOrange = colors.iconBgOrange!;
    const iconYellow = colors.iconBgYellow!;
    const iconCyan = colors.iconBgCyan!;
    const iconRed = colors.iconBgRed!;
    const iconGray = colors.textMuted!;
    const chartColor1 = colors.chartColor1!;
    const chartCacheHit = colors.chartCacheHitColor!;
    // Event colors — guaranteed present by schema
    const ev = [
      colors.eventColor1!,
      colors.eventColor2!,
      colors.eventColor3!,
      colors.eventColor4!,
      colors.eventColor5!,
      colors.eventColor6!,
      colors.eventColor7!,
      colors.eventColor8!
    ];

    // Helper: use theme override if provided, else computed value
    const v = (key: string, computed: string): string => colors[key] || computed;

    // Generate event tier vars for all 8 event colors
    const eventVars = ev
      .map((ec, i) => {
        const n = i + 1;
        return `
      --theme-event-${n}-subtle: ${v(`eventColor${n}Subtle`, rgba(ec, 0.15))};
      --theme-event-${n}-muted: ${v(`eventColor${n}Muted`, rgba(ec, 0.25))};
      --theme-event-${n}-strong: ${v(`eventColor${n}Strong`, rgba(ec, 0.4))};
      --theme-event-${n}-emphasis: ${v(`eventColor${n}Emphasis`, rgba(ec, 0.6))};
      --theme-event-${n}-intense: ${v(`eventColor${n}Intense`, rgba(ec, 0.8))};
      --theme-event-${n}-on-bg: ${v(`eventColor${n}OnBg`, rgba(ec, 0.5))};
      --theme-event-${n}-on-bg-strong: ${v(`eventColor${n}OnBgStrong`, rgba(ec, 0.65))};
      --theme-event-${n}-on-bg-soft: ${v(`eventColor${n}OnBgSoft`, rgba(ec, 0.35))};`;
      })
      .join('\n');

    return `
      /* ===== Opacity Tiers (base color + transparency) ===== */

      /* Primary */
      --theme-primary-faint: ${v('primaryFaint', rgba(primary, 0.08))};
      --theme-primary-subtle: ${v('primarySubtle', rgba(primary, 0.15))};
      --theme-primary-muted: ${v('primaryMuted', rgba(primary, 0.25))};
      --theme-primary-strong: ${v('primaryStrong', rgba(primary, 0.4))};
      --theme-primary-bg: var(--theme-primary-subtle);

      /* Success */
      --theme-success-faint: ${v('successFaint', rgba(success, 0.08))};
      --theme-success-subtle: ${v('successSubtle', rgba(success, 0.15))};
      --theme-success-muted: ${v('successMuted', rgba(success, 0.2))};
      --theme-success-strong: ${v('successStrong', rgba(success, 0.4))};

      /* Warning */
      --theme-warning-faint: ${v('warningFaint', rgba(warning, 0.08))};
      --theme-warning-subtle: ${v('warningSubtle', rgba(warning, 0.15))};
      --theme-warning-muted: ${v('warningMuted', rgba(warning, 0.2))};
      --theme-warning-strong: ${v('warningStrong', rgba(warning, 0.3))};

      /* Error */
      --theme-error-faint: ${v('errorFaint', rgba(error, 0.1))};
      --theme-error-subtle: ${v('errorSubtle', rgba(error, 0.15))};
      --theme-error-muted: ${v('errorMuted', rgba(error, 0.2))};
      --theme-error-strong: ${v('errorStrong', rgba(error, 0.3))};

      /* Info */
      --theme-info-subtle: ${v('infoSubtle', rgba(info, 0.15))};
      --theme-info-muted: ${v('infoMuted', rgba(info, 0.2))};

      /* Accent */
      --theme-accent-faint: ${v('accentFaint', rgba(accent, 0.06))};
      --theme-accent-subtle: ${v('accentSubtle', rgba(accent, 0.15))};
      --theme-accent-muted: ${v('accentMuted', rgba(accent, 0.2))};

      /* Platform */
      --theme-steam-subtle: ${v('steamSubtle', rgba(steam, 0.15))};
      --theme-steam-faint: ${colors.steamFaint};
      --theme-steam-on-border: ${colors.steamOnBorder};
      --theme-steam-strong: ${colors.steamStrong};
      --theme-epic-subtle: ${v('epicSubtle', rgba(epic, 0.15))};
      --theme-epic-faint: ${colors.epicFaint};
      --theme-epic-on-border: ${colors.epicOnBorder};
      --theme-epic-strong: ${colors.epicStrong};
      --theme-epic-muted: ${v('epicMuted', rgba(epic, 0.25))};

      /* Icon Backgrounds */
      --theme-icon-blue-subtle: ${v('iconBlueSubtle', rgba(iconBlue, 0.15))};
      --theme-icon-green-subtle: ${v('iconGreenSubtle', rgba(iconGreen, 0.15))};
      --theme-icon-emerald-subtle: ${v('iconEmeraldSubtle', rgba(iconEmerald, 0.15))};
      --theme-icon-purple-subtle: ${v('iconPurpleSubtle', rgba(iconPurple, 0.15))};
      --theme-icon-indigo-subtle: ${v('iconIndigoSubtle', rgba(iconIndigo, 0.15))};
      --theme-icon-orange-subtle: ${v('iconOrangeSubtle', rgba(iconOrange, 0.15))};
      --theme-icon-yellow-subtle: ${v('iconYellowSubtle', rgba(iconYellow, 0.15))};
      --theme-icon-cyan-subtle: ${v('iconCyanSubtle', rgba(iconCyan, 0.15))};
      --theme-icon-red-subtle: ${v('iconRedSubtle', rgba(iconRed, 0.15))};
      --theme-icon-gray-subtle: ${v('iconGraySubtle', rgba(iconGray, 0.15))};
      --theme-icon-red-muted: ${v('iconRedMuted', rgba(iconRed, 0.2))};
      --theme-icon-purple-faint: ${v('iconPurpleFaint', rgba(iconPurple, 0.1))};

      /* Button/Action */
      --theme-button-bg-subtle: ${v('buttonBgSubtle', rgba(buttonBg, 0.1))};
      --theme-action-delete-subtle: ${v('actionDeleteSubtle', rgba(actionDelete, 0.1))};
      --theme-action-process-subtle: ${v('actionProcessSubtle', rgba(actionProcess, 0.12))};
      --theme-action-process-muted: ${v('actionProcessMuted', rgba(actionProcess, 0.25))};
      --theme-action-process-strong: ${v('actionProcessStrong', rgba(actionProcess, 0.4))};
      --theme-action-reset-subtle: ${v('actionResetSubtle', rgba(actionReset, 0.1))};

      /* Background Alpha */
      --theme-bg-tertiary-muted: ${v('bgTertiaryMuted', rgba(bgTertiary, 0.3))};
      --theme-bg-tertiary-strong: ${v('bgTertiaryStrong', rgba(bgTertiary, 0.5))};
      --theme-bg-tertiary-emphasis: ${v('bgTertiaryEmphasis', rgba(bgTertiary, 0.8))};
      --theme-bg-secondary-strong: ${v('bgSecondaryStrong', rgba(bgSecondary, 0.5))};
      --theme-bg-secondary-emphasis: ${v('bgSecondaryEmphasis', rgba(bgSecondary, 0.6))};
      --theme-bg-primary-emphasis: ${v('bgPrimaryEmphasis', rgba(bgPrimary, 0.8))};
      --theme-card-bg-emphasis: ${v('cardBgEmphasis', rgba(cardBg, 0.85))};
      --theme-card-bg-full: ${v('cardBgFull', rgba(cardBg, 0.95))};

      /* Text Alpha */
      --theme-text-primary-faint: ${v('textPrimaryFaint', rgba(textPrimary, 0.06))};
      --theme-text-primary-strong: ${v('textPrimaryStrong', rgba(textPrimary, 0.3))};
      --theme-text-primary-emphasis: ${v('textPrimaryEmphasis', rgba(textPrimary, 0.6))};
      --theme-text-secondary-subtle: ${v('textSecondarySubtle', rgba(textSecondary, 0.1))};
      --theme-text-secondary-muted: ${v('textSecondaryMuted', rgba(textSecondary, 0.2))};
      --theme-text-muted-faint: ${v('textMutedFaint', rgba(textMuted, 0.08))};
      --theme-text-muted-subtle: ${v('textMutedSubtle', rgba(textMuted, 0.1))};
      --theme-text-muted-muted: ${v('textMutedMuted', rgba(textMuted, 0.2))};

      /* Fixed Colors */
      --theme-shadow-black: ${v('shadowBlack', 'rgba(0, 0, 0, 0.08)')};
      --theme-badge-white-subtle: ${v('badgeWhiteSubtle', 'rgba(255, 255, 255, 0.20)')};
      --theme-glint-white: ${v('glintWhite', 'rgba(255, 255, 255, 0.05)')};

      /* Blend Variables — use rgba opacity (adapts to any theme's colors) */
      --theme-primary-on-bg: ${v('primaryOnBg', rgba(primary, 0.12))};
      --theme-primary-on-bg-hover: ${v('primaryOnBgHover', rgba(primary, 0.18))};
      --theme-primary-on-border: ${v('primaryOnBorder', rgba(primary, 0.25))};
      --theme-success-on-bg: ${v('successOnBg', rgba(success, 0.08))};
      --theme-success-on-border: ${v('successOnBorder', rgba(success, 0.3))};
      --theme-warning-on-error: ${v('warningOnError', rgba(warning, 0.8))};
      --theme-bg-secondary-on-tertiary: ${v('bgSecondaryOnTertiary', rgba(bgSecondary, 0.8))};
      --theme-danger-gradient-start: ${v('dangerGradientStart', 'rgba(255, 107, 107, 0.10)')};
      --theme-danger-gradient-end: ${v('dangerGradientEnd', 'rgba(238, 90, 90, 0.10)')};
      --theme-danger-border: ${v('dangerBorder', 'rgba(255, 107, 107, 0.30)')};
      --theme-chart-hit-highlight: ${v('chartHitHighlight', rgba(chartCacheHit, 0.8))};
      --theme-chart-miss-deep: ${v('chartMissDeep', rgba(error, 0.8))};
      --theme-chart-1-muted: ${v('chartColor1Muted', rgba(chartColor1, 0.3))};
      --theme-chart-1-strong: ${v('chartColor1Strong', rgba(chartColor1, 0.5))};
      --theme-chart-1-emphasis: ${v('chartColor1Emphasis', rgba(chartColor1, 0.75))};

      /* Event Color Tiers */
      ${eventVars}

      /* Glow */
      --theme-glow-soft: ${v('glowSoft', rgba(primary, 0.3))};
      --theme-glow-intense: ${v('glowIntense', rgba(primary, 0.7))};
    `;
  }

  private applyDefaultVariables(): void {
    const defaultTheme = this.getBuiltInThemes().find((t) => t.meta.id === 'dark-default')!;
    this.applyTheme(defaultTheme);
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
    // Use local state (initialized via loadSavedTheme or preference-changed events)
    // Fall back to theme defaults if not yet initialized
    const sharpCorners = this._preferencesInitialized
      ? this._sharpCorners
      : (theme.meta.sharpCorners ?? false);
    const disableFocusOutlines = this._preferencesInitialized
      ? this._disableFocusOutlines
      : (theme.meta.disableFocusOutlines ?? false);
    const disableTooltips = this._preferencesInitialized
      ? this._disableTooltips
      : (theme.meta.disableTooltips ?? false);

    // Apply focus outlines setting
    document.documentElement.setAttribute(
      'data-disable-focus-outlines',
      disableFocusOutlines.toString()
    );

    // Apply tooltips setting
    document.documentElement.setAttribute('data-disable-tooltips', disableTooltips.toString());

    const colors = theme.colors;

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

    const primaryRgb = hexToRgb(colors.primaryColor!);
    const secondaryRgb = hexToRgb(colors.secondaryColor!);

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

      /* Status Aliases */
      --theme-status-success: ${colors.success};
      --theme-status-error: ${colors.error};

      /* Service Colors */
      --theme-steam: ${colors.steamColor};
      --theme-epic: ${colors.epicColor};
      --theme-origin: ${colors.originColor};
      --theme-blizzard: ${colors.blizzardColor};
      --theme-wsus: ${colors.wsusColor};
      --theme-riot: ${colors.riotColor};
      --theme-xbox: ${colors.xboxColor};
      --theme-ubisoft: ${colors.ubisoftColor};

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
      
      /* Floating Icon */
      --theme-floating-icon: ${colors.floatingIconColor};

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
      --theme-scrollbar-track: ${colors.scrollbarTrack};
      --theme-scrollbar-thumb: ${colors.scrollbarThumb};
      --theme-scrollbar-hover: ${colors.scrollbarHover};

      /* Access Indicator Colors */
      --theme-public-access-bg: ${colors.publicAccessBg};
      --theme-public-access-text: ${colors.publicAccessText};
      --theme-public-access-border: ${colors.publicAccessBorder};
      --theme-secured-access-bg: ${colors.securedAccessBg};
      --theme-secured-access-text: ${colors.securedAccessText};
      --theme-secured-access-border: ${colors.securedAccessBorder};

      /* Session Colors */
      --theme-user-session: ${colors.userSessionColor};
      --theme-user-session-bg: ${colors.userSessionBg};
      --theme-guest-session: ${colors.guestSessionColor};
      --theme-guest-session-bg: ${colors.guestSessionBg};
      --theme-active-session: ${colors.activeSessionColor};
      --theme-active-session-bg: ${colors.activeSessionBg};

      /* Event Colors */
      --theme-event-1: ${colors.eventColor1};
      --theme-event-2: ${colors.eventColor2};
      --theme-event-3: ${colors.eventColor3};
      --theme-event-4: ${colors.eventColor4};
      --theme-event-5: ${colors.eventColor5};
      --theme-event-6: ${colors.eventColor6};
      --theme-event-7: ${colors.eventColor7};
      --theme-event-8: ${colors.eventColor8};

      /* Firework Colors */
      --theme-firework-1: ${colors.fireworkColor1};
      --theme-firework-2: ${colors.fireworkColor2};
      --theme-firework-3: ${colors.fireworkColor3};
      --theme-firework-4: ${colors.fireworkColor4};
      --theme-firework-5: ${colors.fireworkColor5};
      --theme-firework-6: ${colors.fireworkColor6};
      --theme-firework-7: ${colors.fireworkColor7};
      --theme-firework-8: ${colors.fireworkColor8};
      --theme-firework-rocket: ${colors.fireworkRocketColor};
      --theme-firework-glow: ${colors.fireworkGlowColor};

      /* Alias Variables for Compatibility */
      --theme-muted: ${colors.textMuted};
      --theme-muted-bg: ${colors.bgTertiary};
      --theme-icon-gray: ${colors.textMuted};

      ${this.generateComputedColorVars(colors)}
    }

    /* Global Transitions */
    body * {
      transition: background-color 0.2s ease, color 0.2s ease;
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

  async loadSavedTheme(
    prefs?: {
      selectedTheme?: string | null;
      sharpCorners?: boolean;
      disableFocusOutlines?: boolean;
      disableTooltips?: boolean;
      picsAlwaysVisible?: boolean;
      disableStickyNotifications?: boolean;
    } | null
  ): Promise<void> {
    // Initialize local preference state from provided preferences
    if (prefs) {
      this.initializePreferences(prefs);
    }

    // Initialize with default settings (no API calls at startup)
    document.documentElement.setAttribute('data-disable-focus-outlines', 'false');
    document.documentElement.setAttribute('data-disable-tooltips', 'false');

    const selectedThemeFromPrefs = prefs?.selectedTheme;

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
            credentials: 'include'
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
      Object.entries(theme.colors)
        .filter(([, value]) => value !== undefined && value !== '')
        .forEach(([key, value]) => {
          toml += `${key} = "${value}"\n`;
        });
    }
    toml += '\n';

    if (theme.css?.content) {
      toml += '[css]\n';
      toml += `content = """\n${theme.css.content}\n"""\n`;
    }

    return toml;
  }

  async setSharpCorners(enabled: boolean): Promise<void> {
    // Update local state immediately
    this._sharpCorners = enabled;

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
    return this._sharpCorners;
  }

  /**
   * Initialize local preference values from preferences object
   * Called from loadSavedTheme with preferences from main.tsx
   */
  initializePreferences(prefs: {
    sharpCorners?: boolean;
    disableFocusOutlines?: boolean;
    disableTooltips?: boolean;
    picsAlwaysVisible?: boolean;
    disableStickyNotifications?: boolean;
  }): void {
    this._sharpCorners = prefs.sharpCorners ?? false;
    this._disableFocusOutlines = prefs.disableFocusOutlines ?? true;
    this._disableTooltips = prefs.disableTooltips ?? false;
    this._picsAlwaysVisible = prefs.picsAlwaysVisible ?? false;
    this._disableStickyNotifications = prefs.disableStickyNotifications ?? false;
    this._preferencesInitialized = true;
  }

  async setDisableFocusOutlines(enabled: boolean): Promise<void> {
    // Update local state immediately
    this._disableFocusOutlines = enabled;

    // Save to API
    await preferencesService.setPreference('disableFocusOutlines', enabled);

    // Trigger CSS update
    document.documentElement.setAttribute('data-disable-focus-outlines', enabled.toString());

    // Dispatch event for any components that need to react
    window.dispatchEvent(new Event('focusoutlineschange'));
  }

  getDisableFocusOutlinesSync(): boolean {
    return this._disableFocusOutlines;
  }

  async setDisableTooltips(enabled: boolean): Promise<void> {
    // Update local state immediately
    this._disableTooltips = enabled;

    // Save to API
    await preferencesService.setPreference('disableTooltips', enabled);

    // Trigger update
    document.documentElement.setAttribute('data-disable-tooltips', enabled.toString());

    // Dispatch event for any components that need to react
    window.dispatchEvent(new Event('tooltipschange'));
  }

  getDisableTooltipsSync(): boolean {
    return this._disableTooltips;
  }

  async setPicsAlwaysVisible(enabled: boolean): Promise<void> {
    // Update local state immediately
    this._picsAlwaysVisible = enabled;

    // Save to API (this will trigger SignalR broadcast to other users)
    await preferencesService.setPreference('picsAlwaysVisible', enabled);

    // Apply immediately for current user
    window.dispatchEvent(new Event('notificationvisibilitychange'));
  }

  getPicsAlwaysVisibleSync(): boolean {
    return this._picsAlwaysVisible;
  }

  async setDisableStickyNotifications(enabled: boolean): Promise<void> {
    // Update local state immediately
    this._disableStickyNotifications = enabled;

    // Save to API (this will trigger SignalR broadcast to other users)
    await preferencesService.setPreference('disableStickyNotifications', enabled);

    // Apply immediately for current user
    window.dispatchEvent(new Event('stickynotificationschange'));
  }

  getDisableStickyNotificationsSync(): boolean {
    return this._disableStickyNotifications;
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
    // Reload preferences from API and reinitialize local state
    const prefs = await preferencesService.loadPreferences();
    this.initializePreferences(prefs);

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
