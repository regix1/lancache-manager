import { readdirSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';
import { collectNodes } from './transpile-module.mjs';

export { collectNodes };
export const repository = fileURLToPath(new URL('../../', import.meta.url));
export const normalizePath = (file) => file.replaceAll('\\', '/');

export function sourceFiles(root = repository) {
  const files = [];
  const excluded = new Set([
    'bin',
    'obj',
    'node_modules',
    'target',
    '.git',
    'wwwroot',
    'Migrations'
  ]);
  function walk(directory, extensions) {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const file = path.join(directory, entry.name);
      if (entry.isDirectory() && !excluded.has(entry.name)) walk(file, extensions);
      else if (
        entry.isFile() &&
        extensions.has(path.extname(entry.name)) &&
        !/\.(?:g|generated|Designer|test|spec)\./i.test(entry.name)
      )
        files.push(file);
    }
  }
  for (const [directory, extensions] of [
    ['Api/LancacheManager', new Set(['.cs'])],
    ['Web/src', new Set(['.ts', '.tsx'])],
    ['rust-processor/src', new Set(['.rs'])]
  ])
    walk(path.join(root, directory), extensions);
  if (!files.length) throw new Error('No shipped sources were found');
  return files.sort().map((file) => ({ file, text: readFileSync(file, 'utf8') }));
}

export function parseSource(file, text) {
  const source = ts.createSourceFile(
    file,
    text,
    ts.ScriptTarget.Latest,
    true,
    /\.tsx$/.test(file) ? ts.ScriptKind.TSX : ts.ScriptKind.TS
  );
  if (source.parseDiagnostics.length)
    throw new Error(
      ts.formatDiagnosticsWithColorAndContext(source.parseDiagnostics, {
        getCanonicalFileName: (name) => name,
        getCurrentDirectory: () => repository,
        getNewLine: () => '\n'
      })
    );
  return source;
}

export function sourceProgram(sources) {
  const configPath = path.join(repository, 'Web/tsconfig.json');
  const config = ts.readConfigFile(configPath, ts.sys.readFile);
  if (config.error)
    throw new Error(ts.flattenDiagnosticMessageText(config.error.messageText, '\n'));
  const options = ts.parseJsonConfigFileContent(
    config.config,
    ts.sys,
    path.dirname(configPath)
  ).options;
  const host = ts.createCompilerHost(options);
  const originals = new Map(
    sources.map(({ file, text }) => [normalizePath(path.resolve(file)), text])
  );
  const read = host.readFile.bind(host);
  host.readFile = (file) => originals.get(normalizePath(path.resolve(file))) ?? read(file);
  const exists = host.fileExists.bind(host);
  host.fileExists = (file) => originals.has(normalizePath(path.resolve(file))) || exists(file);
  const program = ts.createProgram(
    sources.map(({ file }) => file),
    options,
    host
  );
  const diagnostics = program.getSyntacticDiagnostics();
  if (diagnostics.length)
    throw new Error(ts.flattenDiagnosticMessageText(diagnostics[0].messageText, '\n'));
  return program;
}

/** Keep token offsets and literal values, but exclude comment text from every consumer. */
export function tokens(source, file = 'source') {
  const result = [];
  let line = 1;
  let offset = 0;
  const expression =
    /\/\/[^\r\n]*|\/\*[\s\S]*?\*\/|r(#{0,})"[\s\S]*?"\1|"""[\s\S]*?"""|@?"(?:""|\\[\s\S]|[^"\\])*"|'(?:\\.|[^'\\])'|[A-Za-z_$][\w$]*|\d+(?:\.\d+)?|=>|\?\?|\|\||\?\.|[^\s]/g;
  for (const match of source.matchAll(expression)) {
    line += source.slice(offset, match.index).split('\n').length - 1;
    offset = match.index;
    const word = match[0];
    if (word.startsWith('//') || word.startsWith('/*')) continue;
    const literal = word.startsWith('"') || word.startsWith('@"') || word.startsWith("'");
    result.push({
      text: word,
      value: literal ? word.replace(/^@?["']|["']$/g, '') : undefined,
      offset: match.index,
      line
    });
  }
  const stack = [];
  for (let index = 0; index < result.length; index++) {
    const token = result[index];
    if (token.value !== undefined) continue;
    if (['{', '(', '['].includes(token.text)) stack.push(index);
    else if (['}', ')', ']'].includes(token.text)) {
      const opening = stack.pop();
      if (
        opening === undefined ||
        result[opening].text !== { '}': '{', ')': '(', ']': '[' }[token.text]
      )
        throw new Error(`${file}:${token.line}: unbalanced source delimiter`);
      token.pair = opening;
      result[opening].pair = index;
    }
  }
  if (stack.length) throw new Error(`${file}: unclosed source delimiter`);
  return result;
}

export function location(source, node) {
  return `${normalizePath(source.fileName)}:${source.getLineAndCharacterOfPosition(node.getStart(source)).line + 1}`;
}

export function assertClean(violations) {
  if (violations.length) throw new Error(violations.join('\n'));
}
