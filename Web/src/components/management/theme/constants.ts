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
  { name: 'all', label: 'All Pages', icon: Layers, description: 'Colors used across all pages' },
  { name: 'dashboard', label: 'Dashboard', icon: Home, description: 'Main dashboard and overview page' },
  { name: 'downloads', label: 'Downloads', icon: Download, description: 'Downloads and active transfers' },
  { name: 'clients', label: 'Clients', icon: Users, description: 'Client connections and statistics' },
  { name: 'services', label: 'Services', icon: Server, description: 'Service status and statistics' },
  { name: 'management', label: 'Management', icon: Settings, description: 'Cache and theme management' },
  { name: 'charts', label: 'Charts & Graphs', icon: BarChart3, description: 'Data visualizations and charts' }
];

export const colorGroups: ColorGroup[] = [
  // 1. FOUNDATION - Core brand colors
  {
    name: 'foundation',
    icon: Palette,
    description: 'Core brand colors that define your theme\'s identity',
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
    description: 'Text colors for content hierarchy and readability',
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
    description: 'Background colors for UI surfaces and layers',
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
      },
      {
        key: 'cardRing',
        label: 'Card Expanded Ring',
        description: 'Ring/glow around expanded cards',
        affects: ['Download cards expanded state', 'Active card highlight'],
        supportsAlpha: true,
        pages: ['downloads']
      }
    ]
  },

  // 4. INTERACTIVE - Form elements and controls
  {
    name: 'interactive',
    icon: Component,
    description: 'Interactive elements like buttons, inputs, and forms',
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
    description: 'Status indicators, alerts, notifications, banners, and feedback colors',
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
    description: 'Navigation bar and menu styling',
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
    description: 'Data visualization and display elements',
    colors: [
      {
        key: 'badgeBg',
        label: 'Badge Background',
        description: 'Badge and pill backgrounds',
        affects: ['Status badges', 'Tags', 'Labels'],
        supportsAlpha: true,
        pages: []
      },
      {
        key: 'badgeText',
        label: 'Badge Text',
        description: 'Badge text color',
        affects: ['Badge labels', 'Tag text'],
        supportsAlpha: true,
        pages: []
      },
      {
        key: 'progressBar',
        label: 'Progress Bar Fill',
        description: 'Progress indicator color',
        affects: ['Progress bars', 'Loading bars', 'Completion indicators'],
        supportsAlpha: true,
        pages: []
      },
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
    description: 'Security and access control indicators',
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

  // 10. PLATFORM SERVICES - Brand-specific colors
  {
    name: 'platforms',
    icon: Gamepad2,
    description: 'Gaming platform and service colors',
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

  // 11. PERFORMANCE INDICATORS - Hit rate colors
  {
    name: 'performance',
    icon: Activity,
    description: 'Performance indicators and cache hit rate colors',
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

  // 12. ACTION BUTTONS - Specialized action colors
  {
    name: 'actions',
    icon: Sparkles,
    description: 'Action button colors for operations',
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

  // 13. UTILITIES - Misc UI elements
  {
    name: 'utilities',
    icon: Brush,
    description: 'Utility colors for specialized UI elements',
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
        label: 'Blue Icon Background',
        description: 'Blue icon container',
        affects: ['Database icons', 'Info icons'],
        supportsAlpha: true,
        pages: ['dashboard']
      },
      {
        key: 'iconBgGreen',
        label: 'Green Icon Background',
        description: 'Green icon container',
        affects: ['Success icons', 'Online status'],
        supportsAlpha: true,
        pages: ['dashboard']
      },
      {
        key: 'iconBgEmerald',
        label: 'Emerald Icon Background',
        description: 'Emerald icon container',
        affects: ['Trending icons', 'Growth indicators'],
        supportsAlpha: true,
        pages: ['dashboard']
      },
      {
        key: 'iconBgPurple',
        label: 'Purple Icon Background',
        description: 'Purple icon container',
        affects: ['Special features', 'Premium indicators'],
        supportsAlpha: true,
        pages: ['dashboard']
      },
      {
        key: 'iconBgIndigo',
        label: 'Indigo Icon Background',
        description: 'Indigo icon container',
        affects: ['Server icons', 'System indicators'],
        supportsAlpha: true,
        pages: ['dashboard']
      },
      {
        key: 'iconBgOrange',
        label: 'Orange Icon Background',
        description: 'Orange icon container',
        affects: ['Download icons', 'Activity indicators'],
        supportsAlpha: true,
        pages: ['dashboard']
      },
      {
        key: 'iconBgYellow',
        label: 'Yellow Icon Background',
        description: 'Yellow icon container',
        affects: ['Warning icons', 'Client indicators'],
        supportsAlpha: true,
        pages: ['dashboard']
      },
      {
        key: 'iconBgCyan',
        label: 'Cyan Icon Background',
        description: 'Cyan icon container',
        affects: ['Activity icons', 'Performance indicators'],
        supportsAlpha: true,
        pages: ['dashboard']
      },
      {
        key: 'iconBgRed',
        label: 'Red Icon Background',
        description: 'Red icon container',
        affects: ['Error icons', 'Critical indicators'],
        supportsAlpha: true,
        pages: ['dashboard']
      }
    ]
  }
];
