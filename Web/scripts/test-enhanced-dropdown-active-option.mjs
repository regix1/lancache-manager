import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';
import { bindLifted, findSoleNode, liftHookCallback, parseSource } from './transpile-module.mjs';

const COMPONENT_PATH = 'src/components/ui/EnhancedDropdown.tsx';
const sourcePath = process.env.ENHANCED_DROPDOWN_SOURCE;
const source = sourcePath
  ? ts.createSourceFile(
      sourcePath,
      readFileSync(sourcePath, 'utf8'),
      ts.ScriptTarget.Latest,
      true,
      ts.ScriptKind.TSX
    )
  : parseSource(COMPONENT_PATH, ts.ScriptKind.TSX);

const selectableDeclaration = findSoleNode(
  source,
  'isSelectableOption declaration',
  (node) => ts.isFunctionDeclaration(node) && node.name?.text === 'isSelectableOption'
);
const isSelectableOption = bindLifted(`(${selectableDeclaration.getText(source)})`, {});

const visibleOptionsSource = liftHookCallback(
  COMPONENT_PATH,
  'useMemo',
  'searchTerm.trim().toLowerCase()'
);
const selectableValuesSource = liftHookCallback(
  COMPONENT_PATH,
  'useMemo',
  'visibleOptions.filter(isSelectableOption)'
);
const activeEffectSource = liftHookCallback(COMPONENT_PATH, 'useEffect', 'const fallback');
const closeEffectSource = liftHookCallback(COMPONENT_PATH, 'useEffect', "setSearchTerm('')");
const moveActiveSource = liftHookCallback(
  COMPONENT_PATH,
  'useCallback',
  'selectableValues.indexOf(current)'
);
const searchKeySource = findSoleNode(
  source,
  'handleSearchKeyDown declaration',
  (node) =>
    ts.isVariableDeclaration(node) &&
    node.name.getText(source) === 'handleSearchKeyDown' &&
    ts.isCallExpression(node.initializer)
).initializer.arguments[0].getText(source);

const searchChange = findSoleNode(
  source,
  'search input change callback',
  (node) =>
    ts.isJsxAttribute(node) &&
    node.name.getText(source) === 'onChange' &&
    ts.isJsxExpression(node.initializer) &&
    ts.isArrowFunction(node.initializer.expression) &&
    node.initializer.expression.getText(source).includes('setSearchTerm')
).initializer.expression.getText(source);

const options = [
  { value: 'alpha', label: 'Alpha' },
  { value: 'beta', label: 'Beta' },
  { value: 'disabled', label: 'Disabled', disabled: true },
  { value: 'divider', label: 'Group' },
  {
    value: 'europe',
    label: 'Europe',
    submenu: [{ value: 'Europe/Berlin', label: 'Berlin' }]
  }
];

const visibleFor = (searchTerm, searchable = true, currentOptions = options) =>
  bindLifted(visibleOptionsSource, { options: currentOptions, searchTerm, searchable })();

const selectableFor = (visibleOptions) =>
  bindLifted(selectableValuesSource, { visibleOptions, isSelectableOption })();

const applyActiveEffect = ({
  activeValue,
  isOpen = true,
  searchTerm = '',
  searchable = true,
  selectedValue = 'beta',
  selectableValues = selectableFor(visibleFor(searchTerm, searchable))
}) => {
  let next = activeValue;
  const setActiveValue = (update) => {
    next = typeof update === 'function' ? update(next) : update;
  };
  bindLifted(activeEffectSource, {
    isOpen,
    searchTerm,
    searchable,
    selectedValue,
    selectableValues,
    setActiveValue
  })();
  return next;
};

const changeSearch = (activeValue, value) => {
  let nextActive = activeValue;
  let nextSearch = '';
  bindLifted(searchChange, {
    setActiveValue: (next) => {
      nextActive = typeof next === 'function' ? next(nextActive) : next;
    },
    setSearchTerm: (next) => {
      nextSearch = next;
    }
  })({ target: { value } });
  const selectableValues = selectableFor(visibleFor(nextSearch));
  return {
    searchTerm: nextSearch,
    selectableValues,
    activeValue: applyActiveEffect({
      activeValue: nextActive,
      searchTerm: nextSearch,
      selectableValues
    })
  };
};

test('searchable open starts on the selected selectable row', () => {
  assert.equal(applyActiveEffect({ activeValue: null }), 'beta');
});

test('typing resets active navigation before the filtered effect chooses its first row', () => {
  const typed = changeSearch('beta', 'a');
  assert.deepEqual(typed.selectableValues, ['alpha', 'beta']);
  assert.equal(typed.activeValue, 'alpha', 'the still-matching old row does not keep navigation');

  const cleared = changeSearch(typed.activeValue, '');
  assert.equal(cleared.activeValue, 'beta');
  assert.equal(changeSearch('alpha', '   ').activeValue, 'beta');
  assert.equal(changeSearch('beta', 'no match').activeValue, null);
});

test('active rows survive equivalent options and fall back when removed or unavailable', () => {
  const equivalent = options.map((option) =>
    option.submenu
      ? { ...option, submenu: option.submenu.map((entry) => ({ ...entry })) }
      : { ...option }
  );
  assert.equal(
    applyActiveEffect({
      activeValue: 'alpha',
      selectableValues: selectableFor(visibleFor('', true, equivalent))
    }),
    'alpha'
  );
  assert.equal(applyActiveEffect({ activeValue: 'removed', selectedValue: 'beta' }), 'beta');
  assert.equal(applyActiveEffect({ activeValue: null, selectedValue: 'disabled' }), 'alpha');
  assert.equal(applyActiveEffect({ activeValue: null, selectedValue: null }), 'alpha');
});

test('arrow and Enter callbacks move and commit the actual active row', () => {
  const selectableValues = ['alpha', 'beta'];
  let activeValue = 'alpha';
  const setActiveValue = (update) => {
    activeValue = typeof update === 'function' ? update(activeValue) : update;
  };
  const moveActive = bindLifted(moveActiveSource, {
    searchable: true,
    selectableValues,
    selectedValue: 'beta',
    setActiveValue
  });
  moveActive(1);
  assert.equal(activeValue, 'beta');
  moveActive(1);
  assert.equal(activeValue, 'beta', 'navigation stops at the list boundary');
  moveActive(-1);
  assert.equal(activeValue, 'alpha');

  let selected = null;
  let prevented = false;
  const handleSearchKeyDown = bindLifted(searchKeySource, {
    activeValue,
    selectableValues,
    moveActive,
    handleSelect: (value) => {
      selected = value;
    }
  });
  handleSearchKeyDown({
    key: 'Enter',
    preventDefault: () => {
      prevented = true;
    }
  });
  assert.equal(selected, 'alpha');
  assert.equal(prevented, true);
});

test('close clears the filter and reopen activates the committed selection', () => {
  let searchTerm = 'alp';
  let activeValue = 'alpha';
  bindLifted(closeEffectSource, {
    isOpen: false,
    setExpandedSubmenu: () => undefined,
    setSubmenuPosition: () => undefined,
    submenuTriggerRef: { current: {} },
    setSearchTerm: (next) => {
      searchTerm = next;
    },
    setActiveValue: (next) => {
      activeValue = next;
    }
  })();
  assert.equal(searchTerm, '');
  assert.equal(activeValue, null);
  assert.equal(applyActiveEffect({ activeValue, searchTerm, selectedValue: 'alpha' }), 'alpha');
});

test('Escape closes and restores focus without changing the committed value', () => {
  const anchoredCall = findSoleNode(
    source,
    'useAnchoredPanel call',
    (node) => ts.isCallExpression(node) && node.expression.getText(source) === 'useAnchoredPanel'
  );
  const anchoredArgument = anchoredCall.arguments[0];
  assert.ok(ts.isObjectLiteralExpression(anchoredArgument));
  const onEscapeProperty = anchoredArgument.properties.find(
    (property) => property.name?.getText(source) === 'onEscape'
  );
  assert.ok(onEscapeProperty, 'production anchored options supply Escape focus restoration');

  const callbackFor = (name) => {
    const declaration = findSoleNode(
      source,
      `${name} declaration`,
      (node) =>
        ts.isVariableDeclaration(node) &&
        node.name.getText(source) === name &&
        ts.isCallExpression(node.initializer)
    );
    return declaration.initializer.arguments[0].getText(source);
  };

  let isOpen = true;
  const committedValue = 'beta';
  let focusCalls = 0;
  const buttonRef = {
    current: {
      focus: () => {
        focusCalls += 1;
      }
    }
  };
  const setIsOpen = (update) => {
    isOpen = typeof update === 'function' ? update(isOpen) : update;
  };
  const closeDropdown = bindLifted(callbackFor('closeDropdown'), { setIsOpen });
  const focusTrigger = bindLifted(callbackFor('focusTrigger'), { buttonRef });
  const anchoredOptions = bindLifted(`() => (${anchoredArgument.getText(source)})`, {
    isOpen,
    buttonRef,
    dropdownRef: { current: {} },
    closeDropdown,
    focusTrigger,
    MENU_GUTTER_PX: 8,
    place: () => undefined
  })();

  anchoredOptions.onClose();
  assert.equal(isOpen, false, 'outside close keeps using the shared close callback');
  assert.equal(focusCalls, 0, 'outside close does not move focus');
  assert.equal(committedValue, 'beta');

  isOpen = true;
  anchoredOptions.onClose();
  anchoredOptions.onEscape();
  assert.equal(isOpen, false);
  assert.equal(focusCalls, 1, 'Escape returns focus to the trigger');
  assert.equal(committedValue, 'beta');
});

test('search and trigger controls expose their owned list and active row', () => {
  const attributesFor = (element) =>
    new Map(
      element.attributes.properties
        .filter(ts.isJsxAttribute)
        .map((attribute) => [
          attribute.name.getText(source),
          attribute.initializer?.getText(source)
        ])
    );
  const searchInput = findSoleNode(
    source,
    'searchbox input',
    (node) =>
      ts.isJsxSelfClosingElement(node) &&
      node.tagName.getText(source) === 'input' &&
      attributesFor(node).get('role') === '"searchbox"'
  );
  const trigger = findSoleNode(
    source,
    'combobox trigger',
    (node) =>
      ts.isJsxElement(node) &&
      node.openingElement.tagName.getText(source) === 'button' &&
      attributesFor(node.openingElement).get('role') === '"combobox"'
  ).openingElement;
  const searchAttributes = attributesFor(searchInput);
  const triggerAttributes = attributesFor(trigger);
  assert.equal(searchAttributes.get('aria-controls'), '{listboxId}');
  assert.match(searchAttributes.get('aria-activedescendant'), /activeValue/);
  assert.equal(triggerAttributes.get('aria-controls'), '{listboxId}');
  assert.match(triggerAttributes.get('aria-activedescendant'), /!searchable && isOpen/);
  assert.equal(
    applyActiveEffect({ activeValue: null, searchable: false, selectedValue: 'beta' }),
    'beta',
    'the nonsearchable trigger starts on its selected row'
  );
});
