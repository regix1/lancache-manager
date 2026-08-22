import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const __dirname = dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = resolve(__dirname, '../..');
const THEMES_ROOT = join(REPO_ROOT, 'community-themes');
const THEME_SERVICE = join(REPO_ROOT, 'Web/src/services/theme.service.ts');

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
