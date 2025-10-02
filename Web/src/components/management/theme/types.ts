import { ElementType } from 'react';

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
  };
  colors: any;
  custom?: any;
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
