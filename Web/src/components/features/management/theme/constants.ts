import {
  Palette,
  Type,
  Layout,
  Component,
  Square,
  AlertCircle,
  Activity,
  Gamepad2,
  Brush,
  Sparkles,
  Home,
  Download,
  Users,
  Key,
  Settings,
  BarChart3,
  Layers,
  CalendarDays,
  Terminal
} from 'lucide-react';
import { type ColorGroup, type PageGroup } from './types';

export const pageDefinitions: PageGroup[] = [
  { name: 'all', icon: Layers },
  { name: 'dashboard', icon: Home },
  { name: 'downloads', icon: Download },
  { name: 'clients', icon: Users },
  { name: 'authenticate', icon: Key },
  { name: 'users', icon: Users },
  { name: 'events', icon: CalendarDays },
  { name: 'prefill', icon: Terminal },
  { name: 'management', icon: Settings },
  { name: 'charts', icon: BarChart3 }
];

export const colorGroups: ColorGroup[] = [
  // 1. FOUNDATION - Core brand colors
  {
    name: 'foundation',
    icon: Palette,
    colors: [
      {
        key: 'primaryColor',
        supportsAlpha: true,
        pages: [
          'dashboard',
          'downloads',
          'clients',
          'users',
          'events',
          'prefill',
          'management',
          'charts'
        ]
      },
      {
        key: 'secondaryColor',
        supportsAlpha: true,
        pages: [
          'dashboard',
          'downloads',
          'clients',
          'users',
          'events',
          'prefill',
          'management',
          'charts'
        ]
      },
      {
        key: 'accentColor',
        supportsAlpha: true,
        pages: ['authenticate', 'users', 'prefill', 'management']
      }
    ]
  },

  // 2. CONTENT - Text and typography
  {
    name: 'content',
    icon: Type,
    colors: [
      {
        key: 'textPrimary',
        supportsAlpha: true,
        pages: [
          'dashboard',
          'downloads',
          'clients',
          'authenticate',
          'users',
          'events',
          'prefill',
          'management',
          'charts'
        ]
      },
      {
        key: 'textSecondary',
        supportsAlpha: true,
        pages: [
          'dashboard',
          'downloads',
          'clients',
          'authenticate',
          'users',
          'events',
          'prefill',
          'management',
          'charts'
        ]
      },
      {
        key: 'textMuted',
        supportsAlpha: true,
        pages: [
          'dashboard',
          'downloads',
          'clients',
          'authenticate',
          'users',
          'events',
          'prefill',
          'management',
          'charts'
        ]
      },
      {
        key: 'textAccent',
        supportsAlpha: true,
        pages: ['dashboard', 'management']
      },
      {
        key: 'textPlaceholder',
        supportsAlpha: true,
        pages: []
      }
    ]
  },

  // 3. LAYOUT - Surfaces and containers
  {
    name: 'layout',
    icon: Layout,
    colors: [
      {
        key: 'bgPrimary',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'users', 'events', 'prefill', 'management']
      },
      {
        key: 'bgSecondary',
        supportsAlpha: true,
        pages: [
          'dashboard',
          'downloads',
          'clients',
          'authenticate',
          'users',
          'events',
          'prefill',
          'management'
        ]
      },
      {
        key: 'bgTertiary',
        supportsAlpha: true,
        pages: [
          'dashboard',
          'downloads',
          'authenticate',
          'users',
          'events',
          'prefill',
          'management',
          'charts'
        ]
      },
      {
        key: 'bgHover',
        supportsAlpha: true,
        pages: [
          'dashboard',
          'downloads',
          'clients',
          'users',
          'events',
          'prefill',
          'management',
          'charts'
        ]
      },
      {
        key: 'bgElevated',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'users', 'events', 'prefill', 'management']
      },
      {
        key: 'bgSurface',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'users', 'events', 'prefill', 'management']
      },
      {
        key: 'bgSurfaceHover',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'users', 'events', 'prefill', 'management']
      },
      {
        key: 'bgSurfaceActive',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'users', 'events', 'prefill', 'management']
      },
      {
        key: 'bgOverlay',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'users', 'events', 'prefill', 'management']
      },
      {
        key: 'cardBg',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'events', 'prefill', 'management', 'charts']
      },
      {
        key: 'cardBorder',
        supportsAlpha: true,
        pages: ['dashboard', 'users', 'events', 'prefill', 'charts']
      },
      {
        key: 'cardOutline',
        supportsAlpha: true,
        pages: ['downloads']
      }
    ]
  },

  // 4. INTERACTIVE - Form elements and controls
  {
    name: 'interactive',
    icon: Component,
    colors: [
      {
        key: 'buttonBg',
        supportsAlpha: true,
        pages: []
      },
      {
        key: 'buttonHover',
        supportsAlpha: true,
        pages: ['downloads', 'events']
      },
      {
        key: 'buttonText',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'users', 'events', 'prefill', 'management']
      },
      {
        key: 'inputBg',
        supportsAlpha: true,
        pages: []
      },
      {
        key: 'inputBorder',
        supportsAlpha: true,
        pages: []
      },
      {
        key: 'inputFocus',
        supportsAlpha: true,
        pages: []
      },
      {
        key: 'selectedBg',
        supportsAlpha: true,
        pages: [
          'dashboard',
          'downloads',
          'clients',
          'users',
          'events',
          'prefill',
          'management',
          'charts'
        ]
      },
      {
        key: 'selectedBgHover',
        supportsAlpha: true,
        pages: [
          'dashboard',
          'downloads',
          'clients',
          'users',
          'events',
          'prefill',
          'management',
          'charts'
        ]
      },
      {
        key: 'selectedText',
        supportsAlpha: true,
        pages: [
          'dashboard',
          'downloads',
          'clients',
          'users',
          'events',
          'prefill',
          'management',
          'charts'
        ]
      },
      {
        key: 'checkboxAccent',
        supportsAlpha: false, // Browser overrides alpha for accessibility
        pages: ['downloads', 'management']
      },
      {
        key: 'checkboxBorder',
        supportsAlpha: true // Custom styling, supports alpha
      },
      {
        key: 'checkboxBg',
        supportsAlpha: true,
        pages: ['downloads', 'management']
      },
      {
        key: 'checkboxCheckmark',
        supportsAlpha: false,
        pages: ['downloads', 'management']
      },
      {
        key: 'checkboxShadow',
        supportsAlpha: true,
        pages: ['downloads', 'management']
      },
      {
        key: 'checkboxHoverShadow',
        supportsAlpha: true,
        pages: ['downloads', 'management']
      },
      {
        key: 'checkboxHoverBg',
        supportsAlpha: true,
        pages: ['downloads', 'management']
      },
      {
        key: 'checkboxFocus',
        supportsAlpha: true,
        pages: ['downloads', 'management']
      },
      {
        key: 'sliderAccent',
        supportsAlpha: false, // Browser overrides alpha for accessibility
        pages: ['management']
      },
      {
        key: 'sliderThumb',
        supportsAlpha: true
      },
      {
        key: 'sliderTrack',
        supportsAlpha: true
      },
      {
        key: 'dragHandleColor',
        supportsAlpha: true,
        pages: ['dashboard']
      },
      {
        key: 'dragHandleHover',
        supportsAlpha: true,
        pages: ['dashboard']
      }
    ]
  },

  // 5. BORDERS & DIVIDERS
  {
    name: 'borders',
    icon: Square,
    colors: [
      {
        key: 'borderPrimary',
        supportsAlpha: true,
        pages: [
          'dashboard',
          'downloads',
          'clients',
          'authenticate',
          'users',
          'events',
          'prefill',
          'management',
          'charts'
        ]
      },
      {
        key: 'borderSecondary',
        supportsAlpha: true,
        pages: [
          'dashboard',
          'downloads',
          'clients',
          'users',
          'events',
          'prefill',
          'management',
          'charts'
        ]
      },
      {
        key: 'borderFocus',
        supportsAlpha: true,
        pages: ['dashboard', 'prefill', 'management']
      },
      {
        key: 'borderElevated',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'users', 'events', 'prefill', 'management']
      },
      {
        key: 'borderHover',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'users', 'events', 'prefill', 'management']
      },
      { key: 'borderWell', supportsAlpha: true, pages: ['downloads', 'management'] }
    ]
  },

  // 6. FEEDBACK - Status and alerts
  {
    name: 'feedback',
    icon: AlertCircle,
    colors: [
      {
        key: 'success',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'users', 'events', 'prefill', 'management']
      },
      {
        key: 'successBg',
        supportsAlpha: true,
        pages: ['downloads', 'users', 'prefill', 'management']
      },
      {
        key: 'successText',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'users', 'prefill', 'management']
      },
      {
        key: 'warning',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'users', 'prefill', 'management']
      },
      {
        key: 'warningBg',
        supportsAlpha: true,
        pages: ['users', 'prefill', 'management']
      },
      {
        key: 'warningText',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'users', 'prefill', 'management']
      },
      {
        key: 'error',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'users', 'events', 'prefill', 'management']
      },
      {
        key: 'errorBg',
        supportsAlpha: true,
        pages: ['downloads', 'users', 'prefill', 'management']
      },
      {
        key: 'errorText',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'users', 'prefill', 'management']
      },
      {
        key: 'info',
        supportsAlpha: true,
        pages: ['dashboard', 'users', 'events', 'prefill', 'management']
      },
      {
        key: 'infoBg',
        supportsAlpha: true,
        pages: ['users', 'events', 'prefill', 'management']
      },
      {
        key: 'infoText',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'users', 'events', 'prefill', 'management']
      },
      {
        key: 'waiting',
        supportsAlpha: true,
        pages: ['dashboard', 'users', 'events', 'prefill', 'management']
      },
      {
        key: 'waitingBg',
        supportsAlpha: true,
        pages: ['users', 'events', 'prefill', 'management']
      },
      {
        key: 'waitingText',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'users', 'events', 'prefill', 'management']
      },
      { key: 'publicAccessBg', supportsAlpha: true, pages: ['users', 'management'] },
      { key: 'publicAccessText', supportsAlpha: true, pages: ['users', 'management'] },
      { key: 'publicAccessBorder', supportsAlpha: true, pages: ['users', 'management'] },
      { key: 'securedAccessBg', supportsAlpha: true, pages: ['users', 'management'] },
      { key: 'securedAccessText', supportsAlpha: true, pages: ['users', 'management'] },
      { key: 'securedAccessBorder', supportsAlpha: true, pages: ['users', 'management'] }
    ]
  },

  // 7. NAVIGATION
  {
    name: 'navigation',
    icon: Layout,
    colors: [
      { key: 'navBg', supportsAlpha: true },
      { key: 'navBorder', supportsAlpha: true },
      { key: 'navTabActive', supportsAlpha: true, pages: ['management'] },
      { key: 'navTabInactive', supportsAlpha: true, pages: ['management'] },
      { key: 'navTabHover', supportsAlpha: true, pages: ['management'] },
      { key: 'navTabActiveBorder', supportsAlpha: true },
      { key: 'navMobileMenuBg', supportsAlpha: true },
      { key: 'navMobileItemHover', supportsAlpha: true },
      {
        key: 'floatingIconColor',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'users', 'events', 'prefill', 'management']
      },
      {
        key: 'rocketFlameColor',
        supportsAlpha: true,
        pages: [
          'dashboard',
          'downloads',
          'clients',
          'authenticate',
          'users',
          'events',
          'prefill',
          'management'
        ]
      },
      {
        key: 'rocketFlameCoreColor',
        supportsAlpha: true,
        pages: [
          'dashboard',
          'downloads',
          'clients',
          'authenticate',
          'users',
          'events',
          'prefill',
          'management'
        ]
      }
    ]
  },

  // 8. DATA DISPLAY - Progress, badges, charts
  {
    name: 'dataDisplay',
    icon: Activity,
    colors: [
      {
        key: 'progressBg',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'prefill']
      },
      { key: 'chartColor1', supportsAlpha: true, pages: ['dashboard', 'charts'] },
      { key: 'chartColor2', supportsAlpha: true, pages: ['dashboard', 'charts'] },
      { key: 'chartColor3', supportsAlpha: true, pages: ['dashboard', 'charts'] },
      { key: 'chartColor4', supportsAlpha: true, pages: ['dashboard', 'charts'] },
      { key: 'chartColor5', supportsAlpha: true, pages: ['dashboard', 'charts'] },
      { key: 'chartColor6', supportsAlpha: true, pages: ['dashboard', 'charts'] },
      { key: 'chartColor7', supportsAlpha: true, pages: ['dashboard', 'charts'] },
      { key: 'chartColor8', supportsAlpha: true, pages: ['dashboard', 'charts'] },
      {
        key: 'chartCacheHitColor',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'charts']
      },
      {
        key: 'chartCacheMissColor',
        supportsAlpha: true,
        pages: ['dashboard', 'charts']
      },
      { key: 'chartGridColor', supportsAlpha: true, pages: ['dashboard', 'charts'] },
      { key: 'chartTextColor', supportsAlpha: true, pages: ['dashboard', 'charts'] },
      { key: 'chartBorderColor', supportsAlpha: true, pages: ['dashboard', 'charts'] }
    ]
  },

  // 9. GAME CHARTS - Game distribution doughnut (20 slots + other)
  {
    name: 'gameCharts',
    icon: Gamepad2,
    colors: [
      { key: 'gameColor1', supportsAlpha: true, pages: ['dashboard', 'charts'] },
      { key: 'gameColor2', supportsAlpha: true, pages: ['dashboard', 'charts'] },
      { key: 'gameColor3', supportsAlpha: true, pages: ['dashboard', 'charts'] },
      { key: 'gameColor4', supportsAlpha: true, pages: ['dashboard', 'charts'] },
      { key: 'gameColor5', supportsAlpha: true, pages: ['dashboard', 'charts'] },
      { key: 'gameColor6', supportsAlpha: true, pages: ['dashboard', 'charts'] },
      { key: 'gameColor7', supportsAlpha: true, pages: ['dashboard', 'charts'] },
      { key: 'gameColor8', supportsAlpha: true, pages: ['dashboard', 'charts'] },
      { key: 'gameColor9', supportsAlpha: true, pages: ['dashboard', 'charts'] },
      { key: 'gameColor10', supportsAlpha: true, pages: ['dashboard', 'charts'] },
      { key: 'gameColor11', supportsAlpha: true, pages: ['dashboard', 'charts'] },
      { key: 'gameColor12', supportsAlpha: true, pages: ['dashboard', 'charts'] },
      { key: 'gameColor13', supportsAlpha: true, pages: ['dashboard', 'charts'] },
      { key: 'gameColor14', supportsAlpha: true, pages: ['dashboard', 'charts'] },
      { key: 'gameColor15', supportsAlpha: true, pages: ['dashboard', 'charts'] },
      { key: 'gameColor16', supportsAlpha: true, pages: ['dashboard', 'charts'] },
      { key: 'gameColor17', supportsAlpha: true, pages: ['dashboard', 'charts'] },
      { key: 'gameColor18', supportsAlpha: true, pages: ['dashboard', 'charts'] },
      { key: 'gameColor19', supportsAlpha: true, pages: ['dashboard', 'charts'] },
      { key: 'gameColor20', supportsAlpha: true, pages: ['dashboard', 'charts'] },
      { key: 'gameColorOther', supportsAlpha: true, pages: ['dashboard', 'charts'] }
    ]
  },

  // 10. USER SESSIONS - Session management colors
  {
    name: 'sessions',
    icon: Users,
    colors: [
      { key: 'userSessionColor', supportsAlpha: true, pages: ['users'] },
      { key: 'userSessionBg', supportsAlpha: true, pages: ['users'] },
      { key: 'guestSessionColor', supportsAlpha: true, pages: ['users'] },
      { key: 'guestSessionBg', supportsAlpha: true, pages: ['users'] },
      { key: 'activeSessionColor', supportsAlpha: true, pages: ['users'] },
      { key: 'activeSessionBg', supportsAlpha: true, pages: ['users'] }
    ]
  },

  // 11. EVENT COLORS - Calendar event colors
  {
    name: 'events',
    icon: CalendarDays,
    colors: [
      { key: 'eventColor1', supportsAlpha: true, pages: ['dashboard', 'events'] },
      { key: 'eventColor2', supportsAlpha: true, pages: ['dashboard', 'events'] },
      { key: 'eventColor3', supportsAlpha: true, pages: ['dashboard', 'events'] },
      { key: 'eventColor4', supportsAlpha: true, pages: ['dashboard', 'events'] },
      { key: 'eventColor5', supportsAlpha: true, pages: ['dashboard', 'events'] },
      { key: 'eventColor6', supportsAlpha: true, pages: ['dashboard', 'events'] },
      { key: 'eventColor7', supportsAlpha: true, pages: ['dashboard', 'events'] },
      { key: 'eventColor8', supportsAlpha: true, pages: ['dashboard', 'events'] }
    ]
  },

  // 12. PLATFORM SERVICES - Brand-specific colors
  {
    name: 'platforms',
    icon: Gamepad2,
    colors: [
      {
        key: 'steamColor',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'prefill', 'management', 'charts']
      },
      { key: 'steamFaint', supportsAlpha: true, pages: ['prefill', 'management'] },
      { key: 'steamOnBorder', supportsAlpha: true, pages: ['prefill'] },
      { key: 'steamStrong', supportsAlpha: true, pages: ['prefill'] },
      {
        key: 'epicColor',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'prefill', 'management', 'charts']
      },
      { key: 'epicFaint', supportsAlpha: true, pages: ['prefill', 'management'] },
      { key: 'epicOnBorder', supportsAlpha: true, pages: ['prefill'] },
      { key: 'epicStrong', supportsAlpha: true, pages: ['prefill'] },
      {
        key: 'originColor',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'charts']
      },
      {
        key: 'blizzardColor',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'prefill', 'management', 'charts']
      },
      {
        key: 'wsusColor',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'charts']
      },
      {
        key: 'riotColor',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'prefill', 'management', 'charts']
      },
      {
        key: 'xboxColor',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'prefill', 'management', 'charts']
      },
      {
        key: 'ubisoftColor',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'charts']
      },
      {
        key: 'gogColor',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'charts']
      },
      {
        key: 'rockstarColor',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'charts']
      },
      {
        key: 'arenanetColor',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'charts']
      },
      {
        key: 'bsgColor',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'charts']
      },
      {
        key: 'cityofheroesColor',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'charts']
      },
      {
        key: 'codColor',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'charts']
      },
      {
        key: 'daybreakColor',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'charts']
      },
      {
        key: 'frontierColor',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'charts']
      },
      {
        key: 'neverwinterColor',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'charts']
      },
      {
        key: 'nexusmodsColor',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'charts']
      },
      {
        key: 'nintendoColor',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'charts']
      },
      {
        key: 'pathofexileColor',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'charts']
      },
      {
        key: 'renegadexColor',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'charts']
      },
      {
        key: 'sonyColor',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'charts']
      },
      {
        key: 'squareColor',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'charts']
      },
      {
        key: 'tesoColor',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'charts']
      },
      {
        key: 'testColor',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'charts']
      },
      {
        key: 'warframeColor',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'charts']
      },
      {
        key: 'wargamingColor',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'charts']
      }
    ]
  },

  // 13. PERFORMANCE INDICATORS - Hit rate colors
  {
    name: 'performance',
    icon: Activity,
    colors: [
      {
        key: 'hitRateHighBg',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients']
      },
      {
        key: 'hitRateHighText',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients']
      },
      {
        key: 'hitRateMediumBg',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients']
      },
      {
        key: 'hitRateMediumText',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients']
      },
      {
        key: 'hitRateLowBg',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients']
      },
      {
        key: 'hitRateLowText',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients']
      },
      {
        key: 'hitRateWarningBg',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads']
      },
      {
        key: 'hitRateWarningText',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads']
      }
    ]
  },

  // 14. ACTION BUTTONS - Specialized action colors
  {
    name: 'actions',
    icon: Sparkles,
    colors: [
      { key: 'actionResetBg', supportsAlpha: true, pages: ['management'] },
      { key: 'actionResetHover', supportsAlpha: true, pages: ['management'] },
      { key: 'actionProcessBg', supportsAlpha: true, pages: ['management'] },
      { key: 'actionProcessHover', supportsAlpha: true, pages: ['management'] },
      { key: 'actionStopBg', supportsAlpha: true, pages: ['management'] },
      { key: 'actionStopHover', supportsAlpha: true, pages: ['management'] },
      { key: 'actionDeleteBg', supportsAlpha: true, pages: ['management'] },
      { key: 'actionDeleteHover', supportsAlpha: true, pages: ['management'] }
    ]
  },

  // 15. UTILITIES - Misc UI elements
  {
    name: 'utilities',
    icon: Brush,
    colors: [
      { key: 'scrollbarTrack', supportsAlpha: true },
      { key: 'scrollbarThumb', supportsAlpha: true },
      { key: 'scrollbarHover', supportsAlpha: true },
      {
        key: 'iconBgBlue',
        supportsAlpha: true,
        pages: ['dashboard', 'events', 'management']
      },
      {
        key: 'iconBgGreen',
        supportsAlpha: true,
        pages: ['dashboard', 'events', 'management']
      },
      { key: 'iconBgEmerald', supportsAlpha: true, pages: ['dashboard'] },
      {
        key: 'iconBgPurple',
        supportsAlpha: true,
        pages: ['dashboard', 'events', 'management']
      },
      { key: 'iconBgIndigo', supportsAlpha: true, pages: ['dashboard'] },
      {
        key: 'iconBgOrange',
        supportsAlpha: true,
        pages: ['dashboard', 'events', 'management']
      },
      {
        key: 'iconBgYellow',
        supportsAlpha: true,
        pages: ['dashboard', 'management']
      },
      {
        key: 'iconBgCyan',
        supportsAlpha: true,
        pages: ['dashboard', 'events', 'management']
      },
      { key: 'iconBgTeal', supportsAlpha: true, pages: ['dashboard', 'events', 'management'] },
      { key: 'iconBgRed', supportsAlpha: true, pages: ['dashboard', 'events', 'management'] },
      { key: 'iconBlueMuted', supportsAlpha: true, pages: ['management'] },
      { key: 'iconGreenMuted', supportsAlpha: true, pages: ['management'] },
      { key: 'iconEmeraldMuted', supportsAlpha: true, pages: ['management'] },
      { key: 'iconPurpleMuted', supportsAlpha: true, pages: ['management'] },
      { key: 'iconIndigoMuted', supportsAlpha: true, pages: ['management'] },
      { key: 'iconOrangeMuted', supportsAlpha: true, pages: ['management'] },
      { key: 'iconYellowMuted', supportsAlpha: true, pages: ['management'] },
      { key: 'iconCyanMuted', supportsAlpha: true, pages: ['management'] },
      { key: 'iconTealMuted', supportsAlpha: true, pages: ['management'] },
      { key: 'iconRedMuted', supportsAlpha: true, pages: ['prefill'] },
      { key: 'iconGrayMuted', supportsAlpha: true, pages: ['management'] }
    ]
  }
];
