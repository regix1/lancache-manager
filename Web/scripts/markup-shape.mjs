import React from 'react';
import { Loader2 } from 'lucide-react';
import { renderToStaticMarkup } from 'react-dom/server';
import { make } from './transpile-module.mjs';

/**
 * Compares a control drawn at rest with the same control drawn while it saves, from the markup
 * React writes. An element positioned `absolute` or `fixed` takes no room in the row, so it and its
 * contents are left out of the shape: a spinner laid over the content keeps the control's size. Any
 * other element or text the busy control adds, drops or reorders changes the shape.
 */

/** The shipped spinner, drawing the same icon the app draws. */
export const LoadingSpinner = make('src/components/common/LoadingSpinner.tsx', 'LoadingSpinner', {
  React,
  Loader2
});

const OVERLAY = new Set(['absolute', 'fixed']);
const DIMMING = /^(opacity-|brightness-|saturate-|grayscale)/;

/**
 * Parses static markup into element nodes with their class tokens and children.
 *
 * @param {string} markup What `renderToStaticMarkup` returned.
 * @returns {{ tag: string, classes: string[], children: Array<object | string> }} The root's parent.
 */
const parse = (markup) => {
  const root = { tag: '#root', classes: [], children: [] };
  const open = [root];
  for (const [, closing, tag, attributes, text] of markup.matchAll(
    /<(\/?)([a-zA-Z][a-zA-Z0-9-]*)([^>]*)>|([^<]+)/g
  )) {
    const parent = open[open.length - 1];
    if (text !== undefined) {
      if (text.trim() !== '') parent.children.push(text.trim());
    } else if (closing) open.pop();
    else {
      const classes = (/\sclass="([^"]*)"/.exec(attributes)?.[1] ?? '')
        .split(/\s+/)
        .filter(Boolean);
      const node = { tag, classes, children: [] };
      parent.children.push(node);
      if (!attributes.trimEnd().endsWith('/')) open.push(node);
    }
  }
  return root;
};

/** The in-flow element and text outline of a parsed node. */
const outline = (node) =>
  typeof node === 'string'
    ? JSON.stringify(node)
    : `${node.tag}[${node.children
        .filter((child) => typeof child === 'string' || !child.classes.some((c) => OVERLAY.has(c)))
        .map(outline)
        .join(',')}]`;

/** Every class token in a parsed tree. */
const classTokens = (node) =>
  typeof node === 'string' ? [] : [...node.classes, ...node.children.flatMap(classTokens)];

/**
 * What the busy control changes about its shape and brightness compared with the control at rest.
 *
 * @param {React.ReactElement} rest The control at rest.
 * @param {React.ReactElement} busy The same control while it saves.
 * @returns {{ restOutline: string, busyOutline: string, addedDimming: string[] }} Both in-flow
 *   outlines and every dimming class the busy control adds.
 */
export const busyChange = (rest, busy) => {
  const restTree = parse(renderToStaticMarkup(rest));
  const busyTree = parse(renderToStaticMarkup(busy));
  const restClasses = new Set(classTokens(restTree));
  return {
    restOutline: outline(restTree),
    busyOutline: outline(busyTree),
    addedDimming: [...new Set(classTokens(busyTree))].filter(
      (token) => DIMMING.test(token) && !restClasses.has(token)
    )
  };
};
