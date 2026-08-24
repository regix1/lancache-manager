import assert from 'node:assert/strict';
import test from 'node:test';
import { compileToUrl } from './transpile-module.mjs';

const loadEntity = async () => {
  const moduleUrl = await compileToUrl(
    '../src/components/features/management/game-detection/gameRemovalEntity.ts'
  );
  return import(moduleUrl);
};

test('classifyGameFromCacheInfo and identity matchers cover steam, epic, named', async () => {
  const { classifyGameFromCacheInfo, matchesGameRemovalIdentity, matchesGameRemovalComplete } =
    await loadEntity();

  const steam = classifyGameFromCacheInfo({
    game_app_id: 480,
    game_name: 'Spacewar',
    service: 'steam'
  });
  assert.equal(steam.kind, 'steamGame');
  assert.equal(matchesGameRemovalIdentity({ gameAppId: 480 }, steam), true);
  assert.equal(matchesGameRemovalIdentity({ gameAppId: 570 }, steam), false);

  const epicEmptyName = classifyGameFromCacheInfo({
    game_app_id: 0,
    game_name: '',
    service: 'epicgames',
    epic_app_id: 'cat-empty'
  });
  assert.equal(epicEmptyName.kind, 'epicGame');
  assert.equal(
    matchesGameRemovalIdentity({ epicAppId: 'cat-empty', gameName: '' }, epicEmptyName),
    true
  );

  const blizzard = classifyGameFromCacheInfo({
    game_app_id: 0,
    game_name: 'Diablo IV',
    service: 'blizzard'
  });
  const xboxSameName = classifyGameFromCacheInfo({
    game_app_id: 0,
    game_name: 'Diablo IV',
    service: 'xbox'
  });
  assert.equal(blizzard.kind, 'namedGame');
  assert.equal(
    matchesGameRemovalIdentity({ gameName: 'Diablo IV', service: 'blizzard' }, blizzard),
    true
  );
  assert.equal(
    matchesGameRemovalIdentity({ gameName: 'Diablo IV', service: 'xbox' }, blizzard),
    false
  );
  assert.equal(matchesGameRemovalIdentity({ gameName: 'Diablo IV' }, blizzard), true);
  assert.equal(matchesGameRemovalIdentity({ gameName: 'Diablo IV' }, xboxSameName), true);

  assert.equal(
    matchesGameRemovalComplete({ operationId: 'run-1', gameAppId: 480 }, steam, 'run-1'),
    true
  );
  assert.equal(
    matchesGameRemovalComplete({ operationId: 'run-2', gameAppId: 480 }, steam, 'wait-1'),
    false
  );
  assert.equal(matchesGameRemovalComplete({ gameAppId: 480 }, steam, null), true);
});

test('shouldPinOperationIdFromResponse refuses queued and already-running ids', async () => {
  const { shouldPinOperationIdFromResponse } = await loadEntity();

  assert.equal(
    shouldPinOperationIdFromResponse({ operationId: 'run', queued: false, alreadyRunning: false }),
    true
  );
  assert.equal(
    shouldPinOperationIdFromResponse({ operationId: 'wait', queued: true, alreadyRunning: false }),
    false
  );
  assert.equal(
    shouldPinOperationIdFromResponse({
      operationId: 'active',
      queued: false,
      alreadyRunning: true
    }),
    false
  );
  assert.equal(shouldPinOperationIdFromResponse({ queued: false, alreadyRunning: false }), false);
});
