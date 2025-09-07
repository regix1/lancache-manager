// ThemeManager.tsx - Complete Enhanced Version
import React, { useState, useEffect, useRef, } from 'react';
import {
  Palette, Upload, Trash2, Check, Download, Eye, RefreshCw,
  Lock, Plus, EyeOff, ChevronDown, ChevronRight, Info, Save, Copy,
  Sun, Moon, Layout, Type, Square,
  Search, X, Layers,
  Navigation, BarChart3, Hash, MousePointer, FileText, Grid, Bell, Gamepad2
} from 'lucide-react';
import themeService from '../../services/theme.service';
import { Alert } from '../ui/Alert';
import { Button } from '../ui/Button';
import { Card } from '../ui/Card';
import { Modal } from '../ui/Modal';

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

interface ThemeManagerProps {
  isAuthenticated: boolean;
}

interface UIElementConfig {
  id: string;
  name: string;
  description: string;
  icon: React.ElementType;
  properties: {
    key: string;
    label: string;
    description: string;
  }[];
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
  }[];
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
  const [expandedGroups, setExpandedGroups] = useState<string[]>(['navigation']);
  const [copiedColor, setCopiedColor] = useState<string | null>(null);

  // Enhanced state for UI element control
  const [viewMode, setViewMode] = useState<'categories' | 'elements' | 'all'>('elements');
  const [searchTerm, setSearchTerm] = useState('');
  const [selectedElement, setSelectedElement] = useState<string | null>('navigation');
  const [showAdvanced, setShowAdvanced] = useState(false);
  const [quickEditMode, setQuickEditMode] = useState(true);

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

    // Borders
    borderPrimary: '#374151',
    borderSecondary: '#4b5563',
    borderFocus: '#3b82f6',

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

    customCSS: ''
  });

  const fileInputRef = useRef<HTMLInputElement>(null);

  // Define UI Elements with specific properties
  const uiElements: UIElementConfig[] = [
    {
      id: 'navigation',
      name: 'Navigation & Header',
      description: 'Top navigation bar, tabs, and header area',
      icon: Navigation,
      properties: [
        { key: 'bgSecondary', label: 'Navigation Background', description: 'Background color of navigation bar' },
        { key: 'borderPrimary', label: 'Navigation Border', description: 'Border color between sections' },
        { key: 'primaryColor', label: 'Active Tab Color', description: 'Color of active navigation tab' },
        { key: 'textSecondary', label: 'Inactive Tab Text', description: 'Text color of inactive tabs' },
        { key: 'textPrimary', label: 'Header Text', description: 'Main header text color' },
        { key: 'bgHover', label: 'Tab Hover Background', description: 'Background when hovering over tabs' }
      ]
    },
    {
      id: 'dashboard',
      name: 'Dashboard Cards',
      description: 'Stat cards and dashboard panels',
      icon: Grid,
      properties: [
        { key: 'cardBg', label: 'Card Background', description: 'Background color of stat cards' },
        { key: 'cardBorder', label: 'Card Border', description: 'Border color of cards' },
        { key: 'textPrimary', label: 'Card Title', description: 'Title text in cards' },
        { key: 'textSecondary', label: 'Card Subtitle', description: 'Secondary text in cards' },
        { key: 'textMuted', label: 'Card Labels', description: 'Label text color' },
        { key: 'iconBgBlue', label: 'Blue Icon Background', description: 'Background for blue icons' },
        { key: 'iconBgGreen', label: 'Green Icon Background', description: 'Background for green icons' },
        { key: 'iconBgPurple', label: 'Purple Icon Background', description: 'Background for purple icons' }
      ]
    },
    {
      id: 'buttons',
      name: 'Buttons & Actions',
      description: 'All button styles and interactive elements',
      icon: MousePointer,
      properties: [
        { key: 'buttonBg', label: 'Primary Button', description: 'Primary button background' },
        { key: 'buttonHover', label: 'Button Hover', description: 'Button hover state' },
        { key: 'buttonText', label: 'Button Text', description: 'Text color on buttons' },
        { key: 'primaryColor', label: 'Action Color', description: 'Primary action color' },
        { key: 'secondaryColor', label: 'Secondary Action', description: 'Secondary action color' },
        { key: 'error', label: 'Danger Button', description: 'Delete/danger button color' }
      ]
    },
    {
      id: 'forms',
      name: 'Forms & Inputs',
      description: 'Input fields, selects, and form controls',
      icon: FileText,
      properties: [
        { key: 'inputBg', label: 'Input Background', description: 'Background of input fields' },
        { key: 'inputBorder', label: 'Input Border', description: 'Border color of inputs' },
        { key: 'inputFocus', label: 'Input Focus', description: 'Border color when focused' },
        { key: 'textPrimary', label: 'Input Text', description: 'Text color in inputs' },
        { key: 'textMuted', label: 'Placeholder Text', description: 'Placeholder text color' },
        { key: 'bgHover', label: 'Input Hover', description: 'Hover state for inputs' }
      ]
    },
    {
      id: 'tables',
      name: 'Tables & Lists',
      description: 'Data tables and list views',
      icon: Hash,
      properties: [
        { key: 'bgPrimary', label: 'Table Background', description: 'Main table background' },
        { key: 'borderPrimary', label: 'Row Borders', description: 'Border between table rows' },
        { key: 'textPrimary', label: 'Table Text', description: 'Main table text color' },
        { key: 'textSecondary', label: 'Table Headers', description: 'Table header text' },
        { key: 'bgHover', label: 'Row Hover', description: 'Table row hover background' },
        { key: 'primaryColor', label: 'Link Color', description: 'Color of links in tables' }
      ]
    },
    {
      id: 'charts',
      name: 'Charts & Graphs',
      description: 'Data visualization colors',
      icon: BarChart3,
      properties: [
        { key: 'chartColor1', label: 'Chart Color 1', description: 'Primary chart color' },
        { key: 'chartColor2', label: 'Chart Color 2', description: 'Secondary chart color' },
        { key: 'chartColor3', label: 'Chart Color 3', description: 'Tertiary chart color' },
        { key: 'chartColor4', label: 'Chart Color 4', description: 'Fourth chart color' },
        { key: 'chartGridColor', label: 'Grid Lines', description: 'Chart grid line color' },
        { key: 'chartTextColor', label: 'Chart Labels', description: 'Chart text and labels' },
        { key: 'chartCacheHitColor', label: 'Cache Hit Color', description: 'Color for cache hits' },
        { key: 'chartCacheMissColor', label: 'Cache Miss Color', description: 'Color for cache misses' }
      ]
    },
    {
      id: 'alerts',
      name: 'Alerts & Status',
      description: 'Alert messages and status indicators',
      icon: Bell,
      properties: [
        { key: 'success', label: 'Success Color', description: 'Success status color' },
        { key: 'successBg', label: 'Success Background', description: 'Success alert background' },
        { key: 'successText', label: 'Success Text', description: 'Success message text' },
        { key: 'warning', label: 'Warning Color', description: 'Warning status color' },
        { key: 'warningBg', label: 'Warning Background', description: 'Warning alert background' },
        { key: 'warningText', label: 'Warning Text', description: 'Warning message text' },
        { key: 'error', label: 'Error Color', description: 'Error status color' },
        { key: 'errorBg', label: 'Error Background', description: 'Error alert background' },
        { key: 'errorText', label: 'Error Text', description: 'Error message text' }
      ]
    },
    {
      id: 'modals',
      name: 'Modals & Overlays',
      description: 'Modal dialogs and overlay elements',
      icon: Layers,
      properties: [
        { key: 'bgSecondary', label: 'Modal Background', description: 'Modal background color' },
        { key: 'borderPrimary', label: 'Modal Border', description: 'Modal border color' },
        { key: 'textPrimary', label: 'Modal Title', description: 'Modal title text' },
        { key: 'textSecondary', label: 'Modal Content', description: 'Modal body text' },
        { key: 'bgPrimary', label: 'Overlay Background', description: 'Background overlay color' }
      ]
    },
    {
      id: 'downloads',
      name: 'Downloads Section',
      description: 'Downloads tab specific colors',
      icon: Download,
      properties: [
        { key: 'cardBg', label: 'Download Card Background', description: 'Background of download items' },
        { key: 'cardBorder', label: 'Download Card Border', description: 'Border of download items' },
        { key: 'primaryColor', label: 'Service Name Color', description: 'Service name text color' },
        { key: 'success', label: 'Active Download', description: 'Active download indicator' },
        { key: 'textMuted', label: 'Metadata Text', description: 'Metadata and info text' },
        { key: 'chartCacheHitColor', label: 'Cache Hit Bar', description: 'Cache hit progress bar' },
        { key: 'chartCacheMissColor', label: 'Cache Miss Bar', description: 'Cache miss progress bar' }
      ]
    },
    {
      id: 'services',
      name: 'Gaming Services',
      description: 'Colors for different gaming platforms',
      icon: Gamepad2,
      properties: [
        { key: 'steamColor', label: 'Steam', description: 'Steam service color' },
        { key: 'epicColor', label: 'Epic Games', description: 'Epic Games service color' },
        { key: 'originColor', label: 'Origin/EA', description: 'Origin/EA service color' },
        { key: 'blizzardColor', label: 'Blizzard', description: 'Blizzard service color' },
        { key: 'wsusColor', label: 'Windows Update', description: 'Windows Update service color' },
        { key: 'riotColor', label: 'Riot Games', description: 'Riot Games service color' }
      ]
    }
  ];

  // Color groups for category view
  const colorGroups: ColorGroup[] = [
    {
      name: 'core',
      icon: Palette,
      description: 'Main theme colors',
      colors: [
        { key: 'primaryColor', label: 'Primary Color', description: 'Main brand color', affects: ['Buttons', 'Links', 'Active states'] },
        { key: 'secondaryColor', label: 'Secondary Color', description: 'Secondary accent', affects: ['Secondary buttons', 'Highlights'] },
        { key: 'accentColor', label: 'Accent Color', description: 'Special emphasis', affects: ['Badges', 'Tooltips'] }
      ]
    },
    {
      name: 'backgrounds',
      icon: Layout,
      description: 'Background colors',
      colors: [
        { key: 'bgPrimary', label: 'Primary Background', description: 'Main app background', affects: ['Body', 'Main container'] },
        { key: 'bgSecondary', label: 'Secondary Background', description: 'Cards and panels', affects: ['Cards', 'Modals'] },
        { key: 'bgTertiary', label: 'Tertiary Background', description: 'Nested elements', affects: ['Inputs', 'Dropdowns'] },
        { key: 'bgHover', label: 'Hover Background', description: 'Hover states', affects: ['Button hovers', 'List hovers'] }
      ]
    },
    {
      name: 'text',
      icon: Type,
      description: 'Text colors',
      colors: [
        { key: 'textPrimary', label: 'Primary Text', description: 'Main text', affects: ['Headings', 'Body text'] },
        { key: 'textSecondary', label: 'Secondary Text', description: 'Less prominent text', affects: ['Descriptions', 'Subtitles'] },
        { key: 'textMuted', label: 'Muted Text', description: 'Disabled text', affects: ['Placeholders', 'Disabled labels'] },
        { key: 'textAccent', label: 'Accent Text', description: 'Highlighted text', affects: ['Links', 'Active items'] }
      ]
    },
    {
      name: 'borders',
      icon: Square,
      description: 'Border colors',
      colors: [
        { key: 'borderPrimary', label: 'Primary Border', description: 'Main borders', affects: ['Card borders', 'Dividers'] },
        { key: 'borderSecondary', label: 'Secondary Border', description: 'Subtle borders', affects: ['Input borders'] },
        { key: 'borderFocus', label: 'Focus Border', description: 'Focused elements', affects: ['Focused inputs', 'Active tabs'] }
      ]
    }
  ];

  useEffect(() => {
    loadThemes();
    const currentThemeId = themeService.getCurrentThemeId();
    setCurrentTheme(currentThemeId);
  }, []);

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
    setExpandedGroups(prev =>
      prev.includes(groupName)
        ? prev.filter(g => g !== groupName)
        : [...prev, groupName]
    );
  };

  const handleColorChange = (key: string, value: string) => {
    setNewTheme((prev: any) => ({ ...prev, [key]: value }));
  };

  const copyColor = (color: string) => {
    navigator.clipboard.writeText(color);
    setCopiedColor(color);
    setTimeout(() => setCopiedColor(null), 2000);
  };

  const handleDrag = (e: React.DragEvent) => {
    e.preventDefault();
    e.stopPropagation();
    if (e.type === "dragenter" || e.type === "dragover") {
      setDragActive(true);
    } else if (e.type === "dragleave") {
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
      setUploadSuccess(`Theme "${result.meta.name}" uploaded successfully`);
      await loadThemes();
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
      await themeService.deleteTheme(themeId);
      await loadThemes();
      if (currentTheme === themeId) {
        handleThemeChange('dark-default');
      }
      setUploadSuccess(`Theme "${themeName}" deleted successfully`);
      setTimeout(() => setUploadSuccess(null), 5000);
    } catch (error: any) {
      setUploadError(error.message || 'Failed to delete theme');
      setTimeout(() => setUploadError(null), 7000);
    } finally {
      setLoading(false);
    }
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
        id: newTheme.name.toLowerCase().replace(/\s+/g, '-').replace(/[^a-z0-9-]/g, ''),
        name: newTheme.name,
        description: newTheme.description,
        author: newTheme.author,
        version: newTheme.version,
        isDark: newTheme.isDark
      },
      colors: { ...newTheme },
      css: newTheme.customCSS ? { content: newTheme.customCSS } : undefined
    };

    // Remove non-color properties
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
      await loadThemes();
      themeService.applyTheme(theme);
      setCurrentTheme(theme.meta.id);
      setCreateModalOpen(false);
      setUploadSuccess(`Theme "${theme.meta.name}" created successfully`);
      setTimeout(() => setUploadSuccess(null), 5000);
    } catch (error: any) {
      setUploadError(error.message || 'Failed to create theme');
    } finally {
      setLoading(false);
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

  const isSystemTheme = (themeId: string) =>
    ['dark-default', 'light-default'].includes(themeId);

  // Filter properties based on search and selected element
  const getFilteredProperties = () => {
    if (viewMode === 'elements' && selectedElement) {
      const element = uiElements.find(el => el.id === selectedElement);
      if (!element) return [];

      if (searchTerm) {
        return element.properties.filter(prop =>
          prop.label.toLowerCase().includes(searchTerm.toLowerCase()) ||
          prop.description.toLowerCase().includes(searchTerm.toLowerCase())
        );
      }
      return element.properties;
    }

    if (viewMode === 'categories') {
      let allColors: any[] = [];
      colorGroups.forEach(group => {
        if (!expandedGroups.includes(group.name)) return;
        group.colors.forEach(color => {
          allColors.push({ ...color, groupName: group.name });
        });
      });

      if (searchTerm) {
        return allColors.filter(color =>
          color.label.toLowerCase().includes(searchTerm.toLowerCase()) ||
          color.description.toLowerCase().includes(searchTerm.toLowerCase())
        );
      }
      return allColors;
    }

    return [];
  };

  const filteredProperties = getFilteredProperties();

  // Quick presets
  const applyQuickPreset = (preset: string) => {
    const presets: any = {
      'dark-blue': {
        primaryColor: '#3b82f6',
        bgPrimary: '#0f172a',
        bgSecondary: '#1e293b',
        cardBg: '#1e293b',
        buttonBg: '#3b82f6'
      },
      'dark-purple': {
        primaryColor: '#8b5cf6',
        bgPrimary: '#18181b',
        bgSecondary: '#27272a',
        cardBg: '#27272a',
        buttonBg: '#8b5cf6'
      },
      'dark-green': {
        primaryColor: '#10b981',
        bgPrimary: '#0f172a',
        bgSecondary: '#1e293b',
        cardBg: '#1e293b',
        buttonBg: '#10b981'
      },
      'light': {
        primaryColor: '#3b82f6',
        bgPrimary: '#ffffff',
        bgSecondary: '#f9fafb',
        cardBg: '#ffffff',
        textPrimary: '#111827',
        textSecondary: '#374151',
        buttonBg: '#3b82f6'
      }
    };

    if (presets[preset]) {
      setNewTheme((prev: any) => ({ ...prev, ...presets[preset] }));
    }
  };

  return (
    <>
      <Card>
        <div className="flex items-center justify-between mb-4">
          <div className="flex items-center space-x-2">
            <Palette className="w-5 h-5 text-purple-400" />
            <h3 className="text-lg font-semibold" style={{ color: 'var(--theme-text-primary)' }}>
              Theme Management
            </h3>
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
                  onMouseEnter={(e) => e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)'}
                  onMouseLeave={(e) => e.currentTarget.style.backgroundColor = 'transparent'}
                  title="Create new theme"
                >
                  <Plus className="w-4 h-4" />
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
                title="Authentication required"
              >
                <Lock className="w-4 h-4" />
              </button>
            )}
            <button
              onClick={loadThemes}
              disabled={loading}
              className="p-2 rounded-lg transition-colors"
              style={{
                color: 'var(--theme-text-muted)',
                backgroundColor: 'transparent'
              }}
              onMouseEnter={(e) => e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)'}
              onMouseLeave={(e) => e.currentTarget.style.backgroundColor = 'transparent'}
              title="Refresh themes"
            >
              <RefreshCw className={`w-4 h-4 ${loading ? 'animate-spin' : ''}`} />
            </button>
          </div>
        </div>

        <div className="mb-6">
          <label className="block text-sm font-medium mb-2" style={{ color: 'var(--theme-text-secondary)' }}>
            Active Theme
          </label>
          <select
            value={previewTheme || currentTheme}
            onChange={(e) => handleThemeChange(e.target.value)}
            className="w-full rounded px-3 py-2 focus:outline-none"
            style={{
              backgroundColor: 'var(--theme-input-bg)',
              color: 'var(--theme-text-primary)',
              borderWidth: '1px',
              borderStyle: 'solid',
              borderColor: 'var(--theme-input-border)'
            }}
            disabled={loading}
          >
            {themes.map(theme => (
              <option key={theme.meta.id} value={theme.meta.id}>
                {theme.meta.name} {theme.meta.author && theme.meta.author !== 'System' && `by ${theme.meta.author}`}
                {isSystemTheme(theme.meta.id) && ' (System)'}
                {previewTheme === theme.meta.id && ' (Preview)'}
              </option>
            ))}
          </select>
          {previewTheme && (
            <p className="text-xs mt-2" style={{ color: 'var(--theme-warning)' }}>
              Preview mode active. Select a theme to apply it permanently.
            </p>
          )}
        </div>

        {/* Theme List */}
        <div className="mb-6">
          <h4 className="text-sm font-medium mb-3" style={{ color: 'var(--theme-text-secondary)' }}>
            Installed Themes ({themes.length})
          </h4>
          <div className="space-y-2 max-h-64 overflow-y-auto">
            {themes.map(theme => (
              <div
                key={theme.meta.id}
                className="rounded p-3 flex items-center justify-between border-2 transition-colors"
                style={{
                  backgroundColor: 'var(--theme-card-bg)',
                  borderColor: currentTheme === theme.meta.id && !previewTheme
                    ? 'var(--theme-primary)'
                    : previewTheme === theme.meta.id
                      ? 'var(--theme-warning)'
                      : 'transparent'
                }}
              >
                <div className="flex-1">
                  <div className="flex items-center gap-2">
                    <span className="font-medium" style={{ color: 'var(--theme-text-primary)' }}>
                      {theme.meta.name}
                    </span>
                    {theme.meta.isDark ? (
                      <Moon className="w-3 h-3" style={{ color: 'var(--theme-text-muted)' }} />
                    ) : (
                      <Sun className="w-3 h-3" style={{ color: 'var(--theme-warning)' }} />
                    )}
                    {currentTheme === theme.meta.id && !previewTheme && (
                      <span className="px-2 py-0.5 text-xs rounded"
                        style={{
                          backgroundColor: 'var(--theme-primary)',
                          color: 'var(--theme-button-text)'
                        }}>
                        Active
                      </span>
                    )}
                  </div>
                  {theme.meta.description && (
                    <p className="text-xs mt-1" style={{ color: 'var(--theme-text-muted)' }}>
                      {theme.meta.description}
                    </p>
                  )}
                </div>
                <div className="flex items-center gap-2">
                  <button
                    onClick={() => exportTheme(theme)}
                    className="p-2 transition-colors"
                    style={{ color: 'var(--theme-text-muted)' }}
                    title="Export theme"
                  >
                    <Download className="w-4 h-4" />
                  </button>
                  {currentTheme !== theme.meta.id && (
                    <button
                      onClick={() => handlePreview(theme.meta.id)}
                      className="p-2 transition-colors"
                      style={{ color: previewTheme === theme.meta.id ? 'var(--theme-warning)' : 'var(--theme-text-muted)' }}
                      title={previewTheme === theme.meta.id ? "Stop preview" : "Preview theme"}
                    >
                      {previewTheme === theme.meta.id ? <EyeOff className="w-4 h-4" /> : <Eye className="w-4 h-4" />}
                    </button>
                  )}
                  {!isSystemTheme(theme.meta.id) && isAuthenticated && (
                    <button
                      onClick={() => handleDelete(theme.meta.id, theme.meta.name)}
                      disabled={loading}
                      className="p-2 transition-colors disabled:opacity-50"
                      style={{ color: 'var(--theme-error)' }}
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

        {/* Upload Section */}
        {isAuthenticated && (
          <div className="mb-4">
            <h4 className="text-sm font-medium mb-3" style={{ color: 'var(--theme-text-secondary)' }}>
              Upload Custom Theme
            </h4>
            <div
              className={`border-2 border-dashed rounded-lg p-8 text-center transition-colors ${dragActive
                  ? 'border-purple-500 bg-purple-900 bg-opacity-20'
                  : 'border-gray-600 hover:border-gray-500'
                }`}
              onDragEnter={handleDrag}
              onDragLeave={handleDrag}
              onDragOver={handleDrag}
              onDrop={handleDrop}
            >
              <Upload className="w-12 h-12 mx-auto mb-3" style={{ color: 'var(--theme-text-muted)' }} />
              <p className="mb-2" style={{ color: 'var(--theme-text-secondary)' }}>
                Drag and drop a theme file here, or click to browse
              </p>
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
        )}

        {uploadError && (
          <Alert
            color="red"
            withCloseButton
            onClose={() => setUploadError(null)}
          >
            {uploadError}
          </Alert>
        )}

        {uploadSuccess && (
          <Alert
            color="green"
            withCloseButton
            onClose={() => setUploadSuccess(null)}
          >
            {uploadSuccess}
          </Alert>
        )}
      </Card>

      {/* Create/Edit Theme Modal */}
      <Modal
        opened={createModalOpen}
        onClose={() => setCreateModalOpen(false)}
        title="Create Custom Theme"
        size="xl"
      >
        <div className="space-y-6">
          {/* Theme Info */}
          <div className="space-y-4">
            <h4 className="text-sm font-semibold flex items-center gap-2" style={{ color: 'var(--theme-text-primary)' }}>
              <Info className="w-4 h-4" />
              Theme Information
            </h4>
            <div className="grid grid-cols-2 gap-4">
              <div>
                <label className="block text-sm font-medium mb-1" style={{ color: 'var(--theme-text-secondary)' }}>
                  Theme Name *
                </label>
                <input
                  type="text"
                  value={newTheme.name}
                  onChange={(e) => setNewTheme({ ...newTheme, name: e.target.value })}
                  placeholder="My Custom Theme"
                  className="w-full px-3 py-2 rounded focus:outline-none"
                  style={{
                    backgroundColor: 'var(--theme-input-bg)',
                    borderWidth: '1px',
                    borderStyle: 'solid',
                    borderColor: 'var(--theme-input-border)',
                    color: 'var(--theme-text-primary)'
                  }}
                />
              </div>
              <div>
                <label className="block text-sm font-medium mb-1" style={{ color: 'var(--theme-text-secondary)' }}>
                  Theme Type
                </label>
                <div className="flex gap-4 mt-2">
                  <label className="flex items-center gap-2 cursor-pointer">
                    <input
                      type="radio"
                      checked={newTheme.isDark}
                      onChange={() => setNewTheme({ ...newTheme, isDark: true })}
                      className="rounded"
                    />
                    <span className="text-sm" style={{ color: 'var(--theme-text-secondary)' }}>
                      Dark Theme
                    </span>
                  </label>
                  <label className="flex items-center gap-2 cursor-pointer">
                    <input
                      type="radio"
                      checked={!newTheme.isDark}
                      onChange={() => setNewTheme({ ...newTheme, isDark: false })}
                      className="rounded"
                    />
                    <span className="text-sm" style={{ color: 'var(--theme-text-secondary)' }}>
                      Light Theme
                    </span>
                  </label>
                </div>
              </div>
            </div>
          </div>

          {/* View Mode Selector */}
          <div className="flex items-center gap-4 p-4 rounded-lg" style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}>
            <span className="text-sm font-medium" style={{ color: 'var(--theme-text-secondary)' }}>
              Edit Mode:
            </span>
            <div className="flex gap-2">
              <button
                onClick={() => setViewMode('elements')}
                className={`px-3 py-1 rounded text-sm ${viewMode === 'elements' ? 'bg-blue-500 text-white' : 'bg-gray-700 text-gray-300'}`}
              >
                <Grid className="w-4 h-4 inline mr-1" />
                By UI Element
              </button>
              <button
                onClick={() => setViewMode('categories')}
                className={`px-3 py-1 rounded text-sm ${viewMode === 'categories' ? 'bg-blue-500 text-white' : 'bg-gray-700 text-gray-300'}`}
              >
                <Layers className="w-4 h-4 inline mr-1" />
                By Category
              </button>
            </div>
            <div className="flex-1" />
            <div className="flex gap-2">
              <select
                onChange={(e) => applyQuickPreset(e.target.value)}
                className="px-3 py-1 rounded text-sm"
                style={{
                  backgroundColor: 'var(--theme-input-bg)',
                  borderWidth: '1px',
                  borderStyle: 'solid',
                  borderColor: 'var(--theme-input-border)',
                  color: 'var(--theme-text-primary)'
                }}
              >
                <option value="">Quick Presets</option>
                <option value="dark-blue">Dark Blue</option>
                <option value="dark-purple">Dark Purple</option>
                <option value="dark-green">Dark Green</option>
                <option value="light">Light Theme</option>
              </select>
            </div>
          </div>

          <div className="flex items-center gap-4">
            <label className="flex items-center gap-2">
              <input
                type="checkbox"
                checked={quickEditMode}
                onChange={(e) => setQuickEditMode(e.target.checked)}
              />
              <span className="text-sm">Quick Edit Mode</span>
            </label>
            <label className="flex items-center gap-2">
              <input
                type="checkbox"
                checked={showAdvanced}
                onChange={(e) => setShowAdvanced(e.target.checked)}
              />
              <span className="text-sm">Show Advanced Options</span>
            </label>
          </div>

          {/* Search Bar */}
          <div className="relative">
            <Search className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-gray-400" />
            <input
              type="text"
              value={searchTerm}
              onChange={(e) => setSearchTerm(e.target.value)}
              placeholder="Search colors..."
              className="w-full pl-10 pr-10 py-2 rounded"
              style={{
                backgroundColor: 'var(--theme-input-bg)',
                borderWidth: '1px',
                borderStyle: 'solid',
                borderColor: 'var(--theme-input-border)',
                color: 'var(--theme-text-primary)'
              }}
            />
            {searchTerm && (
              <button
                onClick={() => setSearchTerm('')}
                className="absolute right-3 top-1/2 -translate-y-1/2"
              >
                <X className="w-4 h-4 text-gray-400" />
              </button>
            )}
          </div>

          {/* UI Elements Selector (for elements view) */}
          {viewMode === 'elements' && (
            <div className="grid grid-cols-2 md:grid-cols-4 gap-2">
              {uiElements.map(element => {
                const Icon = element.icon;
                const isSelected = selectedElement === element.id;

                return (
                  <button
                    key={element.id}
                    onClick={() => setSelectedElement(element.id)}
                    className={`p-3 rounded-lg border transition-all ${isSelected
                        ? 'border-blue-500 bg-blue-500 bg-opacity-10'
                        : 'border-gray-600 hover:border-gray-500'
                      }`}
                  >
                    <Icon className={`w-5 h-5 mb-1 ${isSelected ? 'text-blue-400' : 'text-gray-400'}`} />
                    <div className="text-xs font-medium">{element.name}</div>
                  </button>
                );
              })}
            </div>
          )}

          {/* Color Properties */}
          <div className="space-y-2 max-h-96 overflow-y-auto">
            {viewMode === 'elements' && filteredProperties.map(prop => (
              <div key={prop.key} className="p-3 rounded-lg border" style={{ borderColor: 'var(--theme-border-primary)' }}>
                <div className="flex items-center gap-3">
                  <div className="flex items-center gap-2">
                    <div
                      className="w-10 h-10 rounded border-2 cursor-pointer"
                      style={{
                        backgroundColor: newTheme[prop.key],
                        borderColor: 'var(--theme-border-secondary)'
                      }}
                      onClick={() => {
                        const input = document.createElement('input');
                        input.type = 'color';
                        input.value = newTheme[prop.key];
                        input.onchange = (e) => handleColorChange(prop.key, (e.target as HTMLInputElement).value);
                        input.click();
                      }}
                    />
                    <input
                      type="text"
                      value={newTheme[prop.key]}
                      onChange={(e) => handleColorChange(prop.key, e.target.value)}
                      className="w-24 px-2 py-1 text-xs font-mono rounded"
                      style={{
                        backgroundColor: 'var(--theme-input-bg)',
                        borderWidth: '1px',
                        borderStyle: 'solid',
                        borderColor: 'var(--theme-input-border)',
                        color: 'var(--theme-text-primary)'
                      }}
                    />
                  </div>
                  <div className="flex-1">
                    <div className="font-medium text-sm">{prop.label}</div>
                    <div className="text-xs" style={{ color: 'var(--theme-text-muted)' }}>
                      {prop.description}
                    </div>
                  </div>
                  <button
                    onClick={() => copyColor(newTheme[prop.key])}
                    className="p-1 rounded hover:bg-opacity-50"
                    style={{ backgroundColor: 'var(--theme-bg-hover)' }}
                    title="Copy color"
                  >
                    {copiedColor === newTheme[prop.key] ? (
                      <Check className="w-3 h-3" style={{ color: 'var(--theme-success)' }} />
                    ) : (
                      <Copy className="w-3 h-3" style={{ color: 'var(--theme-text-muted)' }} />
                    )}
                  </button>
                </div>
              </div>
            ))}

            {viewMode === 'categories' && colorGroups.map(group => {
              const Icon = group.icon;
              const isExpanded = expandedGroups.includes(group.name);

              return (
                <div key={group.name} className="border rounded-lg"
                  style={{ borderColor: 'var(--theme-border-primary)' }}>
                  <button
                    onClick={() => toggleGroup(group.name)}
                    className="w-full px-4 py-3 flex items-center justify-between hover:bg-opacity-50"
                    style={{ backgroundColor: isExpanded ? 'var(--theme-bg-tertiary)' : 'transparent' }}
                  >
                    <div className="flex items-center gap-3">
                      <Icon className="w-4 h-4" style={{ color: 'var(--theme-primary)' }} />
                      <div className="text-left">
                        <h5 className="text-sm font-semibold capitalize" style={{ color: 'var(--theme-text-primary)' }}>
                          {group.name.replace(/([A-Z])/g, ' $1').trim()}
                        </h5>
                        <p className="text-xs" style={{ color: 'var(--theme-text-muted)' }}>
                          {group.description}
                        </p>
                      </div>
                    </div>
                    {isExpanded ? <ChevronDown className="w-4 h-4" /> : <ChevronRight className="w-4 h-4" />}
                  </button>

                  {isExpanded && (
                    <div className="p-4 border-t space-y-3" style={{ borderColor: 'var(--theme-border-primary)' }}>
                      {group.colors.map(color => (
                        <div key={color.key} className="flex items-center gap-3">
                          <div className="flex items-center gap-2">
                            <div
                              className="w-10 h-10 rounded border-2 cursor-pointer"
                              style={{
                                backgroundColor: newTheme[color.key],
                                borderColor: 'var(--theme-border-secondary)'
                              }}
                              onClick={() => {
                                const input = document.createElement('input');
                                input.type = 'color';
                                input.value = newTheme[color.key];
                                input.onchange = (e) => handleColorChange(color.key, (e.target as HTMLInputElement).value);
                                input.click();
                              }}
                            />
                            <input
                              type="text"
                              value={newTheme[color.key]}
                              onChange={(e) => handleColorChange(color.key, e.target.value)}
                              className="w-24 px-2 py-1 text-xs font-mono rounded"
                              style={{
                                backgroundColor: 'var(--theme-input-bg)',
                                borderWidth: '1px',
                                borderStyle: 'solid',
                                borderColor: 'var(--theme-input-border)',
                                color: 'var(--theme-text-primary)'
                              }}
                            />
                          </div>
                          <div className="flex-1">
                            <div className="font-medium text-sm">{color.label}</div>
                            <div className="text-xs" style={{ color: 'var(--theme-text-muted)' }}>
                              {color.description}
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

          {/* Actions */}
          <div className="flex justify-end space-x-3 pt-4 border-t" style={{ borderColor: 'var(--theme-border-primary)' }}>
            <Button
              variant="default"
              onClick={() => setCreateModalOpen(false)}
            >
              Cancel
            </Button>
            <Button
              variant="filled"
              leftSection={<Save className="w-4 h-4" />}
              onClick={handleCreateTheme}
              disabled={!newTheme.name || !isAuthenticated}
              style={{
                backgroundColor: 'var(--theme-button-bg)',
                color: 'var(--theme-button-text)'
              }}
            >
              Create Theme
            </Button>
          </div>
        </div>
      </Modal>
    </>
  );
};

export default ThemeManager;