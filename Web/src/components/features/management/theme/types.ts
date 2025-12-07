import { type ElementType } from 'react';

// Theme color values - all optional as not all themes define every color
export interface ThemeColors {
  // Core colors
  primaryColor?: string;
  secondaryColor?: string;
  accentColor?: string;
  // Backgrounds
  bgPrimary?: string;
  bgSecondary?: string;
  bgTertiary?: string;
  bgHover?: string;
  // Text
  textPrimary?: string;
  textSecondary?: string;
  textMuted?: string;
  textAccent?: string;
  textPlaceholder?: string;
  // And many more optional color properties...
  [key: string]: string | undefined;
}

export interface Theme {
  meta: {
    id: string;
    name: string;
    description?: string;
    author?: string;
    version?: string;
    isDark?: boolean;
    sharpCorners?: boolean;
    disableFocusOutlines?: boolean;
    isCommunityTheme?: boolean; // Marks themes imported from community
    basedOn?: string; // Original theme ID if this is a custom version
  };
  colors: ThemeColors;
  custom?: Record<string, string>;
  css?: { content?: string };
}

export interface ColorGroup {
  name: string;
  icon: ElementType;
  description: string;
  colors: ColorDefinition[];
}

export interface ColorDefinition {
  key: string;
  label: string;
  description: string;
  affects: string[];
  value?: string;
  supportsAlpha?: boolean;
  pages?: string[];
}

export interface PageGroup {
  name: string;
  label: string;
  icon: ElementType;
  description: string;
}

export interface ThemeManagerProps {
  isAuthenticated: boolean;
}

/** Editable theme form data - flattened theme with metadata and colors */
export interface EditableTheme {
  name: string;
  description: string;
  author: string;
  version: string;
  isDark: boolean;
  customCSS: string;
  /** Color values accessed by key (e.g., primaryColor, bgPrimary, etc.) */
  [colorKey: string]: string | boolean;
}
