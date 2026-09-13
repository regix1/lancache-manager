#!/usr/bin/env node
import { dirname, relative, resolve, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import ts from 'typescript';

const sharedFiles = new Set(['utils/dateTimeFormat.ts', 'utils/timezone.ts']);
const dateMethods = new Set([
  'toLocaleString',
  'toLocaleDateString',
  'toLocaleTimeString',
  'toString',
  'toDateString',
  'toTimeString',
  'toUTCString',
  'toGMTString'
]);
const intlMethods = new Set(['format', 'formatToParts', 'formatRange', 'formatRangeToParts']);
const message =
  'Use FormattedTimestamp/useFormattedDateTime, or formatTimestamp with the reader clock, instead of native date formatting.';

function isBuiltin(symbol, owner) {
  return (
    symbol
      ?.getDeclarations()
      ?.some(
        (declaration) =>
          declaration.getSourceFile().isDeclarationFile &&
          /[/\\]lib\.[^/\\]+\.d\.ts$/.test(declaration.getSourceFile().fileName) &&
          (declaration.parent.name?.text === owner || declaration.name?.text === owner)
      ) ?? false
  );
}

function isTimezoneLookup(node) {
  const member = node.parent;
  const call = member?.parent;
  const zone = call?.parent;
  return (
    ts.isPropertyAccessExpression(member) &&
    member.name.text === 'resolvedOptions' &&
    ts.isCallExpression(call) &&
    call.expression === member &&
    ts.isPropertyAccessExpression(zone) &&
    zone.name.text === 'timeZone'
  );
}

export function checkTimestamps(program, srcRoot) {
  const checker = program.getTypeChecker();
  const errors = [];
  for (const source of program.getSourceFiles()) {
    const path = relative(srcRoot, source.fileName);
    if (source.isDeclarationFile || path.startsWith(`..${sep}`) || path === '..') continue;
    const file = path.split(sep).join('/');
    if (sharedFiles.has(file)) continue;

    const report = (node) => {
      const position = source.getLineAndCharacterOfPosition(node.getStart(source));
      errors.push({ file, line: position.line + 1, column: position.character + 1, message });
    };
    const visit = (node) => {
      if (ts.isPropertyAccessExpression(node) || ts.isElementAccessExpression(node)) {
        const propertyType = ts.isElementAccessExpression(node)
          ? checker.getTypeAtLocation(node.argumentExpression)
          : undefined;
        const property = ts.isPropertyAccessExpression(node)
          ? node.name.text
          : propertyType?.isStringLiteral()
            ? propertyType.value
            : undefined;
        const symbol =
          checker.getSymbolAtLocation(node) ??
          (property
            ? checker.getTypeAtLocation(node.expression).getNonNullableType().getProperty(property)
            : undefined);
        const name = symbol?.getName();
        if (
          (dateMethods.has(name) && isBuiltin(symbol, 'Date')) ||
          (intlMethods.has(name) && isBuiltin(symbol, 'DateTimeFormat'))
        )
          report(node);
      }
      if (ts.isCallExpression(node) || ts.isNewExpression(node)) {
        const result = checker.getTypeAtLocation(node).getSymbol();
        const callee = checker.getTypeAtLocation(node.expression).getSymbol();
        if (
          result?.getName() === 'DateTimeFormat' &&
          isBuiltin(result, 'DateTimeFormat') &&
          !isTimezoneLookup(node)
        )
          report(node);
        if (
          ts.isCallExpression(node) &&
          callee?.getName() === 'DateConstructor' &&
          isBuiltin(callee, 'DateConstructor')
        )
          report(node);
      }
      ts.forEachChild(node, visit);
    };
    visit(source);
  }
  return errors;
}

if (process.argv[1] && pathToFileURL(resolve(process.argv[1])).href === import.meta.url) {
  const webRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
  const config = ts.getParsedCommandLineOfConfigFile(
    resolve(webRoot, 'tsconfig.json'),
    {},
    {
      ...ts.sys,
      onUnRecoverableConfigFileDiagnostic: (diagnostic) => {
        console.error(ts.flattenDiagnosticMessageText(diagnostic.messageText, '\n'));
        process.exitCode = 1;
      }
    }
  );
  if (config?.errors.length) {
    for (const error of config.errors)
      console.error(ts.flattenDiagnosticMessageText(error.messageText, '\n'));
    process.exitCode = 1;
  } else if (config) {
    const program = ts.createProgram({ rootNames: config.fileNames, options: config.options });
    const errors = checkTimestamps(program, resolve(webRoot, 'src'));
    for (const error of errors)
      console.error(`${error.file}:${error.line}:${error.column}: ${error.message}`);
    if (errors.length > 0) process.exitCode = 1;
    else
      console.log(
        'Timestamp lint: PASS (native date formatting stays in the shared clock utilities)'
      );
  }
}
