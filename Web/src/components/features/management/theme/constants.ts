import {
  Palette,
  Type,
  Layout,
  Component,
  Square,
  AlertCircle,
  Activity,
  Lock,
  Gamepad2,
  Brush,
  Sparkles,
  Home,
  Download,
  Users,
  Server,
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
  { name: 'services', icon: Server },
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
          'services',
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
        pages: []
      },
      {
        key: 'accentColor',
        supportsAlpha: true,
        pages: ['prefill']
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
          'services',
          'users',
          'events',
          'prefill',
          'management'
        ]
      },
      {
        key: 'textSecondary',
        supportsAlpha: true,
        pages: [
          'dashboard',
          'downloads',
          'clients',
          'services',
          'users',
          'events',
          'prefill',
          'management'
        ]
      },
      {
        key: 'textMuted',
        supportsAlpha: true,
        pages: [
          'dashboard',
          'downloads',
          'clients',
          'services',
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
        pages: []
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
        pages: [
          'dashboard',
          'downloads',
          'clients',
          'services',
          'users',
          'events',
          'prefill',
          'management'
        ]
      },
      {
        key: 'bgSecondary',
        supportsAlpha: true,
        pages: [
          'dashboard',
          'downloads',
          'clients',
          'services',
          'users',
          'events',
          'prefill',
          'management'
        ]
      },
      {
        key: 'bgTertiary',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'users', 'events', 'prefill', 'management']
      },
      {
        key: 'bgHover',
        supportsAlpha: true,
        pages: [
          'dashboard',
          'downloads',
          'clients',
          'services',
          'users',
          'events',
          'prefill',
          'management',
          'charts'
        ]
      },
      {
        key: 'cardBg',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'events']
      },
      {
        key: 'cardBorder',
        supportsAlpha: true,
        pages: ['dashboard', 'events']
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
        pages: []
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
          'services',
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
          'services',
          'users',
          'events',
          'prefill',
          'management'
        ]
      },
      {
        key: 'borderFocus',
        supportsAlpha: true,
        pages: []
      }
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
        pages: ['downloads', 'clients', 'services', 'users', 'prefill']
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
        pages: ['clients', 'services', 'users', 'prefill', 'management']
      },
      {
        key: 'error',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'users', 'prefill', 'management']
      },
      {
        key: 'errorBg',
        supportsAlpha: true,
        pages: ['downloads', 'users', 'prefill', 'management']
      },
      {
        key: 'errorText',
        supportsAlpha: true,
        pages: ['downloads', 'users', 'prefill', 'management']
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
        pages: ['downloads', 'users', 'events', 'prefill', 'management']
      }
    ]
  },

  // 7. NAVIGATION
  {
    name: 'navigation',
    icon: Layout,
    colors: [
      { key: 'navBg', supportsAlpha: true },
      { key: 'navBorder', supportsAlpha: true },
      { key: 'navTabActive', supportsAlpha: true },
      { key: 'navTabInactive', supportsAlpha: true },
      { key: 'navTabHover', supportsAlpha: true },
      { key: 'navTabActiveBorder', supportsAlpha: true },
      { key: 'navMobileMenuBg', supportsAlpha: true },
      { key: 'navMobileItemHover', supportsAlpha: true },
      {
        key: 'floatingIconColor',
        supportsAlpha: true,
        pages: [
          'dashboard',
          'downloads',
          'clients',
          'services',
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
        pages: ['dashboard', 'downloads', 'clients', 'services', 'prefill']
      },
      { key: 'chartColor1', supportsAlpha: true, pages: ['charts'] },
      { key: 'chartColor2', supportsAlpha: true, pages: ['charts'] },
      { key: 'chartColor3', supportsAlpha: true, pages: ['charts'] },
      { key: 'chartColor4', supportsAlpha: true, pages: ['charts'] },
      { key: 'chartColor5', supportsAlpha: true, pages: ['charts'] },
      { key: 'chartColor6', supportsAlpha: true, pages: ['charts'] },
      { key: 'chartColor7', supportsAlpha: true, pages: ['charts'] },
      { key: 'chartColor8', supportsAlpha: true, pages: ['charts'] },
      {
        key: 'chartCacheHitColor',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'services', 'charts']
      },
      {
        key: 'chartCacheMissColor',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'services', 'charts']
      },
      { key: 'chartGridColor', supportsAlpha: true, pages: ['charts'] },
      { key: 'chartTextColor', supportsAlpha: true, pages: ['charts'] }
    ]
  },

  // 9. ACCESS CONTROL - Security indicators
  {
    name: 'accessControl',
    icon: Lock,
    colors: [
      { key: 'publicAccessBg', supportsAlpha: true },
      { key: 'publicAccessText', supportsAlpha: true },
      { key: 'publicAccessBorder', supportsAlpha: true },
      { key: 'securedAccessBg', supportsAlpha: true },
      { key: 'securedAccessText', supportsAlpha: true },
      { key: 'securedAccessBorder', supportsAlpha: true }
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
      { key: 'guestSessionBg', supportsAlpha: true, pages: ['users'] }
    ]
  },

  // 11. EVENT COLORS - Calendar event colors
  {
    name: 'events',
    icon: CalendarDays,
    colors: [
      { key: 'eventColor1', supportsAlpha: true, pages: ['events'] },
      { key: 'eventColor2', supportsAlpha: true, pages: ['events'] },
      { key: 'eventColor3', supportsAlpha: true, pages: ['events'] },
      { key: 'eventColor4', supportsAlpha: true, pages: ['events'] },
      { key: 'eventColor5', supportsAlpha: true, pages: ['events'] },
      { key: 'eventColor6', supportsAlpha: true, pages: ['events'] },
      { key: 'eventColor7', supportsAlpha: true, pages: ['events'] },
      { key: 'eventColor8', supportsAlpha: true, pages: ['events'] }
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
        pages: ['dashboard', 'downloads', 'clients', 'services', 'prefill', 'charts']
      },
      { key: 'steamFaint', supportsAlpha: true, pages: ['prefill'] },
      { key: 'steamOnBorder', supportsAlpha: true, pages: ['prefill'] },
      { key: 'steamStrong', supportsAlpha: true, pages: ['prefill'] },
      {
        key: 'epicColor',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'services', 'charts']
      },
      { key: 'epicFaint', supportsAlpha: true, pages: ['prefill'] },
      { key: 'epicOnBorder', supportsAlpha: true, pages: ['prefill'] },
      { key: 'epicStrong', supportsAlpha: true, pages: ['prefill'] },
      {
        key: 'originColor',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'services', 'charts']
      },
      {
        key: 'blizzardColor',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'services', 'charts']
      },
      {
        key: 'wsusColor',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'services', 'charts']
      },
      {
        key: 'riotColor',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'services', 'charts']
      },
      {
        key: 'xboxColor',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'services', 'charts']
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
        pages: ['dashboard', 'downloads', 'clients', 'services']
      },
      {
        key: 'hitRateHighText',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'services']
      },
      {
        key: 'hitRateMediumBg',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'services']
      },
      {
        key: 'hitRateMediumText',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'services']
      },
      {
        key: 'hitRateLowBg',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'services']
      },
      {
        key: 'hitRateLowText',
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'services']
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
      { key: 'iconBgRed', supportsAlpha: true, pages: ['dashboard', 'events'] }
    ]
  }
];
