import React, { useState, useEffect, useRef } from 'react';
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
  Component,
  Sparkles,
  Activity,
  Edit,
  Search,
  X,
  Percent
} from 'lucide-react';
import themeService from '../../services/theme.service';
import authService from '../../services/auth.service';
import { Alert } from '../ui/Alert';
import { Button } from '../ui/Button';
import { Card } from '../ui/Card';
import { Modal } from '../ui/Modal';
import { API_BASE } from '../../utils/constants';

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
  }[];
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
  const [editingTheme, setEditingTheme] = useState<Theme | null>(null);
  const [expandedGroups, setExpandedGroups] = useState<string[]>([
    'core',
    'backgrounds',
    'text'
  ]);
  const [copiedColor, setCopiedColor] = useState<string | null>(null);
  const [colorEditingStarted, setColorEditingStarted] = useState<Record<string, boolean>>({});
  const [createSearchQuery, setCreateSearchQuery] = useState('');
  const [editSearchQuery, setEditSearchQuery] = useState('');

  const [editedTheme, setEditedTheme] = useState<any>({});
  const [newTheme, setNewTheme] = useState<any>({
    name: '',
    description: '',
    author: '',
    version: '1.0.0',
    isDark: true,

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

    // Status colors with backgrounds and text
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

    // Service colors
    steamColor: '#1e40af',
    epicColor: '#7c3aed',
    originColor: '#ea580c',
    blizzardColor: '#0891b2',
    wsusColor: '#16a34a',
    riotColor: '#dc2626',

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

    // Icon backgrounds (solid colors)
    iconBgBlue: '#3b82f6',
    iconBgGreen: '#10b981',
    iconBgEmerald: '#10b981',
    iconBgPurple: '#8b5cf6',
    iconBgIndigo: '#6366f1',
    iconBgOrange: '#f97316',
    iconBgYellow: '#eab308',
    iconBgCyan: '#06b6d4',
    iconBgRed: '#ef4444',

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
          supportsAlpha: true
        },
        {
          key: 'secondaryColor',
          label: 'Secondary Brand Color',
          description: 'Complementary brand accent',
          affects: ['Secondary buttons', 'Highlights', 'Accents'],
          supportsAlpha: true
        },
        {
          key: 'accentColor',
          label: 'Accent Color',
          description: 'Tertiary accent for special elements',
          affects: ['Special badges', 'Tooltips', 'Info elements'],
          supportsAlpha: true
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
          supportsAlpha: true
        },
        {
          key: 'textSecondary',
          label: 'Secondary Text',
          description: 'Supporting content text',
          affects: ['Descriptions', 'Subtitles', 'Help text'],
          supportsAlpha: true
        },
        {
          key: 'textMuted',
          label: 'Muted Text',
          description: 'De-emphasized text',
          affects: ['Disabled text', 'Timestamps', 'Minor labels'],
          supportsAlpha: true
        },
        {
          key: 'textAccent',
          label: 'Accent Text',
          description: 'Highlighted or linked text',
          affects: ['Links', 'Highlighted values', 'Active menu items'],
          supportsAlpha: true
        },
        {
          key: 'textPlaceholder',
          label: 'Placeholder Text',
          description: 'Input placeholder text color',
          affects: ['Form placeholders', 'Search hints', 'Empty states'],
          supportsAlpha: true
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
          supportsAlpha: true
        },
        {
          key: 'bgSecondary',
          label: 'Surface Background',
          description: 'Elevated surface backgrounds',
          affects: ['Cards', 'Panels', 'Modals', 'Dialogs'],
          supportsAlpha: true
        },
        {
          key: 'bgTertiary',
          label: 'Recessed Background',
          description: 'Sunken or nested elements',
          affects: ['Input fields', 'Wells', 'Code blocks'],
          supportsAlpha: true
        },
        {
          key: 'bgHover',
          label: 'Hover State',
          description: 'Interactive hover backgrounds',
          affects: ['Button hovers', 'List hovers', 'Menu hovers'],
          supportsAlpha: true
        },
        {
          key: 'cardBg',
          label: 'Card Background',
          description: 'Card component background',
          affects: ['Stat cards', 'Content cards', 'Widget backgrounds'],
          supportsAlpha: true
        },
        {
          key: 'cardBorder',
          label: 'Card Border',
          description: 'Card component borders',
          affects: ['Card outlines', 'Panel borders'],
          supportsAlpha: true
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
          supportsAlpha: true
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
          supportsAlpha: true
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
      description: 'Status indicators, alerts, and feedback colors',
      colors: [
        {
          key: 'success',
          label: 'Success Primary',
          description: 'Success state primary color',
          affects: ['Success icons', 'Success buttons', 'Positive actions'],
          supportsAlpha: true
        },
        {
          key: 'successBg',
          label: 'Success Background',
          description: 'Success state background',
          affects: ['Success alerts', 'Success badges', 'Success cards'],
          supportsAlpha: true
        },
        {
          key: 'successText',
          label: 'Success Text',
          description: 'Success state text color',
          affects: ['Success messages', 'Positive values'],
          supportsAlpha: true
        },
        {
          key: 'warning',
          label: 'Warning Primary',
          description: 'Warning state primary color',
          affects: ['Warning icons', 'Caution buttons'],
          supportsAlpha: true
        },
        {
          key: 'warningBg',
          label: 'Warning Background',
          description: 'Warning state background',
          affects: ['Warning alerts', 'Warning badges'],
          supportsAlpha: true
        },
        {
          key: 'warningText',
          label: 'Warning Text',
          description: 'Warning state text color',
          affects: ['Warning messages', 'Caution text'],
          supportsAlpha: true
        },
        {
          key: 'error',
          label: 'Error Primary',
          description: 'Error state primary color',
          affects: ['Error icons', 'Delete buttons', 'Critical actions'],
          supportsAlpha: true
        },
        {
          key: 'errorBg',
          label: 'Error Background',
          description: 'Error state background',
          affects: ['Error alerts', 'Error badges'],
          supportsAlpha: true
        },
        {
          key: 'errorText',
          label: 'Error Text',
          description: 'Error state text color',
          affects: ['Error messages', 'Validation errors'],
          supportsAlpha: true
        },
        {
          key: 'info',
          label: 'Info Primary',
          description: 'Information state primary color',
          affects: ['Info icons', 'Info buttons'],
          supportsAlpha: true
        },
        {
          key: 'infoBg',
          label: 'Info Background',
          description: 'Information state background',
          affects: ['Info alerts', 'Info cards'],
          supportsAlpha: true
        },
        {
          key: 'infoText',
          label: 'Info Text',
          description: 'Information state text color',
          affects: ['Info messages', 'Help content'],
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
          description: 'Navigation bar background',
          affects: ['Header', 'Nav bar', 'Menu background'],
          supportsAlpha: true
        },
        {
          key: 'navBorder',
          label: 'Navigation Border',
          description: 'Navigation separators',
          affects: ['Nav borders', 'Menu dividers'],
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
          supportsAlpha: true
        },
        {
          key: 'epicColor',
          label: 'Epic Games',
          description: 'Epic Games platform color',
          affects: ['Epic badges', 'Epic charts'],
          supportsAlpha: true
        },
        {
          key: 'originColor',
          label: 'Origin/EA',
          description: 'EA/Origin platform color',
          affects: ['Origin badges', 'EA charts'],
          supportsAlpha: true
        },
        {
          key: 'blizzardColor',
          label: 'Blizzard',
          description: 'Blizzard platform color',
          affects: ['Blizzard badges', 'Battle.net charts'],
          supportsAlpha: true
        },
        {
          key: 'wsusColor',
          label: 'Windows Update',
          description: 'Windows Update service color',
          affects: ['WSUS badges', 'Update charts'],
          supportsAlpha: true
        },
        {
          key: 'riotColor',
          label: 'Riot Games',
          description: 'Riot Games platform color',
          affects: ['Riot badges', 'LoL charts'],
          supportsAlpha: true
        }
      ]
    },

    // 11. UTILITIES - Misc UI elements
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
    
    // Debug: Log all color history in localStorage
    console.log('=== Color History in localStorage ===');
    Object.keys(localStorage).forEach(key => {
      if (key.startsWith('color_history_')) {
        console.log(`${key}: ${localStorage.getItem(key)}`);
      }
    });
    console.log('=====================================');
  }, []);

  // Update newTheme colors when create modal opens
  useEffect(() => {
    if (createModalOpen) {
      // Get current theme's colors from computed styles
      const computedStyle = getComputedStyle(document.documentElement);
      const getCurrentColor = (varName: string, fallback: string) => {
        const color = computedStyle.getPropertyValue(varName).trim();
        return color || fallback;
      };

      setNewTheme((prev: any) => ({
        ...prev,
        // Update all color values with current theme colors
        primaryColor: getCurrentColor('--theme-primary', '#3b82f6'),
        secondaryColor: getCurrentColor('--theme-secondary', '#8b5cf6'),
        accentColor: getCurrentColor('--theme-accent', '#06b6d4'),

        bgPrimary: getCurrentColor('--theme-bg-primary', '#111827'),
        bgSecondary: getCurrentColor('--theme-bg-secondary', '#1f2937'),
        bgTertiary: getCurrentColor('--theme-bg-tertiary', '#374151'),
        bgHover: getCurrentColor('--theme-bg-hover', '#4b5563'),

        textPrimary: getCurrentColor('--theme-text-primary', '#ffffff'),
        textSecondary: getCurrentColor('--theme-text-secondary', '#d1d5db'),
        textMuted: getCurrentColor('--theme-text-muted', '#9ca3af'),
        textAccent: getCurrentColor('--theme-text-accent', '#60a5fa'),
        textPlaceholder: getCurrentColor('--theme-text-placeholder', '#6b7280'),
        dragHandleColor: getCurrentColor('--theme-drag-handle-color', '#6b7280'),
        dragHandleHover: getCurrentColor('--theme-drag-handle-hover', '#60a5fa'),

        borderPrimary: getCurrentColor('--theme-border-primary', '#374151'),
        borderSecondary: getCurrentColor('--theme-border-secondary', '#4b5563'),
        borderFocus: getCurrentColor('--theme-border-focus', '#3b82f6'),

        navBg: getCurrentColor('--theme-nav-bg', '#1f2937'),
        navBorder: getCurrentColor('--theme-nav-border', '#374151'),
        navTabActive: getCurrentColor('--theme-nav-tab-active', '#3b82f6'),
        navTabInactive: getCurrentColor('--theme-nav-tab-inactive', '#9ca3af'),
        navTabHover: getCurrentColor('--theme-nav-tab-hover', '#ffffff'),
        navTabActiveBorder: getCurrentColor('--theme-nav-tab-active-border', '#3b82f6'),
        navMobileMenuBg: getCurrentColor('--theme-nav-mobile-menu-bg', '#1f2937'),
        navMobileItemHover: getCurrentColor('--theme-nav-mobile-item-hover', '#374151'),

        success: getCurrentColor('--theme-success', '#10b981'),
        successBg: getCurrentColor('--theme-success-bg', '#064e3b'),
        successText: getCurrentColor('--theme-success-text', '#34d399'),
        warning: getCurrentColor('--theme-warning', '#f59e0b'),
        warningBg: getCurrentColor('--theme-warning-bg', '#78350f'),
        warningText: getCurrentColor('--theme-warning-text', '#fbbf24'),
        error: getCurrentColor('--theme-error', '#ef4444'),
        errorBg: getCurrentColor('--theme-error-bg', '#7f1d1d'),
        errorText: getCurrentColor('--theme-error-text', '#fca5a5'),
        info: getCurrentColor('--theme-info', '#3b82f6'),
        infoBg: getCurrentColor('--theme-info-bg', '#1e3a8a'),
        infoText: getCurrentColor('--theme-info-text', '#93c5fd'),

        steamColor: getCurrentColor('--theme-steam-color', '#1e40af'),
        epicColor: getCurrentColor('--theme-epic-color', '#7c3aed'),
        originColor: getCurrentColor('--theme-origin-color', '#ea580c'),
        blizzardColor: getCurrentColor('--theme-blizzard-color', '#0891b2'),
        wsusColor: getCurrentColor('--theme-wsus-color', '#16a34a'),
        riotColor: getCurrentColor('--theme-riot-color', '#dc2626'),

        cardBg: getCurrentColor('--theme-card-bg', '#1f2937'),
        cardBorder: getCurrentColor('--theme-card-border', '#374151'),
        buttonBg: getCurrentColor('--theme-button-bg', '#3b82f6'),
        buttonHover: getCurrentColor('--theme-button-hover', '#2563eb'),
        buttonText: getCurrentColor('--theme-button-text', '#ffffff'),
        inputBg: getCurrentColor('--theme-input-bg', '#374151'),
        inputBorder: getCurrentColor('--theme-input-border', '#4b5563'),
        inputFocus: getCurrentColor('--theme-input-focus', '#3b82f6'),
        badgeBg: getCurrentColor('--theme-badge-bg', '#3b82f6'),
        badgeText: getCurrentColor('--theme-badge-text', '#ffffff'),
        progressBar: getCurrentColor('--theme-progress-bar', '#3b82f6'),
        progressBg: getCurrentColor('--theme-progress-bg', '#374151'),

        // Icon backgrounds
        iconBgBlue: getCurrentColor('--theme-icon-bg-blue', '#3b82f6'),
        iconBgGreen: getCurrentColor('--theme-icon-bg-green', '#10b981'),
        iconBgEmerald: getCurrentColor('--theme-icon-bg-emerald', '#10b981'),
        iconBgPurple: getCurrentColor('--theme-icon-bg-purple', '#8b5cf6'),
        iconBgIndigo: getCurrentColor('--theme-icon-bg-indigo', '#6366f1'),
        iconBgOrange: getCurrentColor('--theme-icon-bg-orange', '#f97316'),
        iconBgYellow: getCurrentColor('--theme-icon-bg-yellow', '#eab308'),
        iconBgCyan: getCurrentColor('--theme-icon-bg-cyan', '#06b6d4'),
        iconBgRed: getCurrentColor('--theme-icon-bg-red', '#ef4444'),

        // Chart colors
        chartColor1: getCurrentColor('--theme-chart-color-1', '#3b82f6'),
        chartColor2: getCurrentColor('--theme-chart-color-2', '#10b981'),
        chartColor3: getCurrentColor('--theme-chart-color-3', '#f59e0b'),
        chartColor4: getCurrentColor('--theme-chart-color-4', '#ef4444'),
        chartColor5: getCurrentColor('--theme-chart-color-5', '#8b5cf6'),
        chartColor6: getCurrentColor('--theme-chart-color-6', '#06b6d4'),
        chartColor7: getCurrentColor('--theme-chart-color-7', '#f97316'),
        chartColor8: getCurrentColor('--theme-chart-color-8', '#ec4899'),
        chartBorderColor: getCurrentColor('--theme-chart-border-color', '#1f2937'),
        chartGridColor: getCurrentColor('--theme-chart-grid-color', '#374151'),
        chartTextColor: getCurrentColor('--theme-chart-text-color', '#9ca3af'),
        chartCacheHitColor: getCurrentColor('--theme-chart-cache-hit-color', '#10b981'),
        chartCacheMissColor: getCurrentColor('--theme-chart-cache-miss-color', '#f59e0b'),

        // Scrollbar colors
        scrollbarThumb: getCurrentColor('--theme-scrollbar-thumb', '#4b5563'),
        scrollbarTrack: getCurrentColor('--theme-scrollbar-track', '#1f2937'),
        scrollbarThumbHover: getCurrentColor('--theme-scrollbar-thumb-hover', '#6b7280'),
      }));
    }
  }, [createModalOpen]);

  const loadThemes = async () => {
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
  };

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
        console.log(`Saved original create color: ${key} = ${currentValue}`);
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
        console.log(`Saved original color to history: ${key} = ${currentValue}`);
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
      console.log(`Restoring ${key}: ${currentColor} -> ${previousColor}`);
      setEditedTheme((prev: any) => ({ ...prev, [key]: previousColor }));
      localStorage.setItem(historyKey, currentColor || '');
    }
  };
  
  const getEditColorHistory = (key: string) => {
    const historyKey = `color_history_${editingTheme?.meta.id}_${key}`;
    const value = localStorage.getItem(historyKey);
    console.log(`getEditColorHistory: key=${key}, historyKey=${historyKey}, value=${value}`);
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

  const handleDelete = async (themeId: string, themeName: string) => {
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

    if (!window.confirm(`Delete theme "${themeName}"? This cannot be undone.`)) return;

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

        if (result.availableThemes) {
          console.log('Available themes on server:', result.availableThemes);
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
    }
  };

  const cleanupThemes = async () => {
    if (!isAuthenticated) {
      setUploadError('Authentication required to clean up themes');
      setTimeout(() => setUploadError(null), 5000);
      return;
    }

    if (
      !window.confirm(
        'This will DELETE all custom themes (keeping only system themes). This cannot be undone. Continue?'
      )
    ) {
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
    
    console.log('Editing theme:', latestTheme);
    console.log('Theme colors:', latestTheme.colors);

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
    
    console.log('Theme data for editing:', themeData);
    const colorKeys = Object.keys(themeData).filter(k => !['name', 'description', 'author', 'version', 'isDark', 'customCSS'].includes(k));
    console.log('Color keys and values in theme data:');
    colorKeys.forEach(key => {
      console.log(`  ${key}: ${themeData[key]}`);
    });
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

    console.log('Saving theme with colors:', editedTheme);

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
    
    console.log('Theme object to save:', updatedTheme);

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
        inputBg: '#ffffff',
        inputBorder: '#d1d5db',
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

        <div className="mb-6">
          <label className="block text-sm font-medium mb-2 text-themed-secondary">
            Active Theme
          </label>
          <select
            value={previewTheme || currentTheme}
            onChange={(e) => handleThemeChange(e.target.value)}
            className="w-full px-3 py-2 focus:outline-none themed-input"
            disabled={loading}
          >
            {themes.map((theme) => (
              <option key={theme.meta.id} value={theme.meta.id}>
                {theme.meta.name}{' '}
                {theme.meta.author && theme.meta.author !== 'System' && `by ${theme.meta.author}`}
                {isSystemTheme(theme.meta.id) && ' (System)'}
                {previewTheme === theme.meta.id && ' (Preview)'}
              </option>
            ))}
          </select>
          {previewTheme && (
            <p className="text-xs mt-2 text-themed-warning">
              Preview mode active. Select a theme to apply it permanently.
            </p>
          )}
        </div>

        <div className="mb-6">
          <h4 className="text-sm font-medium mb-3 text-themed-secondary">
            Installed Themes ({themes.length})
          </h4>
          <div className="space-y-2 max-h-64 overflow-y-auto">
            {themes.map((theme) => (
              <div
                key={theme.meta.id}
                className="rounded p-3 flex items-center justify-between border-2 transition-colors themed-card"
                style={{
                  borderColor:
                    currentTheme === theme.meta.id && !previewTheme
                      ? 'var(--theme-primary)'
                      : previewTheme === theme.meta.id
                        ? 'var(--theme-warning)'
                        : 'transparent'
                }}
              >
                <div className="flex-1">
                  <div className="flex items-center gap-2">
                    <span className="font-medium text-themed-primary">{theme.meta.name}</span>
                    {theme.meta.isDark ? (
                      <Moon className="w-3 h-3 text-themed-muted" />
                    ) : (
                      <Sun className="w-3 h-3 text-themed-warning" />
                    )}
                    {currentTheme === theme.meta.id && !previewTheme && (
                      <span className="px-2 py-0.5 text-xs rounded themed-button-primary">
                        Active
                      </span>
                    )}
                    {previewTheme === theme.meta.id && (
                      <span
                        className="px-2 py-0.5 text-xs rounded bg-themed-warning text-themed-primary"
                      >
                        Preview
                      </span>
                    )}
                    {isSystemTheme(theme.meta.id) && (
                      <span
                        className="px-2 py-0.5 text-xs rounded bg-themed-hover text-themed-muted"
                      >
                        System
                      </span>
                    )}
                  </div>
                  {theme.meta.description && (
                    <p className="text-xs mt-1 text-themed-muted">{theme.meta.description}</p>
                  )}
                  <div className="flex items-center gap-3 mt-1">
                    {theme.meta.author && (
                      <p className="text-xs text-themed-muted">by {theme.meta.author}</p>
                    )}
                    {theme.meta.version && (
                      <p className="text-xs text-themed-muted">v{theme.meta.version}</p>
                    )}
                  </div>
                </div>
                <div className="flex items-center gap-2">
                  <button
                    onClick={() => exportTheme(theme)}
                    className="p-2 transition-colors text-themed-muted"
                    title="Export theme"
                  >
                    <Download className="w-4 h-4" />
                  </button>
                  {currentTheme !== theme.meta.id && (
                    <button
                      onClick={() => handlePreview(theme.meta.id)}
                      className="p-2 transition-colors"
                      style={{
                        color:
                          previewTheme === theme.meta.id
                            ? 'var(--theme-warning)'
                            : 'var(--theme-text-muted)'
                      }}
                      title={previewTheme === theme.meta.id ? 'Stop preview' : 'Preview theme'}
                    >
                      {previewTheme === theme.meta.id ? (
                        <EyeOff className="w-4 h-4" />
                      ) : (
                        <Eye className="w-4 h-4" />
                      )}
                    </button>
                  )}
                  {currentTheme !== theme.meta.id && (
                    <button
                      onClick={() => handleThemeChange(theme.meta.id)}
                      className="p-2 transition-colors text-themed-accent"
                      title="Apply theme"
                    >
                      <Check className="w-4 h-4" />
                    </button>
                  )}
                  {!isSystemTheme(theme.meta.id) && isAuthenticated && (
                    <button
                      onClick={() => handleEditTheme(theme)}
                      disabled={loading}
                      className="p-2 transition-colors text-themed-secondary"
                      title="Edit theme"
                    >
                      <Edit className="w-4 h-4" />
                    </button>
                  )}
                  {!isSystemTheme(theme.meta.id) && isAuthenticated && (
                    <button
                      onClick={() => handleDelete(theme.meta.id, theme.meta.name)}
                      disabled={loading}
                      className="p-2 transition-colors disabled:opacity-50 text-themed-error"
                      title="Delete theme"
                    >
                      <Trash2 className="w-4 h-4" />
                    </button>
                  )}
                </div>
              </div>
            ))}
          </div>
        </div>

        {isAuthenticated && (
          <>
            <div className="mb-4">
              <h4 className="text-sm font-medium mb-3 text-themed-secondary">
                Upload Custom Theme
              </h4>
              <div
                className={`border-2 border-dashed rounded-lg p-8 text-center transition-colors ${
                  dragActive ? 'border-purple-500 bg-purple-900 bg-opacity-20' : ''
                }`}
                style={{
                  borderColor: dragActive
                    ? 'var(--theme-secondary)'
                    : 'var(--theme-border-secondary)'
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
              <label className="flex items-center gap-2 cursor-pointer">
                <input
                  type="checkbox"
                  checked={newTheme.isDark}
                  onChange={(e) => setNewTheme({ ...newTheme, isDark: e.target.checked })}
                  className="rounded"
                />
                <span className="text-sm text-themed-secondary">Dark Theme</span>
              </label>
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

          {/* Search Bar */}
          <div className="relative">
            <Search className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-themed-muted" />
            <input
              type="text"
              value={createSearchQuery}
              onChange={(e) => setCreateSearchQuery(e.target.value)}
              placeholder="Search colors... (e.g., 'button', 'background', 'text')"
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
            {filterColorGroups(colorGroups, createSearchQuery).map((group) => {
              const Icon = group.icon;
              const isExpanded = expandedGroups.includes(group.name) || createSearchQuery.trim() !== '';

              return (
                <div
                  key={group.name}
                  className="border themed-card rounded-lg"
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
                      className="p-4 border-t themed-card space-y-4"
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
                                const { hex, alpha } = parseColorValue(newTheme[color.key] || '#000000');
                                return (
                                  <>
                                    <div className="relative">
                                      <input
                                        type="color"
                                        value={hex}
                                        onMouseDown={() => handleColorStart(color.key)}
                                        onFocus={() => handleColorStart(color.key)}
                                        onChange={(e) => {
                                          const currentAlpha = parseColorValue(newTheme[color.key]).alpha;
                                          updateColorWithAlpha(color.key, e.target.value, currentAlpha, true);
                                        }}
                                        className="w-12 h-8 rounded cursor-pointer"
                                        style={{ backgroundColor: newTheme[color.key] }}
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
                                      value={newTheme[color.key]}
                                      onFocus={() => handleColorStart(color.key)}
                                      onChange={(e) => handleColorChange(color.key, e.target.value)}
                                      className="w-24 px-2 py-1 text-xs rounded font-mono themed-input"
                                    />
                                    <button
                                      onClick={() => copyColor(newTheme[color.key])}
                                      className="p-1 rounded-lg hover:bg-opacity-50 bg-themed-hover"
                                      title="Copy color"
                                    >
                                      {copiedColor === newTheme[color.key] ? (
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
              <label className="flex items-center gap-2 cursor-pointer">
                <input
                  type="checkbox"
                  checked={editedTheme.isDark || false}
                  onChange={(e) => setEditedTheme({ ...editedTheme, isDark: e.target.checked })}
                  className="rounded"
                />
                <span className="text-sm text-themed-secondary">Dark Theme</span>
              </label>
              <span className="text-xs text-themed-muted">Theme ID: {editingTheme?.meta.id}</span>
            </div>
          </div>

          {/* Search Bar */}
          <div className="relative">
            <Search className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-themed-muted" />
            <input
              type="text"
              value={editSearchQuery}
              onChange={(e) => setEditSearchQuery(e.target.value)}
              placeholder="Search colors... (e.g., 'button', 'background', 'text')"
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
            {filterColorGroups(colorGroups, editSearchQuery).map((group) => {
              const Icon = group.icon;
              const isExpanded = expandedGroups.includes(group.name) || editSearchQuery.trim() !== '';

              return (
                <div
                  key={group.name}
                  className="border themed-card rounded-lg"
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
                      className="p-4 border-t themed-card space-y-4"
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
