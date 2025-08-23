import { ThemeColors } from './types.mjs';

declare const themeColorsLight: ThemeColors;
declare const themeColorsDark: ThemeColors;
declare const semanticColors: {
    light: ThemeColors;
    dark: ThemeColors;
};

export { semanticColors, themeColorsDark, themeColorsLight };
