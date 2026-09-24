import assert from 'node:assert/strict';
import test from 'node:test';
import { compileToUrl } from './transpile-module.mjs';

const loadEntity = async () => {
  const moduleUrl = await compileToUrl(
    '../src/components/features/management/game-detection/gameRemovalEntity.ts'
  );
  return import(moduleUrl);
};

test('classifyGameFromCacheInfo covers steam, epic, named', async () => {
  const { classifyGameFromCacheInfo } = await loadEntity();

  const steam = classifyGameFromCacheInfo({
    game_app_id: 480,
    game_name: 'Spacewar',
    service: 'steam'
  });
  assert.equal(steam.kind, 'steamGame');

  const epicEmptyName = classifyGameFromCacheInfo({
    game_app_id: 0,
    game_name: '',
    service: 'epicgames',
    epic_app_id: 'cat-empty'
  });
  assert.equal(epicEmptyName.kind, 'epicGame');

  const blizzard = classifyGameFromCacheInfo({
    game_app_id: 0,
    game_name: 'Diablo IV',
    service: 'blizzard'
  });
  assert.equal(blizzard.kind, 'namedGame');
});
