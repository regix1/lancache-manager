#!/usr/bin/env node
/* global console, process */
/**
 * validate-badges.mjs
 *
 * CI script that checks for raw badge patterns in TSX files.
 * All badges should use the shared <Badge> or <EvictedBadge> components,
 * or at minimum use the `themed-badge` CSS class for consistent sizing.
 *
 * Catches:
 *   - Raw <span> with inline badge-like Tailwind classes instead of themed-badge
 *   - status-badge-* classes used without the themed-badge base class
 *
 * Usage:
 *   node scripts/validate-badges.mjs
 */

import { readFileSync, readdirSync } from 'fs';
import { join, resolve, dirname } from 'path';
import { fileURLToPath } from 'url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const SRC_DIR = resolve(__dirname, '..', 'src');

// Files that are allowed to use raw badge patterns (the Badge component itself, CSS files)
const ALLOWED_FILES = new Set(['Badge.tsx', 'EvictedBadge.tsx', 'EventList.tsx', 'badges.css']);

function* walkDir(dir) {
  let entries;
  try {
    entries = readdirSync(dir, { withFileTypes: true });
  } catch {
    return;
  }
  for (const entry of entries) {
    const full = join(dir, entry.name);
    if (entry.isDirectory()) {
      if (entry.name === 'node_modules' || entry.name === '.git') continue;
      yield* walkDir(full);
    } else if (entry.isFile() && (entry.name.endsWith('.tsx') || entry.name.endsWith('.ts'))) {
      yield { path: full, name: entry.name };
    }
  }
}

const errors = [];

// Inline badge-like Tailwind (px-N py-0.5 + font- + rounded) that should use themed-badge
const INLINE_BADGE =
  /className=["'`{][^"'`}]*px-[0-9]\.?[0-9]?\s+py-0\.[0-9][^"'`}]*font-(?:bold|extrabold|semibold|medium)[^"'`}]*rounded/g;

for (const { path, name } of walkDir(SRC_DIR)) {
  if (ALLOWED_FILES.has(name)) continue;

  let content;
  try {
    content = readFileSync(path, 'utf8');
  } catch {
    continue;
  }

  const lines = content.split('\n');
  const relPath = path.replace(SRC_DIR + '/', '').replace(SRC_DIR + '\\', '');

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];

    // Skip comments and imports
    if (
      line.trimStart().startsWith('//') ||
      line.trimStart().startsWith('*') ||
      line.trimStart().startsWith('import')
    )
      continue;

    // Check Pattern 1: status-badge-* without themed-badge
    if (
      line.includes('status-badge-') &&
      line.includes('className') &&
      !line.includes('themed-badge')
    ) {
      // Verify it's not a dynamic className where themed-badge is on a different line
      const contextStart = Math.max(0, i - 2);
      const contextEnd = Math.min(lines.length - 1, i + 2);
      const context = lines.slice(contextStart, contextEnd + 1).join('\n');
      if (!context.includes('themed-badge')) {
        errors.push({
          file: relPath,
          line: i + 1,
          type: 'status-badge without themed-badge',
          text: line.trim()
        });
      }
    }

    // Check Pattern 2: Inline badge Tailwind patterns
    if (
      INLINE_BADGE.test(line) &&
      !line.includes('themed-badge') &&
      !line.includes('button') &&
      !line.includes('input') &&
      !line.includes('tab-')
    ) {
      errors.push({
        file: relPath,
        line: i + 1,
        type: 'inline badge pattern (use themed-badge or <Badge>)',
        text: line.trim()
      });
    }
    INLINE_BADGE.lastIndex = 0; // Reset regex state
  }
}

if (errors.length > 0) {
  console.error(
    `\nBADGE LINT: Found ${errors.length} raw badge pattern(s) that should use themed-badge or <Badge>:\n`
  );
  for (const err of errors) {
    console.error(`  ${err.file}:${err.line}`);
    console.error(`    [${err.type}]`);
    console.error(`    ${err.text}\n`);
  }
  console.error('Fix: Use <Badge variant="..."> component or add "themed-badge" base class.\n');
  process.exit(1);
}

console.log('Badge lint: PASS (no raw badge patterns found)');
process.exit(0);
