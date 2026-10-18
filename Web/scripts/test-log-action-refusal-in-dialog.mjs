import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import typescript from 'typescript';
import {
  bindLifted,
  collectNodes,
  findSoleNode,
  liftConstArrow,
  liftHookCallback,
  compileToUrl,
  moduleUrl,
  parseSource
} from './transpile-module.mjs';

const datasourcesPath = 'src/components/features/management/datasources/DatasourcesInfo.tsx';
const logRemovalPath = 'src/components/features/management/log-processing/LogRemovalManager.tsx';
const storagePath = 'src/components/features/management/sections/StorageSection.tsx';

/**
 * A log action the server refuses, for example while an unmapped file removal runs, answers in the
 * dialog the person confirmed it from. The dialog stays open with the reason inside it, so a second
 * click is a retry, and it closes only when the action succeeds.
 *
 * Each handler is lifted out of the component as shipped and run against an API that refuses.
 */

const REFUSAL = 'errors.logs.unmappedRemovalRunning';
const refusedCall = () => Promise.reject(new Error(REFUSAL));
const recorder = () => {
  const calls = [];
  const record = (...args) => {
    calls.push(args);
  };
  record.calls = calls;
  return record;
};
const shared = () => ({
  t: (key) => key,
  getErrorMessage: (error) => error.message,
  onError: recorder(),
  onSuccess: recorder(),
  onDataRefresh: recorder()
});

const resetSource = liftConstArrow(datasourcesPath, 'handleResetPosition');
const deleteSource = liftConstArrow(logRemovalPath, 'executeDeleteLogFile');
const orphanSource = liftHookCallback(storagePath, 'useCallback', 'removeOrphanedDownloads');

const closeSource = liftConstArrow(datasourcesPath, 'closeResetModal');

// Stands in for `useSelectionSet`: which keys are in flight, and every change in order.
const keySet = (initial = []) => {
  const keys = new Set(initial);
  const changes = [];
  return {
    changes,
    isSelected: (key) => keys.has(key),
    setMany: (list, selected) => {
      for (const key of list) {
        if (selected) keys.add(key);
        else keys.delete(key);
        changes.push([key, selected]);
      }
    }
  };
};

// What the dialog state becomes when each recorded update lands on `current`.
const applyUpdates = (setter, current) =>
  setter.calls.reduce(
    (value, [next]) => (typeof next === 'function' ? next(value) : next),
    current
  );

const alphaDialog = { datasource: 'alpha', all: false };
const betaDialog = { datasource: 'beta', all: false };

const runReset = async (api, overrides = {}, position = 'top') => {
  const runningResets = overrides.runningResets ?? keySet();
  const bindings = {
    ...shared(),
    isAdmin: true,
    ApiService: api,
    resetModal: alphaDialog,
    resetModalRef: { current: alphaDialog },
    runningResets,
    isResetRunning: bindLifted(liftConstArrow(datasourcesPath, 'isResetRunning'), {
      runningResets
    }),
    refreshPositions: () => Promise.resolve(),
    setResetModal: recorder(),
    setResetError: recorder(),
    ...overrides
  };
  await bindLifted(resetSource, bindings)(position);
  return bindings;
};

const runDelete = async (api, overrides = {}) => {
  const bindings = {
    ...shared(),
    authMode: 'authenticated',
    ApiService: api,
    pendingLogFileDeletionRef: { current: 'alpha' },
    deletingLogFiles: keySet(),
    setPendingLogFileDeletion: recorder(),
    setLogFileDeleteError: recorder(),
    addNotification: recorder(),
    loadData: recorder(),
    ...overrides
  };
  await bindLifted(deleteSource, bindings)('alpha');
  return bindings;
};

const runOrphans = async (api) => {
  const bindings = {
    ...shared(),
    isAdmin: true,
    ApiService: api,
    selectedOrphanedGroups: [{ downloadIds: [7] }],
    orphanedSelection: { clear: recorder() },
    fetchOrphanedDownloads: () => Promise.resolve(),
    setRemovingOrphaned: recorder(),
    setConfirmRemoveOrphaned: recorder(),
    setRemoveOrphanedError: recorder()
  };
  await bindLifted(orphanSource, bindings)();
  return bindings;
};

test('a refused reset keeps the reset dialog open with the reason inside it', async () => {
  const refused = await runReset({ resetDatasourceLogPosition: refusedCall });
  assert.deepEqual(refused.setResetModal.calls, []);
  assert.deepEqual(refused.setResetError.calls, [[REFUSAL]]);
  assert.deepEqual(refused.onError.calls, []);

  const done = await runReset({ resetDatasourceLogPosition: () => Promise.resolve({}) });
  assert.equal(applyUpdates(done.setResetModal, alphaDialog), null);
});

test('a refused log file delete keeps the delete dialog open with the reason inside it', async () => {
  const refused = await runDelete({ deleteLogFile: refusedCall });
  assert.deepEqual(refused.setPendingLogFileDeletion.calls, []);
  assert.deepEqual(refused.setLogFileDeleteError.calls, [[REFUSAL]]);
  assert.deepEqual(refused.onError.calls, []);

  const done = await runDelete({ deleteLogFile: () => Promise.resolve({}) });
  assert.equal(applyUpdates(done.setPendingLogFileDeletion, 'alpha'), null);
});

test('a refused orphan removal keeps its dialog open with the reason inside it', async () => {
  const refused = await runOrphans({ removeOrphanedDownloads: refusedCall });
  assert.deepEqual(refused.setConfirmRemoveOrphaned.calls, []);
  assert.deepEqual(refused.setRemoveOrphanedError.calls, [[REFUSAL]]);
  assert.deepEqual(refused.onError.calls, []);

  const done = await runOrphans({ removeOrphanedDownloads: () => Promise.resolve({ removed: 1 }) });
  assert.deepEqual(done.setConfirmRemoveOrphaned.calls, [[false]]);
});

const neverCalled = () => {
  throw new Error('the request must not be sent');
};

test('the reset dialog closes while its request waits', () => {
  const setResetModal = recorder();
  bindLifted(closeSource, { runningResets: keySet(['alpha:top']), setResetModal })();
  assert.deepEqual(setResetModal.calls, [[null]]);
});

test('a reset refused after the person closed its dialog is reported as a notification', async () => {
  const refused = await runReset(
    { resetDatasourceLogPosition: refusedCall },
    { resetModalRef: { current: null } }
  );
  assert.deepEqual(
    refused.onError.calls.map(([message, error]) => [message, error.message]),
    [['management.datasources.errors.resetFailed', REFUSAL]]
  );
  assert.deepEqual(refused.setResetError.calls, []);
  // Only this reset's opener was busy, and it is free again.
  assert.deepEqual(refused.runningResets.changes, [
    ['alpha:top', true],
    ['alpha:top', false]
  ]);
});

test("a reset refused while another datasource's dialog is open is reported as a notification", async () => {
  const refused = await runReset(
    { resetDatasourceLogPosition: refusedCall },
    { resetModalRef: { current: betaDialog } }
  );
  assert.equal(refused.onError.calls.length, 1);
  assert.deepEqual(refused.setResetError.calls, []);
});

test("a reset that succeeds while another datasource's dialog is open leaves that dialog open", async () => {
  const done = await runReset(
    { resetDatasourceLogPosition: () => Promise.resolve({}) },
    { resetModal: alphaDialog, resetModalRef: { current: betaDialog } }
  );
  assert.equal(applyUpdates(done.setResetModal, betaDialog), betaDialog);
  assert.deepEqual(done.onSuccess.calls, [['management.datasources.messages.positionReset']]);
});

test('the all-datasources reset closes only its own dialog', async () => {
  const allDialog = { datasource: null, all: true };
  const done = await runReset(
    { resetLogPosition: () => Promise.resolve({}) },
    { resetModal: allDialog, resetModalRef: { current: allDialog } }
  );
  assert.equal(applyUpdates(done.setResetModal, allDialog), null);
  assert.equal(applyUpdates(done.setResetModal, alphaDialog), alphaDialog);
  assert.deepEqual(done.runningResets.changes, [
    ['all:top', true],
    ['all:top', false]
  ]);
});

test('a reset that succeeds after the person closed its dialog still reports and refreshes', async () => {
  let refreshed = 0;
  const done = await runReset(
    { resetDatasourceLogPosition: () => Promise.resolve({}) },
    {
      resetModalRef: { current: null },
      refreshPositions: () => {
        refreshed += 1;
        return Promise.resolve();
      }
    }
  );
  assert.deepEqual(done.onSuccess.calls, [['management.datasources.messages.positionReset']]);
  assert.deepEqual(done.onError.calls, []);
  assert.equal(refreshed, 1);
  assert.deepEqual(done.onDataRefresh.calls, [[]]);
});

test('a log file delete that fails after the person closed its dialog is reported as a notification', async () => {
  const refused = await runDelete(
    { deleteLogFile: refusedCall },
    { pendingLogFileDeletionRef: { current: null } }
  );
  assert.deepEqual(
    refused.onError.calls.map(([message, error]) => [message, error.message]),
    [['management.logRemoval.errors.deleteFailed', REFUSAL]]
  );
  assert.deepEqual(refused.setLogFileDeleteError.calls, []);
  // The counts still reload, and the card's Delete button stops spinning.
  assert.deepEqual(refused.loadData.calls, [[true]]);
  assert.deepEqual(refused.deletingLogFiles.changes, [
    ['alpha', true],
    ['alpha', false]
  ]);
});

test('a log file delete that succeeds after the person closed its dialog still reloads the counts', async () => {
  const done = await runDelete(
    { deleteLogFile: () => Promise.resolve({}) },
    { pendingLogFileDeletionRef: { current: null } }
  );
  assert.deepEqual(done.onError.calls, []);
  assert.deepEqual(done.setLogFileDeleteError.calls, []);
  assert.deepEqual(done.loadData.calls, [[true]]);
  assert.deepEqual(done.deletingLogFiles.changes, [
    ['alpha', true],
    ['alpha', false]
  ]);
});

test("a log file delete that answers while another datasource's dialog is open leaves that dialog alone", async () => {
  const refused = await runDelete(
    { deleteLogFile: refusedCall },
    { pendingLogFileDeletionRef: { current: 'beta' } }
  );
  assert.equal(refused.onError.calls.length, 1);
  assert.deepEqual(refused.setLogFileDeleteError.calls, []);

  const done = await runDelete(
    { deleteLogFile: () => Promise.resolve({}) },
    { pendingLogFileDeletionRef: { current: 'beta' } }
  );
  assert.equal(applyUpdates(done.setPendingLogFileDeletion, 'beta'), 'beta');
});

test('a repeat delete of a log whose delete runs is ignored; another datasource is sent', async () => {
  const ignored = await runDelete(
    { deleteLogFile: neverCalled },
    { deletingLogFiles: keySet(['alpha']) }
  );
  assert.deepEqual(ignored.loadData.calls, []);
  assert.deepEqual(ignored.deletingLogFiles.changes, []);

  const sent = await runDelete(
    { deleteLogFile: () => Promise.resolve({}) },
    { deletingLogFiles: keySet(['beta']) }
  );
  assert.deepEqual(sent.deletingLogFiles.changes, [
    ['alpha', true],
    ['alpha', false]
  ]);
});

const jsxAttributes = (relativePath, tagName, marker) => {
  const sourceFile = parseSource(relativePath, typescript.ScriptKind.TSX);
  const element = findSoleNode(
    sourceFile,
    `${tagName} with ${marker}`,
    (node) =>
      (typescript.isJsxOpeningElement(node) || typescript.isJsxSelfClosingElement(node)) &&
      node.tagName.getText(sourceFile) === tagName &&
      node.attributes.getText(sourceFile).includes(marker)
  );
  return element.attributes.getText(sourceFile);
};

const declarationText = (relativePath, name) => {
  const sourceFile = parseSource(relativePath, typescript.ScriptKind.TSX);
  return findSoleNode(
    sourceFile,
    `${name} declaration`,
    (node) => typescript.isVariableDeclaration(node) && node.name.getText(sourceFile) === name
  ).getText(sourceFile);
};

// The shipped initializer of `const <name> = ...`, evaluated against `bindings`.
const evaluateDeclaration = (relativePath, name, bindings) => {
  const text = declarationText(relativePath, name);
  return bindLifted(`() => (${text.slice(text.indexOf('=') + 1)})`, bindings)();
};

test('Cancel in the reset dialog stays live while the reset waits', () => {
  const cancel = jsxAttributes(datasourcesPath, 'Button', 'onClick={closeResetModal}');
  assert.doesNotMatch(cancel, /disabled=/);
});

test('the delete dialog spins Confirm only for its own datasource, without blocking close', () => {
  const dialog = jsxAttributes(
    logRemovalPath,
    'ConfirmationModal',
    'opened={pendingLogFileDeletion !== null}'
  );
  // `loading` would swallow X, Escape and Cancel; `confirmBusy` spins Confirm and leaves them live.
  assert.doesNotMatch(dialog, /\bloading=/);
  assert.match(
    dialog,
    /confirmBusy=\{\s*shownLogFileDeletion !== null && deletingLogFiles\.isSelected\(shownLogFileDeletion\)\s*\}/
  );
});

test('a running log file delete busies only its own Delete button', () => {
  const opener = jsxAttributes(
    logRemovalPath,
    'Button',
    'setPendingLogFileDeletion(ds.datasource)'
  );
  assert.match(opener, /loading=\{deletingLogFiles\.isSelected\(ds\.datasource\)\}/);
  assert.doesNotMatch(opener, /deletingLogFile\b|deletingLogFiles\.count/);
});

test('a waiting reset busies only its own opener and leaves every other control free', () => {
  const row = jsxAttributes(datasourcesPath, 'Button', 'setResetModal({ datasource: ds.name');
  assert.match(row, /loading=\{isResetRunning\(ds\.name\)\}/);
  assert.doesNotMatch(row, /actionLoading/);

  const menu = jsxAttributes(datasourcesPath, 'ActionMenuItem', 'setResetModal({ datasource: null');
  assert.match(menu, /isResetRunning\('all'\) \? \(\s*<LoadingSpinner inline size="xs" \/>/);
  assert.match(menu, /disabled=\{repositionAllDisabled\}/);
  const menuDisabled = declarationText(datasourcesPath, 'repositionAllDisabled');
  assert.match(menuDisabled, /isResetRunning\('all'\)/);
  assert.doesNotMatch(menuDisabled, /actionLoading/);
  assert.doesNotMatch(declarationText(datasourcesPath, 'repositionDisabled'), /actionLoading/);

  // A reset no longer touches the flag the Process controls read.
  assert.doesNotMatch(resetSource, /setActionLoading/);

  // The dialog's own buttons spin for the dialog's target and position only.
  assert.match(
    jsxAttributes(datasourcesPath, 'Button', "handleResetPosition('top')"),
    /loading=\{runningResets\.isSelected\(`\$\{shownResetTarget\}:top`\)\}/
  );
  assert.match(
    jsxAttributes(datasourcesPath, 'Button', "handleResetPosition('bottom')"),
    /loading=\{runningResets\.isSelected\(`\$\{shownResetTarget\}:bottom`\)\}/
  );
});

test('a repeat press for a target whose reset runs is ignored, either position', async () => {
  for (const running of ['alpha:top', 'alpha:bottom']) {
    const ignored = await runReset(
      { resetDatasourceLogPosition: neverCalled, resetLogPosition: neverCalled },
      { runningResets: keySet([running]) }
    );
    assert.deepEqual(ignored.runningResets.changes, []);
    assert.deepEqual(ignored.setResetModal.calls, []);
    assert.deepEqual(ignored.setResetError.calls, []);
    assert.deepEqual(ignored.onSuccess.calls, []);
  }
});

test("another target's reset is sent while one waits, and the server's refusal shows in its dialog", async () => {
  const busy = 'errors.logs.processingActive';
  const sent = [];
  const refused = await runReset(
    {
      resetDatasourceLogPosition: (name, position) => {
        sent.push([name, position]);
        return Promise.reject(new Error(busy));
      }
    },
    { runningResets: keySet(['all:bottom']) }
  );
  assert.deepEqual(sent, [['alpha', 'top']]);
  assert.deepEqual(refused.setResetError.calls, [[busy]]);
  assert.deepEqual(refused.onError.calls, []);
});

test('a reset pressed while the dialog fades out resets nothing', async () => {
  const ignored = await runReset(
    { resetDatasourceLogPosition: neverCalled, resetLogPosition: neverCalled },
    { resetModal: null }
  );
  assert.deepEqual(ignored.runningResets.changes, []);
  assert.deepEqual(ignored.onSuccess.calls, []);
});

test('the all-datasources dialog still resets every datasource', async () => {
  const resetAll = [];
  await runReset(
    {
      resetDatasourceLogPosition: neverCalled,
      resetLogPosition: (position) => {
        resetAll.push(position);
        return Promise.resolve({});
      }
    },
    { resetModal: { datasource: null, all: true } }
  );
  assert.deepEqual(resetAll, ['top']);
});

test('the orphan removal dialog cannot confirm once no selected record is left', () => {
  const dialog = jsxAttributes(storagePath, 'ConfirmationModal', 'opened={confirmRemoveOrphaned}');
  assert.match(dialog, /confirmDisabled=\{selectedOrphanedCount === 0[ |}]/);
});

test('the log controls are disabled while an unmapped removal runs', () => {
  // The gate is a running or stopping unmapped removal; a queued one has refused nothing yet.
  for (const path of [datasourcesPath, logRemovalPath]) {
    const options = declarationText(path, 'isUnmappedRemovalRunning');
    assert.match(options, /useOperationBusy\(/, path);
    assert.match(options, /types: \['unmapped_file_removal'\]/, path);
    assert.match(options, /status: \['running', 'cancelling'\]/, path);
  }

  // The Reposition menu item and the per-datasource Reposition button.
  for (const name of ['repositionAllDisabled', 'repositionDisabled']) {
    assert.match(declarationText(datasourcesPath, name), /isUnmappedRemovalRunning/, name);
  }
  // The reset dialog's Start from Beginning and Start from End.
  for (const position of ['top', 'bottom']) {
    assert.match(
      jsxAttributes(datasourcesPath, 'Button', `handleResetPosition('${position}')`),
      /disabled=\{isUnmappedRemovalRunning\}/,
      position
    );
  }
  // The Delete log file button and the Remove Selected menu item.
  for (const name of ['deleteLogFileDisabled', 'removeSelectedDisabled']) {
    assert.match(declarationText(logRemovalPath, name), /isUnmappedRemovalRunning/, name);
  }
});

test('the per-service Clear is disabled while an unmapped removal runs, but selecting stays open', () => {
  assert.match(declarationText(logRemovalPath, 'rowDisabled'), /isUnmappedRemovalRunning/);
  assert.doesNotMatch(
    declarationText(logRemovalPath, 'selectionDisabled'),
    /isUnmappedRemovalRunning/
  );
  const row = jsxAttributes(logRemovalPath, 'ServiceRow', 'isDisabled={rowDisabled}');
  assert.match(row, /clearTooltip=\{[\s\S]*isUnmappedRemovalRunning\s*\?\s*unmappedRemovalReason/);
});

test('every log control an unmapped removal disables says why', () => {
  const reason = 'management.storage.unmappedRemovalRunning';
  for (const path of [datasourcesPath, logRemovalPath]) {
    assert.ok(declarationText(path, 'unmappedRemovalReason').includes(`t('${reason}')`), path);
  }

  // Card controls: the gate around each one carries the reason. Reset-all applies only to the menu item.
  assert.match(
    jsxAttributes(datasourcesPath, 'DisabledActionGate', 'disabled={repositionAllDisabled}'),
    /reason=\{repositionAllReason\}/
  );
  assert.match(
    jsxAttributes(datasourcesPath, 'DisabledActionGate', 'disabled={repositionDisabled}'),
    /reason=\{repositionRowReason\}/
  );
  const repositionAllReason = (
    isUnmappedRemovalRunning,
    isProcessing,
    resetAll = false,
    serverReset = false,
    resetKnown = true
  ) =>
    evaluateDeclaration(datasourcesPath, 'repositionAllReason', {
      isUnmappedRemovalRunning,
      unmappedRemovalReason: isUnmappedRemovalRunning ? reason : '',
      resetAllRunningReason: resetAll ? 'management.datasources.resetRunning' : '',
      logResetRunningKnown: resetKnown,
      serverLogResetRunning: serverReset,
      logsInUseReason: isProcessing ? 'management.datasources.logsInUse' : '',
      t: (key) => key
    });
  const repositionRowReason = (isUnmappedRemovalRunning, isProcessing) =>
    evaluateDeclaration(datasourcesPath, 'repositionRowReason', {
      isUnmappedRemovalRunning,
      unmappedRemovalReason: isUnmappedRemovalRunning ? reason : '',
      logsInUseReason: isProcessing ? 'management.datasources.logsInUse' : ''
    });
  assert.equal(repositionAllReason(true, false), reason);
  assert.equal(repositionAllReason(true, true), reason);
  assert.equal(repositionAllReason(false, true), 'management.datasources.logsInUse');
  assert.equal(repositionAllReason(false, false, true), 'management.datasources.resetRunning');
  assert.equal(
    repositionAllReason(false, false, false, true),
    'management.datasources.resetRunning'
  );
  assert.equal(
    repositionAllReason(false, false, false, true, false),
    'management.datasources.resetRunningUnknown'
  );
  assert.equal(repositionAllReason(false, false), '');
  assert.equal(repositionRowReason(true, false), reason);
  assert.equal(repositionRowReason(false, true), 'management.datasources.logsInUse');
  assert.equal(repositionRowReason(false, false), '');

  const logRemovalReason = 'management.logRemoval.removalRunning';
  assert.ok(
    declarationText(logRemovalPath, 'logRemovalActiveReason').includes(`t('${logRemovalReason}')`)
  );
  assert.match(
    jsxAttributes(logRemovalPath, 'DisabledActionGate', 'disabled={removeSelectedDisabled}'),
    /logRemovalActiveReason/
  );
  assert.match(
    jsxAttributes(logRemovalPath, 'DisabledActionGate', 'disabled={deleteLogFileDisabled}'),
    /reason=\{[\s\S]*isUnmappedRemovalRunning\s*\?\s*unmappedRemovalReason/
  );

  // Dialogs left open as a removal starts: Confirm is disabled and the reason shows inside.
  for (const marker of [
    'opened={pendingLogFileDeletion !== null}',
    'opened={pendingServiceRemoval !== null}',
    'opened={showBatchConfirm}'
  ]) {
    assert.match(
      jsxAttributes(logRemovalPath, 'ConfirmationModal', marker),
      /confirmDisabled=\{isUnmappedRemovalRunning\}/,
      marker
    );
  }
  const noteCount = (path) => {
    const sourceFile = parseSource(path, typescript.ScriptKind.TSX);
    return collectNodes(
      sourceFile,
      (node) =>
        typescript.isJsxExpression(node) &&
        /^\{isUnmappedRemovalRunning && \(\s*<Alert[\s\S]*\{unmappedRemovalReason\}/.test(
          node.getText(sourceFile)
        )
    ).length;
  };
  assert.equal(noteCount(datasourcesPath), 1);
  assert.equal(noteCount(logRemovalPath), 3);
});

test('the orphan removal is disabled while an unmapped removal runs, and says why', () => {
  const sourceFile = parseSource(storagePath, typescript.ScriptKind.TSX);
  const gate = declarationText(storagePath, 'isUnmappedRemovalRunning');
  assert.match(gate, /useOperationBusy\(/);
  assert.match(gate, /types: \['unmapped_file_removal'\]/);
  assert.match(gate, /status: \['running', 'cancelling'\]/);
  assert.match(
    declarationText(storagePath, 'unmappedRemovalReason'),
    /t\('management\.storage\.unmappedRemovalRunning'\)/
  );

  const opener = findSoleNode(
    sourceFile,
    'orphan Remove button inside its reason gate',
    (node) =>
      typescript.isJsxElement(node) &&
      node.openingElement.tagName.getText(sourceFile) === 'DisabledActionGate' &&
      node.getText(sourceFile).includes('setConfirmRemoveOrphaned(true)')
  );
  const gateAttributes = opener.openingElement.attributes.getText(sourceFile);
  assert.match(gateAttributes, /reason=\{unmappedRemovalReason\}/);
  assert.match(gateAttributes, /disabled=\{removingOrphaned \|\| isUnmappedRemovalRunning\}/);

  assert.match(
    jsxAttributes(storagePath, 'ConfirmationModal', 'opened={confirmRemoveOrphaned}'),
    /confirmDisabled=\{selectedOrphanedCount === 0 \|\| isUnmappedRemovalRunning\}/
  );
  const notes = collectNodes(
    sourceFile,
    (node) =>
      typescript.isJsxExpression(node) &&
      /^\{isUnmappedRemovalRunning && \(\s*<Alert[\s\S]*\{unmappedRemovalReason\}/.test(
        node.getText(sourceFile)
      )
  );
  assert.equal(notes.length, 1);
});

test('a refused per-service Clear passes the refusal on as it came, never matched by its text', async () => {
  for (const message of [
    'errors.logs.directoryReadOnly',
    'The logs directory is read-only for alpha.'
  ]) {
    const refusal = new Error(message);
    const bindings = {
      ...shared(),
      authMode: 'authenticated',
      ApiService: { removeServiceFromDatasourceLogs: () => Promise.reject(refusal) },
      getServiceDisplayName: (service) => service,
      setPendingServiceRemoval: recorder(),
      markServiceRemovalStarting: recorder(),
      clearServiceRemovalPending: recorder()
    };
    await bindLifted(liftConstArrow(logRemovalPath, 'executeRemoveServiceLogs'), bindings)(
      'alpha',
      'steam'
    );
    assert.equal(bindings.onError.calls.length, 1);
    assert.equal(bindings.onError.calls[0][0], 'management.logRemoval.errors.removeServiceFailed');
    assert.equal(bindings.onError.calls[0][1], refusal, message);
    assert.deepEqual(bindings.clearServiceRemovalPending.calls, [['alpha:steam']]);
  }
});

test('each gated control is disabled exactly when its gate is', () => {
  const attribute = (sourceFile, opening, name) =>
    opening.attributes.properties
      .find(
        (property) =>
          typescript.isJsxAttribute(property) && property.name.getText(sourceFile) === name
      )
      ?.initializer?.getText(sourceFile);
  // Reposition and Process twice each; Clear, Remove Selected and Delete log file; orphan Remove.
  for (const [path, count] of [
    [datasourcesPath, 4],
    [logRemovalPath, 5],
    [storagePath, 1]
  ]) {
    const sourceFile = parseSource(path, typescript.ScriptKind.TSX);
    const gates = collectNodes(
      sourceFile,
      (node) =>
        typescript.isJsxElement(node) &&
        node.openingElement.tagName.getText(sourceFile) === 'DisabledActionGate'
    );
    assert.equal(gates.length, count, path);
    for (const gate of gates) {
      const control = gate.children.find(
        (child) => typescript.isJsxElement(child) || typescript.isJsxSelfClosingElement(child)
      );
      assert.ok(control, path);
      const controlOpening = typescript.isJsxElement(control) ? control.openingElement : control;
      const gateDisabled = attribute(sourceFile, gate.openingElement, 'disabled');
      assert.ok(gateDisabled, path);
      assert.equal(attribute(sourceFile, controlOpening, 'disabled'), gateDisabled, path);
    }
  }
});

test('a Start from End count or a processing pass gives Reposition and Process a reason', async () => {
  // The count holds the processing gate as a tracked log processing run, the same type a pass
  // runs as, so the page's processing check is what disables these controls during it.
  const server = await readFile(
    new URL(
      '../../Api/LancacheManager/Infrastructure/Services/Rust/RustLogProcessorService.cs',
      import.meta.url
    ),
    'utf8'
  );
  const reserve = server.slice(server.indexOf('Task<Guid?> TryReserveProcessingGateAsync'));
  assert.match(
    reserve.slice(0, reserve.indexOf('\n    }\n')),
    /RegisterOperation\(\s*OperationType\.LogProcessing/
  );
  const busy = declarationText(datasourcesPath, 'isProcessing');
  assert.match(busy, /types: \['log_processing'\]/);
  assert.match(busy, /status: \['running', 'waiting'\]/);

  const t = (key) => key;
  assert.equal(
    evaluateDeclaration(datasourcesPath, 'logsInUseReason', { isProcessing: true, t }),
    'management.datasources.logsInUse'
  );
  assert.equal(
    evaluateDeclaration(datasourcesPath, 'logsInUseReason', { isProcessing: false, t }),
    ''
  );

  for (const name of [
    'repositionAllDisabled',
    'repositionDisabled',
    'processAllDisabled',
    'processDisabled'
  ]) {
    assert.match(declarationText(datasourcesPath, name), /isProcessing/, name);
  }
  assert.match(
    jsxAttributes(datasourcesPath, 'DisabledActionGate', 'disabled={processAllDisabled}'),
    /reason=\{logsInUseReason\}/
  );
  assert.match(
    jsxAttributes(datasourcesPath, 'DisabledActionGate', 'disabled={processDisabled}'),
    /logsInUseReason/
  );
  assert.match(
    jsxAttributes(datasourcesPath, 'DisabledActionGate', 'disabled={processDisabled}'),
    /management\.datasources\.noLogLines/
  );
});

// The gate compiled as shipped, with Tooltip replaced by an element that shows its content.
const loadGate = async () => {
  const react = import.meta.resolve('react');
  const tooltipUrl = moduleUrl(
    `import React from '${react}';\n` +
      'export const Tooltip = ({ content, className, children }) =>\n' +
      "  React.createElement('div', { 'data-tooltip': content, className }, children);"
  );
  return (
    await import(
      await compileToUrl(
        '../src/components/features/management/DisabledActionGate.tsx',
        {
          react,
          '@components/ui/Tooltip': tooltipUrl
        },
        { jsx: typescript.JsxEmit.React }
      )
    )
  ).DisabledActionGate;
};

const renderGate = (DisabledActionGate, disabled, reason) =>
  renderToStaticMarkup(
    React.createElement(
      DisabledActionGate,
      { disabled, reason, label: 'Reposition' },
      React.createElement('button', { disabled }, 'Reposition')
    )
  );

test('a disabled control with a reason is a keyboard stop described by that reason', async () => {
  const DisabledActionGate = await loadGate();
  const markup = renderGate(DisabledActionGate, true, 'Logs in use');
  const stop = markup.match(/^<div[^>]*><span([^>]*)>/);
  assert.ok(stop, markup);
  assert.match(stop[1], /role="group"/);
  assert.match(stop[1], /tabindex="0"/);
  assert.match(stop[1], /aria-label="Reposition"/);
  const describedBy = stop[1].match(/aria-describedby="([^"]+)"/);
  assert.ok(describedBy, markup);
  assert.ok(
    markup.includes(`<span id="${describedBy[1]}" class="sr-only">Logs in use</span>`),
    markup
  );
  assert.match(markup, /data-tooltip="Logs in use"/);
});

test('the keyboard stop goes away when the control is enabled or has no reason', async () => {
  const DisabledActionGate = await loadGate();
  for (const [disabled, reason] of [
    [false, 'Logs in use'],
    [true, '']
  ]) {
    const markup = renderGate(DisabledActionGate, disabled, reason);
    // The same wrapper stays around the control, so it keeps its element across the change.
    assert.match(
      markup,
      /^<div[^>]*><span class="[^"]*">(<button[^>]*>Reposition<\/button>)<\/span><\/div>$/
    );
    assert.doesNotMatch(markup, /tabindex|role=|aria-describedby|aria-label|sr-only/);
  }
  // An enabled control keeps its hover text; the control itself takes keyboard focus.
  assert.match(renderGate(DisabledActionGate, false, 'Logs in use'), /data-tooltip="Logs in use"/);
});
