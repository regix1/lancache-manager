import { API_BASE } from '../utils/constants';
import authService from './auth.service';
import preferencesService from './preferences.service';
import * as TOML from 'toml';
import { storage } from '@utils/storage';
import { parseThemeColors, hexToRgba as schemaHexToRgba } from './themeSchema';
import { assertOk } from './apiError';
import i18n from '@/i18n';
import { APP_EVENTS } from '@utils/constants';

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

interface ApplyThemeOptions {
  // Whether this apply is also a choice. It governs the three localStorage keys and nothing else:
  // the style element, the data attributes, the html background and the change event all happen
  // either way, because the page still has to look right and every live consumer still has to
  // repaint. The theme slider repaints on every stop the thumb passes, and those passes are not
  // choices - with the keys written, a drag abandoned by a reload or a tab close comes back as the
  // saved theme, because loadSavedTheme prefers lancache_selected_theme over the server
  // preference. Defaults to true so every existing caller keeps writing.
  persist?: boolean;
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

    window.addEventListener(APP_EVENTS.PREFERENCE_CHANGED, (event: Event) => {
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
              window.dispatchEvent(new Event(APP_EVENTS.TOOLTIPS_CHANGE));
            }
            break;

          case 'picsAlwaysVisible':
            this._picsAlwaysVisible = value as boolean;
            window.dispatchEvent(new Event(APP_EVENTS.NOTIFICATION_VISIBILITY_CHANGE));
            break;

          case 'disableStickyNotifications':
            this._disableStickyNotifications = value as boolean;
            window.dispatchEvent(new Event(APP_EVENTS.STICKY_NOTIFICATIONS_CHANGE));
            break;
        }
      } catch (err) {
        console.error(`[ThemeService] Error handling preference change for ${key}:`, err);
      }
    });

    // Listen for preferences reset event
    window.addEventListener(APP_EVENTS.PREFERENCES_RESET, async () => {
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
          ? i18n.t('management.themes.notifications.preferencesResetGuest')
          : i18n.t('management.themes.notifications.preferencesReset');

        window.dispatchEvent(
          new CustomEvent(APP_EVENTS.SHOW_TOAST, {
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
      if (themeIds.has(theme.meta.id)) {
        const existingIndex = allThemes.findIndex((t) => t.meta.id === theme.meta.id);
        if (existingIndex !== -1) {
          allThemes[existingIndex] = theme;
        }
      } else {
        allThemes.push(theme);
        themeIds.add(theme.meta.id);
      }
    });

    return allThemes;
  }

  private _builtInThemesCache: Theme[] | null = null;
  private _builtInThemesLanguage: string | null = null;

  getBuiltInThemes(): Theme[] {
    // The names and descriptions come from the active language, so a language change has to rebuild
    // the cache or every theme picker keeps showing the previous language.
    if (this._builtInThemesCache && this._builtInThemesLanguage === i18n.language) {
      return this._builtInThemesCache;
    }
    this._builtInThemesLanguage = i18n.language;

    // Run colors through schema to ensure all keys are present (including derived colors)
    const complete = (
      colors: Record<string, string | undefined>
    ): Record<string, string | undefined> =>
      parseThemeColors(colors as Record<string, unknown>) as Record<string, string | undefined>;

    this._builtInThemesCache = [
      {
        meta: {
          id: 'dark-default',
          name: i18n.t('management.themes.builtIn.darkDefault.name'),
          description: i18n.t('management.themes.builtIn.darkDefault.description'),
          author: i18n.t('management.themes.builtIn.systemAuthor'),
          version: '1.0.4',
          isDark: true,
          sharpCorners: false,
          disableFocusOutlines: true,
          disableTooltips: false
        },
        colors: complete({
          // The button parts company with primaryColor here rather than following it: white on the
          // accent itself is 3.68:1, and the accent has to stay where it is because it is also the
          // nav tab, the focus ring and the first chart series. A step down the same blue carries
          // the label at 5.17:1, and the hover takes the step below that.
          buttonBg: '#2563eb',
          buttonHover: '#1d4ed8',
          // These four are shades the schema can now grow from a theme's own colours, and the
          // shades it grows land a little off the Tailwind steps this palette was cut from: the
          // ramp turns toward cyan as it lightens, and this theme's badge ground is brighter than
          // most. Stating them keeps the shipped blues exactly where they have always been while
          // the derivation serves themes that set an accent and nothing else.
          textAccent: '#60a5fa',
          // The status glyph paints itself in `info` on this ground, and blue-500 on blue-900 was
          // 2.82:1. The pair opens up from both ends: the blue climbs a step, the ground drops one.
          info: '#60a5fa',
          infoBg: '#1c3468',
          infoText: '#93c5fd',
          fireworkGlowColor: '#60a5fa',

          // Status and icon inks: the schema defaults clear contrast by climbing the Tailwind
          // 400 ramp, which puts high-chroma mint, amber and cyan on the slate card. Those
          // vibrate as Status Check text and as solid ribbon fills. Same hues, chroma pulled
          // down; every pairing below is measured on bgSecondary (#283649) unless noted.
          success: '#4ab591', // 4.85:1; 5.81:1 on cardBg, 4.72:1 on successBg
          successText: '#62bc9e', // 5.37:1; 5.23:1 on successBg
          warning: '#df9f68', // 5.42:1; 4.55:1 on warningBg
          warningText: '#d3b769', // 6.27:1; 5.26:1 on warningBg
          errorText: '#d89797', // 5.20:1 on errorBg; the old #fca5a5 was a neon pink
          iconBgGreen: '#4ab591',
          iconBgEmerald: '#4ab591',
          iconBgOrange: '#df9f68',
          iconBgYellow: '#c8ad41', // 5.55:1
          iconBgCyan: '#5cacbc', // 4.72:1
          iconBgTeal: '#56b3aa', // 4.91:1
          hitRateHighText: '#62bc9e',
          hitRateLowText: '#d3b769',
          hitRateWarningText: '#d3b769',
          publicAccessText: '#62bc9e',
          publicAccessBg: 'rgba(74, 181, 145, 0.2)',
          publicAccessBorder: 'rgba(74, 181, 145, 0.3)',
          chartCacheHitColor: '#4ab591'
        })
      },
      // Modern, clean light theme (GitHub Primer / Radix pattern):
      // clearly-grey page canvas, crisp white cards with subtle borders + shadows.
      // Radix scale: cards = steps 1-2 (near-white), card borders = step 6 (subtle),
      // interactive borders = step 7 (slate-300). Grey canvas kills the glare.
      {
        meta: {
          id: 'light-default',
          name: i18n.t('management.themes.builtIn.lightDefault.name'),
          description: i18n.t('management.themes.builtIn.lightDefault.description'),
          author: i18n.t('management.themes.builtIn.systemAuthor'),
          version: '5.0.3',
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

          // Backgrounds - Grey canvas, white surfaces
          // Key: the page itself is visibly grey; cards/modals/nav are white and pop
          bgPrimary: '#eef1f6', // Cool grey page canvas - kills the white glare
          bgSecondary: '#ffffff', // White panels: sidebar/dropdowns/list rows
          bgTertiary: '#eff3f8', // Nested elements, table headers (on white cards)
          bgHover: '#e2e8f0', // slate-200 - hover state
          bgElevated: '#ffffff', // Drawer/modal/floating panel background
          bgSurface: '#f6f8fb', // Sections within elevated surfaces
          bgSurfaceHover: '#eef1f6', // Hover on surface elements
          bgSurfaceActive: '#e2e8f0', // Active/pressed surface elements
          bgOverlay: 'rgba(15,23,42,0.5)', // Slate-tinted backdrop overlay

          // Derived-var overrides: the computed defaults are white-alpha tints
          // (rgba of the white surfaces above), which are invisible on light
          // backgrounds. Replace with slate-alpha tints that darken instead.
          skeletonBase: 'rgba(15, 23, 42, 0.06)', // Loading shimmer base
          bgSecondaryStrong: 'rgba(148, 163, 184, 0.2)', // Hover tint on white pills/tabs
          bgTertiaryMuted: 'rgba(148, 163, 184, 0.1)', // Striped table rows
          bgTertiaryStrong: 'rgba(148, 163, 184, 0.16)', // Table row hover

          // Text - Crisp primary with a real hierarchy step-down
          textPrimary: '#0f172a', // slate-900 - near black for crisp readability
          textSecondary: '#334155', // slate-700 - clearly secondary, still strong
          textMuted: '#64748b', // slate-500 - muted but AA on white
          textAccent: '#1d4ed8', // blue-700 - rich blue for links
          textPlaceholder: '#94a3b8', // slate-400 - placeholders recede

          // Drag handle
          dragHandleColor: '#94a3b8',
          dragHandleHover: '#2563eb',

          // Borders - Subtle on cards (Radix step 6), stronger on interactive bits
          borderPrimary: '#dde3ec', // Soft card/divider border
          borderSecondary: '#cbd5e1', // slate-300 - stronger separators
          // parseThemeColors() pre-fills every omitted key from the dark defaults
          // (including borderWell's near-invisible white-alpha rgba), so this must
          // be set explicitly here or recessed wells get no visible edge in light.
          borderWell: '#dde3ec', // Same quiet tone as the card/divider border
          borderFocus: '#2563eb',
          borderElevated: '#dde3ec', // Borders within elevated panels
          borderHover: '#94a3b8', // slate-400 - border hover state

          // Navigation - White bar over the grey canvas, subtle border
          navBg: '#ffffff',
          navBorder: '#dde3ec',
          navTabActive: '#2563eb',
          navTabInactive: '#64748b',
          navTabHover: '#1e293b',
          navTabActiveBorder: '#2563eb',
          navMobileMenuBg: '#ffffff',
          navMobileItemHover: '#eef1f6',

          // Status colors - Vibrant and accessible
          // Badge backgrounds use -100 shades; -50 shades disappear on grey cards
          // Each of these four is the ink for its own -100 badge ground as well as for icons on a
          // white card, and the 600 steps could not carry both: amber-600 read 2.86:1 on amber-100
          // and 3.19:1 on white, emerald-600 3.32:1 and 3.77:1. Every one drops far enough down its
          // own ramp to clear 4.5:1 on the tinted ground, which leaves it well clear on white too.
          // Green goes a step further than the other three: the live pill draws its label in this
          // colour on a 20% wash of the same colour, so the wash eats into whatever margin the
          // green has on the card and 4.5:1 there needs 6.5:1 on white.
          success: '#046b4b', // 5.77:1 on successBg, 6.54:1 on a white card, 4.81:1 on the live pill
          successBg: '#d1fae5', // emerald-100
          successText: '#047857', // emerald-700
          warning: '#a35905', // 4.73:1 on warningBg, 5.27:1 on a white card
          warningBg: '#fef3c7', // amber-100
          warningText: '#92400e', // amber-800; amber-700 sat at 4.52:1 on the ground above
          // Two steps rather than one: at red-600 this sat 2.5 dE from the amber above under
          // simulated deuteranopia, so warning text and error text were the same colour. Red-800
          // holds 17.2 there and 31.6 under protanopia, and reads better on both grounds as well.
          error: '#991b1b', // 6.80:1 on errorBg, 8.31:1 on a white card
          errorBg: '#fee2e2', // red-100
          errorText: '#b91c1c', // red-700
          info: '#1658ea', // 4.76:1 on infoBg, 5.80:1 on a white card
          infoBg: '#dbeafe', // blue-100
          infoText: '#1d4ed8', // blue-700
          waiting: '#9333ea', // purple-600
          waitingBg: '#f3e8ff', // purple-100
          waitingText: '#7e22ce', // purple-700

          // Service colors - the same brand hues the schema defaults carry, darkened where the
          // brand's own value is too pale to read against a white card
          steamColor: '#417a9b', // Valve's accent blue a step down; the light one reads 2.02:1 here
          steamFaint: 'rgba(65, 122, 155, 0.1)',
          steamOnBorder: 'rgba(65, 122, 155, 0.5)',
          steamStrong: 'rgba(65, 122, 155, 0.3)',
          epicColor: '#7c3aed',
          epicFaint: 'rgba(124, 58, 237, 0.1)',
          epicOnBorder: 'rgba(124, 58, 237, 0.5)',
          epicStrong: 'rgba(124, 58, 237, 0.3)',
          originColor: '#ff4747',
          blizzardColor: '#5d6bdc',
          wsusColor: '#1b6fb5', // Off the accent, which it used to match exactly; 29.7 dE from it and 23.0 from Steam
          riotColor: '#d13639',
          xboxColor: '#107C10', // Xbox Green
          ubisoftColor: '#4338ca', // Same violet-blue as the default, darkened for white-card legibility
          gogColor: '#8B3FA0', // Darkened for white-card legibility
          rockstarColor: '#B07D07', // Darkened for white-card legibility
          arenanetColor: '#5C7A4A',
          bsgColor: '#6E7B3A',
          cityofheroesColor: '#2A9CC9', // Darkened for white-card legibility
          codColor: '#C2410C',
          daybreakColor: '#DC5457', // Darkened for white-card legibility
          frontierColor: '#A9761F', // Darkened for white-card legibility
          neverwinterColor: '#5B3A75',
          nexusmodsColor: '#B45309', // Darkened for white-card legibility
          nintendoColor: '#E4000F',
          pathofexileColor: '#B8860B',
          renegadexColor: '#6B7A8C',
          sonyColor: '#003791',
          squareColor: '#6B1210',
          tesoColor: '#5C1F35',
          testColor: '#71717A',
          warframeColor: '#0E9AA0', // Darkened for white-card legibility
          wargamingColor: '#5C5347',

          // Components - White cards with soft borders; shadows give the depth
          cardBg: '#ffffff',
          cardBorder: '#dde3ec', // Subtle card edge; shadow does the separation
          cardOutline: '#2563eb',
          buttonBg: '#2563eb',
          buttonHover: '#1d4ed8',
          buttonText: '#ffffff',
          inputBg: '#ffffff',
          inputBorder: '#cbd5e1', // slate-300 - interactive edges stay visible
          inputFocus: '#2563eb',
          checkboxAccent: '#2563eb',
          checkboxBorder: '#94a3b8', // slate-400
          checkboxBg: '#ffffff',
          checkboxCheckmark: '#ffffff',
          checkboxShadow: '0 1px 2px rgba(0, 0, 0, 0.05)',
          checkboxHoverShadow: '0 0 0 3px rgba(37, 99, 235, 0.12)',
          checkboxHoverBg: '#eef1f6',
          checkboxFocus: '#2563eb',
          sliderAccent: '#2563eb',
          sliderThumb: '#2563eb',
          sliderTrack: '#cbd5e1', // slate-300 - visible track on white cards
          progressBg: '#cbd5e1', // slate-300 - visible track on white cards

          // Hit rate specific - -100 shades so badges read on grey cards
          hitRateHighBg: '#d1fae5',
          hitRateHighText: '#047857',
          hitRateMediumBg: '#dbeafe',
          hitRateMediumText: '#1d4ed8',
          hitRateLowBg: '#fef3c7',
          hitRateLowText: '#b45309',
          hitRateWarningBg: '#fee2e2',
          hitRateWarningText: '#b91c1c',

          // Action buttons - the same two steps down the amber and emerald ramps the shared
          // defaults take, for the same reason: white on amber-600 is 3.19:1 and on emerald-600
          // 3.77:1. Red and blue already carried the label at this depth and stay where they were.
          actionResetBg: '#b45309',
          actionResetHover: '#92400e',
          actionProcessBg: '#047857',
          actionProcessHover: '#065f46',
          actionDeleteBg: '#991b1b',
          actionDeleteHover: '#7f1d1d',

          // Icon backgrounds - the five hues that could not be read as glyphs on a white card
          // (2.94:1 for yellow, 3.56:1 orange, 3.68:1 cyan, 3.74:1 teal, 3.77:1 green) step down
          // their own ramps. Blue, purple, indigo and red already cleared and stay put.
          iconBgBlue: '#2563eb',
          iconBgGreen: '#04845c',
          iconBgEmerald: '#04845c',
          iconBgPurple: '#7c3aed',
          iconBgIndigo: '#4f46e5',
          iconBgOrange: '#c74b0a',
          iconBgYellow: '#9a6903',
          iconBgCyan: '#077e9b',
          iconBgTeal: '#0b8176',
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

          // Game chart colors - 600-shade equivalents of the dark defaults,
          // which are 400/500 shades that wash out on light surfaces
          gameColor1: '#2563eb',
          gameColor2: '#059669',
          gameColor3: '#d97706',
          gameColor4: '#dc2626',
          gameColor5: '#7c3aed',
          gameColor6: '#0891b2',
          gameColor7: '#ea580c',
          gameColor8: '#db2777',
          gameColor9: '#0d9488',
          gameColor10: '#9333ea',
          gameColor11: '#ca8a04',
          gameColor12: '#4f46e5',
          gameColor13: '#65a30d',
          gameColor14: '#be123c',
          gameColor15: '#0284c7',
          gameColor16: '#c026d3',
          gameColor17: '#16a34a',
          gameColor18: '#e11d48',
          gameColor19: '#0e7490',
          gameColor20: '#7e22ce',
          gameColorOther: '#64748b',

          chartBorderColor: '#ffffff', // Matches card bg for segment separation
          chartGridColor: '#d8e0ea', // Visible but quiet gridlines on white
          chartTextColor: '#475569', // Darker for readability
          chartCacheHitColor: '#059669',
          chartCacheMissColor: '#d97706',

          // Scrollbar colors
          scrollbarTrack: '#e2e8f0',
          scrollbarThumb: '#94a3b8',
          scrollbarHover: '#64748b',

          // Access indicator colors
          publicAccessBg: '#d1fae5',
          publicAccessText: '#047857',
          publicAccessBorder: '#3d8f72', // 3.45:1 on the wash; the old #6ee7b7 glowed on white
          securedAccessBg: '#fef3c7',
          securedAccessText: '#b45309',
          securedAccessBorder: '#a68520', // 3.14:1 on the wash; the old #fcd34d was neon gold

          // Session colors
          userSessionColor: '#2563eb',
          userSessionBg: '#dbeafe',
          guestSessionColor: '#0891b2',
          guestSessionBg: '#cffafe',
          activeSessionColor: '#ea580c',
          activeSessionBg: '#ffedd5',

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
          fireworkColor8: '#1e40af', // Deep blue (white is invisible on light backgrounds)
          fireworkRocketColor: '#2563eb', // Blue (uses primaryColor)
          fireworkGlowColor: '#3b82f6' // Blue glow
        })
      },
      // Neutral charcoal dark theme, the counterpart to the blue-slate default:
      // red and green are held equal and blue three points lower, so the ramp reads
      // as flat warm grey instead of as a second blue theme.
      {
        meta: {
          id: 'graphite',
          name: 'Graphite',
          description: i18n.t('management.themes.builtIn.graphite.description'),
          author: i18n.t('management.themes.builtIn.systemAuthor'),
          version: '1.0.2',
          isDark: true,
          sharpCorners: false,
          disableFocusOutlines: true,
          disableTooltips: false
        },
        colors: complete({
          // Core colors - Softer blue, legible against a warm grey ground.
          // Secondary and accent are set here rather than inherited: the shared defaults are the
          // dark slate theme's own violet and cyan, so without these the two dark themes showed an
          // identical pair in the theme slider's swatch strip. Both sit at the same hue offsets from
          // the primary that light-default uses (+43 and -28) and carry the primary's lightness and
          // colorfulness, so the three read as one family.
          primaryColor: '#5b9df5',
          secondaryColor: '#a48be4',
          accentColor: '#34a5b2',

          // Backgrounds - Charcoal ramp, page darkest and controls lightest
          // Key: the recessed well sits BETWEEN page and card, because wells render
          // both nested inside cards and directly on the bare page canvas.
          bgPrimary: '#181815', // Page canvas
          bgSecondary: '#32322f', // List rows, dropdown panels
          bgTertiary: '#1f1f1c', // Recessed well - must stay darker than cardBg or wells invert
          bgHover: '#4e4e4b', // Row hover
          bgElevated: '#282825', // Drawer/modal/floating panel
          bgSurface: '#3a3a37', // Control fill - kept well clear of bgTertiary so controls pop out of the well
          bgSurfaceHover: '#464643', // Hover on surface elements
          bgSurfaceActive: '#565653', // Active/pressed surface elements

          // Text - Warm off-white; pure white glares against charcoal
          textPrimary: '#eaeae4',
          textSecondary: '#acaca6',
          textMuted: '#9e9e98', // Also feeds --theme-icon-gray; bgSecondary is the lightest ground it sits on and the old #94948e read 4.22:1 there
          textAccent: '#7db3f7', // Links - one step lighter than the accent so they read as links
          textPlaceholder: '#74746e',

          // Drag handle
          dragHandleColor: '#74746e',
          dragHandleHover: '#5b9df5',

          // Borders - Neutral hairlines; the ramp step does most of the separating
          borderPrimary: '#3c3c39',
          borderSecondary: '#4a4a47',
          borderElevated: '#454542',
          borderHover: '#5a5a57',

          // Navigation - Bar sits above the page but below the cards
          navBg: '#232320',
          navBorder: '#3c3c39',
          navTabActive: '#5b9df5',
          navTabInactive: '#94948e',
          navTabActiveBorder: '#5b9df5',
          navTabHover: '#eaeae4', // Tracks textPrimary; the shared default here is pure white
          navMobileMenuBg: '#232320',
          navMobileItemHover: '#3c3c39',

          // Components - Cards a step above the page, controls a step above the cards
          cardBg: '#262623',
          cardBorder: '#4a4a47',
          buttonBg: '#246bdd', // Deeper than primaryColor - white button text is only 2.77:1 on the accent itself, and 4.36:1 one step down
          buttonHover: '#1f5cc4',
          inputBg: '#3c3c39',
          inputBorder: '#4a4a47',
          checkboxAccent: '#5b9df5',
          checkboxBorder: '#4a4a47',
          checkboxBg: '#232320',
          checkboxHoverBg: '#3c3c39',
          sliderAccent: '#5b9df5',
          sliderThumb: '#5b9df5',
          sliderTrack: '#3c3c39',
          progressBg: '#3c3c39',

          // Icon backgrounds - Lifted from the dark defaults so every tile clears 4.5:1 on the card
          iconBgBlue: '#5b9df5',
          iconBgGreen: '#69b585',
          iconBgEmerald: '#69b599',
          iconBgPurple: '#b18cf7',
          iconBgIndigo: '#8c96f9',
          iconBgOrange: '#df9f68',
          iconBgYellow: '#c7af60',
          iconBgCyan: '#66aab7',
          iconBgTeal: '#5fb4ab',
          iconBgRed: '#f5767c',

          // Status green and red - held at the icon values above so one green and one red run
          // through icons, badges and text alike. The shared defaults are the slate theme's,
          // which read 5.98:1 (green) and 4.03:1 (red) on this theme's card.
          // Both are measured against bgSecondary, the lighter of this theme's two card grounds and
          // the one the management panels actually sit on; cardBg is a step darker and easier.
          // Sage, not mint: #4ade80 cleared 7.38:1 here but its chroma vibrated against the warm
          // charcoal, especially as Status Check ink and as the solid ribbon fill.
          success: '#69b585', // 5.24:1 on bgSecondary, 6.18:1 on cardBg
          successBg: '#24382c', // Warm olive well; the slate default #053f30 is a cool hole on charcoal
          successText: '#69b585', // 5.10:1 on that well
          hitRateHighBg: '#24382c',
          hitRateHighText: '#69b585',
          publicAccessText: '#69b585',
          publicAccessBg: 'rgba(105, 181, 133, 0.2)',
          publicAccessBorder: 'rgba(105, 181, 133, 0.3)',
          error: '#f5767c', // 4.76:1 on bgSecondary; the old #f4636a was 4.19:1 there

          // Status and series blues - the shared defaults for these keys are the deep blue
          // slate theme's accent, so without an override the page would show two blues at once
          info: '#5b9df5',
          // Deeper than the card by more than a step: the status glyph paints itself in `info` on
          // this ground rather than in infoText, and at the old #1a3f70 that pairing was 3.81:1.
          infoBg: '#143157',
          infoText: '#a9cdf9', // 7.32:1 on that ground
          hitRateMediumBg: '#1a3f70',
          hitRateMediumText: '#a9cdf9',

          // Warning grounds - the shared default #44403c is a near-neutral warm grey, which reads
          // as a distinct chip on the slate theme's blue card but nearly merges into this theme's
          // own warm charcoal card: only lightness separates them, so CIE76 dE falls from 17.5 to
          // 12.4. Carrying the amber into the ground instead of lifting it restores the gap
          // (dE 18.2 on #262623). Ink is the same muted amber as dark-default so the ribbon
          // and warning text do not vibrate on charcoal.
          warning: '#df9f68', // 5.68:1 on bgSecondary, 4.61:1 on warningBg
          warningText: '#d3b769', // 6.58:1 on bgSecondary, 5.33:1 on warningBg
          hitRateLowText: '#d3b769',
          hitRateWarningText: '#d3b769',
          warningBg: '#4d3d29',
          hitRateLowBg: '#4d3d29',
          hitRateWarningBg: '#4d3d29',
          chartCacheHitColor: '#69b585',
          errorText: '#d89797',
          checkboxHoverShadow: '0 0 0 3px rgba(91, 157, 245, 0.1)',
          userSessionColor: '#5b9df5',
          userSessionBg: 'rgba(91, 157, 245, 0.15)',
          chartColor1: '#5b9df5',
          gameColor1: '#5b9df5',
          eventColor1: '#5b9df5',
          fireworkColor1: '#3576e0', // Rocket and first burst use the deeper accent, glow the lighter one
          fireworkColor2: '#5b9df5',
          fireworkRocketColor: '#3576e0',
          fireworkGlowColor: '#5b9df5',

          // Chart chrome
          chartBorderColor: '#262623', // Matches card bg for segment separation
          chartGridColor: '#41413e', // Visible but quiet gridlines on charcoal
          chartTextColor: '#94948e', // Tracks textMuted, as the dark default does

          // Service colors - the real brand hue for each service, lightened where the
          // brand's own value cannot be read against charcoal, or where two services
          // share a hue and need a visible step between them. Steam, Epic, EA, Battle.net and
          // Ubisoft are not restated here: the schema defaults now carry those brand hues and
          // this theme wants the same values.
          wsusColor: '#09a2be', // Same cyan a step down, so Steam's blue and Warframe's teal both clear it on lightness
          riotColor: '#d13639',
          xboxColor: '#1a8c1a', // Xbox's #107c10 lifted six points of lightness at the same hue; the brand value itself reads 2.83:1 here
          gogColor: '#a05fb4', // GOG's true violet is far too dark to read on charcoal
          rockstarColor: '#fcaf17',
          arenanetColor: '#6fa754', // Same green lifted 14 L*, then one more step so Xbox's green clears it
          bsgColor: '#6e7b3a',
          cityofheroesColor: '#6bbd91', // Same green family as success; chroma pulled down with it
          codColor: '#dd6f3a', // Ember orange lifted off 2.93:1, then shifted red of Nexus Mods' brand orange
          daybreakColor: '#fb8d90', // Same salmon lightened until it separates from EA's red on lightness
          frontierColor: '#ddcbb0', // Same sand, saturation cut so it stops glowing on charcoal
          neverwinterColor: '#b98ee0', // Same violet, lightened from 1.67:1 and clear of GOG's violet
          nexusmodsColor: '#f97316',
          nintendoColor: '#e4000f',
          pathofexileColor: '#9c7000', // Darker goldenrod, stepped down clear of Nexus Mods' orange and Wargaming's olive
          renegadexColor: '#4e7b82', // Slate turned cool; at its old blue-grey hue it tracked PlayStation's blue
          sonyColor: '#3d8bd9', // PlayStation navy lightened from 1.41:1
          squareColor: '#c84455', // Deep red, lightened from 1.24:1 and moved off Riot's brand red
          tesoColor: '#b95685', // Same wine, lightened from 1.23:1 and turned pink of Square Enix's red
          testColor: '#a1a1aa', // One step lighter on the same grey ramp, clear of renegadexColor
          warframeColor: '#54b6b6',
          wargamingColor: '#9d9a66' // Olive lifted from 2.01:1 and shifted clear of Path of Exile's gold
        })
      }
    ];
    return this._builtInThemesCache;
  }

  async getTheme(themeId: string): Promise<Theme | null> {
    const builtIn = this.getBuiltInThemes().find((t) => t.meta.id === themeId);
    if (builtIn) return builtIn;

    const response = await fetch(`${API_BASE}/themes/${themeId}`);
    // 404 is an explicit, documented "theme does not exist" result - a legitimate null, not an
    // error. Any other failure is surfaced via the shared ApiError instead of being masked as "no
    // theme" (which previously hid 5xx/network faults behind the same null).
    if (response.status === 404) return null;
    await assertOk(response);

    const tomlText = await response.text();
    return this.parseTomlTheme(tomlText);
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
   * Uses hexToRgba for opacity tiers - simple "base color + transparency".
   * Blend variables use explicit defaults - theme creators override directly.
   */
  private generateComputedColorVars(colors: Record<string, string | undefined>): string {
    const rgba = (hex: string, opacity: number): string => schemaHexToRgba(hex, opacity);

    // Resolve base colors - guaranteed present by schema
    const primary = colors.primaryColor!;
    const accent = colors.accentColor!;
    const success = colors.success!;
    const warning = colors.warning!;
    const error = colors.error!;
    const info = colors.info!;
    const waiting = colors.waiting!;
    const steam = colors.steamColor!;
    const epic = colors.epicColor!;
    const blizzard = colors.blizzardColor!;
    const riot = colors.riotColor!;
    const xbox = colors.xboxColor!;
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
    const iconTeal = colors.iconBgTeal!;
    const iconRed = colors.iconBgRed!;
    const iconGray = colors.textMuted!;
    const chartColor1 = colors.chartColor1!;
    const chartCacheHit = colors.chartCacheHitColor!;
    const chartCacheMiss = colors.chartCacheMissColor!;
    // Event colors - guaranteed present by schema
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

    // Status glow tone: the light a status colour casts, not a surface it paints. One alpha for
    // every status - the -strong ladder cannot be reused here because its alpha varies by status
    // (success 0.4, the rest 0.3), which makes a green segment outshine a red one on the same
    // condensed strip. Tuned against the 2.5px line: the blur halves it, so the brightest visible
    // pixel lands near 0.22.
    const glowAlpha = 0.45;

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

    // Every service with a dedicated base color gets a -subtle and -muted alpha tier
    // (steam/epic/blizzard/riot/xbox additionally keep their hand-tuned faint/on-border/strong below).
    // A service with no entry here has no --theme-<svc> color, so callers fall back to the
    // muted text color rather than emitting a variable that resolves to nothing.
    const platformColors: Record<string, string> = {
      steam,
      epic,
      origin: colors.originColor!,
      blizzard,
      wsus: colors.wsusColor!,
      riot,
      xbox,
      ubisoft: colors.ubisoftColor!,
      gog: colors.gogColor!,
      rockstar: colors.rockstarColor!,
      arenanet: colors.arenanetColor!,
      bsg: colors.bsgColor!,
      cityofheroes: colors.cityofheroesColor!,
      cod: colors.codColor!,
      daybreak: colors.daybreakColor!,
      frontier: colors.frontierColor!,
      neverwinter: colors.neverwinterColor!,
      nexusmods: colors.nexusmodsColor!,
      nintendo: colors.nintendoColor!,
      pathofexile: colors.pathofexileColor!,
      renegadex: colors.renegadexColor!,
      sony: colors.sonyColor!,
      square: colors.squareColor!,
      teso: colors.tesoColor!,
      test: colors.testColor!,
      warframe: colors.warframeColor!,
      wargaming: colors.wargamingColor!
    };
    // epic is the only service that previously had a hand-tuned -muted opacity; preserve it
    const platformMutedOpacity: Record<string, number> = { epic: 0.25 };
    const platformVars = Object.entries(platformColors)
      .map(([key, hex]) => {
        const mutedOpacity = platformMutedOpacity[key] ?? 0.2;
        return `
      --theme-${key}-subtle: ${v(`${key}Subtle`, rgba(hex, 0.15))};
      --theme-${key}-muted: ${v(`${key}Muted`, rgba(hex, mutedOpacity))};`;
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
      --theme-success-glow: ${v('successGlow', rgba(success, glowAlpha))};

      /* Warning */
      --theme-warning-faint: ${v('warningFaint', rgba(warning, 0.08))};
      --theme-warning-subtle: ${v('warningSubtle', rgba(warning, 0.15))};
      --theme-warning-muted: ${v('warningMuted', rgba(warning, 0.2))};
      --theme-warning-strong: ${v('warningStrong', rgba(warning, 0.3))};
      --theme-warning-glow: ${v('warningGlow', rgba(warning, glowAlpha))};

      /* Error */
      --theme-error-faint: ${v('errorFaint', rgba(error, 0.1))};
      --theme-error-subtle: ${v('errorSubtle', rgba(error, 0.15))};
      --theme-error-muted: ${v('errorMuted', rgba(error, 0.2))};
      --theme-error-strong: ${v('errorStrong', rgba(error, 0.3))};
      --theme-error-glow: ${v('errorGlow', rgba(error, glowAlpha))};

      /* Info */
      --theme-info-subtle: ${v('infoSubtle', rgba(info, 0.15))};
      --theme-info-muted: ${v('infoMuted', rgba(info, 0.2))};
      --theme-info-glow: ${v('infoGlow', rgba(info, glowAlpha))};

      /* Waiting (purple notification-visibility tone) */
      --theme-waiting-muted: ${v('waitingMuted', rgba(waiting, 0.2))};
      --theme-waiting-glow: ${v('waitingGlow', rgba(waiting, glowAlpha))};

      /* Accent */
      --theme-accent-faint: ${v('accentFaint', rgba(accent, 0.06))};
      --theme-accent-subtle: ${v('accentSubtle', rgba(accent, 0.15))};
      --theme-accent-muted: ${v('accentMuted', rgba(accent, 0.2))};

      /* Platform */
      --theme-steam-faint: ${colors.steamFaint};
      --theme-steam-on-border: ${colors.steamOnBorder};
      --theme-steam-strong: ${colors.steamStrong};
      --theme-epic-faint: ${colors.epicFaint};
      --theme-epic-on-border: ${colors.epicOnBorder};
      --theme-epic-strong: ${colors.epicStrong};
      --theme-blizzard-faint: ${colors.blizzardFaint};
      --theme-blizzard-on-border: ${colors.blizzardOnBorder};
      --theme-blizzard-strong: ${colors.blizzardStrong};
      --theme-riot-faint: ${v('riotFaint', rgba(riot, 0.1))};
      --theme-riot-on-border: ${v('riotOnBorder', rgba(riot, 0.5))};
      --theme-riot-strong: ${v('riotStrong', rgba(riot, 0.3))};
      --theme-xbox-faint: ${v('xboxFaint', rgba(xbox, 0.1))};
      --theme-xbox-on-border: ${v('xboxOnBorder', rgba(xbox, 0.5))};
      --theme-xbox-strong: ${v('xboxStrong', rgba(xbox, 0.3))};
      ${platformVars}

      /* Icon Backgrounds */
      --theme-icon-blue-subtle: ${v('iconBlueSubtle', rgba(iconBlue, 0.15))};
      --theme-icon-green-subtle: ${v('iconGreenSubtle', rgba(iconGreen, 0.15))};
      --theme-icon-emerald-subtle: ${v('iconEmeraldSubtle', rgba(iconEmerald, 0.15))};
      --theme-icon-purple-subtle: ${v('iconPurpleSubtle', rgba(iconPurple, 0.15))};
      --theme-icon-indigo-subtle: ${v('iconIndigoSubtle', rgba(iconIndigo, 0.15))};
      --theme-icon-orange-subtle: ${v('iconOrangeSubtle', rgba(iconOrange, 0.15))};
      --theme-icon-yellow-subtle: ${v('iconYellowSubtle', rgba(iconYellow, 0.15))};
      --theme-icon-cyan-subtle: ${v('iconCyanSubtle', rgba(iconCyan, 0.15))};
      --theme-icon-teal-subtle: ${v('iconTealSubtle', rgba(iconTeal, 0.15))};
      --theme-icon-red-subtle: ${v('iconRedSubtle', rgba(iconRed, 0.15))};
      --theme-icon-gray-subtle: ${v('iconGraySubtle', rgba(iconGray, 0.15))};
      --theme-icon-blue-muted: ${v('iconBlueMuted', rgba(iconBlue, 0.2))};
      --theme-icon-green-muted: ${v('iconGreenMuted', rgba(iconGreen, 0.2))};
      --theme-icon-emerald-muted: ${v('iconEmeraldMuted', rgba(iconEmerald, 0.2))};
      --theme-icon-purple-muted: ${v('iconPurpleMuted', rgba(iconPurple, 0.2))};
      --theme-icon-indigo-muted: ${v('iconIndigoMuted', rgba(iconIndigo, 0.2))};
      --theme-icon-orange-muted: ${v('iconOrangeMuted', rgba(iconOrange, 0.2))};
      --theme-icon-yellow-muted: ${v('iconYellowMuted', rgba(iconYellow, 0.2))};
      --theme-icon-cyan-muted: ${v('iconCyanMuted', rgba(iconCyan, 0.2))};
      --theme-icon-teal-muted: ${v('iconTealMuted', rgba(iconTeal, 0.2))};
      --theme-icon-red-muted: ${v('iconRedMuted', rgba(iconRed, 0.2))};
      --theme-icon-gray-muted: ${v('iconGrayMuted', rgba(iconGray, 0.2))};
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
      --theme-skeleton-base: ${v('skeletonBase', rgba(textMuted, 0.08))};
      --theme-badge-white-subtle: ${v('badgeWhiteSubtle', 'rgba(255, 255, 255, 0.20)')};
      --theme-glint-white: ${v('glintWhite', 'rgba(255, 255, 255, 0.05)')};

      /* Blend Variables - use rgba opacity (adapts to any theme's colors) */
      --theme-primary-on-bg: ${v('primaryOnBg', rgba(primary, 0.12))};
      --theme-primary-on-bg-hover: ${v('primaryOnBgHover', rgba(primary, 0.18))};
      --theme-primary-on-border: ${v('primaryOnBorder', rgba(primary, 0.25))};

      /* Derived radius for inset corners (1px inside the outer radius). */
      --theme-border-radius-inner: calc(var(--theme-border-radius) - 1px);
      /* Shared elevation for floating dropdown/menu surfaces. */
      --shadow-dropdown: 0 10px 25px -5px rgba(0, 0, 0, 0.3), 0 8px 10px -6px rgba(0, 0, 0, 0.2);
      /* Shared elevation for resting and hovered card surfaces, and for the inset
         well on range-input tracks. Values are verbatim from the rules that carried
         them inline, so adopting them is not a visual change. */
      --shadow-card: 0 1px 3px rgba(0, 0, 0, 0.1), 0 1px 2px rgba(0, 0, 0, 0.06);
      --shadow-card-hover: 0 4px 6px rgba(0, 0, 0, 0.1), 0 2px 4px rgba(0, 0, 0, 0.06);
      --shadow-control-inset: inset 0 1px 2px rgba(0, 0, 0, 0.1);
      /* Range-slider thumb elevation. Each value is written twice in the stylesheet
         because the webkit and moz thumb pseudo-elements cannot share a selector, so
         a token is the only way to keep the two halves from drifting. */
      --shadow-range-thumb: 0 2px 4px rgba(0, 0, 0, 0.2), 0 1px 2px rgba(0, 0, 0, 0.1);
      --shadow-range-thumb-hover: 0 3px 6px rgba(0, 0, 0, 0.3), 0 2px 4px rgba(0, 0, 0, 0.15);
      --shadow-range-thumb-focus: 0 0 0 4px rgba(var(--theme-primary-rgb), 0.1), 0 3px 6px rgba(0, 0, 0, 0.3);

      /* Selected-state surface - the one token pair every selected row/pill/option
         uses, so the highlight weight is tuned here instead of per component. The
         text token stays the normal readable text color on purpose: primary-hued
         text on a primary-tinted fill collapses into the fill in themes whose
         primary runs light. */
      --theme-selected-bg: ${v('selectedBg', rgba(primary, 0.25))};
      --theme-selected-bg-hover: ${v('selectedBgHover', rgba(primary, 0.4))};
      --theme-selected-text: ${v('selectedText', 'var(--theme-text-primary)')};
      --theme-success-on-bg: ${v('successOnBg', rgba(success, 0.08))};
      --theme-success-on-border: ${v('successOnBorder', rgba(success, 0.3))};
      --theme-warning-on-error: ${v('warningOnError', rgba(warning, 0.8))};
      --theme-bg-secondary-on-tertiary: ${v('bgSecondaryOnTertiary', rgba(bgSecondary, 0.8))};
      --theme-danger-gradient-start: ${v('dangerGradientStart', 'rgba(255, 107, 107, 0.10)')};
      --theme-danger-gradient-end: ${v('dangerGradientEnd', 'rgba(238, 90, 90, 0.10)')};
      --theme-danger-border: ${v('dangerBorder', 'rgba(255, 107, 107, 0.30)')};
      --theme-chart-hit-highlight: ${v('chartHitHighlight', rgba(chartCacheHit, 0.8))};
      --theme-chart-miss-deep: ${v('chartMissDeep', rgba(error, 0.8))};
      /* The area wash under a cache-hit or cache-miss line. Both are declared together at the
         same 0.15 alpha every other -subtle tier uses, so a line that moves onto one of these
         colours cannot end up with a fill from a different family. */
      --theme-chart-cache-hit-subtle: ${v('chartCacheHitSubtle', rgba(chartCacheHit, 0.15))};
      --theme-chart-cache-miss-subtle: ${v('chartCacheMissSubtle', rgba(chartCacheMiss, 0.15))};
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

  applyTheme(theme: Theme, options?: ApplyThemeOptions): void {
    if (!theme || !theme.colors) return;

    const persist = options?.persist ?? true;

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
    // The control corner: buttons, inputs, dropdown triggers and menus, and inner wells. 6px
    // rather than the 8px this used to be, because 8px on a 24px-tall xs button is most of the
    // way to a pill. Containers stay on the -lg step below, which keeps cards at twice the
    // control radius, the ratio shadcn and most systems use.
    const borderRadius = sharpCorners ? '0px' : '0.375rem';
    const borderRadiusSm = sharpCorners ? '0px' : '0.2rem';
    const borderRadiusLg = sharpCorners ? '0px' : '0.75rem';

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
      --theme-bg-elevated: ${colors.bgElevated};
      --theme-bg-surface: ${colors.bgSurface};
      --theme-bg-surface-hover: ${colors.bgSurfaceHover};
      --theme-bg-surface-active: ${colors.bgSurfaceActive};
      --theme-bg-overlay: ${colors.bgOverlay};

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
      --theme-border-well: ${colors.borderWell || colors.borderSecondary};
      --theme-border-focus: ${colors.borderFocus};
      --theme-border-elevated: ${colors.borderElevated};
      --theme-border-hover: ${colors.borderHover};
      --theme-border-radius: ${borderRadius};
      --theme-border-radius-sm: ${borderRadiusSm};
      --theme-border-radius-lg: ${borderRadiusLg};

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
      --theme-waiting: ${colors.waiting};
      --theme-waiting-bg: ${colors.waitingBg};
      --theme-waiting-text: ${colors.waitingText};
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
      --theme-gog: ${colors.gogColor};
      --theme-rockstar: ${colors.rockstarColor};
      --theme-arenanet: ${colors.arenanetColor};
      --theme-bsg: ${colors.bsgColor};
      --theme-cityofheroes: ${colors.cityofheroesColor};
      --theme-cod: ${colors.codColor};
      --theme-daybreak: ${colors.daybreakColor};
      --theme-frontier: ${colors.frontierColor};
      --theme-neverwinter: ${colors.neverwinterColor};
      --theme-nexusmods: ${colors.nexusmodsColor};
      --theme-nintendo: ${colors.nintendoColor};
      --theme-pathofexile: ${colors.pathofexileColor};
      --theme-renegadex: ${colors.renegadexColor};
      --theme-sony: ${colors.sonyColor};
      --theme-square: ${colors.squareColor};
      --theme-teso: ${colors.tesoColor};
      --theme-test: ${colors.testColor};
      --theme-warframe: ${colors.warframeColor};
      --theme-wargaming: ${colors.wargamingColor};

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
      --theme-action-stop-bg: ${colors.actionStopBg};
      --theme-action-stop-hover: ${colors.actionStopHover};
      
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
      --theme-icon-teal: ${colors.iconBgTeal};
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

      /* Game Chart Colors */
      --theme-game-1: ${colors.gameColor1};
      --theme-game-2: ${colors.gameColor2};
      --theme-game-3: ${colors.gameColor3};
      --theme-game-4: ${colors.gameColor4};
      --theme-game-5: ${colors.gameColor5};
      --theme-game-6: ${colors.gameColor6};
      --theme-game-7: ${colors.gameColor7};
      --theme-game-8: ${colors.gameColor8};
      --theme-game-9: ${colors.gameColor9};
      --theme-game-10: ${colors.gameColor10};
      --theme-game-11: ${colors.gameColor11};
      --theme-game-12: ${colors.gameColor12};
      --theme-game-13: ${colors.gameColor13};
      --theme-game-14: ${colors.gameColor14};
      --theme-game-15: ${colors.gameColor15};
      --theme-game-16: ${colors.gameColor16};
      --theme-game-17: ${colors.gameColor17};
      --theme-game-18: ${colors.gameColor18};
      --theme-game-19: ${colors.gameColor19};
      --theme-game-20: ${colors.gameColor20};
      --theme-game-other: ${colors.gameColorOther};

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

    // The html element paints the forced scrollbar gutter (overflow-y: scroll) and the overscroll area.
    // Its background is an inline style written once by the preload script in index.html and nothing
    // else rewrites it, so a theme change has to, or the previous page color stays for the session.
    // Written as the property rather than the resolved hex so that a theme whose own CSS redefines
    // the property keeps the gutter in step with the page instead of freezing one boot's value
    if (colors.bgPrimary) {
      root.style.backgroundColor = 'var(--theme-bg-primary)';
    }

    // What is painted, which is not the same thing as what is saved. Every reader of this field
    // asks "is this theme in force right now" - the preference listener uses it to decide whether
    // a requested theme still needs applying, and the corner/outline toggles use it to repaint the
    // theme already on screen - so it follows the paint even during a scrub. It lives in memory
    // and dies with the page, so it cannot survive a reload and cannot cause the bug below.
    this.currentTheme = theme;

    // Everything above is what the page looks like right now; everything below is what it will
    // look like after the next reload. A non-persisting apply keeps the first and skips the
    // second.
    if (persist) {
      // Save theme preferences for all users (authenticated and guests)
      // Save the theme ID and CSS for instant loading on next page load (localStorage for caching)
      storage.setItem('lancache_selected_theme', theme.meta.id);
      storage.setItem('lancache_theme_css', themeStyles);
      storage.setItem('lancache_theme_dark', theme.meta.isDark ? 'true' : 'false');
    }

    // Force re-render. Always: a scrub still has to reach every live consumer, and the event
    // carries no id, so nothing downstream can tell a scrub from a commit.
    window.dispatchEvent(new Event(APP_EVENTS.THEME_CHANGE));
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
    window.dispatchEvent(new Event(APP_EVENTS.TOOLTIPS_CHANGE));
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
    window.dispatchEvent(new Event(APP_EVENTS.NOTIFICATION_VISIBILITY_CHANGE));
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
    window.dispatchEvent(new Event(APP_EVENTS.STICKY_NOTIFICATIONS_CHANGE));
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
  async reloadThemeAfterAuth(hasSession: boolean): Promise<void> {
    // Reload preferences from API and reinitialize local state
    const prefs = await preferencesService.loadPreferences();
    this.initializePreferences(prefs);

    // A theme preview survives the page reload the Preview button triggers -
    // it must win over the real saved preference until the preview is toggled off.
    const previewThemeId = this.getPreviewTheme();
    if (previewThemeId) {
      const previewTheme = await this.getTheme(previewThemeId);
      if (previewTheme) {
        this.applyTheme(previewTheme);
        return;
      }
    }

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
        if (hasSession) {
          // Save to API for future use
          await preferencesService.setPreference('selectedTheme', localThemeId);
        }
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
    window.dispatchEvent(new Event(APP_EVENTS.THEME_PREVIEW_CHANGE));
  }

  getPreviewTheme(): string | null {
    return storage.getItem('lancache_preview_theme');
  }

  clearPreviewTheme(): void {
    storage.removeItem('lancache_preview_theme');
    window.dispatchEvent(new Event(APP_EVENTS.THEME_PREVIEW_CHANGE));
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

  async stopPreview(): Promise<void> {
    const originalTheme = this.getOriginalThemeBeforePreview() || 'dark-default';
    await this.setTheme(originalTheme);
    this.clearPreviewTheme();
    this.clearOriginalThemeBeforePreview();
  }
}

export default new ThemeService();
