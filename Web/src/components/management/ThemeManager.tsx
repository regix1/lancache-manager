import React, { useState, useEffect, useRef, useCallback } from 'react';
import { RotateCcw } from 'lucide-react';
import { Gamepad2 } from 'lucide-react';
import {
  Palette,
  Upload,
  Trash2,
  Check,
  Download,
  Eye,
  RefreshCw,
  Lock,
  Plus,
  EyeOff,
  ChevronDown,
  ChevronRight,
  Info,
  Save,
  Copy,
  Sun,
  Moon,
  Brush,
  Layout,
  Type,
  Square,
  AlertCircle,
  AlertTriangle,
  Component,
  Sparkles,
  Activity,
  Edit,
  Search,
  X,
  Percent,
  MoreVertical,
  Layers
} from 'lucide-react';
import themeService from '../../services/theme.service';
import authService from '../../services/auth.service';
import { Alert } from '../ui/Alert';
import { Button } from '../ui/Button';
import { Card } from '../ui/Card';
import { Modal } from '../ui/Modal';
import { Checkbox } from '../ui/Checkbox';
import { EnhancedDropdown } from '../ui/EnhancedDropdown';
import { API_BASE } from '../../utils/constants';
import { Home, BarChart3, Users, Server, Settings } from 'lucide-react';

interface Theme {
  meta: {
    id: string;
    name: string;
    description?: string;
    author?: string;
    version?: string;
    isDark?: boolean;
  };
  colors: any;
  custom?: any;
  css?: { content?: string };
}

interface ColorGroup {
  name: string;
  icon: React.ElementType;
  description: string;
  colors: {
    key: string;
    label: string;
    description: string;
    affects: string[];
    value?: string;
    supportsAlpha?: boolean; // Allow transparency for this color
    pages?: string[]; // Pages where this color is used
  }[];
}

interface PageGroup {
  name: string;
  label: string;
  icon: React.ElementType;
  description: string;
}

interface ThemeManagerProps {
  isAuthenticated: boolean;
}

const ThemeManager: React.FC<ThemeManagerProps> = ({ isAuthenticated }) => {
  const [themes, setThemes] = useState<Theme[]>([]);
  const [currentTheme, setCurrentTheme] = useState('dark-default');
  const [loading, setLoading] = useState(false);
  const [dragActive, setDragActive] = useState(false);
  const [uploadError, setUploadError] = useState<string | null>(null);
  const [uploadSuccess, setUploadSuccess] = useState<string | null>(null);
  const [previewTheme, setPreviewTheme] = useState<string | null>(null);
  const [createModalOpen, setCreateModalOpen] = useState(false);
  const [editModalOpen, setEditModalOpen] = useState(false);
  const [themePendingDeletion, setThemePendingDeletion] = useState<{ id: string; name: string } | null>(null);
  const [showCleanupModal, setShowCleanupModal] = useState(false);
  const [editingTheme, setEditingTheme] = useState<Theme | null>(null);
  const [expandedGroups, setExpandedGroups] = useState<string[]>(['foundation']);
  const [activeTab, setActiveTab] = useState<'themes' | 'customize'>('themes');
  const [themeActionMenu, setThemeActionMenu] = useState<string | null>(null);
  const [copiedColor, setCopiedColor] = useState<string | null>(null);
  const [colorEditingStarted, setColorEditingStarted] = useState<Record<string, boolean>>({});
  const [createSearchQuery, setCreateSearchQuery] = useState('');
  const [editSearchQuery, setEditSearchQuery] = useState('');
  const [organizationMode, setOrganizationMode] = useState<'category' | 'page'>('category');
  const [selectedPage, setSelectedPage] = useState<string>('all');
  const [editOrganizationMode, setEditOrganizationMode] = useState<'category' | 'page'>('category');
  const [editSelectedPage, setEditSelectedPage] = useState<string>('all');
  const [sharpCorners, setSharpCorners] = useState(false);


  const [editedTheme, setEditedTheme] = useState<any>({});
  const [newTheme, setNewTheme] = useState<any>({
    name: '',
    description: '',
    author: '',
    version: '1.0.0',
    isDark: true,
    // All colors will be populated when the modal opens from the current theme
    customCSS: ''
  });

  const fileInputRef = useRef<HTMLInputElement>(null);

  // Helper functions for color conversion
  const hexToRgba = (hex: string, alpha: number = 1): string => {
    const result = /^#?([a-f\d]{2})([a-f\d]{2})([a-f\d]{2})$/i.exec(hex);
    if (!result) return hex;
    const r = parseInt(result[1], 16);
    const g = parseInt(result[2], 16);
    const b = parseInt(result[3], 16);
    return `rgba(${r}, ${g}, ${b}, ${alpha})`;
  };

  const parseColorValue = (color: string): { hex: string; alpha: number } => {
    // Handle rgba format
    const rgbaMatch = color.match(/rgba?\((\d+),\s*(\d+),\s*(\d+)(?:,\s*([\d.]+))?\)/);
    if (rgbaMatch) {
      const r = parseInt(rgbaMatch[1]);
      const g = parseInt(rgbaMatch[2]);
      const b = parseInt(rgbaMatch[3]);
      const alpha = rgbaMatch[4] ? parseFloat(rgbaMatch[4]) : 1;
      const hex = '#' + [r, g, b].map(x => x.toString(16).padStart(2, '0')).join('');
      return { hex, alpha };
    }
    // Handle hex format
    return { hex: color, alpha: 1 };
  };

  const updateColorWithAlpha = (key: string, hex: string, alpha: number, isCreate: boolean = false) => {
    const colorValue = alpha < 1 ? hexToRgba(hex, alpha) : hex;
    if (isCreate) {
      setNewTheme((prev: any) => ({ ...prev, [key]: colorValue }));
    } else {
      setEditedTheme((prev: any) => ({ ...prev, [key]: colorValue }));
    }
  };

  // Filter color groups based on search
  const filterColorGroups = (groups: ColorGroup[], search: string): ColorGroup[] => {
    if (!search.trim()) return groups;

    const searchLower = search.toLowerCase();
    return groups.map(group => {
      const filteredColors = group.colors.filter(color =>
        color.label.toLowerCase().includes(searchLower) ||
        color.description.toLowerCase().includes(searchLower) ||
        color.affects.some(affect => affect.toLowerCase().includes(searchLower)) ||
        color.key.toLowerCase().includes(searchLower)
      );

      // If group name matches, show all colors in that group
      if (group.name.toLowerCase().includes(searchLower) ||
          group.description.toLowerCase().includes(searchLower)) {
        return group;
      }

      // Otherwise only show groups with matching colors
      return { ...group, colors: filteredColors };
    }).filter(group => group.colors.length > 0);
  };

  // Filter colors by page
  const filterByPage = (groups: ColorGroup[], page: string): ColorGroup[] => {
    if (page === 'all') return groups;

    return groups.map(group => {
      const filteredColors = group.colors.filter(color =>
        color.pages?.includes(page)
      );
      return { ...group, colors: filteredColors };
    }).filter(group => group.colors.length > 0);
  };

  // Get filtered groups based on organization mode
  const getFilteredGroups = useCallback((groups: ColorGroup[], search: string, isEdit: boolean = false): ColorGroup[] => {
    let filtered = groups;

    // Apply page filter if in page mode
    const mode = isEdit ? editOrganizationMode : organizationMode;
    const page = isEdit ? editSelectedPage : selectedPage;

    if (mode === 'page') {
      filtered = filterByPage(filtered, page);
    }

    // Apply search filter
    if (search.trim()) {
      filtered = filterColorGroups(filtered, search);
    }

    return filtered;
  }, [organizationMode, selectedPage, editOrganizationMode, editSelectedPage]);

  // Define available pages
  const pageDefinitions: PageGroup[] = [
    { name: 'all', label: 'All Pages', icon: Layers, description: 'Colors used across all pages' },
    { name: 'dashboard', label: 'Dashboard', icon: Home, description: 'Main dashboard and overview page' },
    { name: 'downloads', label: 'Downloads', icon: Download, description: 'Downloads and active transfers' },
    { name: 'clients', label: 'Clients', icon: Users, description: 'Client connections and statistics' },
    { name: 'services', label: 'Services', icon: Server, description: 'Service status and statistics' },
    { name: 'management', label: 'Management', icon: Settings, description: 'Cache and theme management' },
    { name: 'charts', label: 'Charts & Graphs', icon: BarChart3, description: 'Data visualizations and charts' }
  ];

  const colorGroups: ColorGroup[] = [
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
          pages: ['dashboard', 'downloads', 'clients', 'services', 'management']
        },
        {
          key: 'secondaryColor',
          label: 'Secondary Brand Color',
          description: 'Complementary brand accent',
          affects: ['Secondary buttons', 'Highlights', 'Accents'],
          supportsAlpha: true,
          pages: ['dashboard', 'downloads', 'management']
        },
        {
          key: 'accentColor',
          label: 'Accent Color',
          description: 'Tertiary accent for special elements',
          affects: ['Special badges', 'Tooltips', 'Info elements'],
          supportsAlpha: true,
          pages: ['dashboard', 'downloads', 'clients', 'services', 'management']
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
          pages: ['dashboard', 'downloads', 'clients', 'services', 'management']
        },
        {
          key: 'textAccent',
          label: 'Accent Text',
          description: 'Highlighted or linked text',
          affects: ['Links', 'Highlighted values', 'Active menu items'],
          supportsAlpha: true,
          pages: ['dashboard', 'downloads', 'clients', 'services', 'management']
        },
        {
          key: 'textPlaceholder',
          label: 'Placeholder Text',
          description: 'Input placeholder text color',
          affects: ['Form placeholders', 'Search hints', 'Empty states'],
          supportsAlpha: true,
          pages: ['downloads', 'management']
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
          pages: ['dashboard', 'downloads', 'clients', 'services', 'management']
        },
        {
          key: 'bgSecondary',
          label: 'Surface Background',
          description: 'Elevated surface backgrounds',
          affects: ['Cards', 'Panels', 'Modals', 'Dialogs'],
          supportsAlpha: true,
          pages: ['dashboard', 'downloads', 'clients', 'services', 'management']
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
          pages: ['dashboard', 'downloads', 'clients', 'services', 'management']
        },
        {
          key: 'cardBg',
          label: 'Card Background',
          description: 'Card component background',
          affects: ['Stat cards', 'Content cards', 'Widget backgrounds'],
          supportsAlpha: true,
          pages: ['dashboard', 'downloads', 'clients', 'services']
        },
        {
          key: 'cardBorder',
          label: 'Card Border',
          description: 'Card component borders',
          affects: ['Card outlines', 'Panel borders'],
          supportsAlpha: true,
          pages: ['dashboard', 'downloads', 'clients', 'services']
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
      description: 'Interactive elements like buttons, inputs, and forms',
      colors: [
        {
          key: 'buttonBg',
          label: 'Button Background',
          description: 'Primary button fill color',
          affects: ['Primary buttons', 'Submit buttons', 'CTAs'],
          supportsAlpha: true,
          pages: ['management', 'downloads']
        },
        {
          key: 'buttonHover',
          label: 'Button Hover',
          description: 'Button hover state color',
          affects: ['Button hover effects', 'Active button states'],
          supportsAlpha: true
        },
        {
          key: 'buttonText',
          label: 'Button Text',
          description: 'Button label color',
          affects: ['Button labels', 'Button icons'],
          supportsAlpha: true
        },
        {
          key: 'inputBg',
          label: 'Input Background',
          description: 'Form input background',
          affects: ['Text inputs', 'Textareas', 'Select boxes'],
          supportsAlpha: true,
          pages: ['management', 'downloads']
        },
        {
          key: 'inputBorder',
          label: 'Input Border',
          description: 'Form input border',
          affects: ['Input outlines', 'Field borders'],
          supportsAlpha: true
        },
        {
          key: 'inputFocus',
          label: 'Input Focus',
          description: 'Focused input indicator',
          affects: ['Active input borders', 'Focus rings'],
          supportsAlpha: true
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
          supportsAlpha: true
        },
        {
          key: 'dragHandleHover',
          label: 'Drag Handle Hover',
          description: 'Active drag handle state',
          affects: ['Drag grip hover', 'Active dragging'],
          supportsAlpha: true
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
          supportsAlpha: true
        },
        {
          key: 'borderSecondary',
          label: 'Secondary Border',
          description: 'Subtle borders and dividers',
          affects: ['Input borders', 'Section dividers', 'Subtle lines'],
          supportsAlpha: true
        },
        {
          key: 'borderFocus',
          label: 'Focus Border',
          description: 'Active focus indicators',
          affects: ['Focus rings', 'Active borders', 'Selected outlines'],
          supportsAlpha: true
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
          supportsAlpha: true
        },
        {
          key: 'successBg',
          label: 'Success Background',
          description: 'Success state background (alert background)',
          affects: ['Success alerts', 'Success badges', 'Success cards', 'Alert backgrounds', 'Success banners'],
          supportsAlpha: true
        },
        {
          key: 'successText',
          label: 'Success Text',
          description: 'Success state text color (alert text)',
          affects: ['Success messages', 'Positive values', 'Alert text', 'Success notifications'],
          supportsAlpha: true
        },
        {
          key: 'warning',
          label: 'Warning Primary',
          description: 'Warning state primary color (border for alerts)',
          affects: ['Warning icons', 'Caution buttons', 'Alert borders', 'Warning notifications'],
          supportsAlpha: true
        },
        {
          key: 'warningBg',
          label: 'Warning Background',
          description: 'Warning state background (alert background)',
          affects: ['Warning alerts', 'Warning badges', 'Alert backgrounds', 'Caution banners'],
          supportsAlpha: true
        },
        {
          key: 'warningText',
          label: 'Warning Text',
          description: 'Warning state text color (alert text)',
          affects: ['Warning messages', 'Caution text', 'Alert text', 'Warning notifications'],
          supportsAlpha: true
        },
        {
          key: 'error',
          label: 'Error Primary',
          description: 'Error state primary color (border for alerts)',
          affects: ['Error icons', 'Delete buttons', 'Critical actions', 'Alert borders', 'Error notifications'],
          supportsAlpha: true
        },
        {
          key: 'errorBg',
          label: 'Error Background',
          description: 'Error state background (alert background)',
          affects: ['Error alerts', 'Error badges', 'Alert backgrounds', 'Error banners', 'Danger zones'],
          supportsAlpha: true
        },
        {
          key: 'errorText',
          label: 'Error Text',
          description: 'Error state text color (alert text)',
          affects: ['Error messages', 'Validation errors', 'Alert text', 'Error notifications'],
          supportsAlpha: true
        },
        {
          key: 'info',
          label: 'Info Primary',
          description: 'Information state primary color (border for alerts)',
          affects: ['Info icons', 'Info buttons', 'Alert borders', 'Info notifications'],
          supportsAlpha: true
        },
        {
          key: 'infoBg',
          label: 'Info Background',
          description: 'Information state background (alert background)',
          affects: ['Info alerts', 'Info cards', 'Alert backgrounds', 'Notification panels', 'Info banners'],
          supportsAlpha: true
        },
        {
          key: 'infoText',
          label: 'Info Text',
          description: 'Information state text color (alert text)',
          affects: ['Info messages', 'Help content', 'Alert text', 'Notification text'],
          supportsAlpha: true
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
          supportsAlpha: true
        },
        {
          key: 'badgeText',
          label: 'Badge Text',
          description: 'Badge text color',
          affects: ['Badge labels', 'Tag text'],
          supportsAlpha: true
        },
        {
          key: 'progressBar',
          label: 'Progress Bar Fill',
          description: 'Progress indicator color',
          affects: ['Progress bars', 'Loading bars', 'Completion indicators'],
          supportsAlpha: true
        },
        {
          key: 'progressBg',
          label: 'Progress Track',
          description: 'Progress bar background',
          affects: ['Progress tracks', 'Empty progress state'],
          supportsAlpha: true
        },
        {
          key: 'chartColor1',
          label: 'Chart Primary',
          description: 'Primary chart color',
          affects: ['First data series', 'Main chart color'],
          supportsAlpha: true
        },
        {
          key: 'chartColor2',
          label: 'Chart Secondary',
          description: 'Secondary chart color',
          affects: ['Second data series'],
          supportsAlpha: true
        },
        {
          key: 'chartColor3',
          label: 'Chart Tertiary',
          description: 'Third chart color',
          affects: ['Third data series'],
          supportsAlpha: true
        },
        {
          key: 'chartColor4',
          label: 'Chart Quaternary',
          description: 'Fourth chart color',
          affects: ['Fourth data series'],
          supportsAlpha: true
        },
        {
          key: 'chartCacheHitColor',
          label: 'Cache Hit Color',
          description: 'Cache hit visualization',
          affects: ['Hit rate charts', 'Success indicators'],
          supportsAlpha: true
        },
        {
          key: 'chartCacheMissColor',
          label: 'Cache Miss Color',
          description: 'Cache miss visualization',
          affects: ['Miss rate charts', 'Warning indicators'],
          supportsAlpha: true
        },
        {
          key: 'chartGridColor',
          label: 'Chart Grid',
          description: 'Chart grid lines',
          affects: ['Grid lines', 'Axes'],
          supportsAlpha: true
        },
        {
          key: 'chartTextColor',
          label: 'Chart Text',
          description: 'Chart labels and legends',
          affects: ['Axis labels', 'Legends'],
          supportsAlpha: true
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
          supportsAlpha: true
        },
        {
          key: 'iconBgGreen',
          label: 'Green Icon Background',
          description: 'Green icon container',
          affects: ['Success icons', 'Online status'],
          supportsAlpha: true
        },
        {
          key: 'iconBgEmerald',
          label: 'Emerald Icon Background',
          description: 'Emerald icon container',
          affects: ['Trending icons', 'Growth indicators'],
          supportsAlpha: true
        },
        {
          key: 'iconBgPurple',
          label: 'Purple Icon Background',
          description: 'Purple icon container',
          affects: ['Special features', 'Premium indicators'],
          supportsAlpha: true
        },
        {
          key: 'iconBgIndigo',
          label: 'Indigo Icon Background',
          description: 'Indigo icon container',
          affects: ['Server icons', 'System indicators'],
          supportsAlpha: true
        },
        {
          key: 'iconBgOrange',
          label: 'Orange Icon Background',
          description: 'Orange icon container',
          affects: ['Download icons', 'Activity indicators'],
          supportsAlpha: true
        },
        {
          key: 'iconBgYellow',
          label: 'Yellow Icon Background',
          description: 'Yellow icon container',
          affects: ['Warning icons', 'Client indicators'],
          supportsAlpha: true
        },
        {
          key: 'iconBgCyan',
          label: 'Cyan Icon Background',
          description: 'Cyan icon container',
          affects: ['Activity icons', 'Performance indicators'],
          supportsAlpha: true
        },
        {
          key: 'iconBgRed',
          label: 'Red Icon Background',
          description: 'Red icon container',
          affects: ['Error icons', 'Critical indicators'],
          supportsAlpha: true
        }
      ]
    }
  ];

  useEffect(() => {
    // Load themes and set the current theme ID
    loadThemes();
    const currentThemeId = themeService.getCurrentThemeId();
    setCurrentTheme(currentThemeId);

    // Initialize sharp corners state
    setSharpCorners(themeService.getSharpCorners());

  }, []);

  // Update theme colors when create modal opens
  useEffect(() => {
    if (createModalOpen) {
      // Get current theme's colors from computed styles
      const computedStyle = getComputedStyle(document.documentElement);
      const getCurrentColor = (varName: string) => {
        return computedStyle.getPropertyValue(varName).trim() || '';
      };

      setNewTheme((prev: any) => ({
        ...prev,
        // Update all color values with current theme colors
        primaryColor: getCurrentColor('--theme-primary'),
        secondaryColor: getCurrentColor('--theme-secondary'),
        accentColor: getCurrentColor('--theme-accent'),

        bgPrimary: getCurrentColor('--theme-bg-primary'),
        bgSecondary: getCurrentColor('--theme-bg-secondary'),
        bgTertiary: getCurrentColor('--theme-bg-tertiary'),
        bgHover: getCurrentColor('--theme-bg-hover'),

        textPrimary: getCurrentColor('--theme-text-primary'),
        textSecondary: getCurrentColor('--theme-text-secondary'),
        textMuted: getCurrentColor('--theme-text-muted'),
        textAccent: getCurrentColor('--theme-text-accent'),
        textPlaceholder: getCurrentColor('--theme-text-placeholder'),
        dragHandleColor: getCurrentColor('--theme-drag-handle-color'),
        dragHandleHover: getCurrentColor('--theme-drag-handle-hover'),

        borderPrimary: getCurrentColor('--theme-border-primary'),
        borderSecondary: getCurrentColor('--theme-border-secondary'),
        borderFocus: getCurrentColor('--theme-border-focus'),

        navBg: getCurrentColor('--theme-nav-bg'),
        navBorder: getCurrentColor('--theme-nav-border'),
        navTabActive: getCurrentColor('--theme-nav-tab-active'),
        navTabInactive: getCurrentColor('--theme-nav-tab-inactive'),
        navTabHover: getCurrentColor('--theme-nav-tab-hover'),
        navTabActiveBorder: getCurrentColor('--theme-nav-tab-active-border'),
        navMobileMenuBg: getCurrentColor('--theme-nav-mobile-menu-bg'),
        navMobileItemHover: getCurrentColor('--theme-nav-mobile-item-hover'),

        success: getCurrentColor('--theme-success'),
        successBg: getCurrentColor('--theme-success-bg'),
        successText: getCurrentColor('--theme-success-text'),
        warning: getCurrentColor('--theme-warning'),
        warningBg: getCurrentColor('--theme-warning-bg'),
        warningText: getCurrentColor('--theme-warning-text'),
        error: getCurrentColor('--theme-error'),
        errorBg: getCurrentColor('--theme-error-bg'),
        errorText: getCurrentColor('--theme-error-text'),
        info: getCurrentColor('--theme-info'),
        infoBg: getCurrentColor('--theme-info-bg'),
        infoText: getCurrentColor('--theme-info-text'),

        steamColor: getCurrentColor('--theme-steam-color'),
        epicColor: getCurrentColor('--theme-epic-color'),
        originColor: getCurrentColor('--theme-origin-color'),
        blizzardColor: getCurrentColor('--theme-blizzard-color'),
        wsusColor: getCurrentColor('--theme-wsus-color'),
        riotColor: getCurrentColor('--theme-riot-color'),

        cardBg: getCurrentColor('--theme-card-bg'),
        cardBorder: getCurrentColor('--theme-card-border'),
        cardOutline: getCurrentColor('--theme-card-outline'),
        buttonBg: getCurrentColor('--theme-button-bg'),
        buttonHover: getCurrentColor('--theme-button-hover'),
        buttonText: getCurrentColor('--theme-button-text'),
        inputBg: getCurrentColor('--theme-input-bg'),
        inputBorder: getCurrentColor('--theme-input-border'),
        inputFocus: getCurrentColor('--theme-input-focus'),
        checkboxAccent: getCurrentColor('--theme-checkbox-accent'),
        checkboxBorder: getCurrentColor('--theme-checkbox-border'),
        checkboxBg: getCurrentColor('--theme-checkbox-bg'),
        checkboxCheckmark: getCurrentColor('--theme-checkbox-checkmark'),
        checkboxShadow: getCurrentColor('--theme-checkbox-shadow'),
        checkboxHoverShadow: getCurrentColor('--theme-checkbox-hover-shadow'),
        checkboxHoverBg: getCurrentColor('--theme-checkbox-hover-bg'),
        checkboxFocus: getCurrentColor('--theme-checkbox-focus'),
        sliderAccent: getCurrentColor('--theme-slider-accent'),
        sliderThumb: getCurrentColor('--theme-slider-thumb'),
        sliderTrack: getCurrentColor('--theme-slider-track'),
        badgeBg: getCurrentColor('--theme-badge-bg'),
        badgeText: getCurrentColor('--theme-badge-text'),
        progressBar: getCurrentColor('--theme-progress-bar'),
        progressBg: getCurrentColor('--theme-progress-bg'),

        // Icon backgrounds
        iconBgBlue: getCurrentColor('--theme-icon-bg-blue'),
        iconBgGreen: getCurrentColor('--theme-icon-bg-green'),
        iconBgEmerald: getCurrentColor('--theme-icon-bg-emerald'),
        iconBgPurple: getCurrentColor('--theme-icon-bg-purple'),
        iconBgIndigo: getCurrentColor('--theme-icon-bg-indigo'),
        iconBgOrange: getCurrentColor('--theme-icon-bg-orange'),
        iconBgYellow: getCurrentColor('--theme-icon-bg-yellow'),
        iconBgCyan: getCurrentColor('--theme-icon-bg-cyan'),
        iconBgRed: getCurrentColor('--theme-icon-bg-red'),

        // Chart colors
        chartColor1: getCurrentColor('--theme-chart-color-1'),
        chartColor2: getCurrentColor('--theme-chart-color-2'),
        chartColor3: getCurrentColor('--theme-chart-color-3'),
        chartColor4: getCurrentColor('--theme-chart-color-4'),
        chartColor5: getCurrentColor('--theme-chart-color-5'),
        chartColor6: getCurrentColor('--theme-chart-color-6'),
        chartColor7: getCurrentColor('--theme-chart-color-7'),
        chartColor8: getCurrentColor('--theme-chart-color-8'),
        chartBorderColor: getCurrentColor('--theme-chart-border-color'),
        chartGridColor: getCurrentColor('--theme-chart-grid-color'),
        chartTextColor: getCurrentColor('--theme-chart-text-color'),
        chartCacheHitColor: getCurrentColor('--theme-chart-cache-hit-color'),
        chartCacheMissColor: getCurrentColor('--theme-chart-cache-miss-color'),

        // Scrollbar colors
        scrollbarThumb: getCurrentColor('--theme-scrollbar-thumb'),
        scrollbarTrack: getCurrentColor('--theme-scrollbar-track'),
        scrollbarThumbHover: getCurrentColor('--theme-scrollbar-thumb-hover'),
        scrollbarHover: getCurrentColor('--theme-scrollbar-hover'),

        // Hit rate indicators
        hitRateHighBg: getCurrentColor('--theme-hit-rate-high-bg'),
        hitRateHighText: getCurrentColor('--theme-hit-rate-high-text'),
        hitRateMediumBg: getCurrentColor('--theme-hit-rate-medium-bg'),
        hitRateMediumText: getCurrentColor('--theme-hit-rate-medium-text'),
        hitRateLowBg: getCurrentColor('--theme-hit-rate-low-bg'),
        hitRateLowText: getCurrentColor('--theme-hit-rate-low-text'),
        hitRateWarningBg: getCurrentColor('--theme-hit-rate-warning-bg'),
        hitRateWarningText: getCurrentColor('--theme-hit-rate-warning-text'),

        // Action button colors
        actionResetBg: getCurrentColor('--theme-action-reset-bg'),
        actionResetHover: getCurrentColor('--theme-action-reset-hover'),
        actionProcessBg: getCurrentColor('--theme-action-process-bg'),
        actionProcessHover: getCurrentColor('--theme-action-process-hover'),
        actionDeleteBg: getCurrentColor('--theme-action-delete-bg'),
        actionDeleteHover: getCurrentColor('--theme-action-delete-hover'),

        // Access indicators
        publicAccessBg: getCurrentColor('--theme-public-access-bg'),
        publicAccessText: getCurrentColor('--theme-public-access-text'),
        publicAccessBorder: getCurrentColor('--theme-public-access-border'),
        securedAccessBg: getCurrentColor('--theme-secured-access-bg'),
        securedAccessText: getCurrentColor('--theme-secured-access-text'),
        securedAccessBorder: getCurrentColor('--theme-secured-access-border'),
      }));
    }
  }, [createModalOpen]);

  const loadThemes = useCallback(async () => {
    setLoading(true);
    try {
      const themeList = await themeService.loadThemes();
      setThemes(themeList);
    } catch (error) {
      console.error('Failed to load themes:', error);
      setUploadError('Failed to load themes');
    } finally {
      setLoading(false);
    }
  }, []);

  const handleThemeChange = async (themeId: string) => {
    try {
      if (!themeId) {
        themeService.clearTheme();
        setCurrentTheme('');
        setPreviewTheme(null);
        return;
      }

      const theme = await themeService.getTheme(themeId);
      if (theme) {
        themeService.applyTheme(theme);
        setCurrentTheme(themeId);
        setPreviewTheme(null);
      }
    } catch (error) {
      console.error('Failed to apply theme:', error);
      setUploadError('Failed to apply theme');
    }
  };

  const handlePreview = async (themeId: string) => {
    if (previewTheme === themeId) {
      handleThemeChange(currentTheme);
      setPreviewTheme(null);
    } else {
      try {
        const theme = await themeService.getTheme(themeId);
        if (theme) {
          themeService.applyTheme(theme);
          setPreviewTheme(themeId);
        }
      } catch (error) {
        console.error('Failed to preview theme:', error);
      }
    }
  };

  const toggleGroup = (groupName: string) => {
    setExpandedGroups((prev) =>
      prev.includes(groupName) ? prev.filter((g) => g !== groupName) : [...prev, groupName]
    );
  };

  const [createColorEditingStarted, setCreateColorEditingStarted] = useState<Record<string, boolean>>({});
  
  const handleColorStart = (key: string) => {
    // Save the original color when user starts editing
    if (!createColorEditingStarted[key]) {
      const currentValue = newTheme[key];
      if (currentValue) {
        localStorage.setItem(`color_history_create_${key}`, currentValue);
      }
      setCreateColorEditingStarted(prev => ({ ...prev, [key]: true }));
    }
  };
  
  const handleColorChange = (key: string, value: string) => {
    // Just update the value, don't save to history on every change
    setNewTheme((prev: any) => ({ ...prev, [key]: value }));
  };

  const restoreCreatePreviousColor = (key: string) => {
    const previousColor = localStorage.getItem(`color_history_create_${key}`);
    if (previousColor) {
      // Swap current with history
      const currentColor = newTheme[key];
      setNewTheme((prev: any) => ({ ...prev, [key]: previousColor }));
      localStorage.setItem(`color_history_create_${key}`, currentColor);
    }
  };
  
  const getCreateColorHistory = (key: string) => {
    return localStorage.getItem(`color_history_create_${key}`);
  };

  const handleEditColorStart = (key: string) => {
    // Save the original color when user starts editing (not on every change)
    if (!colorEditingStarted[key]) {
      const currentValue = editedTheme[key];
      if (currentValue && currentValue.match(/^#[0-9a-fA-F]{6}$/)) {
        const historyKey = `color_history_${editingTheme?.meta.id}_${key}`;
        localStorage.setItem(historyKey, currentValue);
      }
      setColorEditingStarted(prev => ({ ...prev, [key]: true }));
    }
  };

  const handleEditColorChange = (key: string, value: string) => {
    // Just update the value, don't save to history on every change
    setEditedTheme((prev: any) => ({ ...prev, [key]: value }));
  };

  const restorePreviousColor = (key: string) => {
    const historyKey = `color_history_${editingTheme?.meta.id}_${key}`;
    const previousColor = localStorage.getItem(historyKey);
    if (previousColor) {
      // Swap current with history
      const currentColor = editedTheme[key];
      setEditedTheme((prev: any) => ({ ...prev, [key]: previousColor }));
      localStorage.setItem(historyKey, currentColor || '');
    }
  };
  
  const getEditColorHistory = (key: string) => {
    const historyKey = `color_history_${editingTheme?.meta.id}_${key}`;
    const value = localStorage.getItem(historyKey);
    return value;
  };

  const copyColor = async (color: string) => {
    try {
      if (navigator.clipboard && navigator.clipboard.writeText) {
        await navigator.clipboard.writeText(color);
      } else {
        // Fallback for browsers without clipboard API
        const textarea = document.createElement('textarea');
        textarea.value = color;
        textarea.style.position = 'fixed';
        textarea.style.left = '-999999px';
        textarea.style.top = '-999999px';
        document.body.appendChild(textarea);
        textarea.focus();
        textarea.select();
        document.execCommand('copy');
        document.body.removeChild(textarea);
      }
      setCopiedColor(color);
      setTimeout(() => setCopiedColor(null), 2000);
    } catch (err) {
      console.error('Failed to copy color:', err);
    }
  };

  const handleDrag = (e: React.DragEvent) => {
    e.preventDefault();
    e.stopPropagation();
    if (e.type === 'dragenter' || e.type === 'dragover') {
      setDragActive(true);
    } else if (e.type === 'dragleave') {
      setDragActive(false);
    }
  };

  const handleDrop = async (e: React.DragEvent) => {
    e.preventDefault();
    e.stopPropagation();
    setDragActive(false);

    if (e.dataTransfer.files && e.dataTransfer.files[0]) {
      await handleFile(e.dataTransfer.files[0]);
    }
  };

  const handleFile = async (file: File) => {
    setUploadError(null);
    setUploadSuccess(null);

    if (!file.name.endsWith('.toml')) {
      setUploadError('Only TOML theme files are supported');
      return;
    }

    if (file.size > 1024 * 1024) {
      setUploadError('Theme file too large (max 1MB)');
      return;
    }

    setLoading(true);
    try {
      const result = await themeService.uploadTheme(file);
      
      // Wait a moment for the server to save the file
      await new Promise(resolve => setTimeout(resolve, 100));
      
      // Reload themes from server to ensure we have the saved version
      const themeList = await themeService.loadThemes();
      setThemes(themeList);
      
      setUploadSuccess(`Theme "${result.meta.name}" uploaded successfully`);
      setTimeout(() => setUploadSuccess(null), 5000);
    } catch (error: any) {
      setUploadError(error.message);
    } finally {
      setLoading(false);
    }
  };

  const handleDelete = (themeId: string, themeName: string) => {
    const isSystemTheme = ['dark-default', 'light-default'].includes(themeId);

    if (isSystemTheme) {
      setUploadError('System themes cannot be deleted');
      setTimeout(() => setUploadError(null), 5000);
      return;
    }

    if (!isAuthenticated) {
      setUploadError('Authentication required to delete custom themes');
      setTimeout(() => setUploadError(null), 5000);
      return;
    }
    setThemePendingDeletion({ id: themeId, name: themeName });
  };

  const executeDeleteTheme = async () => {
    if (!themePendingDeletion) {
      return;
    }

    const { id: themeId, name: themeName } = themePendingDeletion;

    setLoading(true);
    try {
      const response = await fetch(`${API_BASE}/theme/${themeId}`, {
        method: 'DELETE',
        headers: authService.getAuthHeaders()
      });

      const result = await response.json();

      if (response.ok && result.success) {
        await loadThemes();

        if (currentTheme === themeId) {
          handleThemeChange('dark-default');
        }

        const deletedFiles = result.filesDeleted?.join(', ') || 'theme files';
        setUploadSuccess(`Theme "${themeName}" deleted successfully (removed: ${deletedFiles})`);
        setTimeout(() => setUploadSuccess(null), 5000);
      } else if (response.status === 404) {
        setUploadError(`Theme "${themeName}" not found on server. ${result.details || ''}`);

        setThemes((prev) => prev.filter((t) => t.meta.id !== themeId));

        if (currentTheme === themeId) {
          handleThemeChange('dark-default');
        }

        setTimeout(() => setUploadError(null), 10000);
      } else {
        const errorMsg = result.error || result.message || 'Failed to delete theme';
        const details = result.details ? ` Details: ${result.details}` : '';
        setUploadError(`${errorMsg}${details}`);
        setTimeout(() => setUploadError(null), 7000);
      }
    } catch (error: any) {
      console.error('Delete request failed:', error);
      setUploadError(`Failed to delete theme: ${error.message || 'Network error'}`);
      setTimeout(() => setUploadError(null), 7000);
    } finally {
      setLoading(false);
      setThemePendingDeletion(null);
    }
  };

  const cleanupThemes = () => {
    if (!isAuthenticated) {
      setUploadError('Authentication required to clean up themes');
      setTimeout(() => setUploadError(null), 5000);
      return;
    }

    setShowCleanupModal(true);
  };

  const executeCleanupThemes = async () => {
    if (!isAuthenticated) {
      setUploadError('Authentication required to clean up themes');
      setTimeout(() => setUploadError(null), 5000);
      setShowCleanupModal(false);
      return;
    }

    setLoading(true);

    try {
      const response = await fetch(`${API_BASE}/theme/cleanup`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          ...authService.getAuthHeaders()
        }
      });

      if (!response.ok) {
        throw new Error('Failed to cleanup themes');
      }

      const result = await response.json();

      await loadThemes();

      const remainingThemeIds = themes.map((t) => t.meta.id);
      if (!remainingThemeIds.includes(currentTheme)) {
        handleThemeChange('dark-default');
      }

      setUploadSuccess(result.message || 'All custom themes have been deleted');
      setTimeout(() => setUploadSuccess(null), 5000);
    } catch (error: any) {
      setUploadError('Failed to cleanup themes: ' + error.message);
      setTimeout(() => setUploadError(null), 5000);
    } finally {
      setLoading(false);
      setShowCleanupModal(false);
    }
  };

  const handleEditTheme = (theme: Theme) => {
    if (!isAuthenticated) {
      setUploadError('Authentication required to edit themes');
      setTimeout(() => setUploadError(null), 5000);
      return;
    }

    if (isSystemTheme(theme.meta.id)) {
      setUploadError('System themes cannot be edited');
      setTimeout(() => setUploadError(null), 5000);
      return;
    }

    // Always use the most recent version from the themes array
    const latestTheme = themes.find(t => t.meta.id === theme.meta.id) || theme;
    

    // Reset the color editing started flags
    setColorEditingStarted({});
    setEditingTheme(latestTheme);
    
    // Start with all theme colors
    const themeData = {
      name: latestTheme.meta.name,
      description: latestTheme.meta.description || '',
      author: latestTheme.meta.author || '',
      version: latestTheme.meta.version || '1.0.0',
      isDark: latestTheme.meta.isDark !== false,
      ...latestTheme.colors,
      customCSS: latestTheme.css?.content || ''
    };
    
    // Only add defaults if the properties don't exist
    if (!latestTheme.colors.dragHandleColor) {
      themeData.dragHandleColor = latestTheme.meta.isDark ? '#6b7280' : '#9ca3af';
    }
    if (!latestTheme.colors.dragHandleHover) {
      themeData.dragHandleHover = latestTheme.meta.isDark ? '#60a5fa' : '#2563eb';
    }
    
    setEditedTheme(themeData);
    setEditModalOpen(true);
  };

  const handleSaveEditedTheme = async () => {
    if (!isAuthenticated || !editingTheme) return;

    if (!editedTheme.name.trim()) {
      setUploadError('Theme name is required');
      setTimeout(() => setUploadError(null), 5000);
      return;
    }


    // Create a clean colors object without meta properties
    const cleanColors: any = {};
    Object.entries(editedTheme).forEach(([key, value]) => {
      if (!['name', 'description', 'author', 'version', 'isDark', 'customCSS'].includes(key)) {
        cleanColors[key] = value;
      }
    });

    const updatedTheme: Theme = {
      meta: {
        id: editingTheme.meta.id,
        name: editedTheme.name,
        description: editedTheme.description,
        author: editedTheme.author,
        version: editedTheme.version,
        isDark: editedTheme.isDark
      },
      colors: cleanColors,
      css: editedTheme.customCSS ? { content: editedTheme.customCSS } : undefined
    };
    

    const tomlContent = themeService.exportTheme(updatedTheme);
    const blob = new Blob([tomlContent], { type: 'text/plain' });
    const file = new File([blob], `${updatedTheme.meta.id}.toml`, { type: 'text/plain' });

    setLoading(true);
    try {
      // Upload the theme file
      await themeService.uploadTheme(file);
      
      // Wait a moment for the server to save the file
      await new Promise(resolve => setTimeout(resolve, 100));
      
      // Reload themes from server to get the actual saved version
      const themeList = await themeService.loadThemes();
      setThemes(themeList);
      
      // Find the saved theme and apply it if it's current
      const savedTheme = themeList.find(t => t.meta.id === updatedTheme.meta.id);
      if (savedTheme) {
        if (currentTheme === editingTheme.meta.id) {
          themeService.applyTheme(savedTheme);
        }
        setUploadSuccess(`Theme "${savedTheme.meta.name}" updated successfully`);
      } else {
        throw new Error('Theme was uploaded but not found on server');
      }
      
      // Clear color history and reset flags for this theme after successful save
      Object.keys(editedTheme).forEach(key => {
        const historyKey = `color_history_${editingTheme.meta.id}_${key}`;
        localStorage.removeItem(historyKey);
      });
      setColorEditingStarted({});
      
      // Close modal
      setEditModalOpen(false);
      setEditingTheme(null);
      setEditedTheme({});
      setTimeout(() => setUploadSuccess(null), 5000);
      
    } catch (error: any) {
      setUploadError(error.message || 'Failed to update theme');
      setLoading(false);
      return; // Exit early on error
    }
    
    setLoading(false);
  };

  const handleCreateTheme = async () => {
    if (!isAuthenticated) {
      setUploadError('Authentication required to create themes');
      return;
    }

    if (!newTheme.name.trim()) {
      setUploadError('Theme name is required');
      setTimeout(() => setUploadError(null), 5000);
      return;
    }

    const theme: Theme = {
      meta: {
        id: newTheme.name
          .toLowerCase()
          .replace(/\s+/g, '-')
          .replace(/[^a-z0-9-]/g, ''),
        name: newTheme.name,
        description: newTheme.description,
        author: newTheme.author,
        version: newTheme.version,
        isDark: newTheme.isDark
      },
      colors: { ...newTheme },
      css: newTheme.customCSS ? { content: newTheme.customCSS } : undefined
    };

    delete theme.colors.name;
    delete theme.colors.description;
    delete theme.colors.author;
    delete theme.colors.version;
    delete theme.colors.isDark;
    delete theme.colors.customCSS;

    const tomlContent = themeService.exportTheme(theme);
    const blob = new Blob([tomlContent], { type: 'text/plain' });
    const file = new File([blob], `${theme.meta.id}.toml`, { type: 'text/plain' });

    setLoading(true);
    try {
      await themeService.uploadTheme(file);
      
      // Wait a moment for the server to save the file
      await new Promise(resolve => setTimeout(resolve, 100));
      
      // Reload themes from server to ensure we have the saved version
      const themeList = await themeService.loadThemes();
      setThemes(themeList);
      
      // Find and apply the created theme
      const savedTheme = themeList.find(t => t.meta.id === theme.meta.id);
      if (savedTheme) {
        themeService.applyTheme(savedTheme);
        setCurrentTheme(savedTheme.meta.id);
      }
      
      setCreateModalOpen(false);
      
      // Clear create color history and reset flags after successful save
      Object.keys(newTheme).forEach(key => {
        localStorage.removeItem(`color_history_create_${key}`);
      });
      setCreateColorEditingStarted({});

      setNewTheme({
        name: '',
        description: '',
        author: '',
        version: '1.0.0',
        isDark: true,

        primaryColor: '#3b82f6',
        secondaryColor: '#8b5cf6',
        accentColor: '#06b6d4',

        bgPrimary: '#111827',
        bgSecondary: '#1f2937',
        bgTertiary: '#374151',
        bgHover: '#4b5563',

        textPrimary: '#ffffff',
        textSecondary: '#d1d5db',
        textMuted: '#9ca3af',
        textAccent: '#60a5fa',
        dragHandleColor: '#6b7280',
        dragHandleHover: '#60a5fa',

        borderPrimary: '#374151',
        borderSecondary: '#4b5563',
        borderFocus: '#3b82f6',

        navBg: '#1f2937',
        navBorder: '#374151',
        navTabActive: '#3b82f6',
        navTabInactive: '#9ca3af',
        navTabHover: '#ffffff',
        navTabActiveBorder: '#3b82f6',
        navMobileMenuBg: '#1f2937',
        navMobileItemHover: '#374151',

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

        steamColor: '#1e40af',
        epicColor: '#7c3aed',
        originColor: '#ea580c',
        blizzardColor: '#0891b2',
        wsusColor: '#16a34a',
        riotColor: '#dc2626',

        cardBg: '#1f2937',
        cardBorder: '#374151',
        cardOutline: '#3b82f6',
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

        // Access indicators
        publicAccessBg: 'rgba(16, 185, 129, 0.2)',
        publicAccessText: '#34d399',
        publicAccessBorder: 'rgba(16, 185, 129, 0.3)',
        securedAccessBg: 'rgba(245, 158, 11, 0.2)',
        securedAccessText: '#fbbf24',
        securedAccessBorder: 'rgba(245, 158, 11, 0.3)',

        customCSS: ''
      });

      setUploadSuccess(`Theme "${theme.meta.name}" created successfully`);
      setTimeout(() => setUploadSuccess(null), 5000);
    } catch (error: any) {
      setUploadError(error.message || 'Failed to create theme');
    } finally {
      setLoading(false);
    }
  };

  const loadPresetColors = (preset: 'dark' | 'light') => {
    if (preset === 'dark') {
      setNewTheme((prev: any) => ({
        ...prev,
        isDark: true,
        bgPrimary: '#111827',
        bgSecondary: '#1f2937',
        bgTertiary: '#374151',
        bgHover: '#4b5563',
        textPrimary: '#ffffff',
        textSecondary: '#d1d5db',
        textMuted: '#9ca3af',
        dragHandleColor: '#6b7280',
        dragHandleHover: '#00ff00',
        borderPrimary: '#374151',
        borderSecondary: '#4b5563',
        navBg: '#1f2937',
        navBorder: '#374151',
        navTabActive: '#3b82f6',
        navTabInactive: '#9ca3af',
        navTabHover: '#ffffff',
        navTabActiveBorder: '#3b82f6',
        navMobileMenuBg: '#1f2937',
        navMobileItemHover: '#374151',
        cardBg: '#1f2937',
        cardBorder: '#374151',
        cardOutline: '#3b82f6',
        inputBg: '#374151',
        inputBorder: '#4b5563',
        progressBg: '#374151',
        successBg: '#064e3b',
        successText: '#34d399',
        warningBg: '#78350f',
        warningText: '#fbbf24',
        errorBg: '#7f1d1d',
        errorText: '#fca5a5',
        infoBg: '#1e3a8a',
        infoText: '#93c5fd',
        chartBorderColor: '#1f2937',
        chartGridColor: '#374151',
        chartTextColor: '#9ca3af',
        scrollbarTrack: '#374151',
        scrollbarThumb: '#6B7280',
        scrollbarHover: '#9CA3AF'
      }));
    } else {
      setNewTheme((prev: any) => ({
        ...prev,
        isDark: false,
        bgPrimary: '#ffffff',
        bgSecondary: '#f9fafb',
        bgTertiary: '#f3f4f6',
        bgHover: '#e5e7eb',
        textPrimary: '#111827',
        textSecondary: '#374151',
        textMuted: '#6b7280',
        dragHandleColor: '#9ca3af',
        dragHandleHover: '#2563eb',
        borderPrimary: '#e5e7eb',
        borderSecondary: '#d1d5db',
        navBg: '#f9fafb',
        navBorder: '#e5e7eb',
        navTabActive: '#3b82f6',
        navTabInactive: '#6b7280',
        navTabHover: '#111827',
        navTabActiveBorder: '#3b82f6',
        navMobileMenuBg: '#f9fafb',
        navMobileItemHover: '#e5e7eb',
        cardBg: '#ffffff',
        cardBorder: '#e5e7eb',
        cardOutline: '#3b82f6',
        buttonBg: '#3b82f6',
        buttonHover: '#2563eb',
        buttonText: '#ffffff',  // White text on blue button for 8.6:1 contrast
        inputBg: '#ffffff',
        inputBorder: '#d1d5db',
        inputFocus: '#3b82f6',
        checkboxAccent: '#3b82f6',
        checkboxBorder: '#d1d5db',
        checkboxBg: '#ffffff',
        checkboxCheckmark: '#ffffff',
        checkboxShadow: 'none',
        checkboxHoverShadow: 'none',
        checkboxHoverBg: '#f3f4f6',
        checkboxFocus: '#3b82f6',
        sliderAccent: '#3b82f6',
        sliderThumb: '#3b82f6',
        sliderTrack: '#e5e7eb',
        badgeBg: '#3b82f6',
        badgeText: '#ffffff',
        progressBar: '#3b82f6',
        progressBg: '#e5e7eb',
        successBg: '#d1fae5',
        successText: '#065f46',
        warningBg: '#fef3c7',
        warningText: '#92400e',
        errorBg: '#fee2e2',
        errorText: '#991b1b',
        infoBg: '#dbeafe',
        infoText: '#1e40af',
        chartBorderColor: '#e5e7eb',
        chartGridColor: '#d1d5db',
        chartTextColor: '#6b7280',
        scrollbarTrack: '#f3f4f6',
        scrollbarThumb: '#9ca3af',
        scrollbarHover: '#6b7280'
      }));
    }
  };

  const exportTheme = (theme: Theme) => {
    const tomlContent = themeService.exportTheme(theme);
    const blob = new Blob([tomlContent], { type: 'text/plain' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `${theme.meta.id}.toml`;
    a.click();
    URL.revokeObjectURL(url);
  };

  const downloadSampleTheme = () => {
    const sampleToml = `# Sample Theme for LANCache Manager
[meta]
name = "My Custom Theme"
id = "my-custom-theme"
description = "A beautiful custom theme"
author = "Your Name"
version = "1.0.0"
isDark = true

[colors]
# Core colors
primaryColor = "#3b82f6"
secondaryColor = "#8b5cf6"
accentColor = "#06b6d4"

# Backgrounds
bgPrimary = "#111827"
bgSecondary = "#1f2937"
bgTertiary = "#374151"
bgHover = "#4b5563"

# Text
textPrimary = "#ffffff"
textSecondary = "#d1d5db"
textMuted = "#9ca3af"
textAccent = "#60a5fa"
dragHandleColor = "#6b7280"
dragHandleHover = "#60a5fa"

# Borders
borderPrimary = "#374151"
borderSecondary = "#4b5563"
borderFocus = "#3b82f6"

# Navigation colors
navBg = "#1f2937"
navBorder = "#374151"
navTabActive = "#3b82f6"
navTabInactive = "#9ca3af"
navTabHover = "#ffffff"
navTabActiveBorder = "#3b82f6"
navMobileMenuBg = "#1f2937"
navMobileItemHover = "#374151"

# Hit Rate & Status Badge colors
hitRateHighBg = "#064e3b"
hitRateHighText = "#34d399"
hitRateMediumBg = "#1e3a8a"
hitRateMediumText = "#93c5fd"
hitRateLowBg = "#ea580c"
hitRateLowText = "#fb923c"
statusWarningBg = "#78350f"
statusWarningText = "#fbbf24"

# Action Button colors
actionResetBg = "#f59e0b"
actionResetHover = "#d97706"
actionProcessBg = "#10b981"
actionProcessHover = "#059669"
actionDeleteBg = "#ef4444"
actionDeleteHover = "#dc2626"

# Status colors
success = "#10b981"
successBg = "#064e3b"
successText = "#34d399"
warning = "#f59e0b"
warningBg = "#78350f"
warningText = "#fbbf24"
error = "#ef4444"
errorBg = "#7f1d1d"
errorText = "#fca5a5"
info = "#3b82f6"
infoBg = "#1e3a8a"
infoText = "#93c5fd"

# Service colors
steamColor = "#1e40af"
epicColor = "#7c3aed"
originColor = "#ea580c"
blizzardColor = "#0891b2"
wsusColor = "#16a34a"
riotColor = "#dc2626"

# Components
cardBg = "#1f2937"
cardBorder = "#374151"
cardOutline = "#3b82f6"
buttonBg = "#3b82f6"
buttonHover = "#2563eb"
buttonText = "#ffffff"
inputBg = "#374151"
inputBorder = "#4b5563"
inputFocus = "#3b82f6"
badgeBg = "#3b82f6"
badgeText = "#ffffff"
progressBar = "#3b82f6"
progressBg = "#374151"

# Icon backgrounds (solid colors, no gradients)
iconBgBlue = "#3b82f6"
iconBgGreen = "#10b981"
iconBgEmerald = "#10b981"
iconBgPurple = "#8b5cf6"
iconBgIndigo = "#6366f1"
iconBgOrange = "#f97316"
iconBgYellow = "#eab308"
iconBgCyan = "#06b6d4"
iconBgRed = "#ef4444"

# Chart colors
chartColor1 = "#3b82f6"
chartColor2 = "#10b981"
chartColor3 = "#f59e0b"
chartColor4 = "#ef4444"
chartColor5 = "#8b5cf6"
chartColor6 = "#06b6d4"
chartColor7 = "#f97316"
chartColor8 = "#ec4899"
chartBorderColor = "#1f2937"
chartGridColor = "#374151"
chartTextColor = "#9ca3af"
chartCacheHitColor = "#10b981"
chartCacheMissColor = "#f59e0b"

[css]
content = """
/* Add any custom CSS here */
"""`;

    const blob = new Blob([sampleToml], { type: 'text/plain' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = 'sample-theme.toml';
    a.click();
    URL.revokeObjectURL(url);
  };

  const isSystemTheme = (themeId: string) => ['dark-default', 'light-default'].includes(themeId);

  return (
    <>
      <Card>
        <div className="flex items-center justify-between mb-4">
          <div className="flex items-center space-x-2">
            <Palette className="w-5 h-5 text-themed-accent" />
            <h3 className="text-lg font-semibold text-themed-primary">Theme Management</h3>
          </div>
          <div className="flex items-center space-x-2">
            {isAuthenticated ? (
              <>
                <button
                  onClick={() => setCreateModalOpen(true)}
                  className="p-2 rounded-lg transition-colors"
                  style={{
                    color: 'var(--theme-text-muted)',
                    backgroundColor: 'transparent'
                  }}
                  onMouseEnter={(e) =>
                    (e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)')
                  }
                  onMouseLeave={(e) => (e.currentTarget.style.backgroundColor = 'transparent')}
                  title="Create new theme"
                >
                  <Plus className="w-4 h-4" />
                </button>
                <button
                  onClick={cleanupThemes}
                  disabled={loading}
                  className="p-2 rounded-lg transition-colors"
                  style={{
                    color: 'var(--theme-text-muted)',
                    backgroundColor: 'transparent'
                  }}
                  onMouseEnter={(e) =>
                    (e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)')
                  }
                  onMouseLeave={(e) => (e.currentTarget.style.backgroundColor = 'transparent')}
                  title="Delete all custom themes"
                >
                  <Sparkles className="w-4 h-4" />
                </button>
              </>
            ) : (
              <button
                disabled
                className="p-2 rounded-lg transition-colors opacity-50 cursor-not-allowed"
                style={{
                  color: 'var(--theme-text-muted)',
                  backgroundColor: 'transparent'
                }}
                title="Authentication required to create themes"
              >
                <Lock className="w-4 h-4" />
              </button>
            )}
            <button
              onClick={() => loadThemes()}
              disabled={loading}
              className="p-2 rounded-lg transition-colors"
              style={{
                color: 'var(--theme-text-muted)',
                backgroundColor: 'transparent'
              }}
              onMouseEnter={(e) =>
                (e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)')
              }
              onMouseLeave={(e) => (e.currentTarget.style.backgroundColor = 'transparent')}
              title="Refresh themes"
            >
              <RefreshCw className={`w-4 h-4 ${loading ? 'animate-spin' : ''}`} />
            </button>
          </div>
        </div>

        {/* Tab Navigation */}
        <div className="flex gap-2 mb-6 border-b" style={{ borderColor: 'var(--theme-border)' }}>
          <button
            onClick={() => setActiveTab('themes')}
            className={`px-4 py-2 font-medium transition-colors ${
              activeTab === 'themes'
                ? 'text-themed-accent'
                : 'text-themed-muted hover:text-themed-primary'
            }`}
            style={activeTab === 'themes' ? { borderBottom: '2px solid var(--theme-primary)' } : {}}
          >
            <Layers className="w-4 h-4 inline-block mr-2" />
            Themes ({themes.length})
          </button>
          <button
            onClick={() => setActiveTab('customize')}
            className={`px-4 py-2 font-medium transition-colors ${
              activeTab === 'customize'
                ? 'text-themed-accent'
                : 'text-themed-muted hover:text-themed-primary'
            }`}
            style={activeTab === 'customize' ? { borderBottom: '2px solid var(--theme-primary)' } : {}}
          >
            <Brush className="w-4 h-4 inline-block mr-2" />
            Customize
          </button>
        </div>

        {activeTab === 'themes' ? (
          <>
            {/* Active Theme Selector */}
            <div className="mb-6 p-4 rounded-lg bg-themed-tertiary">
              <label className="block text-sm font-medium mb-2 text-themed-secondary">
                Active Theme
              </label>
              <EnhancedDropdown
                options={themes.map((theme) => ({
                  value: theme.meta.id,
                  label: `${theme.meta.name}${theme.meta.author && theme.meta.author !== 'System' ? ` by ${theme.meta.author}` : ''}${isSystemTheme(theme.meta.id) ? ' (System)' : ''}${previewTheme === theme.meta.id ? ' (Preview)' : ''}`
                }))}
                value={previewTheme || currentTheme}
                onChange={handleThemeChange}
                placeholder="Select a theme"
                className="w-full"
              />
              {previewTheme && (
                <p className="text-xs mt-2 text-themed-warning">
                  Preview mode active. Select a theme to apply it permanently.
                </p>
              )}
            </div>

            {/* Sharp Corners Toggle */}
            <div className="mb-6 p-4 rounded-lg bg-themed-tertiary">
              <div className="flex items-center justify-between">
                <div>
                  <span className="text-sm font-medium text-themed-secondary">Sharp Corners</span>
                  <p className="text-xs text-themed-muted mt-1">
                    Use sharp corners instead of rounded ones throughout the interface
                  </p>
                </div>
                <Checkbox
                  checked={sharpCorners}
                  onChange={(e) => {
                    const newValue = e.target.checked;
                    setSharpCorners(newValue);
                    themeService.setSharpCorners(newValue);
                  }}
                  label=""
                  className="ml-3"
                />
              </div>
            </div>

            {/* Theme Cards Grid */}
            <div className="mb-6">
              <h4 className="text-sm font-medium mb-3 text-themed-secondary">
                Installed Themes
              </h4>
              <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
                {themes.map((theme) => {
                  const isActive = currentTheme === theme.meta.id && !previewTheme;
                  const isPreviewing = previewTheme === theme.meta.id;
                  const isSystem = isSystemTheme(theme.meta.id);

                  return (
                    <div
                      key={theme.meta.id}
                      className="rounded-lg p-4 transition-all hover:shadow-lg themed-card relative"
                      style={{
                        border: `2px solid ${isActive ? 'var(--theme-primary)' :
                                    isPreviewing ? 'var(--theme-warning)' :
                                    'var(--theme-border-primary)'}`
                      }}
                    >
                      {/* Theme Header */}
                      <div className="flex items-start justify-between mb-3">
                        <div className="flex-1">
                          <div className="flex items-center gap-2 mb-1">
                            <span className="font-semibold text-themed-primary">{theme.meta.name}</span>
                            {theme.meta.isDark ? (
                              <Moon className="w-3 h-3 text-themed-muted" />
                            ) : (
                              <Sun className="w-3 h-3 text-themed-warning" />
                            )}
                            {isActive && (
                              <span className="px-2 py-0.5 text-xs rounded themed-button-primary">
                                Active
                              </span>
                            )}
                            {isPreviewing && (
                              <span className="px-2 py-0.5 text-xs rounded bg-themed-warning text-themed-primary">
                                Preview
                              </span>
                            )}
                            {isSystem && (
                              <Lock className="w-3 h-3 text-themed-muted" />
                            )}
                          </div>
                          {theme.meta.description && (
                            <p className="text-xs text-themed-muted mb-1">{theme.meta.description}</p>
                          )}
                          <div className="flex items-center gap-3 text-xs text-themed-muted">
                            {theme.meta.author && <span>by {theme.meta.author}</span>}
                            {theme.meta.version && <span>v{theme.meta.version}</span>}
                          </div>
                        </div>

                        {/* Action Menu Button */}
                        <div className="relative">
                          <button
                            onClick={() => setThemeActionMenu(themeActionMenu === theme.meta.id ? null : theme.meta.id)}
                            className="p-1 rounded hover:bg-themed-hover transition-colors"
                          >
                            <MoreVertical className="w-4 h-4 text-themed-muted" />
                          </button>

                          {/* Dropdown Menu */}
                          {themeActionMenu === theme.meta.id && (
                            <div className="absolute right-0 mt-1 w-40 bg-themed-secondary rounded-lg shadow-lg z-10" style={{
                              border: '1px solid var(--theme-border-primary)'
                            }}>
                              {!isActive && (
                                <button
                                  onClick={() => {
                                    handleThemeChange(theme.meta.id);
                                    setThemeActionMenu(null);
                                  }}
                                  className="w-full px-3 py-2 text-left text-sm hover:bg-themed-hover flex items-center gap-2"
                                >
                                  <Check className="w-3 h-3" />
                                  Apply Theme
                                </button>
                              )}
                              <button
                                onClick={() => {
                                  handlePreview(theme.meta.id);
                                  setThemeActionMenu(null);
                                }}
                                className="w-full px-3 py-2 text-left text-sm hover:bg-themed-hover flex items-center gap-2"
                              >
                                {isPreviewing ? <EyeOff className="w-3 h-3" /> : <Eye className="w-3 h-3" />}
                                {isPreviewing ? 'Stop Preview' : 'Preview'}
                              </button>
                              {!isSystem && isAuthenticated && (
                                <button
                                  onClick={() => {
                                    handleEditTheme(theme);
                                    setThemeActionMenu(null);
                                  }}
                                  className="w-full px-3 py-2 text-left text-sm hover:bg-themed-hover flex items-center gap-2"
                                >
                                  <Edit className="w-3 h-3" />
                                  Edit Theme
                                </button>
                              )}
                              <button
                                onClick={() => {
                                  exportTheme(theme);
                                  setThemeActionMenu(null);
                                }}
                                className="w-full px-3 py-2 text-left text-sm hover:bg-themed-hover flex items-center gap-2"
                              >
                                <Download className="w-3 h-3" />
                                Export
                              </button>
                              {!isSystem && isAuthenticated && (
                                <>
                                  <div className="border-t my-1" style={{ borderColor: 'var(--theme-border-primary)' }} />
                                  <button
                                    onClick={() => {
                                      handleDelete(theme.meta.id, theme.meta.name);
                                      setThemeActionMenu(null);
                                    }}
                                    className="w-full px-3 py-2 text-left text-sm flex items-center gap-2 transition-colors"
                                    style={{
                                      color: 'var(--theme-error-text)',
                                      backgroundColor: 'transparent'
                                    }}
                                    onMouseEnter={(e) => {
                                      e.currentTarget.style.backgroundColor = 'var(--theme-error-bg)';
                                    }}
                                    onMouseLeave={(e) => {
                                      e.currentTarget.style.backgroundColor = 'transparent';
                                    }}
                                  >
                                    <Trash2 className="w-3 h-3" />
                                    Delete
                                  </button>
                                </>
                              )}
                            </div>
                          )}
                        </div>
                      </div>

                      {/* Color Preview Strip */}
                      <div className="flex gap-1 mt-3">
                        <div
                          className="flex-1 h-6 rounded"
                          style={{ backgroundColor: theme.colors.primaryColor || '#3b82f6' }}
                          title="Primary"
                        />
                        <div
                          className="flex-1 h-6 rounded"
                          style={{ backgroundColor: theme.colors.secondaryColor || '#8b5cf6' }}
                          title="Secondary"
                        />
                        <div
                          className="flex-1 h-6 rounded"
                          style={{ backgroundColor: theme.colors.accentColor || '#06b6d4' }}
                          title="Accent"
                        />
                        <div
                          className="flex-1 h-6 rounded"
                          style={{ backgroundColor: theme.colors.bgPrimary || '#111827' }}
                          title="Background"
                        />
                        <div
                          className="flex-1 h-6 rounded"
                          style={{
                            border: '1px solid var(--theme-border)',
                            backgroundColor: theme.colors.textPrimary || '#ffffff'
                          }}
                          title="Text"
                        />
                      </div>
                    </div>
                  );
                })}
              </div>
            </div>

            {isAuthenticated && (
              <>
                <div className="mb-4">
              <h4 className="text-sm font-medium mb-3 text-themed-secondary">
                Upload Custom Theme
              </h4>
              <div
                className={`border-dashed rounded-lg p-8 text-center transition-colors ${
                  dragActive ? 'bg-purple-900 bg-opacity-20' : ''
                }`}
                style={{
                  border: dragActive ? '2px dashed var(--theme-primary)' : '2px dashed var(--theme-border-secondary)'
                }}
                onDragEnter={handleDrag}
                onDragLeave={handleDrag}
                onDragOver={handleDrag}
                onDrop={handleDrop}
              >
                <Upload className="w-12 h-12 mx-auto mb-3 text-themed-muted" />
                <p className="mb-2 text-themed-secondary">
                  Drag and drop a theme file here, or click to browse
                </p>
                <p className="text-xs mb-3 text-themed-muted">TOML format, max 1MB</p>
                <input
                  ref={fileInputRef}
                  type="file"
                  accept=".toml"
                  onChange={(e) => e.target.files?.[0] && handleFile(e.target.files[0])}
                  className="hidden"
                />
                <Button
                  variant="filled"
                  color="purple"
                  onClick={() => fileInputRef.current?.click()}
                  disabled={loading}
                  loading={loading}
                >
                  Browse Files
                </Button>
              </div>
                </div>

                <div className="flex justify-center">
                  <Button
                    variant="subtle"
                    leftSection={<Download className="w-4 h-4" />}
                    onClick={downloadSampleTheme}
                  >
                    Download Sample TOML Theme
                  </Button>
                </div>
              </>
            )}

            {!isAuthenticated && (
              <Alert color="yellow">
                Authentication required to create, upload, or delete custom themes
              </Alert>
            )}
          </>
        ) : (
          /* Customize Tab */
          <div className="space-y-4">
            <Alert color="blue">
              <div className="flex items-center gap-2">
                <Info className="w-4 h-4" />
                <span>Select a theme above and click Edit to customize its colors</span>
              </div>
            </Alert>

            <div className="p-4 rounded-lg bg-themed-tertiary">
              <h4 className="text-sm font-semibold text-themed-primary mb-2">Quick Actions</h4>
              <div className="flex gap-2 flex-wrap">
                <Button
                  variant="default"
                  size="sm"
                  leftSection={<Plus className="w-4 h-4" />}
                  onClick={() => setCreateModalOpen(true)}
                  disabled={!isAuthenticated}
                >
                  Create New Theme
                </Button>
                <Button
                  variant="default"
                  size="sm"
                  leftSection={<Download className="w-4 h-4" />}
                  onClick={downloadSampleTheme}
                >
                  Download Sample
                </Button>
                {themes.find(t => t.meta.id === currentTheme) && !isSystemTheme(currentTheme) && (
                  <Button
                    variant="default"
                    size="sm"
                    leftSection={<Edit className="w-4 h-4" />}
                    onClick={() => handleEditTheme(themes.find(t => t.meta.id === currentTheme)!)}
                    disabled={!isAuthenticated}
                  >
                    Edit Current Theme
                  </Button>
                )}
              </div>
            </div>

            <div className="p-4 rounded-lg bg-themed-tertiary">
              <h4 className="text-sm font-semibold text-themed-primary mb-3">Color Groups Overview</h4>
              <div className="text-xs text-themed-muted mb-3">Themes contain {colorGroups.reduce((acc, g) => acc + g.colors.length, 0)} customizable colors organized into groups:</div>
              <div className="grid grid-cols-1 md:grid-cols-2 gap-2">
                {colorGroups.map((group) => {
                  const Icon = group.icon;
                  return (
                    <div key={group.name} className="flex items-start gap-2 text-sm p-2 rounded hover:bg-themed-hover transition-colors">
                      <Icon className="w-4 h-4 text-themed-accent mt-0.5" />
                      <div>
                        <span className="text-themed-primary font-medium capitalize">
                          {group.name.replace(/([A-Z])/g, ' $1').trim()}
                        </span>
                        <span className="text-themed-muted text-xs block">
                          {group.colors.length} colors - {group.description}
                        </span>
                      </div>
                    </div>
                  );
                })}
              </div>
            </div>
          </div>
        )}

        {uploadError && (
          <Alert color="red" withCloseButton onClose={() => setUploadError(null)}>
            {uploadError}
          </Alert>
        )}

        {uploadSuccess && (
        <Alert color="green" withCloseButton onClose={() => setUploadSuccess(null)}>
          {uploadSuccess}
        </Alert>
      )}
      </Card>

      <Modal
        opened={themePendingDeletion !== null}
        onClose={() => {
          if (!loading) {
            setThemePendingDeletion(null);
          }
        }}
        title={
          <div className="flex items-center space-x-3">
            <AlertTriangle className="w-6 h-6 text-themed-warning" />
            <span>Delete Theme</span>
          </div>
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            Delete theme <strong>{themePendingDeletion?.name}</strong>? This will permanently remove the theme files from
            the server.
          </p>

          <Alert color="yellow">
            <p className="text-sm">This action cannot be undone.</p>
          </Alert>

          <div className="flex justify-end space-x-3 pt-2">
            <Button variant="default" onClick={() => setThemePendingDeletion(null)} disabled={loading}>
              Cancel
            </Button>
            <Button
              variant="filled"
              color="red"
              leftSection={<Trash2 className="w-4 h-4" />}
              onClick={executeDeleteTheme}
              loading={loading}
            >
              Delete Theme
            </Button>
          </div>
        </div>
      </Modal>

      <Modal
        opened={showCleanupModal}
        onClose={() => {
          if (!loading) {
            setShowCleanupModal(false);
          }
        }}
        title={
          <div className="flex items-center space-x-3">
            <AlertTriangle className="w-6 h-6 text-themed-warning" />
            <span>Clean Up Custom Themes</span>
          </div>
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            Remove all custom themes, keeping only the built-in defaults? This will permanently delete uploaded theme
            files.
          </p>

          <Alert color="yellow">
            <p className="text-sm">You will need to re-upload any custom themes you want to use after this operation.</p>
          </Alert>

          <div className="flex justify-end space-x-3 pt-2">
            <Button variant="default" onClick={() => setShowCleanupModal(false)} disabled={loading}>
              Cancel
            </Button>
            <Button
              variant="filled"
              color="red"
              leftSection={<Trash2 className="w-4 h-4" />}
              onClick={executeCleanupThemes}
              loading={loading}
            >
              Delete Custom Themes
            </Button>
          </div>
        </div>
      </Modal>

      <Modal
        opened={createModalOpen}
        onClose={() => {
          setCreateModalOpen(false);
          setCreateColorEditingStarted({});
        }}
        title="Create Custom Theme"
        size="xl"
      >
        <div className="space-y-6">
          {/* Theme Metadata */}
          <div className="space-y-4">
            <h4 className="text-sm font-semibold flex items-center gap-2 text-themed-primary">
              <Info className="w-4 h-4" />
              Theme Information
            </h4>
            <div className="grid grid-cols-2 gap-4">
              <div>
                <label className="block text-sm font-medium mb-1 text-themed-secondary">
                  Theme Name *
                </label>
                <input
                  type="text"
                  value={newTheme.name}
                  onChange={(e) => setNewTheme({ ...newTheme, name: e.target.value })}
                  placeholder="My Custom Theme"
                  className="w-full px-3 py-2 focus:outline-none themed-input"
                />
              </div>
              <div>
                <label className="block text-sm font-medium mb-1 text-themed-secondary">
                  Author
                </label>
                <input
                  type="text"
                  value={newTheme.author}
                  onChange={(e) => setNewTheme({ ...newTheme, author: e.target.value })}
                  placeholder="Your Name"
                  className="w-full px-3 py-2 focus:outline-none themed-input"
                />
              </div>
            </div>
            <div>
              <label className="block text-sm font-medium mb-1 text-themed-secondary">
                Description
              </label>
              <input
                type="text"
                value={newTheme.description}
                onChange={(e) => setNewTheme({ ...newTheme, description: e.target.value })}
                placeholder="A beautiful custom theme"
                className="w-full px-3 py-2 rounded focus:outline-none themed-input"
              />
            </div>
            <div className="flex items-center gap-4">
              <Checkbox
                checked={newTheme.isDark}
                onChange={(e) => setNewTheme({ ...newTheme, isDark: e.target.checked })}
                variant="rounded"
                label="Dark Theme"
              />
              <button
                onClick={() => loadPresetColors('dark')}
                className="px-3 py-1 text-xs rounded-lg flex items-center gap-1 bg-themed-tertiary text-themed-secondary"
              >
                <Moon className="w-3 h-3" />
                Load Dark Preset
              </button>
              <button
                onClick={() => loadPresetColors('light')}
                className="px-3 py-1 text-xs rounded-lg flex items-center gap-1 bg-themed-tertiary text-themed-secondary"
              >
                <Sun className="w-3 h-3" />
                Load Light Preset
              </button>
            </div>
          </div>

          {/* Organization Mode Toggle */}
          <div className="flex gap-2">
            <button
              onClick={() => setOrganizationMode('category')}
              className={`flex-1 px-3 py-2 rounded-lg text-sm font-medium transition-all duration-200 ${
                organizationMode === 'category'
                  ? 'bg-primary text-themed-button'
                  : 'bg-themed-tertiary text-themed-secondary hover:bg-themed-hover'
              }`}
            >
              <Layers className="w-4 h-4 inline-block mr-2" />
              By Category
            </button>
            <button
              onClick={() => setOrganizationMode('page')}
              className={`flex-1 px-3 py-2 rounded-lg text-sm font-medium transition-all duration-200 ${
                organizationMode === 'page'
                  ? 'bg-primary text-themed-button'
                  : 'bg-themed-tertiary text-themed-secondary hover:bg-themed-hover'
              }`}
            >
              <Layout className="w-4 h-4 inline-block mr-2" />
              By Page
            </button>
          </div>

          {/* Page Selector (when in page mode) */}
          <div className={`transition-all duration-300 overflow-hidden ${
            organizationMode === 'page' ? 'max-h-40 opacity-100 mt-4' : 'max-h-0 opacity-0'
          }`}>
            <label className="block text-sm font-medium text-themed-primary mb-2">
              Select Page
            </label>
            <div className="grid grid-cols-3 gap-2">
              {pageDefinitions.map((page) => {
                const Icon = page.icon;
                return (
                  <button
                    key={page.name}
                    onClick={() => setSelectedPage(page.name)}
                    className={`px-3 py-2 rounded-lg text-sm font-medium transition-all duration-200 flex items-center gap-2 ${
                      selectedPage === page.name
                        ? 'bg-primary'
                        : 'bg-themed-tertiary text-themed-secondary hover:bg-themed-hover'
                    }`}
                    style={{
                      color: selectedPage === page.name
                        ? 'var(--theme-button-text)'
                        : undefined
                    }}
                    title={page.description}
                  >
                    <Icon className="w-4 h-4" />
                    {page.label}
                  </button>
                );
              })}
            </div>
          </div>

          {/* Search Bar */}
          <div className="relative mt-4">
            <Search className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-themed-muted" />
            <input
              type="text"
              value={createSearchQuery}
              onChange={(e) => setCreateSearchQuery(e.target.value)}
              placeholder="Search colors... (e.g., 'alert', 'header', 'button', 'background')"
              className="w-full pl-10 pr-10 py-2 themed-input"
            />
            {createSearchQuery && (
              <button
                onClick={() => setCreateSearchQuery('')}
                className="absolute right-3 top-1/2 -translate-y-1/2 text-themed-muted hover:text-themed-primary"
              >
                <X className="w-4 h-4" />
              </button>
            )}
          </div>


          {/* Color Groups */}
          <div className="space-y-4 max-h-96 overflow-y-auto">
            {getFilteredGroups(colorGroups, createSearchQuery).map((group) => {
              const Icon = group.icon;
              const isExpanded = expandedGroups.includes(group.name) || createSearchQuery.trim() !== '';

              return (
                <div
                  key={group.name}
                  className="themed-card rounded-lg"
                  style={{ border: '1px solid var(--theme-border)' }}
                >
                  <button
                    onClick={() => toggleGroup(group.name)}
                    className="w-full px-4 py-3 flex items-center justify-between hover:bg-opacity-50"
                    style={{
                      backgroundColor: isExpanded ? 'var(--theme-bg-tertiary)' : 'transparent'
                    }}
                  >
                    <div className="flex items-center gap-3">
                      <Icon className="w-4 h-4 text-themed-accent" />
                      <div className="text-left">
                        <h5 className="text-sm font-semibold capitalize text-themed-primary">
                          {group.name.replace(/([A-Z])/g, ' $1').trim()}
                        </h5>
                        <p className="text-xs text-themed-muted">{group.description}</p>
                      </div>
                    </div>
                    {isExpanded ? (
                      <ChevronDown className="w-4 h-4" />
                    ) : (
                      <ChevronRight className="w-4 h-4" />
                    )}
                  </button>

                  {isExpanded && (
                    <div
                      className="p-4 themed-card space-y-4"
                      style={{ borderTop: '1px solid var(--theme-border)' }}
                    >
                      {group.colors.map((color) => (
                        <div key={color.key} className="space-y-2">
                          <div className="flex items-center justify-between">
                            <div className="flex-1">
                              <label className="block text-sm font-medium text-themed-primary">
                                {color.label}
                              </label>
                              <p className="text-xs text-themed-muted">{color.description}</p>
                              <div className="flex flex-wrap gap-1 mt-1">
                                {color.affects.map((item, idx) => (
                                  <span
                                    key={idx}
                                    className="text-xs px-1.5 py-0.5 rounded"
                                    style={{
                                      backgroundColor: 'var(--theme-bg-hover)',
                                      color: 'var(--theme-text-secondary)'
                                    }}
                                  >
                                    {item}
                                  </span>
                                ))}
                              </div>
                            </div>
                            <div className="flex items-center gap-2">
                              {(() => {
                                // Use current theme color as fallback instead of black
                                const computedStyle = getComputedStyle(document.documentElement);
                                // Map color keys to CSS variable names
                                const cssVarMap: Record<string, string> = {
                                  primaryColor: '--theme-primary',
                                  secondaryColor: '--theme-secondary',
                                  accentColor: '--theme-accent',
                                  bgPrimary: '--theme-bg-primary',
                                  bgSecondary: '--theme-bg-secondary',
                                  bgTertiary: '--theme-bg-tertiary',
                                  bgHover: '--theme-bg-hover',
                                  textPrimary: '--theme-text-primary',
                                  textSecondary: '--theme-text-secondary',
                                  textMuted: '--theme-text-muted',
                                  textAccent: '--theme-text-accent',
                                  borderPrimary: '--theme-border-primary',
                                  borderSecondary: '--theme-border-secondary',
                                  borderFocus: '--theme-border-focus',
                                  success: '--theme-success',
                                  warning: '--theme-warning',
                                  error: '--theme-error',
                                  info: '--theme-info',
                                  buttonBg: '--theme-button-bg',
                                  buttonHover: '--theme-button-hover',
                                  inputBg: '--theme-input-bg',
                                  inputBorder: '--theme-input-border',
                                  inputFocus: '--theme-input-focus',
                                  checkboxAccent: '--theme-checkbox-accent',
                                  checkboxBorder: '--theme-checkbox-border',
                                  checkboxBg: '--theme-checkbox-bg',
                                  checkboxCheckmark: '--theme-checkbox-checkmark',
                                  checkboxShadow: '--theme-checkbox-shadow',
                                  checkboxHoverShadow: '--theme-checkbox-hover-shadow',
                                  checkboxHoverBg: '--theme-checkbox-hover-bg',
                                  checkboxFocus: '--theme-checkbox-focus',
                                  sliderAccent: '--theme-slider-accent',
                                  sliderThumb: '--theme-slider-thumb',
                                  sliderTrack: '--theme-slider-track',
                                  cardBg: '--theme-card-bg',
                                  cardBorder: '--theme-card-border',
                                  cardOutline: '--theme-card-outline',
                                  badgeBg: '--theme-badge-bg',
                                  progressBar: '--theme-progress-bar',
                                  iconBgBlue: '--theme-icon-bg-blue',
                                  iconBgGreen: '--theme-icon-bg-green',
                                  iconBgPurple: '--theme-icon-bg-purple',
                                  chartColor1: '--theme-chart-color-1',
                                  chartColor2: '--theme-chart-color-2',
                                  chartColor3: '--theme-chart-color-3',
                                  chartColor4: '--theme-chart-color-4',
                                  chartColor5: '--theme-chart-color-5',
                                  chartColor6: '--theme-chart-color-6',
                                };
                                const cssVarName = cssVarMap[color.key] || `--theme-${color.key.replace(/([A-Z])/g, '-$1').toLowerCase()}`;
                                const currentThemeColor = computedStyle.getPropertyValue(cssVarName).trim() || '#3b82f6';
                                const { hex, alpha } = parseColorValue(newTheme[color.key] || currentThemeColor);
                                return (
                                  <>
                                    <div className="relative">
                                      <input
                                        type="color"
                                        value={hex}
                                        onMouseDown={() => handleColorStart(color.key)}
                                        onFocus={() => handleColorStart(color.key)}
                                        onChange={(e) => {
                                          const currentAlpha = parseColorValue(newTheme[color.key] || currentThemeColor).alpha;
                                          updateColorWithAlpha(color.key, e.target.value, currentAlpha, true);
                                        }}
                                        className="w-12 h-8 rounded cursor-pointer"
                                        style={{ backgroundColor: newTheme[color.key] || currentThemeColor }}
                                      />
                                    </div>
                                    {color.supportsAlpha && (
                                      <div className="flex items-center gap-1">
                                        <Percent className="w-3 h-3 text-themed-muted" />
                                        <input
                                          type="range"
                                          min="0"
                                          max="100"
                                          value={Math.round(alpha * 100)}
                                          onChange={(e) => {
                                            const newAlpha = parseInt(e.target.value) / 100;
                                            updateColorWithAlpha(color.key, hex, newAlpha, true);
                                          }}
                                          className="w-16"
                                          title={`Opacity: ${Math.round(alpha * 100)}%`}
                                        />
                                        <span className="text-xs text-themed-muted w-8">
                                          {Math.round(alpha * 100)}%
                                        </span>
                                      </div>
                                    )}
                                    <input
                                      type="text"
                                      value={newTheme[color.key] || currentThemeColor}
                                      onFocus={() => handleColorStart(color.key)}
                                      onChange={(e) => handleColorChange(color.key, e.target.value)}
                                      className="w-24 px-2 py-1 text-xs rounded font-mono themed-input"
                                    />
                                    <button
                                      onClick={() => copyColor(newTheme[color.key] || currentThemeColor)}
                                      className="p-1 rounded-lg hover:bg-opacity-50 bg-themed-hover"
                                      title="Copy color"
                                    >
                                      {copiedColor === (newTheme[color.key] || currentThemeColor) ? (
                                        <Check
                                          className="w-3 h-3"
                                          style={{ color: 'var(--theme-success)' }}
                                        />
                                      ) : (
                                        <Copy className="w-3 h-3 text-themed-muted" />
                                      )}
                                    </button>
                                    {getCreateColorHistory(color.key) && (
                                      <button
                                        onClick={() => restoreCreatePreviousColor(color.key)}
                                        className="p-1 rounded-lg hover:bg-opacity-50 bg-themed-hover"
                                        title={`Restore previous color: ${getCreateColorHistory(color.key)}`}
                                      >
                                        <RotateCcw className="w-3 h-3 text-themed-muted" />
                                      </button>
                                    )}
                                  </>
                                );
                              })()}
                            </div>
                          </div>
                        </div>
                      ))}
                    </div>
                  )}
                </div>
              );
            })}
          </div>

          {/* Custom CSS */}
          <div>
            <label className="block text-sm font-medium mb-1 text-themed-secondary">
              Custom CSS (Optional)
            </label>
            <textarea
              value={newTheme.customCSS}
              onChange={(e) => setNewTheme({ ...newTheme, customCSS: e.target.value })}
              placeholder="/* Add any custom CSS here */"
              rows={4}
              className="w-full px-3 py-2 rounded font-mono text-xs focus:outline-none themed-input"
            />
          </div>

          {/* Actions */}
          <div
            className="flex justify-end space-x-3 pt-4 border-t"
            style={{ borderColor: 'var(--theme-border-primary)' }}
          >
            <Button variant="default" onClick={() => setCreateModalOpen(false)}>
              Cancel
            </Button>
            <Button
              variant="filled"
              leftSection={<Save className="w-4 h-4" />}
              onClick={handleCreateTheme}
              disabled={!newTheme.name || !isAuthenticated}
              className="themed-button-primary"
            >
              Create Theme
            </Button>
          </div>
        </div>
      </Modal>

      <Modal
        opened={editModalOpen}
        onClose={() => {
          setEditModalOpen(false);
          setEditingTheme(null);
          setEditedTheme({});
          setColorEditingStarted({});
        }}
        title={`Edit Theme: ${editingTheme?.meta.name || ''}`}
        size="xl"
      >
        <div className="space-y-6">
          {/* Theme Metadata */}
          <div className="space-y-4">
            <h4 className="text-sm font-semibold flex items-center gap-2 text-themed-primary">
              <Info className="w-4 h-4" />
              Theme Information
            </h4>
            <div className="grid grid-cols-2 gap-4">
              <div>
                <label className="block text-sm font-medium mb-1 text-themed-secondary">
                  Theme Name *
                </label>
                <input
                  type="text"
                  value={editedTheme.name || ''}
                  onChange={(e) => setEditedTheme({ ...editedTheme, name: e.target.value })}
                  placeholder="My Custom Theme"
                  className="w-full px-3 py-2 focus:outline-none themed-input"
                />
              </div>
              <div>
                <label className="block text-sm font-medium mb-1 text-themed-secondary">
                  Author
                </label>
                <input
                  type="text"
                  value={editedTheme.author || ''}
                  onChange={(e) => setEditedTheme({ ...editedTheme, author: e.target.value })}
                  placeholder="Your Name"
                  className="w-full px-3 py-2 focus:outline-none themed-input"
                />
              </div>
            </div>
            <div>
              <label className="block text-sm font-medium mb-1 text-themed-secondary">
                Description
              </label>
              <input
                type="text"
                value={editedTheme.description || ''}
                onChange={(e) => setEditedTheme({ ...editedTheme, description: e.target.value })}
                placeholder="A beautiful custom theme"
                className="w-full px-3 py-2 rounded focus:outline-none themed-input"
              />
            </div>
            <div className="flex items-center gap-4">
              <Checkbox
                checked={editedTheme.isDark || false}
                onChange={(e) => setEditedTheme({ ...editedTheme, isDark: e.target.checked })}
                variant="rounded"
                label="Dark Theme"
              />
              <span className="text-xs text-themed-muted">Theme ID: {editingTheme?.meta.id}</span>
            </div>
          </div>

          {/* Organization Mode Toggle */}
          <div className="flex gap-2">
            <button
              onClick={() => setEditOrganizationMode('category')}
              className={`flex-1 px-3 py-2 rounded-lg text-sm font-medium transition-all duration-200 ${
                editOrganizationMode === 'category'
                  ? 'bg-primary text-themed-button'
                  : 'bg-themed-tertiary text-themed-secondary hover:bg-themed-hover'
              }`}
            >
              <Layers className="w-4 h-4 inline-block mr-2" />
              By Category
            </button>
            <button
              onClick={() => setEditOrganizationMode('page')}
              className={`flex-1 px-3 py-2 rounded-lg text-sm font-medium transition-all duration-200 ${
                editOrganizationMode === 'page'
                  ? 'bg-primary text-themed-button'
                  : 'bg-themed-tertiary text-themed-secondary hover:bg-themed-hover'
              }`}
            >
              <Layout className="w-4 h-4 inline-block mr-2" />
              By Page
            </button>
          </div>

          {/* Page Selector (when in page mode) */}
          <div className={`transition-all duration-300 overflow-hidden ${
            editOrganizationMode === 'page' ? 'max-h-40 opacity-100 mt-4' : 'max-h-0 opacity-0'
          }`}>
            <label className="block text-sm font-medium text-themed-primary mb-2">
              Select Page
            </label>
            <div className="grid grid-cols-3 gap-2">
              {pageDefinitions.map((page) => {
                const Icon = page.icon;
                return (
                  <button
                    key={page.name}
                    onClick={() => setEditSelectedPage(page.name)}
                    className={`px-3 py-2 rounded-lg text-sm font-medium transition-all duration-200 flex items-center gap-2 ${
                      editSelectedPage === page.name
                        ? 'bg-primary'
                        : 'bg-themed-tertiary text-themed-secondary hover:bg-themed-hover'
                    }`}
                    style={{
                      color: editSelectedPage === page.name
                        ? 'var(--theme-button-text)'
                        : undefined
                    }}
                    title={page.description}
                  >
                    <Icon className="w-4 h-4" />
                    {page.label}
                  </button>
                );
              })}
            </div>
          </div>

          {/* Search Bar */}
          <div className="relative mt-4">
            <Search className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-themed-muted" />
            <input
              type="text"
              value={editSearchQuery}
              onChange={(e) => setEditSearchQuery(e.target.value)}
              placeholder="Search colors... (e.g., 'alert', 'header', 'button', 'background')"
              className="w-full pl-10 pr-10 py-2 themed-input"
            />
            {editSearchQuery && (
              <button
                onClick={() => setEditSearchQuery('')}
                className="absolute right-3 top-1/2 -translate-y-1/2 text-themed-muted hover:text-themed-primary"
              >
                <X className="w-4 h-4" />
              </button>
            )}
          </div>

          {/* Color Groups */}
          <div className="space-y-4 max-h-96 overflow-y-auto">
            {getFilteredGroups(colorGroups, editSearchQuery, true).map((group) => {
              const Icon = group.icon;
              const isExpanded = expandedGroups.includes(group.name) || editSearchQuery.trim() !== '';

              return (
                <div
                  key={group.name}
                  className="themed-card rounded-lg"
                  style={{ border: '1px solid var(--theme-border)' }}
                >
                  <button
                    onClick={() => toggleGroup(group.name)}
                    className="w-full px-4 py-3 flex items-center justify-between hover:bg-opacity-50"
                    style={{
                      backgroundColor: isExpanded ? 'var(--theme-bg-tertiary)' : 'transparent'
                    }}
                  >
                    <div className="flex items-center gap-3">
                      <Icon className="w-4 h-4 text-themed-accent" />
                      <div className="text-left">
                        <h5 className="text-sm font-semibold capitalize text-themed-primary">
                          {group.name.replace(/([A-Z])/g, ' $1').trim()}
                        </h5>
                        <p className="text-xs text-themed-muted">{group.description}</p>
                      </div>
                    </div>
                    {isExpanded ? (
                      <ChevronDown className="w-4 h-4" />
                    ) : (
                      <ChevronRight className="w-4 h-4" />
                    )}
                  </button>

                  {isExpanded && (
                    <div
                      className="p-4 themed-card space-y-4"
                      style={{ borderTop: '1px solid var(--theme-border)' }}
                    >
                      {group.colors.map((color) => (
                        <div key={color.key} className="space-y-2">
                          <div className="flex items-center justify-between">
                            <div className="flex-1">
                              <label className="block text-sm font-medium text-themed-primary">
                                {color.label}
                              </label>
                              <p className="text-xs text-themed-muted">{color.description}</p>
                              <div className="flex flex-wrap gap-1 mt-1">
                                {color.affects.map((item, idx) => (
                                  <span
                                    key={idx}
                                    className="text-xs px-1.5 py-0.5 rounded"
                                    style={{
                                      backgroundColor: 'var(--theme-bg-hover)',
                                      color: 'var(--theme-text-secondary)'
                                    }}
                                  >
                                    {item}
                                  </span>
                                ))}
                              </div>
                            </div>
                            <div className="flex items-center gap-2">
                              {(() => {
                                const { hex, alpha } = parseColorValue(editedTheme[color.key] || '#000000');
                                return (
                                  <>
                                    <div className="relative">
                                      <input
                                        type="color"
                                        value={hex}
                                        onMouseDown={() => handleEditColorStart(color.key)}
                                        onFocus={() => handleEditColorStart(color.key)}
                                        onChange={(e) => {
                                          const currentAlpha = parseColorValue(editedTheme[color.key] || '#000000').alpha;
                                          updateColorWithAlpha(color.key, e.target.value, currentAlpha, false);
                                        }}
                                        className="w-12 h-8 rounded cursor-pointer"
                                        style={{ backgroundColor: editedTheme[color.key] || '#000000' }}
                                      />
                                    </div>
                                    {color.supportsAlpha && (
                                      <div className="flex items-center gap-1">
                                        <Percent className="w-3 h-3 text-themed-muted" />
                                        <input
                                          type="range"
                                          min="0"
                                          max="100"
                                          value={Math.round(alpha * 100)}
                                          onChange={(e) => {
                                            const newAlpha = parseInt(e.target.value) / 100;
                                            updateColorWithAlpha(color.key, hex, newAlpha, false);
                                          }}
                                          className="w-16"
                                          title={`Opacity: ${Math.round(alpha * 100)}%`}
                                        />
                                        <span className="text-xs text-themed-muted w-8">
                                          {Math.round(alpha * 100)}%
                                        </span>
                                      </div>
                                    )}
                                    <input
                                      type="text"
                                      value={editedTheme[color.key] || ''}
                                      onFocus={() => handleEditColorStart(color.key)}
                                      onChange={(e) => handleEditColorChange(color.key, e.target.value)}
                                      className="w-24 px-2 py-1 text-xs rounded font-mono themed-input"
                                      placeholder={color.key}
                                    />
                                    <button
                                      onClick={() => copyColor(editedTheme[color.key] || '')}
                                      className="p-1 rounded-lg hover:bg-opacity-50 bg-themed-hover"
                                      title="Copy color"
                                    >
                                      {copiedColor === editedTheme[color.key] ? (
                                        <Check
                                          className="w-3 h-3"
                                          style={{ color: 'var(--theme-success)' }}
                                        />
                                      ) : (
                                        <Copy className="w-3 h-3 text-themed-muted" />
                                      )}
                                    </button>
                                    {(() => {
                                      const historyColor = getEditColorHistory(color.key);
                                      if (!historyColor) return null;

                                      return (
                                        <button
                                          onClick={() => restorePreviousColor(color.key)}
                                          className="p-1 rounded-lg hover:bg-opacity-50 bg-themed-hover"
                                          title={`Restore previous color: ${historyColor}`}
                                        >
                                          <RotateCcw className="w-3 h-3 text-themed-muted" />
                                        </button>
                                      );
                                    })()}
                                  </>
                                );
                              })()}
                            </div>
                          </div>
                        </div>
                      ))}
                    </div>
                  )}
                </div>
              );
            })}
          </div>

          {/* Custom CSS */}
          <div>
            <label className="block text-sm font-medium mb-1 text-themed-secondary">
              Custom CSS (Optional)
            </label>
            <textarea
              value={editedTheme.customCSS || ''}
              onChange={(e) => setEditedTheme({ ...editedTheme, customCSS: e.target.value })}
              placeholder="/* Add any custom CSS here */"
              rows={4}
              className="w-full px-3 py-2 rounded font-mono text-xs focus:outline-none themed-input"
            />
          </div>

          {/* Actions */}
          <div
            className="flex justify-end space-x-3 pt-4 border-t"
            style={{ borderColor: 'var(--theme-border-primary)' }}
          >
            <Button
              variant="default"
              onClick={() => {
                setEditModalOpen(false);
                setEditingTheme(null);
                setEditedTheme({});
              }}
            >
              Cancel
            </Button>
            <Button
              variant="filled"
              leftSection={<Save className="w-4 h-4" />}
              onClick={handleSaveEditedTheme}
              disabled={!editedTheme.name || !isAuthenticated || loading}
              className="themed-button-primary"
            >
              Save Changes
            </Button>
          </div>
        </div>
      </Modal>
    </>
  );
};

export default ThemeManager;
