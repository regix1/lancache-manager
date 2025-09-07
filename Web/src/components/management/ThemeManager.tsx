import React, { useState, useEffect, useRef } from 'react';
import { Gamepad2 } from "lucide-react";
import { 
  Palette, Upload, Trash2, Check, Download, Eye, RefreshCw, 
  Lock, Plus, EyeOff, ChevronDown, ChevronRight, Info, Save, Copy,
  Sun, Moon, Brush, Layout, Type, Square, AlertCircle, Component
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
  const [expandedGroups, setExpandedGroups] = useState<string[]>(['core', 'backgrounds']);
  const [copiedColor, setCopiedColor] = useState<string | null>(null);
  
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
    
    customCSS: ''
  });

  const fileInputRef = useRef<HTMLInputElement>(null);

  const colorGroups: ColorGroup[] = [
    {
      name: 'core',
      icon: Palette,
      description: 'Main theme colors used throughout the interface',
      colors: [
        {
          key: 'primaryColor',
          label: 'Primary Color',
          description: 'Main brand color',
          affects: ['Buttons', 'Links', 'Active states', 'Focus rings']
        },
        {
          key: 'secondaryColor',
          label: 'Secondary Color',
          description: 'Complementary accent color',
          affects: ['Secondary buttons', 'Highlights', 'Purple text elements']
        },
        {
          key: 'accentColor',
          label: 'Accent Color',
          description: 'Additional accent for special elements',
          affects: ['Special badges', 'Tooltips', 'Notifications']
        }
      ]
    },
    {
      name: 'backgrounds',
      icon: Layout,
      description: 'Background colors for different UI layers',
      colors: [
        {
          key: 'bgPrimary',
          label: 'Primary Background',
          description: 'Main app background',
          affects: ['Body background', 'Main container']
        },
        {
          key: 'bgSecondary',
          label: 'Secondary Background',
          description: 'Card and panel backgrounds',
          affects: ['Cards', 'Panels', 'Modals']
        },
        {
          key: 'bgTertiary',
          label: 'Tertiary Background',
          description: 'Input and nested element backgrounds',
          affects: ['Input fields', 'Dropdown menus', 'Code blocks']
        },
        {
          key: 'bgHover',
          label: 'Hover Background',
          description: 'Background for hover states',
          affects: ['Button hovers', 'List item hovers', 'Menu item hovers']
        }
      ]
    },
    {
      name: 'text',
      icon: Type,
      description: 'Text colors for different content hierarchies',
      colors: [
        {
          key: 'textPrimary',
          label: 'Primary Text',
          description: 'Main text color',
          affects: ['Headings', 'Body text', 'Labels']
        },
        {
          key: 'textSecondary',
          label: 'Secondary Text',
          description: 'Less prominent text',
          affects: ['Descriptions', 'Subtitles', 'Help text']
        },
        {
          key: 'textMuted',
          label: 'Muted Text',
          description: 'Disabled or placeholder text',
          affects: ['Placeholders', 'Disabled labels', 'Timestamps']
        },
        {
          key: 'textAccent',
          label: 'Accent Text',
          description: 'Highlighted or linked text',
          affects: ['Links', 'Highlighted values', 'Active menu items']
        }
      ]
    },
    {
      name: 'borders',
      icon: Square,
      description: 'Border colors for separators and outlines',
      colors: [
        {
          key: 'borderPrimary',
          label: 'Primary Border',
          description: 'Main border color',
          affects: ['Card borders', 'Dividers', 'Table borders']
        },
        {
          key: 'borderSecondary',
          label: 'Secondary Border',
          description: 'Subtle borders',
          affects: ['Input borders', 'Section dividers']
        },
        {
          key: 'borderFocus',
          label: 'Focus Border',
          description: 'Border color for focused elements',
          affects: ['Focused inputs', 'Active tabs', 'Selected items']
        }
      ]
    },
    {
      name: 'status',
      icon: AlertCircle,
      description: 'Colors for status indicators and alerts',
      colors: [
        {
          key: 'success',
          label: 'Success',
          description: 'Success state color',
          affects: ['Success buttons', 'Progress bars', 'Online status']
        },
        {
          key: 'successBg',
          label: 'Success Background',
          description: 'Background for success elements',
          affects: ['Success alerts', 'Success badges']
        },
        {
          key: 'successText',
          label: 'Success Text',
          description: 'Text color for success states',
          affects: ['Success messages', 'Positive values']
        },
        {
          key: 'warning',
          label: 'Warning',
          description: 'Warning state color',
          affects: ['Warning buttons', 'Caution indicators']
        },
        {
          key: 'warningBg',
          label: 'Warning Background',
          description: 'Background for warning elements',
          affects: ['Warning alerts', 'Warning badges']
        },
        {
          key: 'warningText',
          label: 'Warning Text',
          description: 'Text color for warning states',
          affects: ['Warning messages', 'Caution text']
        },
        {
          key: 'error',
          label: 'Error',
          description: 'Error state color',
          affects: ['Error buttons', 'Delete actions', 'Offline status']
        },
        {
          key: 'errorBg',
          label: 'Error Background',
          description: 'Background for error elements',
          affects: ['Error alerts', 'Error badges']
        },
        {
          key: 'errorText',
          label: 'Error Text',
          description: 'Text color for error states',
          affects: ['Error messages', 'Validation errors']
        },
        {
          key: 'info',
          label: 'Info',
          description: 'Informational state color',
          affects: ['Info buttons', 'Info indicators']
        },
        {
          key: 'infoBg',
          label: 'Info Background',
          description: 'Background for info elements',
          affects: ['Info alerts', 'Info badges']
        },
        {
          key: 'infoText',
          label: 'Info Text',
          description: 'Text color for info states',
          affects: ['Info messages', 'Help content']
        }
      ]
    },
    {
      name: 'services',
      icon: Gamepad2,
      description: 'Colors for different gaming services',
      colors: [
        {
          key: 'steamColor',
          label: 'Steam',
          description: 'Steam service color',
          affects: ['Steam badges', 'Steam charts', 'Steam stats']
        },
        {
          key: 'epicColor',
          label: 'Epic Games',
          description: 'Epic Games service color',
          affects: ['Epic badges', 'Epic charts', 'Epic stats']
        },
        {
          key: 'originColor',
          label: 'Origin/EA',
          description: 'Origin/EA service color',
          affects: ['Origin badges', 'Origin charts', 'Origin stats']
        },
        {
          key: 'blizzardColor',
          label: 'Blizzard',
          description: 'Blizzard service color',
          affects: ['Blizzard badges', 'Blizzard charts', 'Blizzard stats']
        },
        {
          key: 'wsusColor',
          label: 'Windows Update',
          description: 'Windows Update service color',
          affects: ['WSUS badges', 'WSUS charts', 'WSUS stats']
        },
        {
          key: 'riotColor',
          label: 'Riot Games',
          description: 'Riot Games service color',
          affects: ['Riot badges', 'Riot charts', 'Riot stats']
        }
      ]
    },
    {
      name: 'components',
      icon: Component,
      description: 'Specific colors for UI components',
      colors: [
        {
          key: 'cardBg',
          label: 'Card Background',
          description: 'Background for card components',
          affects: ['Stat cards', 'Content cards', 'List items']
        },
        {
          key: 'cardBorder',
          label: 'Card Border',
          description: 'Border for card components',
          affects: ['Card outlines', 'Panel borders']
        },
        {
          key: 'buttonBg',
          label: 'Button Background',
          description: 'Primary button background',
          affects: ['Primary buttons', 'CTAs']
        },
        {
          key: 'buttonHover',
          label: 'Button Hover',
          description: 'Button hover state',
          affects: ['Button hover effects']
        },
        {
          key: 'buttonText',
          label: 'Button Text',
          description: 'Button text color',
          affects: ['Button labels']
        },
        {
          key: 'inputBg',
          label: 'Input Background',
          description: 'Form input background',
          affects: ['Text inputs', 'Selects', 'Textareas']
        },
        {
          key: 'inputBorder',
          label: 'Input Border',
          description: 'Form input border',
          affects: ['Input outlines']
        },
        {
          key: 'inputFocus',
          label: 'Input Focus',
          description: 'Focused input border',
          affects: ['Active input borders']
        },
        {
          key: 'badgeBg',
          label: 'Badge Background',
          description: 'Badge background color',
          affects: ['Status badges', 'Tags', 'Pills']
        },
        {
          key: 'badgeText',
          label: 'Badge Text',
          description: 'Badge text color',
          affects: ['Badge labels']
        },
        {
          key: 'progressBar',
          label: 'Progress Bar',
          description: 'Progress indicator color',
          affects: ['Progress bars', 'Loading bars']
        },
        {
          key: 'progressBg',
          label: 'Progress Background',
          description: 'Progress bar background',
          affects: ['Progress track']
        }
      ]
    },
    {
      name: 'icons',
      icon: Brush,
      description: 'Solid background colors for icon containers (no gradients)',
      colors: [
        {
          key: 'iconBgBlue',
          label: 'Blue Icon Background',
          description: 'Blue icon container background',
          affects: ['Database icons', 'Info icons']
        },
        {
          key: 'iconBgGreen',
          label: 'Green Icon Background',
          description: 'Green icon container background',
          affects: ['Success icons', 'Online status']
        },
        {
          key: 'iconBgEmerald',
          label: 'Emerald Icon Background',
          description: 'Emerald icon container background',
          affects: ['Trending icons', 'Growth indicators']
        },
        {
          key: 'iconBgPurple',
          label: 'Purple Icon Background',
          description: 'Purple icon container background',
          affects: ['Special features', 'Premium indicators']
        },
        {
          key: 'iconBgIndigo',
          label: 'Indigo Icon Background',
          description: 'Indigo icon container background',
          affects: ['Server icons', 'System indicators']
        },
        {
          key: 'iconBgOrange',
          label: 'Orange Icon Background',
          description: 'Orange icon container background',
          affects: ['Download icons', 'Activity indicators']
        },
        {
          key: 'iconBgYellow',
          label: 'Yellow Icon Background',
          description: 'Yellow icon container background',
          affects: ['Warning icons', 'Client indicators']
        },
        {
          key: 'iconBgCyan',
          label: 'Cyan Icon Background',
          description: 'Cyan icon container background',
          affects: ['Activity icons', 'Performance indicators']
        },
        {
          key: 'iconBgRed',
          label: 'Red Icon Background',
          description: 'Red icon container background',
          affects: ['Error icons', 'Critical indicators']
        }
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
      setUploadError(error.message);
    } finally {
      setLoading(false);
    }
  };

  const handleCreateTheme = async () => {
    if (!isAuthenticated) {
      setUploadError('Authentication required to create themes');
      return;
    }

    const theme: Theme = {
      meta: {
        id: newTheme.name.toLowerCase().replace(/\s+/g, '-'),
        name: newTheme.name,
        description: newTheme.description,
        author: newTheme.author,
        version: newTheme.version,
        isDark: newTheme.isDark
      },
      colors: { ...newTheme },
      css: newTheme.customCSS ? { content: newTheme.customCSS } : undefined
    };
    
    // Remove non-color properties from colors object
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
      
      // Reset form
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
        borderPrimary: '#374151',
        borderSecondary: '#4b5563',
        borderFocus: '#3b82f6',
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
        borderPrimary: '#374151',
        borderSecondary: '#4b5563',
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
        infoText: '#93c5fd'
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
        borderPrimary: '#e5e7eb',
        borderSecondary: '#d1d5db',
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
        infoText: '#1e40af'
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

# Borders
borderPrimary = "#374151"
borderSecondary = "#4b5563"
borderFocus = "#3b82f6"

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

  const isSystemTheme = (themeId: string) => 
    ['dark-default', 'light-default'].includes(themeId);

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
                    {previewTheme === theme.meta.id && (
                      <span className="px-2 py-0.5 text-xs rounded"
                        style={{ 
                          backgroundColor: 'var(--theme-warning)',
                          color: 'var(--theme-bg-primary)'
                        }}>
                        Preview
                      </span>
                    )}
                    {isSystemTheme(theme.meta.id) && (
                      <span className="px-2 py-0.5 text-xs rounded"
                        style={{ 
                          backgroundColor: 'var(--theme-bg-hover)',
                          color: 'var(--theme-text-muted)'
                        }}>
                        System
                      </span>
                    )}
                  </div>
                  {theme.meta.description && (
                    <p className="text-xs mt-1" style={{ color: 'var(--theme-text-muted)' }}>
                      {theme.meta.description}
                    </p>
                  )}
                  <div className="flex items-center gap-3 mt-1">
                    {theme.meta.author && (
                      <p className="text-xs" style={{ color: 'var(--theme-text-muted)' }}>
                        by {theme.meta.author}
                      </p>
                    )}
                    {theme.meta.version && (
                      <p className="text-xs" style={{ color: 'var(--theme-text-muted)' }}>
                        v{theme.meta.version}
                      </p>
                    )}
                  </div>
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
                  {currentTheme !== theme.meta.id && (
                    <button
                      onClick={() => handleThemeChange(theme.meta.id)}
                      className="p-2 transition-colors"
                      style={{ color: 'var(--theme-primary)' }}
                      title="Apply theme"
                    >
                      <Check className="w-4 h-4" />
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

        {isAuthenticated && (
          <>
            <div className="mb-4">
              <h4 className="text-sm font-medium mb-3" style={{ color: 'var(--theme-text-secondary)' }}>
                Upload Custom Theme
              </h4>
              <div
                className={`border-2 border-dashed rounded-lg p-8 text-center transition-colors ${
                  dragActive
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
                <p className="text-xs mb-3" style={{ color: 'var(--theme-text-muted)' }}>
                  TOML format, max 1MB
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

      <Modal
        opened={createModalOpen}
        onClose={() => setCreateModalOpen(false)}
        title="Create Custom Theme"
        size="xl"
      >
        <div className="space-y-6">
          {/* Theme Metadata */}
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
                  Author
                </label>
                <input
                  type="text"
                  value={newTheme.author}
                  onChange={(e) => setNewTheme({ ...newTheme, author: e.target.value })}
                  placeholder="Your Name"
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
            </div>
            <div>
              <label className="block text-sm font-medium mb-1" style={{ color: 'var(--theme-text-secondary)' }}>
                Description
              </label>
              <input
                type="text"
                value={newTheme.description}
                onChange={(e) => setNewTheme({ ...newTheme, description: e.target.value })}
                placeholder="A beautiful custom theme"
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
            <div className="flex items-center gap-4">
              <label className="flex items-center gap-2 cursor-pointer">
                <input
                  type="checkbox"
                  checked={newTheme.isDark}
                  onChange={(e) => setNewTheme({ ...newTheme, isDark: e.target.checked })}
                  className="rounded"
                />
                <span className="text-sm" style={{ color: 'var(--theme-text-secondary)' }}>
                  Dark Theme
                </span>
              </label>
              <button
                onClick={() => loadPresetColors('dark')}
                className="px-3 py-1 text-xs rounded flex items-center gap-1"
                style={{
                  backgroundColor: 'var(--theme-bg-tertiary)',
                  color: 'var(--theme-text-secondary)'
                }}
              >
                <Moon className="w-3 h-3" />
                Load Dark Preset
              </button>
              <button
                onClick={() => loadPresetColors('light')}
                className="px-3 py-1 text-xs rounded flex items-center gap-1"
                style={{
                  backgroundColor: 'var(--theme-bg-tertiary)',
                  color: 'var(--theme-text-secondary)'
                }}
              >
                <Sun className="w-3 h-3" />
                Load Light Preset
              </button>
            </div>
          </div>

          {/* Color Groups */}
          <div className="space-y-4 max-h-96 overflow-y-auto">
            {colorGroups.map((group) => {
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
                    <div className="p-4 border-t space-y-4" style={{ borderColor: 'var(--theme-border-primary)' }}>
                      {group.colors.map((color) => (
                        <div key={color.key} className="space-y-2">
                          <div className="flex items-center justify-between">
                            <div className="flex-1">
                              <label className="block text-sm font-medium" style={{ color: 'var(--theme-text-primary)' }}>
                                {color.label}
                              </label>
                              <p className="text-xs" style={{ color: 'var(--theme-text-muted)' }}>
                                {color.description}
                              </p>
                              <div className="flex flex-wrap gap-1 mt-1">
                                {color.affects.map((item, idx) => (
                                  <span key={idx} className="text-xs px-1.5 py-0.5 rounded"
                                    style={{ 
                                      backgroundColor: 'var(--theme-bg-hover)',
                                      color: 'var(--theme-text-secondary)'
                                    }}>
                                    {item}
                                  </span>
                                ))}
                              </div>
                            </div>
                            <div className="flex items-center gap-2">
                              <div className="relative">
                                <input
                                  type="color"
                                  value={newTheme[color.key]}
                                  onChange={(e) => handleColorChange(color.key, e.target.value)}
                                  className="w-12 h-8 rounded cursor-pointer"
                                  style={{ backgroundColor: newTheme[color.key] }}
                                />
                              </div>
                              <input
                                type="text"
                                value={newTheme[color.key]}
                                onChange={(e) => handleColorChange(color.key, e.target.value)}
                                className="w-24 px-2 py-1 text-xs rounded font-mono"
                                style={{
                                  backgroundColor: 'var(--theme-input-bg)',
                                  borderWidth: '1px',
                                  borderStyle: 'solid',
                                  borderColor: 'var(--theme-input-border)',
                                  color: 'var(--theme-text-primary)'
                                }}
                              />
                              <button
                                onClick={() => copyColor(newTheme[color.key])}
                                className="p-1 rounded hover:bg-opacity-50"
                                style={{ backgroundColor: 'var(--theme-bg-hover)' }}
                                title="Copy color"
                              >
                                {copiedColor === newTheme[color.key] ? (
                                  <Check className="w-3 h-3" style={{ color: 'var(--theme-success)' }} />
                                ) : (
                                  <Copy className="w-3 h-3" style={{ color: 'var(--theme-text-muted)' }} />
                                )}
                              </button>
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
            <label className="block text-sm font-medium mb-1" style={{ color: 'var(--theme-text-secondary)' }}>
              Custom CSS (Optional)
            </label>
            <textarea
              value={newTheme.customCSS}
              onChange={(e) => setNewTheme({ ...newTheme, customCSS: e.target.value })}
              placeholder="/* Add any custom CSS here */"
              rows={4}
              className="w-full px-3 py-2 rounded font-mono text-xs focus:outline-none"
              style={{
                backgroundColor: 'var(--theme-input-bg)',
                borderWidth: '1px',
                borderStyle: 'solid',
                borderColor: 'var(--theme-input-border)',
                color: 'var(--theme-text-primary)'
              }}
            />
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