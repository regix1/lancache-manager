import assert from 'node:assert/strict';
import test from 'node:test';
import ts from 'typescript';
import {
  bindLifted,
  declaredValue,
  findSoleNode,
  liftConstArrow,
  parseSource,
  transpile
} from './transpile-module.mjs';

/**
 * The event editor, its delete confirm and its date pickers, and the downloads list of an open
 * event, driven through the components that ship. Each component is compiled out of its file and
 * run against a small hook runtime, so a handler is called exactly as the dialog or the socket
 * would call it, and the next render shows what the user would see.
 */

const modalPath = 'src/components/features/events/EventModal.tsx';
const listPath = 'src/components/features/events/EventList.tsx';

const h = (type, props, ...children) =>
  typeof type === 'function'
    ? type({ ...props, children: children.flat(Infinity) })
    : {
        type,
        props: props ?? {},
        children: children.flat(Infinity).filter((child) => child != null && child !== false)
      };
const walk = (node, type) => [
  ...(node?.type === type ? [node] : []),
  ...(node?.children ?? []).flatMap((child) => walk(child, type))
];
const textOf = (node) =>
  typeof node === 'string' ? node : (node?.children ?? []).map(textOf).join('');

/** The component declared as `const <name> = ...` in `relativePath`, with its free names bound. */
const component = (relativePath, name, bindings) => {
  const source = parseSource(relativePath, ts.ScriptKind.TSX);
  const declaration = findSoleNode(
    source,
    `${name} component`,
    (node) => ts.isVariableDeclaration(node) && node.name.getText(source) === name
  );
  const compiled = transpile(
    `const ${name} = ${declaration.initializer.getText(source)};`,
    ts.ModuleKind.CommonJS,
    { jsx: ts.JsxEmit.React, jsxFactory: 'h', jsxFragmentFactory: 'Fragment' }
  );
  return new Function(...Object.keys(bindings), 'h', 'Fragment', `${compiled}\nreturn ${name};`)(
    ...Object.values(bindings),
    h,
    'fragment'
  );
};

function hooks() {
  const slots = [];
  let cursor = 0;
  let effects = [];
  const changed = (previous, next) =>
    !previous ||
    !next ||
    previous.length !== next.length ||
    next.some((value, index) => value !== previous[index]);
  const runtime = {
    useState(initial) {
      const index = cursor++;
      if (!slots[index])
        slots[index] = { value: typeof initial === 'function' ? initial() : initial };
      return [
        slots[index].value,
        (value) => {
          slots[index].value = typeof value === 'function' ? value(slots[index].value) : value;
        }
      ];
    },
    useRef(initial) {
      const index = cursor++;
      if (!slots[index]) slots[index] = { current: initial };
      return slots[index];
    },
    useMemo(make, dependencies) {
      const index = cursor++;
      if (changed(slots[index]?.dependencies, dependencies))
        slots[index] = { value: make(), dependencies };
      return slots[index].value;
    },
    useCallback(callback, dependencies) {
      return runtime.useMemo(() => callback, dependencies);
    },
    useEffect(callback, dependencies) {
      const index = cursor++;
      if (changed(slots[index]?.dependencies, dependencies)) {
        effects.push(() => {
          slots[index]?.cleanup?.();
          slots[index] = { dependencies, cleanup: callback() };
        });
      }
    },
    render(render, props) {
      cursor = 0;
      const tree = render(props);
      const pending = effects;
      effects = [];
      pending.forEach((effect) => effect());
      return tree;
    }
  };
  return runtime;
}

const deferred = () => {
  let resolve, reject;
  const promise = new Promise((yes, no) => {
    resolve = yes;
    reject = no;
  });
  return { promise, resolve, reject };
};
const settle = () => new Promise((resolve) => setImmediate(resolve));

const savedEvent = {
  id: 7,
  name: 'Winter LAN',
  startTimeUtc: '2030-01-10T10:00:00Z',
  endTimeUtc: '2030-01-10T18:00:00Z',
  colorIndex: 2
};

function createEditor({ event = null, initialStart, createEvent, updateEvent, deleteEvent } = {}) {
  const runtime = hooks();
  const closed = [];
  const saved = [];
  const render = component(modalPath, 'EventModal', {
    ...runtime,
    useTranslation: () => ({ t: (key) => key }),
    useEvents: () => ({
      createEvent: createEvent ?? (async () => undefined),
      updateEvent: updateEvent ?? (async () => undefined),
      deleteEvent: deleteEvent ?? (async () => undefined)
    }),
    useReaderClock: () => ({}),
    useTimeFilter: () => ({ setTimeRange: () => undefined, setSelectedEventIds: () => undefined }),
    formatTimestamp: (date) => String(date),
    getEventColorVar: (index) => `color-${index}`,
    getErrorMessage: (error) => error.message,
    APP_EVENTS: {},
    COLOR_INDEXES: [1, 2],
    noAutofill: {},
    FormField: ({ children }) => children[0]({}),
    Modal: 'Modal',
    ConfirmationModal: 'ConfirmationModal',
    DateTimePicker: 'DateTimePicker',
    Button: 'Button',
    Alert: 'Alert',
    CalendarDays: 'CalendarDays',
    Calendar: 'Calendar',
    Check: 'Check',
    Trash2: 'Trash2'
  });
  const props = {
    opened: true,
    event,
    initialStart,
    onClose: () => closed.push(true),
    onSave: () => saved.push(true)
  };
  let tree = runtime.render(render, props);
  const draw = () => {
    tree = runtime.render(render, props);
    return tree;
  };
  const button = (label) => walk(tree, 'Button').find((node) => textOf(node) === label);
  return {
    closed,
    saved,
    draw,
    button,
    modal: () => walk(tree, 'Modal')[0],
    confirm: () => walk(tree, 'ConfirmationModal')[0],
    pickers: () => walk(tree, 'DateTimePicker'),
    alerts: () => walk(tree, 'Alert'),
    typeName: (value) => walk(tree, 'input')[0].props.onChange({ target: { value } }),
    submit: () => walk(tree, 'form')[0].props.onSubmit({ preventDefault: () => undefined })
  };
}

test('a delete that succeeds closes the confirm and the editor together', async () => {
  const editor = createEditor({ event: savedEvent });
  editor.button('events.modal.actions.delete').props.onClick();
  editor.draw();
  assert.equal(editor.confirm().props.opened, true);

  await editor.confirm().props.onConfirm();
  editor.draw();

  assert.equal(editor.confirm().props.opened, false);
  assert.deepEqual(editor.saved, [true]);
});

test('a delete that fails keeps the confirm open with its error, and Confirm retries', async () => {
  let attempts = 0;
  const editor = createEditor({
    event: savedEvent,
    deleteEvent: async () => {
      attempts += 1;
      if (attempts === 1) throw new Error('server refused');
    }
  });
  editor.button('events.modal.actions.delete').props.onClick();
  editor.draw();

  await editor.confirm().props.onConfirm();
  editor.draw();

  const confirm = editor.confirm();
  assert.equal(confirm.props.opened, true);
  const [alert] = walk(confirm, 'Alert');
  assert.equal(alert.props.title, 'events.modal.errors.deleteFailed');
  assert.equal(textOf(alert), 'server refused');
  assert.deepEqual(editor.saved, []);

  await confirm.props.onConfirm();
  editor.draw();
  assert.equal(editor.confirm().props.opened, false);
  assert.deepEqual(editor.saved, [true]);
});

test('a day click starts the new event on that day', () => {
  const editor = createEditor({ initialStart: new Date(2031, 4, 20) });
  const fields = walk(editor.draw(), 'Button').filter((node) => walk(node, 'Calendar').length > 0);
  fields.forEach((field) => field.props.onClick());
  editor.draw();

  const valueOf = (title) =>
    editor.pickers().find((picker) => picker.props.title === title).props.value;
  const start = valueOf('events.modal.selectStartDateTime');
  const end = valueOf('events.modal.selectEndDateTime');
  assert.equal(start.getFullYear(), 2031);
  assert.equal(start.getMonth(), 4);
  assert.equal(start.getDate(), 20);
  assert.equal(end.getTime() - start.getTime(), 4 * 60 * 60 * 1000);
});

test('both date pickers stay rendered with opened false until their field is clicked', () => {
  const editor = createEditor();
  const closedPickers = editor.pickers();
  assert.equal(closedPickers.length, 2);
  assert.deepEqual(
    closedPickers.map((picker) => picker.props.opened),
    [false, false]
  );
  assert.deepEqual(
    closedPickers.map((picker) => picker.props.title),
    ['events.modal.selectStartDateTime', 'events.modal.selectEndDateTime']
  );

  const fields = walk(editor.draw(), 'Button').filter((node) => walk(node, 'Calendar').length > 0);
  fields[0].props.onClick();
  editor.draw();
  assert.deepEqual(
    editor.pickers().map((picker) => picker.props.opened),
    [true, false]
  );

  editor.pickers()[0].props.onClose();
  editor.draw();
  assert.deepEqual(
    editor.pickers().map((picker) => picker.props.opened),
    [false, false]
  );
});

test('Cancel stays enabled but leaves the editor open while a create runs', async () => {
  const held = deferred();
  const editor = createEditor({ createEvent: () => held.promise });
  editor.typeName('Spring LAN');
  editor.draw();
  editor.submit();
  editor.draw();

  const cancel = editor.button('actions.cancel');
  assert.notEqual(cancel.props.disabled, true);
  cancel.props.onClick();
  assert.deepEqual(editor.closed, []);

  held.resolve();
  await settle();
  assert.deepEqual(editor.saved, [true]);
  assert.deepEqual(editor.closed, []);
});

test('Escape, a backdrop click and the X leave the editor open while a create runs', async () => {
  const held = deferred();
  const editor = createEditor({ createEvent: () => held.promise });
  editor.typeName('Spring LAN');
  editor.draw();
  editor.submit();
  editor.draw();

  // The dialog calls its onClose for Escape, for a backdrop click and for the X alike.
  for (const dismissal of ['escape', 'backdrop', 'close button']) {
    editor.modal().props.onClose();
    assert.deepEqual(editor.closed, [], `${dismissal} closed the editor during the create`);
  }

  held.resolve();
  await settle();
  editor.draw();
  editor.modal().props.onClose();
  assert.deepEqual(editor.closed, [true]);
});

test('a failed save keeps its error on screen while the retry runs', async () => {
  const held = deferred();
  let attempts = 0;
  const editor = createEditor({
    createEvent: () => {
      attempts += 1;
      return attempts === 1 ? Promise.reject(new Error('server refused')) : held.promise;
    }
  });
  editor.typeName('Spring LAN');
  editor.draw();
  await editor.submit();
  editor.draw();
  assert.equal(textOf(editor.alerts()[0]), 'server refused');

  editor.submit();
  editor.draw();
  assert.equal(textOf(editor.alerts()[0]), 'server refused');
  held.resolve();
  await settle();
  assert.deepEqual(editor.saved, [true]);
});

test('Delete ignores a click while a save runs', () => {
  const held = deferred();
  const editor = createEditor({ event: savedEvent, updateEvent: () => held.promise });
  editor.submit();
  editor.draw();
  editor.button('events.modal.actions.delete').props.onClick();
  editor.draw();
  assert.equal(editor.confirm().props.opened, false);
  held.resolve();
});

const activeEvent = {
  id: 5,
  name: 'Live LAN',
  startTimeUtc: new Date(Date.now() - 60 * 60 * 1000).toISOString(),
  endTimeUtc: new Date(Date.now() + 60 * 60 * 1000).toISOString(),
  colorIndex: 1
};
const download = (id, patch = {}) => ({ id, service: 'steam', totalBytes: 10, ...patch });

function createList() {
  const runtime = hooks();
  const reads = [];
  const notices = [];
  const handlers = new Map();
  const signalR = {
    on: (name, handler) => handlers.set(name, handler),
    off: (name) => handlers.delete(name),
    isConnected: true
  };
  const render = component(listPath, 'EventList', {
    ...runtime,
    useTranslation: () => ({ t: (key) => key }),
    useReaderClock: () => ({}),
    useTimeFilter: () => ({ setTimeRange: () => undefined, setSelectedEventIds: () => undefined }),
    useMockMode: () => ({ mockMode: false }),
    useErrorHandler: () => ({ notifyError: (...args) => notices.push(args) }),
    useSignalR: () => signalR,
    useRefreshRate: () => ({ getRefreshInterval: () => 0 }),
    useRefreshThrottle: () => (run) => run(),
    useReconnectRefetch: () => undefined,
    DOWNLOAD_LIST_EVENTS: declaredValue(listPath, 'DOWNLOAD_LIST_EVENTS'),
    formatTimestamp: (value) => String(value),
    ApiService: {
      getEventDownloads: () => {
        const read = deferred();
        reads.push(read);
        return read.promise;
      }
    },
    MockDataService: {},
    APP_EVENTS: {},
    EventCard: 'EventCard',
    SectionHeader: 'SectionHeader',
    CalendarClock: 'CalendarClock',
    Zap: 'Zap',
    History: 'History'
  });
  const props = { events: [activeEvent], onEventClick: () => undefined };
  let tree = runtime.render(render, props);
  const draw = () => {
    tree = runtime.render(render, props);
    return tree;
  };
  return {
    reads,
    notices,
    draw,
    handlers,
    card: () => walk(tree, 'EventCard')[0]
  };
}

test('the newest read of an open event wins over an older read that ends later', async () => {
  const list = createList();
  list.card().props.onExpandClick(activeEvent.id);
  list.draw();
  list.handlers.get('DownloadTagged')();
  list.draw();
  assert.equal(list.reads.length, 2);

  list.reads[1].resolve([download(2)]);
  await settle();
  list.draw();
  assert.deepEqual(
    list.card().props.cacheEntry.downloads.map((row) => row.id),
    [2]
  );

  list.reads[0].resolve([download(1)]);
  await settle();
  list.draw();
  assert.deepEqual(
    list.card().props.cacheEntry.downloads.map((row) => row.id),
    [2]
  );
});

test('a refetch of a loaded event keeps its rows instead of a spinner', async () => {
  const list = createList();
  list.card().props.onExpandClick(activeEvent.id);
  list.draw();
  list.reads[0].resolve([download(1)]);
  await settle();
  list.draw();

  list.handlers.get('DownloadsRefresh')();
  list.draw();
  const entry = list.card().props.cacheEntry;
  assert.equal(entry.loading, false);
  assert.deepEqual(
    entry.downloads.map((row) => row.id),
    [1]
  );
  assert.equal(list.card().props.isExpanded, true);

  list.reads[1].resolve([download(1), download(3)]);
  await settle();
  list.draw();
  assert.deepEqual(
    list.card().props.cacheEntry.downloads.map((row) => row.id),
    [1, 3]
  );
});

test('a failed refetch keeps the rows and the open row, and reports once', async () => {
  const list = createList();
  list.card().props.onExpandClick(activeEvent.id);
  list.draw();
  list.reads[0].resolve([download(1)]);
  await settle();
  list.draw();

  list.handlers.get('DownloadTagged')();
  list.draw();
  list.reads[1].reject(new Error('offline'));
  await settle();
  list.draw();

  assert.equal(list.notices.length, 1);
  assert.equal(list.card().props.isExpanded, true);
  assert.deepEqual(
    list.card().props.cacheEntry.downloads.map((row) => row.id),
    [1]
  );
});

test('a failed first read closes the row', async () => {
  const list = createList();
  list.card().props.onExpandClick(activeEvent.id);
  list.draw();
  list.reads[0].reject(new Error('offline'));
  await settle();
  list.draw();

  assert.equal(list.notices.length, 1);
  assert.equal(list.card().props.isExpanded, false);
  assert.equal(list.card().props.cacheEntry, undefined);
});

test('two unnamed games of one service stay two rows, and one game stays one row', () => {
  const groupDownloadsByGame = bindLifted(liftConstArrow(listPath, 'groupDownloadsByGame'), {
    getServiceFilterKey: (service) => service,
    getGameDisplayName: (gameName, service, unknownLabel) => gameName ?? unknownLabel
  });

  const differentGames = groupDownloadsByGame(
    [download(1, { gameAppId: 440 }), download(2, { gameAppId: 570 })],
    'Unknown game'
  );
  assert.equal(differentGames.length, 2);

  const sameGame = groupDownloadsByGame(
    [download(1, { gameAppId: 440 }), download(2, { gameAppId: 440 })],
    'Unknown game'
  );
  assert.equal(sameGame.length, 1);
  assert.equal(sameGame[0].count, 2);
});
