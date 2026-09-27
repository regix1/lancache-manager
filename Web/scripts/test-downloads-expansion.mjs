import assert from 'node:assert/strict';
import test from 'node:test';
import ts from 'typescript';
import {
  bindLifted,
  compileToUrl,
  findSoleNode,
  liftHookCallback,
  moduleUrl,
  parseSource
} from './transpile-module.mjs';

const DOWNLOADS_TAB = 'src/components/features/downloads/DownloadsTab.tsx';
const COMPACT_VIEW = 'src/components/features/downloads/CompactView.tsx';
const IP_DOWNLOAD_GROUP = 'src/components/features/downloads/IpDownloadGroup.tsx';

const jsxAttribute = (source, element, name) => {
  const attribute = element.openingElement.attributes.properties.find(
    (property) => ts.isJsxAttribute(property) && property.name.getText(source) === name
  );
  assert.ok(attribute && ts.isJsxAttribute(attribute), `${name} is present`);
  return attribute;
};

const attributeExpression = (source, element, name) => {
  const initializer = jsxAttribute(source, element, name).initializer;
  assert.ok(
    initializer && ts.isJsxExpression(initializer) && initializer.expression,
    `${name} has an expression`
  );
  return initializer.expression.getText(source);
};

const disclosureButton = (source, click) =>
  findSoleNode(
    source,
    `button calling ${click}`,
    (node) =>
      ts.isJsxElement(node) &&
      node.openingElement.tagName.getText(source) === 'button' &&
      node.openingElement.attributes.properties.some(
        (property) =>
          ts.isJsxAttribute(property) &&
          property.name.getText(source) === 'onClick' &&
          property.initializer?.getText(source).includes(click)
      )
  );

const controlledRegion = (source, id) =>
  findSoleNode(
    source,
    `region controlled by ${id}`,
    (node) =>
      ts.isJsxElement(node) &&
      node.openingElement.tagName.getText(source) === 'div' &&
      node.openingElement.attributes.properties.some(
        (property) =>
          ts.isJsxAttribute(property) &&
          property.name.getText(source) === 'id' &&
          property.initializer?.getText(source) === `{${id}}`
      )
  );

const enclosingDisclosure = (source, node) => {
  let ancestor = node.parent;
  while (ancestor) {
    if (
      ts.isJsxElement(ancestor) &&
      ancestor.openingElement.tagName.getText(source) === 'CollapsibleRegion'
    ) {
      return ancestor;
    }
    ancestor = ancestor.parent;
  }
  assert.fail('the controlled region is inside CollapsibleRegion');
};

const assertDisclosure = (source, click, expanded, id) => {
  const button = disclosureButton(source, click);
  assert.equal(jsxAttribute(source, button, 'type').initializer.getText(source), '"button"');
  assert.equal(attributeExpression(source, button, 'aria-expanded'), expanded);
  assert.equal(attributeExpression(source, button, 'aria-controls'), id);

  const region = controlledRegion(source, id);
  assert.equal(attributeExpression(source, region, 'inert'), `!${expanded}`);
  assert.equal(attributeExpression(source, enclosingDisclosure(source, region), 'open'), expanded);
};

test('download disclosures use native keyboard buttons with matching expanded state and regions', () => {
  const compactView = parseSource(COMPACT_VIEW, ts.ScriptKind.TSX);
  const ipDownloadGroup = parseSource(IP_DOWNLOAD_GROUP, ts.ScriptKind.TSX);

  assertDisclosure(compactView, 'onItemClick(group.id)', 'isExpanded', 'detailsId');
  assertDisclosure(compactView, 'toggleIp(ip, ipDownloads.length)', 'expanded', 'regionId');
  assertDisclosure(
    ipDownloadGroup,
    'toggleIp(clientIp, clientDownloads.length)',
    'expanded',
    'regionId'
  );
});

const reactUrl = moduleUrl(`
  export const runtime = {
    cursor: 0,
    effects: [],
    states: [],
    begin() { this.cursor = 0; this.effects = []; },
    flush() { for (const effect of this.effects.splice(0)) effect(); },
    reset() { this.cursor = 0; this.effects = []; this.states = []; }
  };
  export const useState = (initial) => {
    const index = runtime.cursor++;
    if (!(index in runtime.states)) {
      runtime.states[index] = typeof initial === 'function' ? initial() : initial;
    }
    const setState = (value) => {
      runtime.states[index] =
        typeof value === 'function' ? value(runtime.states[index]) : value;
    };
    return [runtime.states[index], setState];
  };
  export const useMemo = (make) => make();
  export const useLayoutEffect = (effect) => runtime.effects.push(effect);
`);

const [{ useIpExpansion }, { runtime }] = await Promise.all([
  import(
    await compileToUrl('../src/components/features/downloads/useIpExpansion.ts', {
      react: reactUrl
    })
  ),
  import(reactUrl)
]);

const member = (id, clientIp) => ({ id, clientIp });

const renderExpansion = (downloads, ready, flush = true) => {
  runtime.begin();
  const expansion = useIpExpansion(downloads, ready);
  if (flush) runtime.flush();
  return expansion;
};

test('pending placeholder members do not seed an IP before the confirmed six arrive', () => {
  runtime.reset();
  const placeholder = renderExpansion([member(1, '10.0.0.1')], false);
  assert.equal(placeholder.isIpExpanded('10.0.0.1', 1), false);

  const confirmed = renderExpansion(
    Array.from({ length: 6 }, (_unused, index) => member(index + 1, '10.0.0.1')),
    true,
    false
  );
  assert.equal(
    confirmed.isIpExpanded('10.0.0.1', 6),
    false,
    'the first confirmed render uses the full member count before the layout effect stores it'
  );
  runtime.flush();
  assert.equal(renderExpansion([], true).isIpExpanded('10.0.0.1', 0), false);
});

test('confirmed small IP groups seed open and multiple IPs use their own full counts', () => {
  runtime.reset();
  const downloads = [
    ...Array.from({ length: 6 }, (_unused, index) => member(index + 1, '10.0.0.1')),
    member(7, '10.0.0.2'),
    member(8, '10.0.0.2')
  ];
  const expansion = renderExpansion(downloads, true, false);

  assert.equal(expansion.isIpExpanded('10.0.0.1', 6), false);
  assert.equal(expansion.isIpExpanded('10.0.0.2', 2), true);
  runtime.flush();
  const stored = renderExpansion(downloads, true);
  assert.equal(stored.isIpExpanded('10.0.0.1', 6), false);
  assert.equal(stored.isIpExpanded('10.0.0.2', 2), true);
});

test('the first click inverts the displayed default and filtering does not replace it', () => {
  runtime.reset();
  const downloads = Array.from({ length: 6 }, (_unused, index) => member(index + 1, '10.0.0.1'));
  let expansion = renderExpansion(downloads, true, false);
  assert.equal(expansion.isIpExpanded('10.0.0.1', 2), false);
  expansion.toggleIp('10.0.0.1', 2);

  expansion = renderExpansion(downloads, true);
  assert.equal(
    expansion.isIpExpanded('10.0.0.1', 2),
    true,
    'the visible filtered count cannot change the default that the full six members established'
  );
});

test('refresh keeps explicit choices and seeds only newly seen IPs', () => {
  runtime.reset();
  const original = Array.from({ length: 4 }, (_unused, index) => member(index + 1, '10.0.0.1'));
  let expansion = renderExpansion(original, true);
  expansion.toggleIp('10.0.0.1', 4);

  const refreshed = [
    ...Array.from({ length: 7 }, (_unused, index) => member(index + 1, '10.0.0.1')),
    member(8, '10.0.0.2')
  ];
  expansion = renderExpansion(refreshed, true, false);
  assert.equal(expansion.isIpExpanded('10.0.0.1', 7), false);
  assert.equal(expansion.isIpExpanded('10.0.0.2', 1), true);
});

const memberEffect = liftHookCallback(DOWNLOADS_TAB, 'useEffect', 'ApiService.getDownloadsByIds');

const deferred = () => {
  let resolve;
  let reject;
  const promise = new Promise((accept, fail) => {
    resolve = accept;
    reject = fail;
  });
  return { promise, reject, resolve };
};

const flushPromises = async () => {
  await Promise.resolve();
  await Promise.resolve();
};

const runMemberEffect = ({ expandedItem, expandRequestRef, pending, rows, state, calls }) =>
  bindLifted(memberEffect, {
    expandedItem,
    expandRequestRef,
    serverPage: { items: rows },
    mockMode: false,
    setExpandedMembers: (value) => {
      state.members = typeof value === 'function' ? value(state.members) : value;
    },
    setExpandError: (value) => calls.errors.push(value),
    ApiService: {
      getDownloadsByIds: (ids, signal) => {
        calls.requests.push({ ids, signal });
        return pending.promise;
      }
    },
    notifyError: (...args) => calls.notifications.push(args),
    t: (key) => key,
    getErrorMessage: (error) => error.message,
    setExpandedItem: (value) => {
      state.expandedItem = typeof value === 'function' ? value(state.expandedItem) : value;
    }
  })();

const row = (id, primaryId) => ({
  id,
  downloadIds: [primaryId, primaryId + 1],
  primaryDownload: member(primaryId, `10.0.0.${primaryId}`)
});

test('A-B-A member requests accept only the latest success and ignore stale failures', async () => {
  const expandRequestRef = { current: 0 };
  const state = { expandedItem: 'A', members: null };
  const calls = { errors: [], notifications: [], requests: [] };
  const rows = [row('A', 1), row('B', 3)];
  const firstA = deferred();
  const b = deferred();
  const secondA = deferred();

  const cleanupA = runMemberEffect({
    expandedItem: 'A',
    expandRequestRef,
    pending: firstA,
    rows,
    state,
    calls
  });
  state.expandedItem = 'B';
  cleanupA();
  const cleanupB = runMemberEffect({
    expandedItem: 'B',
    expandRequestRef,
    pending: b,
    rows,
    state,
    calls
  });
  state.expandedItem = 'A';
  cleanupB();
  runMemberEffect({
    expandedItem: 'A',
    expandRequestRef,
    pending: secondA,
    rows,
    state,
    calls
  });

  firstA.resolve([member(10, '10.0.0.10')]);
  b.reject(new Error('stale B failure'));
  await flushPromises();
  assert.equal(
    state.members.ready,
    false,
    'ignored aborts cannot publish stale members or readiness'
  );
  assert.deepEqual(calls.notifications, [], 'a stale failure does not notify');
  assert.equal(state.expandedItem, 'A', 'a stale failure does not clear the newer expansion');

  const latest = Array.from({ length: 6 }, (_unused, index) => member(index + 20, '10.0.0.1'));
  secondA.resolve(latest);
  await flushPromises();
  assert.deepEqual(state.members, { groupId: 'A', downloads: latest, ready: true });
});

test('a successful empty member response is ready and preserves the primary row fallback', async () => {
  const expandRequestRef = { current: 0 };
  const state = { expandedItem: 'A', members: null };
  const calls = { errors: [], notifications: [], requests: [] };
  const pending = deferred();
  const rows = [row('A', 1)];

  runMemberEffect({ expandedItem: 'A', expandRequestRef, pending, rows, state, calls });
  assert.deepEqual(state.members, { groupId: 'A', downloads: [], ready: false });
  pending.resolve([]);
  await flushPromises();
  assert.deepEqual(state.members, { groupId: 'A', downloads: [], ready: true });

  const [assembled] = bindLifted(
    liftHookCallback(DOWNLOADS_TAB, 'useMemo', 'expandedMembers.downloads'),
    {
      serverPage: { items: rows },
      expandedMembers: state.members,
      toDownloadGroup: (item, downloads) => ({ ...item, downloads })
    }
  )();
  assert.deepEqual(assembled.downloads, [rows[0].primaryDownload]);
});

test('the active member failure reports once and collapses only its own group', async () => {
  const expandRequestRef = { current: 0 };
  const state = { expandedItem: 'A', members: null };
  const calls = { errors: [], notifications: [], requests: [] };
  const pending = deferred();

  runMemberEffect({
    expandedItem: 'A',
    expandRequestRef,
    pending,
    rows: [row('A', 1)],
    state,
    calls
  });
  pending.reject(new Error('member failure'));
  await flushPromises();

  assert.equal(calls.notifications.length, 1);
  assert.deepEqual(calls.errors, [null, 'member failure']);
  assert.equal(state.expandedItem, null);
});

test('a closing group clears only its own retained confirmed members', () => {
  const exit = liftHookCallback(DOWNLOADS_TAB, 'useCallback', 'current?.groupId === id');
  let members = { groupId: 'B', downloads: [member(3, '10.0.0.3')], ready: true };
  const onExit = bindLifted(exit, {
    setExpandedMembers: (update) => {
      members = update(members);
    }
  });

  onExit('A');
  assert.equal(members.groupId, 'B', 'a late A exit cannot clear the newer B members');
  onExit('B');
  assert.equal(members, null);
});
