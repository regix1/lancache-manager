import assert from 'node:assert/strict';
import test from 'node:test';
import ts from 'typescript';
import { bindLifted, findSoleNode, parseSource } from './transpile-module.mjs';

const path = 'src/components/features/management/sections/PrefillSessionsSection.tsx';
const source = parseSource(path, ts.ScriptKind.TSX);

for (const action of ['handleTerminateSession', 'handleTerminateAll', 'handleBanBySession']) {
  test(`${action} refreshes committed partial results before releasing its retry control`, async () => {
    for (const failed of [true, false]) {
      const events = [];
      let release;
      const refresh = new Promise((resolve) => {
        release = resolve;
      });
      const request = async () => {
        events.push('request');
        if (failed)
          throw Object.assign(new Error('Access changed; cleanup is incomplete. Retry.'), {
            status: 503
          });
        return { count: 2 };
      };
      const declaration = findSoleNode(
        source,
        action,
        (node) => ts.isVariableDeclaration(node) && node.name.getText(source) === action
      );
      const run = bindLifted(declaration.initializer.getText(source), {
        setTerminatingSession: (value) => events.push(['terminating', value]),
        setTerminatingAll: (value) => events.push(['all', value]),
        setBanningSession: (value) => events.push(['banning', value]),
        setTerminateAllConfirm: (value) => events.push(['confirm', value]),
        setBanConfirm: (value) => events.push(['confirm', value]),
        banConfirm: { sessionId: 'guest-steam', reason: '' },
        ApiError: Error,
        ApiService: {
          terminatePrefillSession: request,
          terminateAllPrefillSessions: request,
          banPrefillUserBySession: request
        },
        onSuccess: (message) => events.push(['success', message]),
        onError: (message) => events.push(['error', message]),
        getErrorMessage: (error) => error.message,
        t: (key) => key,
        loadSessions: async () => {
          events.push('sessions');
          await refresh;
        },
        loadBans: async () => {
          events.push('bans');
          await refresh;
        }
      });
      const pending = run('guest-steam');
      await Promise.resolve();
      await Promise.resolve();
      assert.ok(events.includes('sessions'));
      assert.equal(events.includes('bans'), action === 'handleBanBySession');
      assert.equal(
        events.some((event) => Array.isArray(event) && event[0] === 'success'),
        !failed
      );
      assert.equal(
        events.some((event) => Array.isArray(event) && event[0] === 'error'),
        failed
      );
      if (failed)
        assert.equal(
          events.some((event) => Array.isArray(event) && event[0] === 'confirm'),
          action !== 'handleTerminateSession'
        );
      const busy = events[0][0];
      assert.equal(events.filter((event) => Array.isArray(event) && event[0] === busy).length, 1);
      release();
      await pending;
      assert.deepEqual(events.at(-1), [busy, action === 'handleTerminateAll' ? false : null]);
    }
  });
}
