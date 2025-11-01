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
  Layers
} from 'lucide-react';
import { ColorGroup, PageGroup } from './types';

export const pageDefinitions: PageGroup[] = [
  {
    name: 'all',
    label: 'All Pages',
    icon: Layers,
    description: 'View all colors - Shows every color used throughout the application'
  },
  {
    name: 'dashboard',
    label: 'Dashboard',
    icon: Home,
    description: '8 stat cards (Total Cache, Used Space, Bandwidth Saved, Added to Cache, Total Served, Active Downloads, Active Clients, Cache Hit Ratio), service chart, recent downloads panel, top clients table, and drag-to-reorder handles'
  },
  {
    name: 'downloads',
    label: 'Downloads',
    icon: Download,
    description: 'Download cards with game banners, service/client filters (dropdowns), checkboxes (show metadata, hide localhost, etc.), pagination controls, cache hit/miss badges, view mode toggles (compact/normal), and export buttons'
  },
  {
    name: 'clients',
    label: 'Clients',
    icon: Users,
    description: 'Client statistics table showing IP addresses, download counts, bandwidth usage, cache hit/miss bytes, hit rate progress bars, and last activity timestamps'
  },
  {
    name: 'services',
    label: 'Services',
    icon: Server,
    description: 'Service statistics table with platform-colored badges (Steam, Epic, Origin, Blizzard, WSUS, Riot), download counts, bandwidth totals, cache hit/miss bytes, and hit rate progress bars'
  },
  {
    name: 'users',
    label: 'Users',
    icon: Users,
    description: 'Session management for authenticated users and guests - USER/GUEST badges, user icons, IP addresses, device info, last seen timestamps, revoke/delete actions, and session statistics'
  },
  {
    name: 'management',
    label: 'Management',
    icon: Settings,
    description: 'Authentication (API keys), Steam integration, database reset/clear buttons, cache management, log processing, corruption detection, game cache detector, theme customization (modals, color pickers, checkboxes, sliders), GC settings, alert banners, and Grafana endpoints'
  },
  {
    name: 'charts',
    label: 'Charts & Graphs',
    icon: BarChart3,
    description: 'Enhanced service charts with 8 data series colors, cache hit/miss colors, grid lines, axis labels, legends, and performance trend visualizations'
  }
];

export const colorGroups: ColorGroup[] = [
  // 1. FOUNDATION - Core brand colors
  {
    name: 'foundation',
    icon: Palette,
    description: 'Primary, secondary, and accent colors - The core colors that define your theme\'s visual identity',
    colors: [
      {
        key: 'primaryColor',
        label: 'Primary Brand Color',
        description: 'Main brand color used throughout',
        affects: ['Primary buttons', 'Links', 'Active states', 'Focus rings'],
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'management', 'charts']
      },
      {
        key: 'secondaryColor',
        label: 'Secondary Brand Color',
        description: 'Complementary brand accent',
        affects: ['Secondary buttons', 'Highlights', 'Accents'],
        supportsAlpha: true,
        pages: []
      },
      {
        key: 'accentColor',
        label: 'Accent Color',
        description: 'Tertiary accent for special elements',
        affects: ['Special badges', 'Tooltips', 'Info elements'],
        supportsAlpha: true,
        pages: []
      }
    ]
  },

  // 2. CONTENT - Text and typography
  {
    name: 'content',
    icon: Type,
    description: 'Headings, body text, labels, and descriptions - Controls all text colors throughout the app',
    colors: [
      {
        key: 'textPrimary',
        label: 'Primary Text',
        description: 'Main content text color',
        affects: ['Headings', 'Body text', 'Labels'],
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'services', 'management']
      },
      {
        key: 'textSecondary',
        label: 'Secondary Text',
        description: 'Supporting content text',
        affects: ['Descriptions', 'Subtitles', 'Help text'],
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'services', 'management']
      },
      {
        key: 'textMuted',
        label: 'Muted Text',
        description: 'De-emphasized text',
        affects: ['Disabled text', 'Timestamps', 'Minor labels'],
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'services', 'management', 'charts']
      },
      {
        key: 'textAccent',
        label: 'Accent Text',
        description: 'Highlighted or linked text',
        affects: ['Links', 'Highlighted values', 'Active menu items'],
        supportsAlpha: true,
        pages: []
      },
      {
        key: 'textPlaceholder',
        label: 'Placeholder Text',
        description: 'Input placeholder text color',
        affects: ['Form placeholders', 'Search hints', 'Empty states'],
        supportsAlpha: true,
        pages: []
      }
    ]
  },

  // 3. LAYOUT - Surfaces and containers
  {
    name: 'layout',
    icon: Layout,
    description: 'Backgrounds, cards, panels, and surfaces - Controls layering and depth in your theme',
    colors: [
      {
        key: 'bgPrimary',
        label: 'Base Background',
        description: 'Main application background',
        affects: ['Body', 'Main container', 'Base layer'],
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'services']
      },
      {
        key: 'bgSecondary',
        label: 'Surface Background',
        description: 'Elevated surface backgrounds',
        affects: ['Cards', 'Panels', 'Modals', 'Dialogs'],
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'services']
      },
      {
        key: 'bgTertiary',
        label: 'Recessed Background',
        description: 'Sunken or nested elements',
        affects: ['Input fields', 'Wells', 'Code blocks'],
        supportsAlpha: true,
        pages: ['downloads', 'management']
      },
      {
        key: 'bgHover',
        label: 'Hover State',
        description: 'Interactive hover backgrounds',
        affects: ['Button hovers', 'List hovers', 'Menu hovers'],
        supportsAlpha: true,
        pages: ['dashboard', 'clients', 'services', 'management', 'charts']
      },
      {
        key: 'cardBg',
        label: 'Card Background',
        description: 'Card component background',
        affects: ['Stat cards', 'Content cards', 'Widget backgrounds'],
        supportsAlpha: true,
        pages: ['dashboard']
      },
      {
        key: 'cardBorder',
        label: 'Card Border',
        description: 'Card component borders',
        affects: ['Card outlines', 'Panel borders'],
        supportsAlpha: true,
        pages: ['dashboard']
      },
      {
        key: 'cardOutline',
        label: 'Card Focus Outline',
        description: 'Outline color when cards are clicked/focused',
        affects: ['Download cards focus outline', 'Interactive card selection'],
        supportsAlpha: true,
        pages: ['downloads']
      }
    ]
  },

  // 4. INTERACTIVE - Form elements and controls
  {
    name: 'interactive',
    icon: Component,
    description: 'Buttons, inputs, checkboxes, and sliders - All interactive controls and form elements',
    colors: [
      {
        key: 'buttonBg',
        label: 'Button Background',
        description: 'Primary button fill color',
        affects: ['Primary buttons', 'Submit buttons', 'CTAs'],
        supportsAlpha: true,
        pages: []
      },
      {
        key: 'buttonHover',
        label: 'Button Hover',
        description: 'Button hover state color',
        affects: ['Button hover effects', 'Active button states'],
        supportsAlpha: true,
        pages: []
      },
      {
        key: 'buttonText',
        label: 'Button Text',
        description: 'Button label color',
        affects: ['Button labels', 'Button icons'],
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'management']
      },
      {
        key: 'inputBg',
        label: 'Input Background',
        description: 'Form input background',
        affects: ['Text inputs', 'Textareas', 'Select boxes'],
        supportsAlpha: true,
        pages: []
      },
      {
        key: 'inputBorder',
        label: 'Input Border',
        description: 'Form input border',
        affects: ['Input outlines', 'Field borders'],
        supportsAlpha: true,
        pages: []
      },
      {
        key: 'inputFocus',
        label: 'Input Focus',
        description: 'Focused input indicator',
        affects: ['Active input borders', 'Focus rings'],
        supportsAlpha: true,
        pages: []
      },
      {
        key: 'checkboxAccent',
        label: 'Checkbox Accent',
        description: 'Checkbox checked state color',
        affects: ['Checkbox checkmarks', 'Checkbox backgrounds'],
        supportsAlpha: false,  // Browser overrides alpha for accessibility
        pages: ['downloads', 'management']
      },
      {
        key: 'checkboxBorder',
        label: 'Checkbox Border',
        description: 'Checkbox border color when unchecked',
        affects: ['Checkbox borders', 'Unchecked state'],
        supportsAlpha: true  // Custom styling, supports alpha
      },
      {
        key: 'checkboxBg',
        label: 'Checkbox Background',
        description: 'Background color of unchecked checkboxes',
        affects: ['Checkbox background when unchecked'],
        supportsAlpha: true,
        pages: ['downloads', 'management']
      },
      {
        key: 'checkboxCheckmark',
        label: 'Checkbox Checkmark',
        description: 'Color of the checkmark symbol',
        affects: ['Checkbox checkmark color'],
        supportsAlpha: false,
        pages: ['downloads', 'management']
      },
      {
        key: 'checkboxShadow',
        label: 'Checkbox Shadow',
        description: 'Shadow effect for checkboxes',
        affects: ['Checkbox shadow'],
        supportsAlpha: true,
        pages: ['downloads', 'management']
      },
      {
        key: 'checkboxHoverShadow',
        label: 'Checkbox Hover Shadow',
        description: 'Shadow effect when hovering checkboxes',
        affects: ['Checkbox hover shadow'],
        supportsAlpha: true,
        pages: ['downloads', 'management']
      },
      {
        key: 'checkboxHoverBg',
        label: 'Checkbox Hover Background',
        description: 'Background color when hovering unchecked checkboxes',
        affects: ['Checkbox hover background'],
        supportsAlpha: true,
        pages: ['downloads', 'management']
      },
      {
        key: 'checkboxFocus',
        label: 'Checkbox Focus',
        description: 'Outline color when checkbox is focused',
        affects: ['Checkbox focus outline'],
        supportsAlpha: true,
        pages: ['downloads', 'management']
      },
      {
        key: 'sliderAccent',
        label: 'Slider Accent',
        description: 'Range slider accent color',
        affects: ['Slider thumbs', 'Slider filled tracks'],
        supportsAlpha: false,  // Browser overrides alpha for accessibility
        pages: ['management']
      },
      {
        key: 'sliderThumb',
        label: 'Slider Thumb',
        description: 'Range slider thumb/handle',
        affects: ['Slider drag handles'],
        supportsAlpha: true
      },
      {
        key: 'sliderTrack',
        label: 'Slider Track',
        description: 'Range slider track background',
        affects: ['Slider backgrounds', 'Progress tracks'],
        supportsAlpha: true
      },
      {
        key: 'dragHandleColor',
        label: 'Drag Handle',
        description: 'Drag and reorder controls',
        affects: ['Drag grips', 'Reorder handles', 'Move indicators'],
        supportsAlpha: true,
        pages: ['dashboard']
      },
      {
        key: 'dragHandleHover',
        label: 'Drag Handle Hover',
        description: 'Active drag handle state',
        affects: ['Drag grip hover', 'Active dragging'],
        supportsAlpha: true,
        pages: ['dashboard']
      }
    ]
  },

  // 5. BORDERS & DIVIDERS
  {
    name: 'borders',
    icon: Square,
    description: 'Border colors for separators and outlines',
    colors: [
      {
        key: 'borderPrimary',
        label: 'Primary Border',
        description: 'Main divider and border color',
        affects: ['Card borders', 'Dividers', 'Separators'],
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'services', 'management', 'charts']
      },
      {
        key: 'borderSecondary',
        label: 'Secondary Border',
        description: 'Subtle borders and dividers',
        affects: ['Input borders', 'Section dividers', 'Subtle lines'],
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'services', 'management']
      },
      {
        key: 'borderFocus',
        label: 'Focus Border',
        description: 'Active focus indicators',
        affects: ['Focus rings', 'Active borders', 'Selected outlines'],
        supportsAlpha: true,
        pages: []
      }
    ]
  },

  // 6. FEEDBACK - Status and alerts
  {
    name: 'feedback',
    icon: AlertCircle,
    description: 'Success, warning, error, and info states - Alert banners, notifications, and status messages',
    colors: [
      {
        key: 'success',
        label: 'Success Primary',
        description: 'Success state primary color (border for alerts)',
        affects: ['Success icons', 'Success buttons', 'Positive actions', 'Alert borders', 'Success notifications'],
        supportsAlpha: true,
        pages: ['dashboard', 'management']
      },
      {
        key: 'successBg',
        label: 'Success Background',
        description: 'Success state background (alert background)',
        affects: ['Success alerts', 'Success badges', 'Success cards', 'Alert backgrounds', 'Success banners'],
        supportsAlpha: true,
        pages: ['downloads']
      },
      {
        key: 'successText',
        label: 'Success Text',
        description: 'Success state text color (alert text)',
        affects: ['Success messages', 'Positive values', 'Alert text', 'Success notifications'],
        supportsAlpha: true,
        pages: ['downloads']
      },
      {
        key: 'warning',
        label: 'Warning Primary',
        description: 'Warning state primary color (border for alerts)',
        affects: ['Warning icons', 'Caution buttons', 'Alert borders', 'Warning notifications'],
        supportsAlpha: true,
        pages: ['dashboard']
      },
      {
        key: 'warningBg',
        label: 'Warning Background',
        description: 'Warning state background (alert background)',
        affects: ['Warning alerts', 'Warning badges', 'Alert backgrounds', 'Caution banners'],
        supportsAlpha: true,
        pages: []
      },
      {
        key: 'warningText',
        label: 'Warning Text',
        description: 'Warning state text color (alert text)',
        affects: ['Warning messages', 'Caution text', 'Alert text', 'Warning notifications'],
        supportsAlpha: true,
        pages: []
      },
      {
        key: 'error',
        label: 'Error Primary',
        description: 'Error state primary color (border for alerts)',
        affects: ['Error icons', 'Delete buttons', 'Critical actions', 'Alert borders', 'Error notifications'],
        supportsAlpha: true,
        pages: []
      },
      {
        key: 'errorBg',
        label: 'Error Background',
        description: 'Error state background (alert background)',
        affects: ['Error alerts', 'Error badges', 'Alert backgrounds', 'Error banners', 'Danger zones'],
        supportsAlpha: true,
        pages: ['management']
      },
      {
        key: 'errorText',
        label: 'Error Text',
        description: 'Error state text color (alert text)',
        affects: ['Error messages', 'Validation errors', 'Alert text', 'Error notifications'],
        supportsAlpha: true,
        pages: ['downloads', 'management']
      },
      {
        key: 'info',
        label: 'Info Primary',
        description: 'Information state primary color (border for alerts)',
        affects: ['Info icons', 'Info buttons', 'Alert borders', 'Info notifications'],
        supportsAlpha: true,
        pages: ['management']
      },
      {
        key: 'infoBg',
        label: 'Info Background',
        description: 'Information state background (alert background)',
        affects: ['Info alerts', 'Info cards', 'Alert backgrounds', 'Notification panels', 'Info banners'],
        supportsAlpha: true,
        pages: ['management']
      },
      {
        key: 'infoText',
        label: 'Info Text',
        description: 'Information state text color (alert text)',
        affects: ['Info messages', 'Help content', 'Alert text', 'Notification text'],
        supportsAlpha: true,
        pages: ['downloads', 'management']
      }
    ]
  },

  // 7. NAVIGATION
  {
    name: 'navigation',
    icon: Layout,
    description: 'Header, tabs, and menus - Top navigation bar, active/inactive tabs, and mobile menu',
    colors: [
      {
        key: 'navBg',
        label: 'Navigation Background',
        description: 'Navigation bar background (header background)',
        affects: ['Header', 'Nav bar', 'Menu background', 'Top bar', 'App header'],
        supportsAlpha: true
      },
      {
        key: 'navBorder',
        label: 'Navigation Border',
        description: 'Navigation separators (header border)',
        affects: ['Nav borders', 'Menu dividers', 'Header separator', 'Top bar border'],
        supportsAlpha: true
      },
      {
        key: 'navTabActive',
        label: 'Active Tab',
        description: 'Active navigation item',
        affects: ['Current page', 'Active tab'],
        supportsAlpha: true
      },
      {
        key: 'navTabInactive',
        label: 'Inactive Tab',
        description: 'Inactive navigation items',
        affects: ['Unselected tabs', 'Inactive menu items'],
        supportsAlpha: true
      },
      {
        key: 'navTabHover',
        label: 'Tab Hover',
        description: 'Navigation hover state',
        affects: ['Tab hovers', 'Menu hovers'],
        supportsAlpha: true
      },
      {
        key: 'navTabActiveBorder',
        label: 'Active Tab Indicator',
        description: 'Active tab underline/border',
        affects: ['Tab indicators', 'Active borders'],
        supportsAlpha: true
      },
      {
        key: 'navMobileMenuBg',
        label: 'Mobile Menu Background',
        description: 'Mobile navigation background',
        affects: ['Mobile menu', 'Dropdown menus'],
        supportsAlpha: true
      },
      {
        key: 'navMobileItemHover',
        label: 'Mobile Menu Hover',
        description: 'Mobile menu item hover',
        affects: ['Mobile hovers', 'Dropdown hovers'],
        supportsAlpha: true
      }
    ]
  },

  // 8. DATA DISPLAY - Progress, badges, charts
  {
    name: 'dataDisplay',
    icon: Activity,
    description: 'Charts, graphs, and progress bars - Data visualization colors for all chart types',
    colors: [
      {
        key: 'progressBg',
        label: 'Progress Track',
        description: 'Progress bar background',
        affects: ['Progress tracks', 'Empty progress state'],
        supportsAlpha: true,
        pages: []
      },
      {
        key: 'chartColor1',
        label: 'Chart Primary',
        description: 'Primary chart color',
        affects: ['First data series', 'Main chart color'],
        supportsAlpha: true,
        pages: ['charts']
      },
      {
        key: 'chartColor2',
        label: 'Chart Secondary',
        description: 'Secondary chart color',
        affects: ['Second data series'],
        supportsAlpha: true,
        pages: ['charts']
      },
      {
        key: 'chartColor3',
        label: 'Chart Tertiary',
        description: 'Third chart color',
        affects: ['Third data series'],
        supportsAlpha: true,
        pages: ['charts']
      },
      {
        key: 'chartColor4',
        label: 'Chart Quaternary',
        description: 'Fourth chart color',
        affects: ['Fourth data series'],
        supportsAlpha: true,
        pages: ['charts']
      },
      {
        key: 'chartColor5',
        label: 'Chart Color 5',
        description: 'Fifth chart color',
        affects: ['Fifth data series'],
        supportsAlpha: true,
        pages: ['charts']
      },
      {
        key: 'chartColor6',
        label: 'Chart Color 6',
        description: 'Sixth chart color',
        affects: ['Sixth data series'],
        supportsAlpha: true,
        pages: ['charts']
      },
      {
        key: 'chartColor7',
        label: 'Chart Color 7',
        description: 'Seventh chart color',
        affects: ['Seventh data series'],
        supportsAlpha: true,
        pages: ['charts']
      },
      {
        key: 'chartColor8',
        label: 'Chart Color 8',
        description: 'Eighth chart color',
        affects: ['Eighth data series'],
        supportsAlpha: true,
        pages: ['charts']
      },
      {
        key: 'chartCacheHitColor',
        label: 'Cache Hit Color',
        description: 'Cache hit visualization',
        affects: ['Hit rate charts', 'Success indicators'],
        supportsAlpha: true,
        pages: ['charts']
      },
      {
        key: 'chartCacheMissColor',
        label: 'Cache Miss Color',
        description: 'Cache miss visualization',
        affects: ['Miss rate charts', 'Warning indicators'],
        supportsAlpha: true,
        pages: ['charts']
      },
      {
        key: 'chartGridColor',
        label: 'Chart Grid',
        description: 'Chart grid lines',
        affects: ['Grid lines', 'Axes'],
        supportsAlpha: true,
        pages: ['charts']
      },
      {
        key: 'chartTextColor',
        label: 'Chart Text',
        description: 'Chart labels and legends',
        affects: ['Axis labels', 'Legends'],
        supportsAlpha: true,
        pages: ['charts']
      }
    ]
  },

  // 9. ACCESS CONTROL - Security indicators
  {
    name: 'accessControl',
    icon: Lock,
    description: 'Public and secured badges - Security status indicators for API access control',
    colors: [
      {
        key: 'publicAccessBg',
        label: 'Public Access Background',
        description: 'Public/open access indicator',
        affects: ['Public badges', 'Open access indicators'],
        supportsAlpha: true
      },
      {
        key: 'publicAccessText',
        label: 'Public Access Text',
        description: 'Public access text color',
        affects: ['Public labels', 'Open text'],
        supportsAlpha: true
      },
      {
        key: 'publicAccessBorder',
        label: 'Public Access Border',
        description: 'Public access border',
        affects: ['Public badge borders'],
        supportsAlpha: true
      },
      {
        key: 'securedAccessBg',
        label: 'Secured Access Background',
        description: 'Secured/locked access indicator',
        affects: ['Security badges', 'Locked indicators'],
        supportsAlpha: true
      },
      {
        key: 'securedAccessText',
        label: 'Secured Access Text',
        description: 'Secured access text color',
        affects: ['Security labels', 'Locked text'],
        supportsAlpha: true
      },
      {
        key: 'securedAccessBorder',
        label: 'Secured Access Border',
        description: 'Secured access border',
        affects: ['Security badge borders'],
        supportsAlpha: true
      }
    ]
  },

  // 10. USER SESSIONS - Session management colors
  {
    name: 'sessions',
    icon: Users,
    description: 'Authenticated users and guests - Colors for session badges and icons in the Users tab',
    colors: [
      {
        key: 'userSessionColor',
        label: 'Authenticated User Color',
        description: 'Icon and text color for authenticated users',
        affects: ['USER badge text', 'Authenticated user icon', 'Authenticated stat card'],
        supportsAlpha: true,
        pages: ['users']
      },
      {
        key: 'userSessionBg',
        label: 'Authenticated User Background',
        description: 'Background color for authenticated user badges',
        affects: ['USER badge background', 'Authenticated icon background'],
        supportsAlpha: true,
        pages: ['users']
      },
      {
        key: 'guestSessionColor',
        label: 'Guest User Color',
        description: 'Icon and text color for guest users',
        affects: ['GUEST badge text', 'Guest user icon', 'Guest stat card'],
        supportsAlpha: true,
        pages: ['users']
      },
      {
        key: 'guestSessionBg',
        label: 'Guest User Background',
        description: 'Background color for guest user badges',
        affects: ['GUEST badge background', 'Guest icon background'],
        supportsAlpha: true,
        pages: ['users']
      }
    ]
  },

  // 11. PLATFORM SERVICES - Brand-specific colors
  {
    name: 'platforms',
    icon: Gamepad2,
    description: 'Steam, Epic, Origin, Blizzard, WSUS, Riot - Platform-specific colors for badges and charts',
    colors: [
      {
        key: 'steamColor',
        label: 'Steam',
        description: 'Steam platform color',
        affects: ['Steam badges', 'Steam charts'],
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'services', 'charts']
      },
      {
        key: 'epicColor',
        label: 'Epic Games',
        description: 'Epic Games platform color',
        affects: ['Epic badges', 'Epic charts'],
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'services', 'charts']
      },
      {
        key: 'originColor',
        label: 'Origin/EA',
        description: 'EA/Origin platform color',
        affects: ['Origin badges', 'EA charts'],
        pages: ['dashboard', 'downloads', 'clients', 'services', 'charts'],
        supportsAlpha: true
      },
      {
        key: 'blizzardColor',
        label: 'Blizzard',
        description: 'Blizzard platform color',
        affects: ['Blizzard badges', 'Battle.net charts'],
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'services', 'charts']
      },
      {
        key: 'wsusColor',
        label: 'Windows Update',
        description: 'Windows Update service color',
        affects: ['WSUS badges', 'Update charts'],
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'services', 'charts']
      },
      {
        key: 'riotColor',
        label: 'Riot Games',
        description: 'Riot Games platform color',
        affects: ['Riot badges', 'Riot charts'],
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'services', 'charts']
      }
    ]
  },

  // 12. PERFORMANCE INDICATORS - Hit rate colors
  {
    name: 'performance',
    icon: Activity,
    description: 'Cache hit rate badges - High/medium/low performance indicators for cache efficiency',
    colors: [
      {
        key: 'hitRateHighBg',
        label: 'High Hit Rate Background',
        description: 'Background for high cache hit rates',
        affects: ['90%+ hit rate badges', 'Success indicators'],
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'services']
      },
      {
        key: 'hitRateHighText',
        label: 'High Hit Rate Text',
        description: 'Text color for high hit rates',
        affects: ['High hit rate labels'],
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'services']
      },
      {
        key: 'hitRateMediumBg',
        label: 'Medium Hit Rate Background',
        description: 'Background for medium cache hit rates',
        affects: ['50-89% hit rate badges'],
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'services']
      },
      {
        key: 'hitRateMediumText',
        label: 'Medium Hit Rate Text',
        description: 'Text color for medium hit rates',
        affects: ['Medium hit rate labels'],
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'services']
      },
      {
        key: 'hitRateLowBg',
        label: 'Low Hit Rate Background',
        description: 'Background for low cache hit rates',
        affects: ['0-49% hit rate badges'],
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'services']
      },
      {
        key: 'hitRateLowText',
        label: 'Low Hit Rate Text',
        description: 'Text color for low hit rates',
        affects: ['Low hit rate labels'],
        supportsAlpha: true,
        pages: ['dashboard', 'downloads', 'clients', 'services']
      },
      {
        key: 'hitRateWarningBg',
        label: 'Warning Hit Rate Background',
        description: 'Background for warning hit rates',
        affects: ['Critical performance warnings'],
        supportsAlpha: true,
        pages: ['dashboard', 'downloads']
      },
      {
        key: 'hitRateWarningText',
        label: 'Warning Hit Rate Text',
        description: 'Text color for warning hit rates',
        affects: ['Warning labels'],
        supportsAlpha: true,
        pages: ['dashboard', 'downloads']
      }
    ]
  },

  // 13. ACTION BUTTONS - Specialized action colors
  {
    name: 'actions',
    icon: Sparkles,
    description: 'Reset, Process, and Delete buttons - Special action buttons in the management page',
    colors: [
      {
        key: 'actionResetBg',
        label: 'Reset Button Background',
        description: 'Reset action button background',
        affects: ['Reset buttons', 'Clear actions'],
        supportsAlpha: true,
        pages: ['management']
      },
      {
        key: 'actionResetHover',
        label: 'Reset Button Hover',
        description: 'Reset button hover state',
        affects: ['Reset button hover'],
        supportsAlpha: true,
        pages: ['management']
      },
      {
        key: 'actionProcessBg',
        label: 'Process Button Background',
        description: 'Process action button background',
        affects: ['Process buttons', 'Start actions'],
        supportsAlpha: true,
        pages: ['management']
      },
      {
        key: 'actionProcessHover',
        label: 'Process Button Hover',
        description: 'Process button hover state',
        affects: ['Process button hover'],
        supportsAlpha: true,
        pages: ['management']
      },
      {
        key: 'actionDeleteBg',
        label: 'Delete Button Background',
        description: 'Delete action button background',
        affects: ['Delete buttons', 'Remove actions'],
        supportsAlpha: true,
        pages: ['management']
      },
      {
        key: 'actionDeleteHover',
        label: 'Delete Button Hover',
        description: 'Delete button hover state',
        affects: ['Delete button hover'],
        supportsAlpha: true,
        pages: ['management']
      }
    ]
  },

  // 14. UTILITIES - Misc UI elements
  {
    name: 'utilities',
    icon: Brush,
    description: 'Scrollbars and icon backgrounds - Specialized colors for dashboard stat card icons',
    colors: [
      {
        key: 'scrollbarTrack',
        label: 'Scrollbar Track',
        description: 'Scrollbar background track',
        affects: ['Scrollbar tracks', 'Scroll gutters'],
        supportsAlpha: true
      },
      {
        key: 'scrollbarThumb',
        label: 'Scrollbar Thumb',
        description: 'Scrollbar draggable element',
        affects: ['Scrollbar handles'],
        supportsAlpha: true
      },
      {
        key: 'scrollbarHover',
        label: 'Scrollbar Hover',
        description: 'Scrollbar hover state',
        affects: ['Scrollbar hover effects'],
        supportsAlpha: true
      },
      {
        key: 'iconBgBlue',
        label: 'Database Icon (Total Cache)',
        description: 'Background for database/storage icons',
        affects: ['Total Cache card icon', 'Database indicators'],
        supportsAlpha: true,
        pages: ['dashboard']
      },
      {
        key: 'iconBgGreen',
        label: 'Hard Drive Icon (Used Space)',
        description: 'Background for hard drive/storage icons',
        affects: ['Used Space card icon', 'Storage indicators'],
        supportsAlpha: true,
        pages: ['dashboard']
      },
      {
        key: 'iconBgEmerald',
        label: 'Trending Icon (Bandwidth Saved)',
        description: 'Background for trending/growth icons',
        affects: ['Bandwidth Saved card icon', 'Growth indicators'],
        supportsAlpha: true,
        pages: ['dashboard']
      },
      {
        key: 'iconBgPurple',
        label: 'Zap Icon (Added to Cache)',
        description: 'Background for lightning/zap icons',
        affects: ['Added to Cache card icon', 'Speed indicators'],
        supportsAlpha: true,
        pages: ['dashboard']
      },
      {
        key: 'iconBgIndigo',
        label: 'Server Icon (Total Served)',
        description: 'Background for server icons',
        affects: ['Total Served card icon', 'Server indicators'],
        supportsAlpha: true,
        pages: ['dashboard']
      },
      {
        key: 'iconBgOrange',
        label: 'Download Icon (Active Downloads)',
        description: 'Background for download icons',
        affects: ['Active Downloads card icon', 'Download indicators'],
        supportsAlpha: true,
        pages: ['dashboard']
      },
      {
        key: 'iconBgYellow',
        label: 'Users Icon (Active Clients)',
        description: 'Background for users/people icons',
        affects: ['Active Clients card icon', 'Client indicators'],
        supportsAlpha: true,
        pages: ['dashboard']
      },
      {
        key: 'iconBgCyan',
        label: 'Activity Icon (Cache Hit Ratio)',
        description: 'Background for activity/performance icons',
        affects: ['Cache Hit Ratio card icon', 'Performance indicators'],
        supportsAlpha: true,
        pages: ['dashboard']
      },
      {
        key: 'iconBgRed',
        label: 'Error Icon (Critical States)',
        description: 'Background for error/critical icons',
        affects: ['Error indicators', 'Critical state icons'],
        supportsAlpha: true,
        pages: ['dashboard']
      }
    ]
  }
];
