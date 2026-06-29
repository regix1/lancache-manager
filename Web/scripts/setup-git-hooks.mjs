import { execSync } from 'node:child_process';
import { chmodSync, existsSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const hooksDir = path.join(scriptDir, 'git-hooks');

const hookNames = ['pre-commit', 'pre-push'];
for (const hookName of hookNames) {
  const hookPath = path.join(hooksDir, hookName);
  if (existsSync(hookPath)) {
    chmodSync(hookPath, 0o755);
  }
}

try {
  const repoRoot = execSync('git rev-parse --show-toplevel', { encoding: 'utf8' }).trim();
  const relativeHooksPath = path.relative(repoRoot, hooksDir);
  execSync(`git config core.hooksPath "${relativeHooksPath}"`, { cwd: repoRoot, stdio: 'inherit' });
  console.log(`Git hooks installed at ${relativeHooksPath}`);
} catch {
  console.warn('Skipping git hook install: not inside a git repository.');
}
