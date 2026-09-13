import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import { bindLifted, findSoleNode, parseSource, transpile } from './transpile-module.mjs';

const componentPath = 'src/components/features/prefill/PrefillHomePage.tsx';
const eventPath = 'src/components/features/prefill/hooks/usePrefillEventHandlers.ts';
const source = parseSource(componentPath, typescript.ScriptKind.TSX);

const functionSource = (name) =>
  findSoleNode(
    source,
    `${name} function`,
    (node) => typescript.isFunctionDeclaration(node) && node.name?.getText(source) === name
  ).getText(source);

const Button = Symbol('Button');
const Fragment = Symbol('Fragment');
const services = ['steam', 'epic', 'battlenet', 'riot', 'xbox'].map((id) => ({
  id,
  displayName: id,
  icon: Symbol(`${id} icon`),
  homeCardClass: `prefill-service-card--${id}`,
  homeDescriptionKey: `prefill.home.${id}.description`,
  homeFeatureKeys: [`prefill.home.${id}.feature`],
  homeLoginNoteKey: `prefill.home.${id}.loginNote`
}));

const h = (type, props, ...children) => ({
  type,
  props: {
    ...props,
    children: children.length <= 1 ? children[0] : children
  }
});

const compiled = transpile(
  `${functionSource('ServiceFeatureList')}\n${functionSource('PrefillHomePage').replace(/^export\s+/, '')}`,
  typescript.ModuleKind.CommonJS,
  {
    jsx: typescript.JsxEmit.React,
    jsxFactory: 'h',
    jsxFragmentFactory: 'Fragment'
  }
);

const PrefillHomePage = new Function(
  'useEffect',
  'useMemo',
  'useState',
  'useTranslation',
  'useMediaQuery',
  'PREFILL_SERVICES',
  'Button',
  'CollapsibleRegion',
  'Shield',
  'AlertCircle',
  'ChevronDown',
  'h',
  'Fragment',
  `${compiled}\nreturn PrefillHomePage;`
)(
  (effect) => effect(),
  (create) => create(),
  (initial) => [initial, () => undefined],
  () => ({ t: (key) => key }),
  () => false,
  services,
  Button,
  Symbol('CollapsibleRegion'),
  Symbol('Shield'),
  Symbol('AlertCircle'),
  Symbol('ChevronDown'),
  h,
  Fragment
);

const render = (onServiceStart, overrides = {}) =>
  PrefillHomePage({
    onServiceStart,
    error: null,
    errorService: 'steam',
    isAdmin: false,
    steamPrefillEnabled: true,
    epicPrefillEnabled: false,
    battlenetPrefillEnabled: false,
    riotPrefillEnabled: false,
    xboxPrefillEnabled: false,
    ...overrides
  });

const visit = (value, found = []) => {
  if (Array.isArray(value)) {
    value.forEach((item) => visit(item, found));
    return found;
  }
  if (!value || typeof value !== 'object') return found;
  found.push(value);
  visit(value.props?.children, found);
  return found;
};

const startButtons = (tree) =>
  visit(tree).filter(
    (node) => node.type === Button && node.props.children === 'prefill.home.startSession'
  );

const eventCallback = (eventName) => {
  const eventSource = parseSource(eventPath, typescript.ScriptKind.TSX);
  const registration = findSoleNode(eventSource, `${eventName} registration`, (node) => {
    if (!typescript.isCallExpression(node) || node.arguments.length < 2) return false;
    if (!typescript.isPropertyAccessExpression(node.expression)) return false;
    return (
      node.expression.name.getText(eventSource) === 'on' &&
      node.arguments[0].getText(eventSource).includes(`'${eventName}'`) &&
      typescript.isArrowFunction(node.arguments[1])
    );
  });
  return registration.arguments[1].getText(eventSource);
};

test('one-service guest waits for the existing Start Session button', () => {
  const starts = [];
  const tree = render((service) => starts.push(service));

  assert.equal(starts.length, 0);
  assert.notEqual(tree, null);
  const buttons = startButtons(tree);
  assert.equal(buttons.length, 1);

  buttons[0].props.onClick();
  assert.deepEqual(starts, ['steam']);

  render((service) => starts.push(service));
  render((service) => starts.push(service));
  assert.deepEqual(starts, ['steam']);
});

test('owner and global terminal callbacks return to Start without replacement', () => {
  const starts = [];
  const ended = [];
  const state = [];
  const bindings = {
    addLog: () => undefined,
    t: (key) => key,
    setSession: (value) => state.push(['session', value]),
    setIsLoggedIn: (value) => state.push(['loggedIn', value]),
    setIsPrefillActive: (value) => state.push(['active', value]),
    clearCancelTracking: () => undefined,
    stopAnimations: () => undefined,
    setPrefillProgress: (value) => state.push(['progress', value]),
    clearAllPrefillStorage: () => undefined,
    onSessionEnd: () => ended.push('ended'),
    sessionRef: { current: { id: 'steam-session' } }
  };
  const ownerEnded = bindLifted(eventCallback('SessionEnded'), bindings);
  const globallyTerminated = bindLifted(eventCallback('DaemonSessionTerminated'), bindings);

  ownerEnded({ sessionId: 'steam-session', reason: 'ended' });
  assert.ok(state.some(([name, value]) => name === 'session' && value === null));
  assert.equal(startButtons(render((service) => starts.push(service))).length, 1);

  globallyTerminated({ sessionId: 'steam-session', reason: 'terminated' });
  ownerEnded({ sessionId: 'steam-session', reason: 'duplicate' });
  assert.equal(startButtons(render((service) => starts.push(service))).length, 1);
  assert.equal(starts.length, 0);
  assert.equal(ended.length, 3);
});

test('grant filtering and create-error retry remain explicit', () => {
  const starts = [];
  const multiple = render((service) => starts.push(service), { epicPrefillEnabled: true });
  assert.equal(startButtons(multiple).length, 2);

  const none = render((service) => starts.push(service), { steamPrefillEnabled: false });
  assert.equal(startButtons(none).length, 0);

  const error = render((service) => starts.push(service), { error: 'Docker unavailable' });
  const retry = startButtons(error);
  assert.equal(retry.length, 1);
  retry[0].props.onClick();
  assert.deepEqual(starts, ['steam']);
});
