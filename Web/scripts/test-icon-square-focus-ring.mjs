import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import test from 'node:test';
import { pathToFileURL } from 'node:url';
import { builtStyles } from './transpile-module.mjs';

// A keyboard user tabbing onto a square icon button (kebab, menu trigger, accordion chevron) away
// from the Users page sees the same 2 px focus ring every other button shows, not a 1 px edge. The
// built app's stylesheets are loaded into a page holding one labeled button and one square icon
// button of each variant, each reached with Tab, at 1440 and 390 px. Runs only with PLAYWRIGHT_PATH
// set to a playwright-core install whose Chromium is downloaded, against the built app.

const FOCUS_COLOR = 'rgb(18, 52, 86)';
const SQUARE_VARIANTS = ['filled', 'menu', 'accordion'];
const BUTTON_CLASSES =
  'themed-button-radius font-medium whitespace-nowrap smooth-transition flex items-center justify-center gap-2 button-press';

const page = (css) => `<!doctype html>
<html><head><style>${css}</style>
<style>:root { --theme-border-focus: ${FOCUS_COLOR}; }</style></head>
<body>
<button id="labeled" data-variant="filled" class="${BUTTON_CLASSES}">Save</button>
${SQUARE_VARIANTS.map(
  (variant) =>
    `<button id="square-${variant}" data-variant="${variant}" aria-label="${variant}" class="${BUTTON_CLASSES} btn-icon-square"><svg width="16" height="16"></svg></button>`
).join('\n')}
</body></html>`;

/** The focus ring a focused element paints once its transitions have run, as the browser computes it. */
const ringOf = () => {
  const focused = globalThis.document.activeElement;
  for (const animation of focused.getAnimations()) animation.finish();
  const style = globalThis.getComputedStyle(focused);
  return {
    id: globalThis.document.activeElement.id,
    outline: `${style.outlineStyle} ${style.outlineWidth} ${style.outlineColor}`,
    border: `${style.borderTopStyle} ${style.borderTopWidth} ${style.borderTopColor}`
  };
};

test(
  'a square icon button shows the same 2 px focus ring as a labeled button',
  { skip: !process.env.PLAYWRIGHT_PATH },
  async () => {
    assert.ok(
      existsSync(new URL('../dist/index.html', import.meta.url)),
      'run npm run build before this test'
    );
    const { chromium } = await import(pathToFileURL(process.env.PLAYWRIGHT_PATH).href);
    const browser = await chromium.launch({ headless: true });
    const wrong = [];
    try {
      for (const width of [1440, 390]) {
        const context = await browser.newContext({ viewport: { width, height: 900 } });
        const tab = await context.newPage();
        await tab.setContent(page(builtStyles()));
        const rings = [];
        for (let press = 0; press < 1 + SQUARE_VARIANTS.length; press += 1) {
          await tab.keyboard.press('Tab');
          rings.push(await tab.evaluate(ringOf));
        }
        await context.close();
        const labeled = rings.find((ring) => ring.id === 'labeled');
        assert.equal(
          labeled?.outline,
          `solid 2px ${FOCUS_COLOR}`,
          `at ${width} px a focused labeled button paints ${labeled?.outline}, so the page proves nothing`
        );
        for (const variant of SQUARE_VARIANTS) {
          const ring = rings.find((each) => each.id === `square-${variant}`);
          if (ring?.outline !== labeled.outline)
            wrong.push(
              `at ${width} px a focused ${variant} square icon button paints outline "${ring?.outline}" and border "${ring?.border}", not the labeled button's "${labeled.outline}" ring`
            );
        }
      }
    } finally {
      await browser.close();
    }
    assert.deepEqual(wrong, [], wrong.join('; '));
  }
);
