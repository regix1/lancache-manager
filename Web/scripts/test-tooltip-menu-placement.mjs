import assert from 'node:assert/strict';
import test from 'node:test';
import ts from 'typescript';
import { bindLifted, findSoleNode, liftHookCallback, parseSource } from './transpile-module.mjs';

// Drives the layout effect that places an edge tooltip, with the viewport, the menu panel and the
// row standing in for the DOM. Tooltips paint above menu panels, so a box placed above or below a
// row inside an open menu lands on the neighboring option.

const TOOLTIP_PATH = 'src/components/ui/Tooltip.tsx';
const tooltipSource = parseSource(TOOLTIP_PATH, ts.ScriptKind.TSX);
const clampSource = parseSource('src/utils/viewportClamp.ts');

const clampToViewport = bindLifted(
  `(${findSoleNode(
    clampSource,
    'clampToViewport declaration',
    (node) => ts.isFunctionDeclaration(node) && node.name?.text === 'clampToViewport'
  )
    .getText(clampSource)
    .replace(/^export\s+/, '')})`,
  {}
);

const menuPanelSelector = findSoleNode(
  tooltipSource,
  'MENU_PANEL_SELECTOR declaration',
  (node) =>
    ts.isVariableDeclaration(node) && node.name.getText(tooltipSource) === 'MENU_PANEL_SELECTOR'
).initializer.text;

const placeEffectSource = liftHookCallback(TOOLTIP_PATH, 'useLayoutEffect', 'tooltipRect');

const VIEWPORT = { innerWidth: 1424, innerHeight: 1049 };
const BOX = { width: 180, height: 30 };
const OFFSET = 8;

const rectOf = (left, top, width, height) => ({
  left,
  top,
  width,
  height,
  right: left + width,
  bottom: top + height
});

// Places a box for `row`, which sits inside `panel` when one is given and holds a disabled control
// when `disabled` is set, and returns where it went, or null when the box is never shown.
const place = (row, panel, position, viewport = VIEWPORT, disabled = false) => {
  let placed = null;
  const trigger = {
    getBoundingClientRect: () => row,
    closest: (selector) =>
      panel && selector === menuPanelSelector ? { getBoundingClientRect: () => panel } : null,
    querySelector: () => (disabled ? {} : null)
  };
  const effect = bindLifted(placeEffectSource, {
    ref: { current: { getBoundingClientRect: () => rectOf(0, 0, BOX.width, BOX.height) } },
    trigger,
    position,
    offset: OFFSET,
    clampToViewport,
    setPos: (value) => {
      placed = value;
    },
    setIsReady: () => undefined,
    onNoRoom: () => undefined,
    window: viewport,
    MENU_PANEL_SELECTOR: menuPanelSelector
  });
  effect();
  return placed === null ? null : rectOf(placed.x, placed.y, BOX.width, BOX.height);
};

const overlaps = (a, b) =>
  a.left < b.right && b.left < a.right && a.top < b.bottom && b.top < a.bottom;

test('a row inside an open menu gets its tooltip beside the menu, level with the row', () => {
  // The header's Time Range menu: against the right edge, its second row under the first.
  const rightPanel = rectOf(1076, 57, 255, 589);
  const secondRow = rectOf(1077, 151, 253, 61);
  const besideRight = place(secondRow, rightPanel, 'top');
  assert.equal(overlaps(besideRight, rightPanel), false, 'the box must not cover the menu');
  assert.equal(besideRight.right, rightPanel.left - OFFSET);
  assert.equal(besideRight.top, secondRow.top + secondRow.height / 2 - BOX.height / 2);

  // A menu against the left edge has no room on its left, so the box goes to its right.
  const leftPanel = rectOf(16, 57, 255, 589);
  const rowInLeftPanel = rectOf(17, 151, 253, 61);
  const besideLeft = place(rowInLeftPanel, leftPanel, 'top');
  assert.equal(overlaps(besideLeft, leftPanel), false, 'the box must not cover the menu');
  assert.equal(besideLeft.left, leftPanel.right + OFFSET);
});

// The Time Range menu on a 390 px phone screen fills the middle of the screen.
const PHONE = { innerWidth: 390, innerHeight: 844 };
const PHONE_PANEL = rectOf(108, 57, 256, 589);
const PHONE_ROW = rectOf(109, 339, 254, 61);

test('an enabled row inside a menu with no room on either side gets no box', () => {
  assert.equal(place(PHONE_ROW, PHONE_PANEL, 'top', PHONE), null, 'the box would land on its row');
});

test('a disabled row inside a menu with no room on either side keeps its reason above the row', () => {
  const box = place(PHONE_ROW, PHONE_PANEL, 'top', PHONE, true);
  assert.notEqual(box, null, 'a disabled control must still say why');
  assert.equal(overlaps(box, PHONE_ROW), false, 'the reason must not cover its own row');
  assert.equal(box.bottom, PHONE_ROW.top - OFFSET);

  // A row at the top of the screen has no room above, so its reason goes below it.
  const topRow = rectOf(109, 20, 254, 40);
  const below = place(topRow, PHONE_PANEL, 'top', PHONE, true);
  assert.equal(overlaps(below, topRow), false, 'the reason must not cover its own row');
  assert.equal(below.top, topRow.bottom + OFFSET);
});

// ── Nested tooltips ─────────────────────────────────────────────────────────
// A disabled control can sit inside several tooltips, each giving its own reason. Pointer arrival
// and focus reach every one of them, so each tooltip is mounted here with the trigger handlers the
// component renders, and the boxes that end up open are counted.

const liftComponentArrow = (name) =>
  findSoleNode(
    tooltipSource,
    `${name} declaration`,
    (node) =>
      ts.isVariableDeclaration(node) &&
      node.name.getText(tooltipSource) === name &&
      node.initializer !== undefined &&
      ts.isArrowFunction(node.initializer)
  ).initializer.getText(tooltipSource);

const triggerAttributes = findSoleNode(
  tooltipSource,
  'tooltip trigger element',
  (node) =>
    ts.isJsxOpeningElement(node) &&
    node.attributes.properties.some(
      (attribute) =>
        ts.isJsxAttribute(attribute) &&
        attribute.name.getText(tooltipSource) === 'ref' &&
        attribute.initializer?.getText(tooltipSource) === '{triggerRef}'
    )
).attributes.properties.map((attribute) => [
  attribute.name.getText(tooltipSource),
  attribute.initializer?.expression?.getText(tooltipSource)
]);

const triggerAttribute = (name) => triggerAttributes.find(([key]) => key === name)?.[1];

const handleMouseEnterSource = liftComponentArrow('handleMouseEnter');
const handleFocusSource = liftComponentArrow('handleFocus');
const ownsEventSource = liftComponentArrow('ownsEvent');

// An element that answers `closest` for an attribute selector, the only kind the trigger uses.
const element = (parent, attributes = {}) => {
  const self = {
    parent,
    attributes,
    closest: (selector) => {
      const name = /^\[([\w-]+)\]$/.exec(selector)?.[1];
      assert.ok(name, `unexpected selector ${selector}`);
      for (let node = self; node; node = node.parent) {
        if (name in node.attributes) return node;
      }
      return null;
    },
    contains: (node) => {
      for (let current = node; current; current = current.parent) {
        if (current === self) return true;
      }
      return false;
    },
    matches: () => true,
    getBoundingClientRect: () => rectOf(0, 0, 10, 10)
  };
  return self;
};

// Mounts one tooltip whose trigger sits inside `parent`, recording its content in `open` while its
// box is drawn.
const mountTooltip = (parent, content, open, document) => {
  const hasContent = content !== '';
  const attributes = Object.fromEntries(
    triggerAttributes
      .filter(([name]) => name.startsWith('data-'))
      .map(([name, expression]) => [name, bindLifted(expression, { hasContent })])
      .filter(([, value]) => value !== undefined)
  );
  const trigger = element(parent, attributes);
  const triggerRef = { current: trigger };
  const setShow = (value) => {
    if (value && hasContent) open.add(content);
    else open.delete(content);
  };
  const closeNow = () => setShow(false);
  const common = {
    tooltipsDisabled: false,
    addsNothing: () => false,
    closeNow,
    triggerRef,
    setX: () => undefined,
    setY: () => undefined,
    setShow
  };
  const handleMouseEnter = bindLifted(handleMouseEnterSource, {
    ...common,
    hideTimeoutRef: { current: null },
    showTimeoutRef: { current: null },
    menuCoversTrigger: () => false,
    closeOpenTooltip: null,
    SHOW_DELAY_MS: 150
  });
  const handleFocus = bindLifted(handleFocusSource, {
    ...common,
    menuCoversTrigger: () => false,
    setFocusHeldByMenu: () => undefined,
    requestAnimationFrame: (callback) => callback(),
    document
  });
  const ownsEvent = bindLifted(ownsEventSource, { triggerRef });
  const handlers = { handleMouseEnter, handleFocus, ownsEvent };
  return {
    trigger,
    onMouseEnter: bindLifted(triggerAttribute('onMouseEnter'), handlers),
    onFocus: bindLifted(triggerAttribute('onFocus'), handlers)
  };
};

// Three tooltips, outermost first, around one control.
const mountNest = (reasons) => {
  const open = new Set();
  const document = { activeElement: null };
  const tooltips = [];
  let parent = null;
  for (const reason of reasons) {
    const tooltip = mountTooltip(parent, reason, open, document);
    tooltips.push(tooltip);
    parent = tooltip.trigger;
  }
  const control = element(parent);
  document.activeElement = control;
  return { open, tooltips, control };
};

test('the pointer on a control inside nested tooltips opens only the innermost reason', (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const { open, tooltips, control } = mountNest(['disk', 'log writer', 'folder missing']);
  // Entering the control enters every tooltip around it, outermost first.
  for (const tooltip of tooltips) {
    tooltip.onMouseEnter({ target: control, clientX: 0, clientY: 0 });
  }
  t.mock.timers.tick(150);
  assert.deepEqual([...open], ['folder missing']);
});

test('focus on a control inside nested tooltips opens only the innermost reason', () => {
  const { open, tooltips, control } = mountNest(['disk', 'log writer', 'folder missing']);
  // Focus bubbles, so the innermost tooltip hears it first.
  for (const tooltip of [...tooltips].reverse()) {
    tooltip.onFocus({ target: control });
  }
  assert.deepEqual([...open], ['folder missing']);
});

test('an inner tooltip with nothing to say leaves the box to the next one out', (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const { open, tooltips, control } = mountNest(['disk', 'log writer', '']);
  for (const tooltip of tooltips) {
    tooltip.onMouseEnter({ target: control, clientX: 0, clientY: 0 });
  }
  t.mock.timers.tick(150);
  assert.deepEqual([...open], ['log writer']);
});

test('a control outside any menu keeps the side it asked for', () => {
  const button = rectOf(400, 300, 120, 40);
  const above = place(button, null, 'top');
  assert.equal(above.bottom, button.top - OFFSET);
  assert.equal(above.left, button.left + button.width / 2 - BOX.width / 2);
});

// ── Boxes while a menu panel is on the page ─────────────────────────────────
// The guard that keeps a box off an open menu runs against a page whose body holds the given
// panels, each matched by its class the way the real selector matches it.

const panel = (className, attributes = {}) => ({
  className,
  hasAttribute: (name) => name in attributes
});

const pageWith = (panels) => {
  const classes = menuPanelSelector.split(',').map((part) => part.trim().replace(/^\./, ''));
  const matching = (selector) => {
    assert.equal(selector, menuPanelSelector);
    return panels.filter((candidate) => classes.includes(candidate.className));
  };
  return {
    panels,
    activeElement: null,
    documentElement: { classList: { contains: () => false } },
    body: {},
    querySelector: (selector) => matching(selector)[0] ?? null,
    querySelectorAll: (selector) => matching(selector),
    addEventListener: () => undefined,
    removeEventListener: () => undefined
  };
};

const liftMenuCoversTrigger = (page) =>
  bindLifted(liftComponentArrow('menuCoversTrigger'), {
    MENU_PANEL_SELECTOR: menuPanelSelector,
    document: page
  });

// A trigger that sits outside every menu panel.
const outsideTrigger = { closest: () => null, contains: () => true };

const showEffectSource = liftHookCallback(TOOLTIP_PATH, 'useEffect', 'overlayCoversTrigger');

// Runs the effect that keeps an open box off a covered trigger and returns each setShow it made.
const runShowEffect = (page, trigger) => {
  const calls = [];
  bindLifted(showEffectSource, {
    show: true,
    setShow: (value) => calls.push(value),
    triggerRef: { current: trigger },
    showTimeoutRef: { current: null },
    document: page,
    window: { addEventListener: () => undefined, removeEventListener: () => undefined },
    MutationObserver: class {
      observe = () => undefined;
      disconnect = () => undefined;
    },
    menuCoversTrigger: liftMenuCoversTrigger(page),
    setTimeout: () => 0,
    clearTimeout: () => undefined
  })();
  return calls;
};

const actionMenuSource = parseSource('src/components/ui/ActionMenu.tsx', ts.ScriptKind.TSX);
const liftActionMenuArrow = (name) =>
  findSoleNode(
    actionMenuSource,
    `${name} declaration`,
    (node) =>
      ts.isVariableDeclaration(node) &&
      node.name.getText(actionMenuSource) === name &&
      node.initializer !== undefined &&
      ts.isArrowFunction(node.initializer)
  ).initializer.getText(actionMenuSource);

// The panel element in `source` whose attributes contain `marker`, with the data attributes it
// renders for `closing`, matched on the page by `className`.
const renderedPanel = (source, marker, className, closing) => {
  const opening = findSoleNode(
    source,
    `${className} panel element`,
    (node) => ts.isJsxOpeningElement(node) && node.attributes.getText(source).includes(marker)
  );
  const attributes = {};
  for (const attribute of opening.attributes.properties) {
    if (!ts.isJsxAttribute(attribute)) continue;
    const name = attribute.name.getText(source);
    if (!name.startsWith('data-')) continue;
    const value = bindLifted(`() => (${attribute.initializer.expression.getText(source)})`, {
      closing
    })();
    if (value !== undefined) attributes[name] = value;
  }
  return panel(className, attributes);
};

const actionMenuPanel = (closing) =>
  renderedPanel(actionMenuSource, 'am-dropdown', 'am-dropdown', closing);

// The other panels that return focus or hold a box while they fade out, by source file, the text
// that finds the panel element, and the class the page matches.
const fadingPanels = [
  ['src/components/ui/EnhancedDropdown.tsx', 'ed-dropdown--menu', 'ed-dropdown'],
  ['src/components/ui/MultiSelectDropdown.tsx', 'msd-dropdown', 'msd-dropdown'],
  [
    'src/components/common/GitHubProjectsDropdown.tsx',
    'github-dropdown-container',
    'github-dropdown-container'
  ],
  [
    'src/components/features/events/CalendarSettingsPopover.tsx',
    'calendar-settings-popover',
    'calendar-settings-popover'
  ]
].map(([path, marker, className]) => ({
  className,
  render: (closing) =>
    renderedPanel(parseSource(path, ts.ScriptKind.TSX), marker, className, closing)
}));

test('the trigger of an open projects list or calendar settings panel shows no box over it', () => {
  for (const className of ['github-dropdown-container', 'calendar-settings-popover']) {
    const menuCoversTrigger = liftMenuCoversTrigger(pageWith([panel(className)]));
    assert.equal(menuCoversTrigger(outsideTrigger), true, `guard hides tooltip for ${className}`);
  }
});

test('the control Tab reaches from the last menu item keeps its box while the menu fades out', () => {
  assert.deepEqual(
    runShowEffect(pageWith([actionMenuPanel(true)]), outsideTrigger),
    [],
    'setShow calls at focus'
  );
  // A menu that is still open keeps hiding it.
  assert.deepEqual(runShowEffect(pageWith([actionMenuPanel(false)]), outsideTrigger), [false]);
});

test('the trigger Escape returns focus to keeps its box while its dropdown or popover fades out', () => {
  for (const { className, render } of fadingPanels) {
    assert.deepEqual(
      runShowEffect(pageWith([render(true)]), outsideTrigger),
      [],
      `setShow calls while ${className} fades out`
    );
    assert.deepEqual(
      runShowEffect(pageWith([render(false)]), outsideTrigger),
      [false],
      `an open ${className} still hides it`
    );
  }
});

test('a control under a resting pointer shows its box once the menu that held it back closes', () => {
  for (const stillHovered of [true, false]) {
    const page = pageWith([panel('am-dropdown')]);
    const menuCoversTrigger = liftMenuCoversTrigger(page);
    const triggerRef = {
      current: { closest: () => null, matches: (selector) => selector === ':hover' && stillHovered }
    };
    const shows = [];
    const setShow = (value) => shows.push(value);
    let held = false;
    const setHoverHeldByMenu = (value) => {
      held = value;
    };
    bindLifted(handleMouseEnterSource, {
      tooltipsDisabled: false,
      hideTimeoutRef: { current: null },
      showTimeoutRef: { current: null },
      addsNothing: () => false,
      menuCoversTrigger,
      triggerRef,
      closeOpenTooltip: null,
      closeNow: () => undefined,
      setX: () => undefined,
      setY: () => undefined,
      setShow,
      setHoverHeldByMenu,
      SHOW_DELAY_MS: 150
    })({ clientX: 0, clientY: 0 });
    assert.deepEqual(shows, [], 'no box over the open menu');
    assert.equal(held, true, 'the pointer that arrived under the open menu is remembered');

    let onChange = null;
    bindLifted(liftHookCallback(TOOLTIP_PATH, 'useEffect', 'hoverHeldByMenu'), {
      hoverHeldByMenu: held,
      focusHeldByMenu: false,
      menuCoversTrigger,
      triggerRef,
      setShow,
      setHoverHeldByMenu,
      setFocusHeldByMenu: () => undefined,
      closeOpenTooltip: null,
      closeNow: () => undefined,
      document: page,
      MutationObserver: class {
        constructor(callback) {
          onChange = callback;
        }
        observe = () => undefined;
        disconnect = () => undefined;
      }
    })();
    page.panels.length = 0;
    onChange([]);
    assert.deepEqual(
      shows,
      stillHovered ? [true] : [],
      `pointer still on the control: ${stillHovered}`
    );
    assert.equal(held, false);
  }
});

const replayEffectSource = liftHookCallback(TOOLTIP_PATH, 'useEffect', 'hoverHeldByMenu');

// Runs the effect that replays a held pointer arrival, and returns the page mutation it waits for
// and the cleanup React calls when the hold changes.
const mountReplay = (bindings) => {
  let observing = false;
  let onChange = null;
  const cleanup = bindLifted(replayEffectSource, {
    hoverHeldByMenu: false,
    focusHeldByMenu: false,
    setHoverHeldByMenu: () => undefined,
    setFocusHeldByMenu: () => undefined,
    ...bindings,
    MutationObserver: class {
      constructor(callback) {
        onChange = callback;
      }
      observe = () => {
        observing = true;
      };
      disconnect = () => {
        observing = false;
      };
    }
  })();
  return {
    mutate: () => {
      if (observing) onChange([]);
    },
    cleanup: () => cleanup?.()
  };
};

test('a box the fading menu held back replaces the focus box Tab out put up, so one box shows', () => {
  const page = pageWith([actionMenuPanel(false)]);
  const open = new Set();
  // Tab out of the last item lands focus on the next control, whose box holds the page's slot.
  open.add('Next control help');
  const closeNextBox = () => open.delete('Next control help');
  const closeOutsideBox = () => open.delete('Outside reason');
  const replay = mountReplay({
    hoverHeldByMenu: true,
    menuCoversTrigger: liftMenuCoversTrigger(page),
    triggerRef: { current: { closest: () => null, matches: (selector) => selector === ':hover' } },
    setShow: (value) => (value ? open.add('Outside reason') : closeOutsideBox()),
    setHoverHeldByMenu: () => undefined,
    closeOpenTooltip: closeNextBox,
    closeNow: closeOutsideBox,
    document: page
  });
  page.panels.splice(0, 1, actionMenuPanel(true));
  replay.mutate();
  assert.deepEqual([...open], ['Outside reason']);
});

// Back onto the trigger from the open panel, then the click that closes the menu, on a desktop.
const closingClickLeavesNoBox = (controlStopsClick) => {
  const page = pageWith([panel('ed-dropdown')]);
  const menuCoversTrigger = liftMenuCoversTrigger(page);
  const triggerRef = {
    current: { closest: () => null, matches: (selector) => selector === ':hover' }
  };
  const shows = [];
  const setShow = (value) => shows.push(value);
  let held = false;
  const setHoverHeldByMenu = (value) => {
    held = value;
  };
  const closeNow = () => setShow(false);
  // Back onto the label from the open panel.
  bindLifted(handleMouseEnterSource, {
    tooltipsDisabled: false,
    hideTimeoutRef: { current: null },
    showTimeoutRef: { current: null },
    addsNothing: () => false,
    menuCoversTrigger,
    triggerRef,
    closeOpenTooltip: null,
    closeNow,
    setX: () => undefined,
    setY: () => undefined,
    setShow,
    setHoverHeldByMenu,
    SHOW_DELAY_MS: 150
  })({ clientX: 0, clientY: 0 });
  assert.equal(held, true);
  const replay = mountReplay({
    hoverHeldByMenu: held,
    menuCoversTrigger,
    triggerRef,
    setShow,
    setHoverHeldByMenu,
    closeOpenTooltip: null,
    closeNow,
    document: page
  });

  // The click, delivered the way the browser does it: the trigger's capture handler on the way
  // down, then the control inside, then the trigger's bubble handler unless the control stopped it.
  const clickBindings = {
    isMobile: false,
    tooltipsDisabled: false,
    addsNothing: () => false,
    ownsEvent: () => true,
    triggerRef,
    show: false,
    setX: () => undefined,
    setY: () => undefined,
    setShow,
    closeNow,
    setHoverHeldByMenu,
    setFocusHeldByMenu: () => undefined
  };
  const click = {
    target: triggerRef.current,
    stopped: false,
    preventDefault: () => undefined,
    stopPropagation() {
      this.stopped = true;
    }
  };
  const runTriggerHandler = (name) => bindLifted(triggerAttribute(name), clickBindings)(click);
  runTriggerHandler('onClickCapture');
  if (controlStopsClick) click.stopPropagation();
  if (!click.stopped) runTriggerHandler('onClick');
  if (!held) replay.cleanup();
  page.panels.length = 0;
  replay.mutate();
  assert.deepEqual(shows, [false], 'no box after the closing click');
};

test('a click that closes the menu leaves no box on the label the pointer came back to', () => {
  closingClickLeavesNoBox(false);
});

test('a closing click a dropdown trigger stops still leaves no box on the wrapper around it', () => {
  closingClickLeavesNoBox(true);
});

test('Tab into a menu opened by a click shows the reason of the disabled row it reaches', () => {
  const page = pageWith([panel('am-dropdown')]);
  const focusable = () => {
    const control = {
      focus: () => {
        page.activeElement = control;
      }
    };
    return control;
  };
  // A click focused the menu's trigger, which keeps no focus ring.
  const menuTrigger = focusable();
  menuTrigger.focus();
  const load = focusable();
  // The disabled row's keyboard stop and the tooltip around it, inside the open panel.
  const rowStop = focusable();
  rowStop.matches = (selector) => selector === ':focus-visible';
  const rowTrigger = {
    closest: (selector) =>
      selector === '[data-tooltip]'
        ? rowTrigger
        : selector === menuPanelSelector
          ? page.panels[0]
          : null,
    contains: (node) => node === rowStop,
    getBoundingClientRect: () => rectOf(0, 0, 10, 10)
  };
  rowStop.closest = (selector) => (selector === '[data-tooltip]' ? rowTrigger : null);
  const items = [load, rowStop];
  const menuBindings = {
    isOpen: true,
    onClose: () => assert.fail('the menu stays open'),
    dropdownRef: { current: {} },
    getFocusable: () => items,
    focusTrigger: () => undefined,
    focusNextAfterTrigger: () => undefined
  };
  const tab = (target) => ({
    key: 'Tab',
    shiftKey: false,
    target,
    preventDefault: () => undefined,
    stopPropagation: () => undefined
  });

  bindLifted(liftActionMenuArrow('handleTriggerKeyDown'), menuBindings)(tab(menuTrigger));
  assert.equal(page.activeElement, load);
  // Load is not the last item, so the menu leaves this Tab to the browser.
  bindLifted(liftActionMenuArrow('handleMenuKeyDown'), menuBindings)(tab(load));
  rowStop.focus();

  const shows = [];
  const triggerRef = { current: rowTrigger };
  const handleFocus = bindLifted(handleFocusSource, {
    tooltipsDisabled: false,
    addsNothing: () => false,
    closeNow: () => undefined,
    triggerRef,
    setX: () => undefined,
    setY: () => undefined,
    setShow: (value) => shows.push(value),
    menuCoversTrigger: liftMenuCoversTrigger(page),
    setFocusHeldByMenu: () => assert.fail('a row inside the open menu is not held back'),
    requestAnimationFrame: (callback) => callback(),
    document: page
  });
  const ownsEvent = bindLifted(ownsEventSource, { triggerRef });
  bindLifted(triggerAttribute('onFocus'), { handleFocus, ownsEvent })({ target: rowStop });
  assert.deepEqual(shows, [true]);
  assert.deepEqual(runShowEffect(page, rowTrigger), [], 'the open menu does not hide its own row');
});

test('an enabled menu row with no room for its box on a phone leaves the first Escape to the menu', () => {
  // The open box's effect adds only its Escape listener to the document here: the click listeners
  // wait on a timer that never runs.
  const listeners = new Set();
  const page = {
    ...pageWith([panel('ed-dropdown')]),
    addEventListener: (type, listener) => listeners.add(listener),
    removeEventListener: (type, listener) => listeners.delete(listener)
  };
  const row = {
    closest: (selector) =>
      selector === menuPanelSelector ? { getBoundingClientRect: () => PHONE_PANEL } : null,
    contains: () => true,
    getBoundingClientRect: () => PHONE_ROW,
    querySelector: () => null
  };
  // React runs the open box's effect when show turns true and its cleanup when it turns false.
  let show = false;
  let cleanup;
  const setShow = (value) => {
    if (value === show) return;
    show = value;
    if (show) {
      cleanup = bindLifted(showEffectSource, {
        show,
        setShow,
        triggerRef: { current: row },
        showTimeoutRef: { current: null },
        document: page,
        window: { addEventListener: () => undefined, removeEventListener: () => undefined },
        MutationObserver: class {
          observe = () => undefined;
          disconnect = () => undefined;
        },
        menuCoversTrigger: liftMenuCoversTrigger(page),
        setTimeout: () => 0,
        clearTimeout: () => undefined
      })();
    } else {
      cleanup();
    }
  };
  const closeNow = bindLifted(liftHookCallback(TOOLTIP_PATH, 'useCallback', 'setShow(false)'), {
    showTimeoutRef: { current: null },
    hideTimeoutRef: { current: null },
    setShow
  });

  // The pointer rests on the option, its box opens and measures, and finds no room beside the menu.
  setShow(true);
  bindLifted(placeEffectSource, {
    ref: { current: { getBoundingClientRect: () => rectOf(0, 0, BOX.width, BOX.height) } },
    trigger: row,
    position: 'top',
    offset: OFFSET,
    clampToViewport,
    setPos: () => assert.fail('the box would land on its own row'),
    setIsReady: () => undefined,
    onNoRoom: closeNow,
    window: PHONE,
    MENU_PANEL_SELECTOR: menuPanelSelector
  })();

  const escape = {
    key: 'Escape',
    stopped: false,
    stopPropagation() {
      this.stopped = true;
    }
  };
  for (const listener of listeners) listener(escape);
  assert.equal(escape.stopped, false, 'the first Escape reaches the menu');
  assert.equal(show, false, 'no box is shown');
  // The box above is told no room through the same handler the tooltip passes it.
  const edgeTooltip = findSoleNode(
    tooltipSource,
    'EdgeTooltip element',
    (node) =>
      ts.isJsxSelfClosingElement(node) && node.tagName.getText(tooltipSource) === 'EdgeTooltip'
  );
  assert.ok(edgeTooltip.attributes.getText(tooltipSource).includes('onNoRoom={closeNow}'));
});

test('a control Tab reached while a menu was open shows its box once the menu leaves', () => {
  for (const stillFocused of [true, false]) {
    const page = pageWith([panel('ed-dropdown')]);
    const menuCoversTrigger = liftMenuCoversTrigger(page);
    const control = { matches: (selector) => selector === ':focus-visible' };
    page.activeElement = control;
    const triggerRef = {
      current: {
        closest: () => null,
        contains: (node) => node === control,
        matches: () => false,
        getBoundingClientRect: () => rectOf(0, 0, 10, 10)
      }
    };
    // The open box's effect takes down a box an open menu covers.
    const shows = [];
    const setShow = (value) => {
      shows.push(value);
      if (value) runShowEffect(page, triggerRef.current).forEach((next) => shows.push(next));
    };
    let focusHeld = false;
    const setFocusHeldByMenu = (value) => {
      focusHeld = value;
    };
    // Focus arrives while the time filter menu is fully open.
    bindLifted(handleFocusSource, {
      tooltipsDisabled: false,
      addsNothing: () => false,
      closeNow: () => undefined,
      triggerRef,
      setX: () => undefined,
      setY: () => undefined,
      setShow,
      menuCoversTrigger,
      setFocusHeldByMenu,
      requestAnimationFrame: (callback) => callback(),
      document: page
    })({ target: control });
    const replay = mountReplay({
      focusHeldByMenu: focusHeld,
      menuCoversTrigger,
      triggerRef,
      setShow,
      setFocusHeldByMenu,
      closeOpenTooltip: null,
      closeNow: () => undefined,
      document: page
    });
    // Escape closes the menu, and its panel leaves the page once the fade ends.
    if (!stillFocused) page.activeElement = null;
    page.panels.length = 0;
    replay.mutate();
    assert.equal(shows.at(-1) === true, stillFocused, `control still focused: ${stillFocused}`);
    assert.equal(focusHeld, false);
  }
});
