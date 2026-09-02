import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';

/**
 * The dashboard batch response is one JSON object written by C# and read by TypeScript, and no
 * compiler sees both halves. When a field is added, renamed or dropped on the server and the
 * browser type is not changed with it, `tsc`, `lint`, `knip` and `vite` all stay green because the
 * declaration and its readers are consistently wrong together, and the page throws on load.
 *
 * So compare the two shapes directly: the property names out of the C# classes that serialize, and
 * the member names out of the interfaces the browser reads them through. The API serializes with
 * `JsonNamingPolicy.CamelCase` (Program.cs:73), so a C# `DownloadTotals` is a wire `downloadTotals`.
 */

const repoFile = (relativePath) => new URL(`../../${relativePath}`, import.meta.url);
const webFile = (relativePath) => new URL(`../${relativePath}`, import.meta.url);

/** The text between the braces of `class <name>`, so sibling classes in the same file are ignored. */
const classBody = (source, name) => {
  const header = source.indexOf(`class ${name}`);
  assert.notEqual(header, -1, `no C# class named ${name}`);

  const open = source.indexOf('{', header);
  let depth = 0;
  for (let i = open; i < source.length; i += 1) {
    if (source[i] === '{') depth += 1;
    if (source[i] === '}') {
      depth -= 1;
      if (depth === 0) return source.slice(open + 1, i);
    }
  }

  throw new Error(`unbalanced braces in C# class ${name}`);
};

/** The wire names of the auto-properties a C# class serializes. */
const serializedNames = (source, name) =>
  classBody(source, name)
    .split('\n')
    .map((line) => /^\s*public\s+.+?\s+(\w+)\s*\{\s*get;/.exec(line))
    .filter(Boolean)
    .map(([, property]) => property[0].toLowerCase() + property.slice(1))
    .sort();

/** The member names of a TypeScript interface, read with the compiler rather than a regex. */
const interfaceNames = (file, name) => {
  const sourceFile = ts.createSourceFile(
    file,
    readFileSync(webFile(file), 'utf8'),
    ts.ScriptTarget.Latest,
    true,
    ts.ScriptKind.TS
  );

  const found = [];
  const visit = (node) => {
    if (ts.isInterfaceDeclaration(node) && node.name.text === name) {
      found.push(...node.members.map((member) => member.name.getText(sourceFile)));
    }
    ts.forEachChild(node, visit);
  };
  ts.forEachChild(sourceFile, visit);

  assert.notEqual(found.length, 0, `no TypeScript interface named ${name} in ${file}`);
  return found.sort();
};

const batchResponse = readFileSync(
  repoFile('Api/LancacheManager/Models/Responses/Dashboard/DashboardBatchResponse.cs'),
  'utf8'
);

// The recent-downloads section is typed `object?` on the response above, so its group shape is
// declared where it is built rather than beside the other sections.
const batchService = readFileSync(
  repoFile('Api/LancacheManager/Core/Services/Dashboard/DashboardBatchService.cs'),
  'utf8'
);

test('every section the batch endpoint sends is a field the browser type declares', () => {
  assert.deepEqual(
    serializedNames(batchResponse, 'DashboardBatchResponse'),
    interfaceNames('src/contexts/DashboardDataContext/types.ts', 'DashboardBatchResponse')
  );
});

test('the download totals the batch sends twice match the type both readers share', () => {
  assert.deepEqual(
    serializedNames(batchResponse, 'DownloadTotals'),
    interfaceNames('src/types.ts', 'DownloadTotals')
  );
});

test('a recent-downloads group carries the fields the panel row draws from', () => {
  assert.deepEqual(
    serializedNames(batchService, 'DashboardGameGroup'),
    interfaceNames('src/contexts/DashboardDataContext/types.ts', 'DashboardGameGroup')
  );
});

test('a service filter entry carries the fields the dropdown reads off it', () => {
  assert.deepEqual(
    serializedNames(batchResponse, 'ServiceFilterOption'),
    interfaceNames('src/types.ts', 'ServiceFilterOption')
  );
});
