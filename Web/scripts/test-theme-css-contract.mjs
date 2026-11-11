import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { compileToUrl, moduleUrl } from './transpile-module.mjs';

const __dirname = dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = resolve(__dirname, '../..');
const THEMES_ROOT = join(REPO_ROOT, 'community-themes');
const THEME_SERVICE = join(REPO_ROOT, 'Web/src/services/theme.service.ts');
const BUTTONS_CSS = join(REPO_ROOT, 'Web/src/styles/components/buttons.css');
const WEB_SRC = join(REPO_ROOT, 'Web/src');

const forbiddenThemeRules = [
  {
    name: 'transition: all',
    pattern: /\btransition\s*:\s*all\b/i
  },
  {
    name: 'universal focus selector',
    pattern: /(?:^|[,{])\s*\*\s*:focus-visible\b/im
  },
  {
    name: 'removed focus outline',
    pattern: /\boutline\s*:\s*none\b/i
  },
  {
    name: 'bare button interaction selector',
    pattern: /^\s*button(?:\s*:(?:hover|active|focus|focus-visible))?\s*(?:,|\{)/im
  },
  {
    name: 'global themed-card hover',
    pattern: /\.themed-card\s*:\s*hover\b/i
  },
  {
    name: 'global themed hover background image',
    pattern: /\.hover\\\\:bg-themed-hover\s*:\s*hover\b/i
  }
];

function extractCss(source) {
  return source.match(/\[css\]\s*content\s*=\s*"""([\s\S]*?)"""/m)?.[1] ?? null;
}

function readCommunityThemeCss() {
  return readdirSync(THEMES_ROOT)
    .filter((name) => name.endsWith('.toml'))
    .sort()
    .map((name) => ({
      name,
      css: extractCss(readFileSync(join(THEMES_ROOT, name), 'utf8'))
    }))
    .filter((theme) => theme.css !== null);
}

test('community theme CSS leaves interaction states to application components', () => {
  const violations = [];
  for (const theme of readCommunityThemeCss()) {
    for (const rule of forbiddenThemeRules) {
      if (rule.pattern.test(theme.css)) {
        violations.push(`${theme.name}: ${rule.name}`);
      }
    }
  }

  assert.deepEqual(violations, []);
});

test('community theme CSS still permits static theme decoration', () => {
  const cssBlocks = readCommunityThemeCss().map((theme) => theme.css);
  assert.ok(cssBlocks.length > 0, 'expected at least one community theme CSS block');
  assert.ok(
    cssBlocks.some((css) => css.includes('background-image:')),
    'expected a static card texture to exercise the allowed path'
  );
  assert.ok(
    cssBlocks.some((css) => css.includes('::-webkit-scrollbar-thumb')),
    'expected custom scrollbar decoration to exercise the allowed path'
  );
});

test('generated theme CSS does not transition every element', () => {
  const source = readFileSync(THEME_SERVICE, 'utf8');
  assert.doesNotMatch(source, /body\s*\*\s*\{[^}]*\btransition\s*:/s);
});

const loadThemeService = async () =>
  compileToUrl('../src/services/theme.service.ts', {
    '@/i18n': moduleUrl(`export default { t: (k) => k };`),
    '../utils/constants': moduleUrl(`export const API_BASE = '/api';`),
    '@utils/constants': moduleUrl(`export const APP_EVENTS = { SHOW_TOAST: 'show-toast' };`),
    '@utils/error': moduleUrl(`export const getErrorMessage = (error) => error.message;`),
    '@utils/storage': moduleUrl(
      `export const storage = { getItem: () => null, setItem: () => {}, removeItem: () => {} };`
    ),
    './auth.service': moduleUrl(`export default { authMode: 'authenticated' };`),
    './preferences.service': moduleUrl(
      `export default { setPreference: async () => true, loadPreferences: async () => ({}) };`
    ),
    toml: moduleUrl(`export const parse = () => ({});`),
    './themeSchema': await compileToUrl('../src/services/themeSchema.ts'),
    './apiError': moduleUrl(`export const assertOk = async (response) => response;`)
  }).then(async (url) => (await import(url)).default);

/** Returns the theme color key behind the `background-color` token of the rule with this exact selector. */
const neutralFillKey = (css, tokenKeys, selector) => {
  const start = css.indexOf(`${selector} {`);
  if (start === -1) return null;
  const body = css.slice(start, css.indexOf('}', start));
  const token = body.match(/background-color:\s*var\((--[\w-]+)\)/)?.[1];
  assert.ok(token, `${selector} sets no background-color token`);
  assert.ok(tokenKeys.has(token), `${token} is not a theme color token`);
  return tokenKeys.get(token);
};

test('neutral buttons rest on a fill that stands off the light surfaces', async () => {
  const css = readFileSync(BUTTONS_CSS, 'utf8');
  const tokenKeys = new Map(
    [
      ...readFileSync(THEME_SERVICE, 'utf8').matchAll(/(--theme-[\w-]+):\s*\$\{colors\.(\w+)\}/g)
    ].map((match) => [match[1], match[2]])
  );
  const sharedRestKey = neutralFillKey(css, tokenKeys, '.btn-field-surface');
  // With no light rule the shared rule paints light themes too.
  const lightRestKey =
    neutralFillKey(css, tokenKeys, ":where([data-theme='light']) .btn-field-surface") ??
    sharedRestKey;
  const hoverKey = neutralFillKey(
    css,
    tokenKeys,
    '.btn-field-surface:not(.is-open):not(:disabled):hover'
  );

  const themes = (await loadThemeService()).getBuiltInThemes();
  assert.ok(
    themes.some((theme) => !theme.meta.isDark),
    'expected at least one built-in light theme'
  );
  for (const theme of themes) {
    if (theme.meta.isDark) {
      assert.equal(sharedRestKey, 'inputBg', `${theme.meta.id}: dark rest fill moved off inputBg`);
      continue;
    }
    const rest = theme.colors[lightRestKey].toLowerCase();
    for (const key of ['cardBg', 'bgSecondary', 'bgPrimary', hoverKey]) {
      assert.notEqual(
        rest,
        theme.colors[key].toLowerCase(),
        `${theme.meta.id}: resting neutral fill ${lightRestKey} equals ${key}`
      );
    }
  }
});

/** Returns the body of the dark-theme rule whose selector list holds this exact selector, or null. */
const darkRuleBody = (file, selector) => {
  const css = readFileSync(join(WEB_SRC, file), 'utf8');
  const match = css.match(
    new RegExp(
      `\\[data-theme='dark'\\] ${selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\s*[,{][^}]*`
    )
  );
  return match?.[0] ?? null;
};

// Menus, popovers, tooltips and the color picker: dark themes lift them with a lighter fill
// and a border instead of a shadow.
const floatingLayers = [
  [
    'components/features/management/schedules/ScheduleIntervalPicker.css',
    '.schedule-interval-popover'
  ],
  ['styles/components/dropdowns.css', '.ed-dropdown'],
  ['styles/components/dropdowns.css', '.msd-dropdown'],
  ['styles/components/dropdowns.css', '.github-dropdown'],
  ['styles/components/dropdowns.css', '.am-dropdown'],
  ['styles/components/dropdowns.css', '.dash-hidden-dropdown'],
  ['styles/components/help-popover.css', '.help-popover'],
  ['styles/features/user.css', '.tooltip-edge'],
  ['styles/features/events.css', '.calendar-settings-popover'],
  ['styles/features/events.css', '.calendar-day-popover'],
  ['styles/features/theme-editor-form.css', '.theme-color-popup']
];

// Cards, bars and hover lifts keep their fill and edge and only lose the shadow.
const raisedLayers = [
  ['components/common/CondensedNotificationStrip.css', '.condensed-strip-panel'],
  ['components/ui/BackToTopButton.css', '.back-to-top-button'],
  ['styles/components/error-boundary.css', '.error-boundary-container'],
  ['styles/features/downloads.css', '.dl-card:not(.dl-card-expanded):hover'],
  ['styles/features/downloads.css', '.card-grid-item:hover'],
  ['styles/features/downloads.css', '.card-grid-item:active'],
  ['styles/features/downloads.css', '.active-downloads-view .download-item:hover'],
  ['styles/components/dropdowns.css', '.msd-checkbox-selected'],
  ['styles/components/cards.css', '.no-dark-shadow']
];

test('dark themes float menus and popovers on a lighter fill and a border, with no shadow', () => {
  const missing = [];
  for (const [file, selector] of floatingLayers) {
    const body = darkRuleBody(file, selector);
    for (const declaration of [
      'background-color: var(--theme-bg-surface);',
      'border-color: var(--theme-card-border);',
      'box-shadow: none;'
    ]) {
      if (!body?.includes(declaration)) missing.push(`${file} ${selector}: ${declaration}`);
    }
  }
  // The header strip takes the menu's fill, so it is not a darker band inside a lighter menu.
  const header = darkRuleBody('styles/components/dropdowns.css', '.github-dropdown-header');
  if (!header?.includes('background-color: var(--theme-bg-surface);')) {
    missing.push(
      'dropdowns.css .github-dropdown-header: background-color: var(--theme-bg-surface);'
    );
  }
  assert.deepEqual(missing, []);
});

test('dark community themes lighten a hovered neutral button by a visible step', () => {
  // A neutral button rests on inputBg and takes bgSurfaceHover on hover; 12 is the smallest
  // per-channel step the other dark themes ship.
  const tooFaint = [];
  for (const file of readdirSync(THEMES_ROOT).filter((name) => name.endsWith('.toml'))) {
    const toml = readFileSync(join(THEMES_ROOT, file), 'utf8');
    if (!/^isDark\s*=\s*true/m.test(toml)) continue;
    const hover = toml.match(/^bgSurfaceHover\s*=\s*"(#[0-9a-fA-F]{6})"/m)[1];
    const rest = toml.match(/^inputBg\s*=\s*"(#[0-9a-fA-F]{6})"/m)[1];
    const step = Math.max(
      ...[1, 3, 5].map((i) =>
        Math.abs(parseInt(hover.slice(i, i + 2), 16) - parseInt(rest.slice(i, i + 2), 16))
      )
    );
    if (step < 12) tooFaint.push(`${file}: step ${step}`);
  }
  assert.deepEqual(tooFaint, []);
});

test('dark themes draw no shadow on cards, bars and hover lifts', () => {
  const missing = raisedLayers
    .filter(([file, selector]) => !darkRuleBody(file, selector)?.includes('box-shadow: none;'))
    .map(([file, selector]) => `${file} ${selector}`);
  assert.deepEqual(missing, []);
});

test('no stylesheet targets the data-theme value dark-default, which the app never sets', () => {
  const hits = readdirSync(WEB_SRC, { recursive: true })
    .filter((name) => name.endsWith('.css'))
    .filter((name) =>
      readFileSync(join(WEB_SRC, name), 'utf8').includes("[data-theme='dark-default']")
    );
  assert.deepEqual(hits, []);
});

test('components drop their dark-theme shadows through the shared classes', () => {
  const source = (file) => readFileSync(join(WEB_SRC, file), 'utf8');
  const count = (file, text) => source(file).split(text).length - 1;
  assert.doesNotMatch(source('components/common/StatCard.tsx'), /hover:shadow-lg/);
  assert.doesNotMatch(
    source('components/features/management/theme/ThemeCard.tsx'),
    /hover:shadow-md/
  );
  assert.equal(count('components/common/UniversalNotificationBar.tsx', 'no-dark-shadow'), 1);
  assert.equal(count('components/ui/ToggleSwitch.tsx', 'no-dark-shadow'), 1);
  assert.equal(count('components/features/memory/MemoryDiagnostics.tsx', 'no-dark-shadow'), 3);
  assert.equal(
    count('components/features/management/theme/ImprovedColorPicker.tsx', 'theme-color-popup'),
    1
  );
  assert.match(
    source('components/features/dashboard/ServiceAnalyticsChart/DoughnutChart.tsx'),
    /'--theme-bg-surface'/
  );
});
