import { colorGroups } from '../../features/management/theme/constants';
import {
  type ColorDefinition,
  type EditableTheme,
  type Theme,
  type ThemeColors
} from '../../features/management/theme/types';

/**
 * Uniform control size across the create and edit theme modals. `md` is the 40px row height
 * (44px on a phone) the rest of Management sits on; every Button and SegmentedControl on these
 * two surfaces reads from this so the row cannot drift to mixed sizes.
 */
export const THEME_MODAL_CONTROL_SIZE = 'md' as const;

/**
 * The colors the Basics pane exposes, in the order it shows them: brand, surfaces, text, border,
 * then the three status hues. `normalizeThemeColors` fills the rest of the palette from these when
 * a theme is applied, so setting only this set already produces a complete theme.
 */
const BASE_COLOR_KEYS = [
  'primaryColor',
  'secondaryColor',
  'accentColor',
  'bgPrimary',
  'bgSecondary',
  'bgTertiary',
  'textPrimary',
  'textSecondary',
  'textMuted',
  'borderPrimary',
  'success',
  'warning',
  'error'
];

const colorsByKey = new Map(
  colorGroups.flatMap((group) => group.colors).map((color) => [color.key, color])
);

/**
 * Definitions for the base keys. A key the group data does not carry is skipped rather than
 * rendered as a blank row - `secondaryColor` has a shipped default and a theme card that shows it
 * but has not always been in the editor's group list.
 */
export const THEME_BASE_COLORS: ColorDefinition[] = BASE_COLOR_KEYS.map((key) =>
  colorsByKey.get(key)
).filter((color): color is ColorDefinition => color !== undefined);

/**
 * How long the draft must hold still before the live preview repaints. The color picker reports a
 * value on every pointer move during a drag, and each apply rewrites the whole stylesheet, so the
 * preview follows the settle rather than the drag.
 */
export const THEME_PREVIEW_SETTLE_MS = 200;

/** Identifies the draft while it is on screen; nothing persists under it. */
const PREVIEW_THEME_ID = 'theme-draft-preview';

/**
 * The draft as a theme the theme service can apply. The editor holds metadata and colors in one
 * flat record and `applyTheme` wants them separated.
 */
export function toPreviewTheme(draft: EditableTheme): Theme {
  const { name, description, author, version, isDark, customCSS, ...colors } = draft;
  return {
    meta: { id: PREVIEW_THEME_ID, name, description, author, version, isDark },
    colors: colors as ThemeColors,
    css: customCSS ? { content: customCSS } : undefined
  };
}
