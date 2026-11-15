import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
import { hookStore } from './component-hooks.mjs';
import { bindLifted, liftConstArrow, make, translator } from './transpile-module.mjs';

/**
 * A key rotation that could not remove the other accounts still answers with the new key, and its
 * answer carries a key saying the accounts were kept. The new-key dialog is the only screen the owner
 * sees after the rotation ends their session, so the shipped regenerate handler has to keep that key
 * from the answer and the shipped dialog has to show it, in both shipped languages.
 */

const MANAGER = 'src/components/features/management/steam/AuthenticationManager.tsx';
const DIALOG = 'src/components/modals/auth/ApiKeyRotatedModal.tsx';
const KEPT = 'management.auth.rotatedModal.accountsKept';

/** Every string in an element tree, children and element-valued props alike. */
const textOf = (node) => {
  if (node === null || node === undefined || typeof node === 'boolean') return [];
  if (typeof node === 'string' || typeof node === 'number') return [String(node)];
  if (Array.isArray(node)) return node.flatMap(textOf);
  if (typeof node === 'object' && node.props)
    return Object.values(node.props).flatMap((value) =>
      typeof value === 'function' ? [] : textOf(value)
    );
  return [];
};

/** Draws nothing of its own; its children stay in the tree for `textOf`. */
const part = (name) => {
  const component = () => null;
  Object.defineProperty(component, 'name', { value: name });
  return component;
};

const drawDialog = (locale, stageKey) => {
  const { hooks, draw } = hookStore();
  const Dialog = make(DIALOG, 'ApiKeyRotatedModal', {
    React: { ...React, ...hooks },
    useState: hooks.useState,
    useTranslation: () => ({ t: translator(locale) }),
    Key: part('Key'),
    Modal: part('Modal'),
    Button: part('Button'),
    Alert: part('Alert'),
    useCopyFeedback: () => [false, () => undefined],
    copyText: async () => true
  });
  return textOf(
    draw(() => Dialog({ opened: true, apiKey: 'lm_new', stageKey, onClose: () => undefined }))
  );
};

test('the regenerate handler keeps the kept-accounts key from the rotation answer', async () => {
  const set = {};
  const setter = (name) => (value) => {
    set[name] = value;
  };
  const regenerate = bindLifted(liftConstArrow(MANAGER, 'handleRegenerate'), {
    setRegenerating: setter('regenerating'),
    setRegenerateError: setter('regenerateError'),
    setShowRegenerateConfirm: setter('showRegenerateConfirm'),
    setRotatedKey: setter('rotatedKey'),
    setRotatedStageKey: setter('rotatedStageKey'),
    fetch: async () => ({}),
    ApiService: {
      getFetchOptions: (options) => options,
      handleResponse: async () => ({ apiKey: 'lm_new', stageKey: KEPT })
    },
    getErrorMessage: (error) => String(error)
  });

  await regenerate();

  assert.equal(set.rotatedKey, 'lm_new', 'the handler did not keep the new key');
  assert.equal(
    set.rotatedStageKey,
    KEPT,
    'the handler dropped the answer key that says the other accounts were kept'
  );
});

test('the new-key dialog says the other accounts were kept, in en and zh', () => {
  for (const locale of ['en', 'zh']) {
    const shown = drawDialog(locale, KEPT);
    assert.ok(
      shown.includes(translator(locale)(KEPT)),
      `the ${locale} dialog does not say the other accounts were kept; it shows ${JSON.stringify(shown)}`
    );
  }
});

test('the new-key dialog adds no notice after a rotation that removed every account', () => {
  const shown = drawDialog('en', null);
  assert.ok(!shown.includes(translator('en')(KEPT)), 'a clean rotation says accounts were kept');
});
