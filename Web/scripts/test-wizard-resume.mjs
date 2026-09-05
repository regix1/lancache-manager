import assert from 'node:assert/strict';
import test from 'node:test';
import ts from 'typescript';
import { collectNodes, parseSource, transpile } from './transpile-module.mjs';

/**
 * The setup wizard writes the step it is on to the server and reads it back on the next visit, so
 * an operator who closes the browser halfway through returns to the step they left. The reading
 * half is two plain declarations inside the flow hook with no runtime imports, so they are lifted
 * out of the source and run here exactly as the wizard runs them.
 */

const hookFile = parseSource('src/hooks/useInitializationFlow.ts');

const { resolveInitialStep, normalizeServerStep } = (() => {
  const wanted = new Set([
    'resolveInitialStep',
    'resolveStepForPostgresMode',
    'normalizeServerStep'
  ]);
  const declarations = collectNodes(
    hookFile,
    (node) => ts.isFunctionDeclaration(node) && node.name && wanted.has(node.name.text)
  ).map((node) => node.getText(hookFile));
  assert.equal(declarations.length, wanted.size, 'missing a wizard entry-step declaration');

  const harness = `${declarations.join('\n')}\nmodule.exports = { resolveInitialStep, normalizeServerStep };`;
  const module = { exports: {} };
  new Function('module', 'exports', transpile(harness, ts.ModuleKind.CommonJS))(
    module,
    module.exports
  );
  return module.exports;
})();

/** Every step the wizard can be on, read from the InitStep union so a new step is covered. */
const wizardSteps = (() => {
  const alias = collectNodes(
    hookFile,
    (node) => ts.isTypeAliasDeclaration(node) && node.name.text === 'InitStep'
  );
  assert.equal(alias.length, 1, 'InitStep is not declared once');
  assert.ok(ts.isUnionTypeNode(alias[0].type), 'InitStep is not a union');
  return alias[0].type.types.map((member) => {
    assert.ok(ts.isLiteralTypeNode(member) && ts.isStringLiteral(member.literal));
    return member.literal.text;
  });
})();

// The account step is never resumed from the stored value: it is chosen from the account state,
// and a stored 'admin-account' would otherwise reopen the wizard on an installation that has one.
const resumableSteps = wizardSteps.filter((step) => step !== 'admin-account');

const install = (overrides) => ({
  isCompleted: false,
  hasProcessedLogs: false,
  needsPostgresCredentials: false,
  accountExists: true,
  currentSetupStep: null,
  dataSourceChoice: null,
  completedPlatforms: null,
  mode: 'embedded',
  postgresHost: null,
  postgresPort: null,
  postgresDatabase: null,
  postgresUser: null,
  ...overrides
});

test('every step the wizard can store is read back as itself', () => {
  assert.ok(resumableSteps.includes('platform-setup'));
  for (const step of resumableSteps) {
    assert.equal(normalizeServerStep(step), step);
  }
});

test('the retired API key step resumes at the step that followed it', () => {
  assert.equal(normalizeServerStep('api-key'), 'permissions-check');
});

test('a missing or unknown stored step is reported as none rather than guessed', () => {
  for (const stored of [null, '', 'admin-account', 'API-KEY', 'platform', 'permissions-check ']) {
    assert.equal(normalizeServerStep(stored), null, JSON.stringify(stored));
  }
});

test('a wizard left on the platform step reopens on the platform step', () => {
  const step = resolveInitialStep(
    install({ currentSetupStep: 'platform-setup', dataSourceChoice: null }),
    false
  );
  assert.equal(step, 'platform-setup');
});

test('every stored step past the database reopens where it was left', () => {
  const pastDatabase = resumableSteps.filter(
    (step) => !['database-setup', 'external-db-form', 'external-db-confirm'].includes(step)
  );
  for (const stored of pastDatabase) {
    for (const mode of ['embedded', 'external']) {
      assert.equal(
        resolveInitialStep(install({ currentSetupStep: stored, mode }), false),
        stored,
        `${stored} on ${mode}`
      );
    }
  }
});

test('a wizard stored on the database step still asks for the missing credentials', () => {
  // Reading the stored step as a later one would skip straight past the credentials the server is
  // still waiting for, and every step after that fails without a database.
  assert.equal(
    resolveInitialStep(
      install({ currentSetupStep: 'database-setup', needsPostgresCredentials: true }),
      false
    ),
    'database-setup'
  );
  assert.equal(
    resolveInitialStep(
      install({
        currentSetupStep: 'database-setup',
        needsPostgresCredentials: true,
        mode: 'external'
      }),
      false
    ),
    'external-db-form'
  );
  // Once the credentials are on disk the same stored step confirms the connection instead.
  assert.equal(
    resolveInitialStep(install({ currentSetupStep: 'database-setup' }), false),
    'external-db-confirm'
  );
});

test('a wizard with nothing stored starts at the database step', () => {
  assert.equal(
    resolveInitialStep(install({ needsPostgresCredentials: true }), false),
    'database-setup'
  );
  assert.equal(
    resolveInitialStep(install({ currentSetupStep: 'api-key' }), false),
    'permissions-check'
  );
});
