import assert from 'node:assert/strict';
import test from 'node:test';
import { pathToFileURL } from 'node:url';
import ts from 'typescript';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import {
  bindLifted,
  builtStyles,
  collectNodes,
  make,
  parseSource,
  translator
} from './transpile-module.mjs';

// Measures the client exclusion row that holds a prefill rule for any address, as the shipped
// row renders it with the built stylesheet. Run after `npm run build`, with PLAYWRIGHT_PATH set
// to a playwright-core install whose Chromium is downloaded.

const translate = translator('en');
const noop = () => undefined;
const icon = ({ className, size }) =>
  React.createElement('svg', {
    className,
    width: size ?? 16,
    height: size ?? 16,
    'aria-hidden': true
  });
const passthrough = ({ children }) => React.createElement(React.Fragment, null, children);

const hooks = {
  useState: React.useState,
  useRef: React.useRef,
  useCallback: React.useCallback,
  useEffect: React.useEffect,
  useLayoutEffect: React.useLayoutEffect,
  useMemo: React.useMemo,
  memo: React.memo,
  useTranslation: () => ({ t: translate })
};

const renderPrefillRuleRow = () => {
  const Tooltip = make('src/components/ui/Tooltip.tsx', 'Tooltip', {
    React,
    ...hooks,
    createPortal: noop,
    document: { documentElement: { getAttribute: () => null } },
    Info: icon,
    useMediaQuery: () => false,
    APP_EVENTS: { TOOLTIPS_CHANGE: 'tooltips-change' },
    clampToViewport: (value) => value
  });
  const LoadingSpinner = make('src/components/common/LoadingSpinner.tsx', 'LoadingSpinner', {
    React,
    Loader2: icon
  });
  const Button = make('src/components/ui/Button.tsx', 'Button', {
    React,
    LoadingSpinner,
    useOptionalDirectoryPermissionsContext: () => null
  });
  const SegmentedControl = make('src/components/ui/SegmentedControl.tsx', 'SegmentedControl', {
    React,
    Tooltip
  });
  const MultiSelectDropdown = make(
    'src/components/ui/MultiSelectDropdown.tsx',
    'MultiSelectDropdown',
    {
      React,
      ...hooks,
      noAutofill: {},
      createPortal: noop,
      ChevronDown: icon,
      Check: icon,
      CustomScrollbar: passthrough,
      Tooltip,
      useAnchoredPanel: () => ({ present: false, closing: false, position: {} }),
      clampToViewport: (value) => value,
      MENU_GUTTER_PX: 8
    }
  );
  const constants = parseSource('src/components/features/prefill/persistentPrefillConstants.ts');
  const services = collectNodes(
    constants,
    (node) =>
      ts.isVariableDeclaration(node) &&
      node.name.getText(constants) === 'PERSISTENT_PREFILL_SERVICES'
  )[0];
  assert.ok(services, 'PERSISTENT_PREFILL_SERVICES is declared');
  const PERSISTENT_PREFILL_SERVICES = bindLifted(
    `() => (${services.initializer.getText(constants)})`,
    {}
  )();

  const section = parseSource('src/components/features/management/sections/ClientsSection.tsx');
  const rows = collectNodes(
    section,
    (node) =>
      ts.isJsxElement(node) &&
      node.openingElement.attributes.properties.some(
        (attribute) =>
          ts.isJsxAttribute(attribute) &&
          attribute.name.getText(section) === 'className' &&
          attribute.initializer?.getText(section).includes('clients-exclusion-row')
      )
  );
  assert.equal(rows.length, 1, 'ClientsSection renders one client exclusion row');
  const row = bindLifted(
    `(rule) => (${rows[0].getText(section)})`,
    {
      React,
      t: translate,
      PERSISTENT_PREFILL_SERVICES,
      MultiSelectDropdown,
      SegmentedControl,
      Tooltip,
      Button,
      Trash2: icon,
      ClientIpDisplay: ({ clientIp }) => React.createElement('span', null, clientIp),
      handleChangeServices: noop,
      handleChangeTraffic: noop,
      handleChangeMode: noop,
      handleRemoveExcluded: noop
    },
    { jsx: ts.JsxEmit.React }
  );
  return renderToStaticMarkup(
    row({ rowId: 1, ip: '', traffic: 'prefill', mode: 'exclude', services: [] })
  );
};

test(
  'the services dropdown in a prefill rule row is as tall as the two segmented controls beside it',
  { skip: !process.env.PLAYWRIGHT_PATH },
  async () => {
    const markup = renderPrefillRuleRow();
    const styles = builtStyles();
    const { chromium } = await import(pathToFileURL(process.env.PLAYWRIGHT_PATH).href);
    const browser = await chromium.launch({ headless: true });
    try {
      for (const viewport of [
        { width: 1280, height: 800 },
        { width: 390, height: 844 }
      ]) {
        for (const deviceScaleFactor of [1, 1.1]) {
          const page = await browser.newPage({ viewport, deviceScaleFactor });
          await page.route('**/*', (route) => route.abort());
          await page.setContent(
            `<!doctype html><html lang="en"><head><style>${styles}</style></head>` +
              `<body><div class="mgmt-list divided-list">${markup}</div></body></html>`
          );
          const measured = await page.evaluate((placeholder) => {
            const actions = globalThis.document.querySelector(
              '.clients-exclusion-row .mgmt-row__actions'
            );
            const dropdown = [...actions.querySelectorAll('button')].find((button) =>
              button.textContent.includes(placeholder)
            );
            const height = (element) => element.getBoundingClientRect().height;
            return {
              width: globalThis.innerWidth,
              dropdown: dropdown ? height(dropdown) : null,
              segments: [...actions.querySelectorAll('[role="radiogroup"]')].map(height)
            };
          }, translate('management.sections.clients.allServices'));
          await page.close();

          const where = `${viewport.width}x${viewport.height} at scale ${deviceScaleFactor}`;
          assert.equal(measured.width, viewport.width, `viewport width at ${where}`);
          assert.notEqual(measured.dropdown, null, `the All services dropdown renders at ${where}`);
          assert.equal(measured.segments.length, 2, `both segmented controls render at ${where}`);
          for (const segment of measured.segments) {
            assert.ok(
              Math.abs(measured.dropdown - segment) < 0.5,
              `at ${where} the All services dropdown is ${measured.dropdown}px tall but the ` +
                `segmented control beside it is ${segment}px tall`
            );
          }
        }
      }
    } finally {
      await browser.close();
    }
  }
);
