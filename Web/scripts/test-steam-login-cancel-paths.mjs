import assert from 'node:assert/strict';
import test from 'node:test';
import ts from 'typescript';
import { bindLifted, collectNodes, liftConstArrow, parseSource } from './transpile-module.mjs';

/**
 * The two ways a Steam sign-in is abandoned without the server being told.
 *
 * The phone-approval wait outlives the request that started it. While it runs the account is marked
 * as signing in and every later attempt is refused with "a Steam sign-in is already in progress",
 * for up to two minutes. Dismissing the modal already sends the cancel; switching to manual code
 * entry, and the whole setup wizard, did not.
 */

const MODAL_PATH = 'src/components/modals/auth/SteamAuthModal.tsx';
const WIZARD_STEP_PATH = 'src/components/initialization/steps/SteamPicsAuthStep.tsx';

/** The switch-to-manual-code handler as the modal runs it. */
const runSwitchToManualCode = () => {
  const calls = { cancelRequest: 0, cancelLogin: 0, actions: [] };
  const record =
    (name) =>
    (...args) =>
      calls.actions.push([name, ...args]);

  bindLifted(liftConstArrow(MODAL_PATH, 'handleSwitchToManualCode'), {
    cancelPendingRequest: () => {
      calls.cancelRequest += 1;
    },
    onCancelLogin: () => {
      calls.cancelLogin += 1;
    },
    actions: {
      setWaitingForMobileConfirmation: record('setWaitingForMobileConfirmation'),
      setNeedsTwoFactor: record('setNeedsTwoFactor'),
      setUseManualCode: record('setUseManualCode'),
      setTwoFactorCode: record('setTwoFactorCode')
    }
  })();

  return calls;
};

test('switching to manual code entry cancels the phone wait it abandons', () => {
  const calls = runSwitchToManualCode();
  assert.equal(calls.cancelLogin, 1);
  assert.equal(calls.cancelRequest, 1);
});

test('switching to manual code entry still opens the code box', () => {
  assert.deepEqual(runSwitchToManualCode().actions, [
    ['setWaitingForMobileConfirmation', false],
    ['setNeedsTwoFactor', true],
    ['setUseManualCode', true],
    ['setTwoFactorCode', '']
  ]);
});

test('a sign-in that succeeded does not cancel itself', async () => {
  let cancelLogins = 0;
  let closes = 0;
  const submitting = [];

  const handleSubmit = bindLifted(liftConstArrow(MODAL_PATH, 'handleSubmit'), {
    isSubmitting: false,
    loading: false,
    setIsSubmitting: (value) => submitting.push(value),
    handleAuthenticate: async () => true,
    onClose: () => {
      closes += 1;
    },
    onCancelLogin: () => {
      cancelLogins += 1;
    }
  });

  await handleSubmit();

  assert.equal(closes, 1);
  assert.equal(cancelLogins, 0);
  assert.deepEqual(submitting, [true, false]);
});

test('the setup wizard hands the modal a cancel of its own', () => {
  const sourceFile = parseSource(WIZARD_STEP_PATH, ts.ScriptKind.TSX);
  const modals = collectNodes(
    sourceFile,
    (node) =>
      ts.isJsxSelfClosingElement(node) && node.tagName.getText(sourceFile) === 'SteamAuthModal'
  );

  assert.equal(modals.length, 1, `expected exactly one SteamAuthModal in ${WIZARD_STEP_PATH}`);
  const attributes = modals[0].attributes.properties.map((property) =>
    property.name?.getText(sourceFile)
  );
  assert.ok(
    attributes.includes('onCancelLogin'),
    'the wizard runs the same in-process sign-in, so its dismiss has to reach the server too'
  );
});

test('the wizard cancel calls the endpoint the dismiss path already uses', () => {
  let cancels = 0;

  bindLifted(liftConstArrow(WIZARD_STEP_PATH, 'handleCancelLogin'), {
    ApiService: {
      cancelSteamLogin: async () => {
        cancels += 1;
      }
    },
    getErrorMessage: (err) => String(err)
  })();

  assert.equal(cancels, 1);
});
