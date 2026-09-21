import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';
import {
  assertClean,
  collectNodes,
  location,
  normalizePath,
  repository,
  sourceFiles,
  sourceProgram,
  tokens
} from './source-contracts.mjs';

export function lifetime(name) {
  const word = name.replaceAll('_', '').toLowerCase();
  const absolute = word.match(/^(.*?)(?:expiresat|expiry|deadline)(?:utc|str|ms)?$/);
  if (absolute) return { stem: absolute[1], kind: 'absolute' };
  const relative = word.match(/^(.*?)(?:expiresin|timeremaining|ttl)(?:seconds|milliseconds|ms)?$/);
  return relative ? { stem: relative[1], kind: 'relative' } : undefined;
}

function duplicates(names) {
  const absolute = new Set(
    names
      .map(lifetime)
      .filter((entry) => entry?.kind === 'absolute')
      .map((entry) => entry.stem)
  );
  return names.filter((name) => {
    const entry = lifetime(name);
    return entry?.kind === 'relative' && absolute.has(entry.stem);
  });
}

export function checkDeadlines(sources) {
  const violations = [];
  const counts = { files: sources.length, types: 0, absolute: 0 };
  const scripts = sources.filter(({ file }) => /\.tsx?$/.test(file));
  const program = sourceProgram(scripts);
  const checker = program.getTypeChecker();
  for (const { file } of scripts) {
    const source = program.getSourceFile(file);
    if (!source) throw new Error(`Source was not parsed: ${file}`);
    const nodes = collectNodes(source, () => true);
    const writes = new Map();
    for (const node of nodes) {
      if (!ts.isBinaryExpression(node) || node.operatorToken.kind !== ts.SyntaxKind.EqualsToken)
        continue;
      const symbol = checker.getSymbolAtLocation(node.left);
      if (!symbol) continue;
      if (!writes.has(symbol)) writes.set(symbol, []);
      writes.get(symbol).push(node);
    }
    function assigned(node, seen, read) {
      const symbol = checker.getSymbolAtLocation(node);
      for (const declaration of symbol?.declarations ?? [])
        if (ts.isVariableDeclaration(declaration) && declaration.initializer)
          return read(declaration.initializer, seen);
      const assignments = (writes.get(symbol) ?? []).filter(
        (entry) => entry.getStart(source) < node.getStart(source)
      );
      return assignments.length ? read(assignments.at(-1).right, seen) : undefined;
    }
    function clock(node, seen = new Set()) {
      if (!node || seen.has(node)) return false;
      seen.add(node);
      if (ts.isIdentifier(node)) return Boolean(assigned(node, seen, clock));
      if (ts.isPropertyAccessExpression(node) && /createdAt/i.test(node.name.text)) return true;
      if (ts.isCallExpression(node))
        return (
          node.expression.getText(source) === 'Date.now' ||
          node.arguments.some((argument) => clock(argument, new Set(seen)))
        );
      if (
        ts.isFunctionLike(node) ||
        ts.isObjectLiteralExpression(node) ||
        ts.isArrayLiteralExpression(node)
      )
        return false;
      let found = false;
      ts.forEachChild(node, (child) => {
        if (!found && clock(child, new Set(seen))) found = true;
      });
      return found;
    }
    function origin(node, seen = new Set()) {
      if (!node || seen.has(node)) return undefined;
      seen.add(node);
      if (ts.isParenthesizedExpression(node) || ts.isAsExpression(node))
        return origin(node.expression, seen);
      if (ts.isPropertyAccessExpression(node)) {
        const entry = lifetime(node.name.text);
        if (entry)
          return {
            ...entry,
            names: checker
              .getTypeAtLocation(node.expression)
              .getProperties()
              .map((item) => item.name)
          };
      }
      if (ts.isIdentifier(node)) {
        const value = assigned(node, seen, origin);
        if (value) return value;
        const symbol = checker.getSymbolAtLocation(node);
        for (const declaration of symbol?.declarations ?? []) {
          if (ts.isVariableDeclaration(declaration) && declaration.initializer)
            return origin(declaration.initializer, seen);
          if (
            ts.isBindingElement(declaration) &&
            ts.isVariableDeclaration(declaration.parent.parent)
          ) {
            const receiver = declaration.parent.parent.initializer;
            const entry = lifetime(
              declaration.propertyName?.getText(source) ?? declaration.name.getText(source)
            );
            if (receiver && entry)
              return {
                ...entry,
                names: checker
                  .getTypeAtLocation(receiver)
                  .getProperties()
                  .map((item) => item.name)
              };
          }
        }
      }
      return undefined;
    }
    function rawAdmission(node) {
      if (
        !(ts.isInterfaceDeclaration(node) || ts.isTypeAliasDeclaration(node)) ||
        node.modifiers?.some((modifier) => modifier.kind === ts.SyntaxKind.ExportKeyword)
      )
        return false;
      const owns = (entry) =>
        checker.getTypeAtLocation(entry).symbol?.declarations?.includes(node) ||
        checker.getTypeAtLocation(entry).aliasSymbol?.declarations?.includes(node);
      const reads = nodes.filter(
        (entry) => ts.isPropertyAccessExpression(entry) && owns(entry.expression)
      );
      if (
        !reads.some((entry) => lifetime(entry.name.text)?.kind === 'absolute') ||
        reads.some((entry) => lifetime(entry.name.text)?.kind === 'relative')
      )
        return false;
      const escaped = nodes.some(
        (entry) =>
          ts.isIdentifier(entry) &&
          owns(entry) &&
          !(ts.isPropertyAccessExpression(entry.parent) && entry.parent.expression === entry) &&
          !ts.isTypeReferenceNode(entry.parent) &&
          !ts.isInterfaceDeclaration(entry.parent) &&
          !ts.isTypeAliasDeclaration(entry.parent) &&
          !ts.isParameter(entry.parent)
      );
      if (escaped) return false;
      return nodes.some(
        (entry) =>
          ts.isReturnStatement(entry) &&
          entry.expression &&
          ts.isObjectLiteralExpression(entry.expression) &&
          entry.expression.properties.some(
            (property) =>
              ts.isPropertyAssignment(property) &&
              lifetime(property.name.getText(source))?.kind === 'absolute' &&
              [property.initializer, ...collectNodes(property.initializer, () => true)].some(
                (child) => reads.includes(child)
              )
          )
      );
    }
    for (const node of nodes) {
      let members;
      if (
        ts.isInterfaceDeclaration(node) ||
        ts.isTypeLiteralNode(node) ||
        ts.isClassDeclaration(node)
      )
        members = node.members;
      else if (ts.isTypeAliasDeclaration(node) && !ts.isTypeLiteralNode(node.type)) members = [];
      else if (ts.isObjectLiteralExpression(node)) members = node.properties;
      else if (ts.isJsxAttributes(node)) members = node.properties;
      if (members) {
        counts.types++;
        const names = [
          ...new Set([
            ...members
              .filter((member) => member.name)
              .map((member) => member.name.getText(source).replace(/^['"]|['"]$/g, '')),
            ...checker
              .getTypeAtLocation(node)
              .getProperties()
              .map((member) => member.name)
          ])
        ];
        counts.absolute += names.filter((name) => lifetime(name)?.kind === 'absolute').length;
        const repeated = duplicates(names);
        if (
          repeated.length &&
          !rawAdmission(node) &&
          !(
            ts.isTypeLiteralNode(node) &&
            ts.isTypeAliasDeclaration(node.parent) &&
            rawAdmission(node.parent)
          )
        )
          violations.push(`${location(source, node)}: dual deadline fields ${repeated.join(', ')}`);
      }
      if (
        (ts.isBinaryExpression(node) && node.operatorToken.kind === ts.SyntaxKind.PlusToken) ||
        (ts.isCallExpression(node) &&
          /\.Add(?:Seconds|Milliseconds)\s*$/.test(node.expression.getText(source)))
      ) {
        if (!clock(node)) continue;
        for (const child of [node, ...collectNodes(node, () => true)]) {
          const entry = origin(child);
          if (
            entry?.kind === 'relative' &&
            entry.names.some(
              (name) => lifetime(name)?.kind === 'absolute' && lifetime(name).stem === entry.stem
            )
          ) {
            violations.push(
              `${location(source, node)}: reconstructs a supplied ${entry.stem || 'same-lifetime'} deadline`
            );
            break;
          }
        }
      }
    }
  }
  const csharp = sources.filter(({ file }) => file.endsWith('.cs'));
  const contracts = new Map();
  const declarations = [];
  for (const { file, text } of csharp) {
    const words = tokens(text, file);
    for (let index = 0; index < words.length; index++) {
      if (!['class', 'record', 'struct'].includes(words[index].text)) continue;
      const name = words[index + 1]?.text;
      let start = index + 2;
      if (words[start]?.text === 'class' || words[start]?.text === 'struct') start++;
      while (start < words.length && !['{', '(', ';'].includes(words[start].text)) start++;
      if (!['{', '('].includes(words[start]?.text)) continue;
      const end = words[start].pair;
      const names = [];
      const fields = new Map();
      let alias;
      for (let member = start + 1; member < end; member++) {
        if (words[member].text === 'JsonPropertyName') alias = words[member + 2]?.value;
        const after = words[member + 1]?.text;
        if (
          words[member].value === undefined &&
          /^[A-Za-z_]\w*$/.test(words[member].text) &&
          ((after === '{' && ['get', 'init', 'set'].includes(words[member + 2]?.text)) ||
            after === '=>' ||
            ([';', '='].includes(after) && lifetime(words[member].text)) ||
            (words[start].text === '(' && [',', ')', '='].includes(after)))
        ) {
          names.push(alias ?? words[member].text);
          fields.set(words[member].text, alias ?? words[member].text);
          alias = undefined;
        }
        if (words[member].text === '{') member = words[member].pair;
      }
      counts.types++;
      counts.absolute += names.filter((field) => lifetime(field)?.kind === 'absolute').length;
      contracts.set(name, names);
      const repeated = duplicates(names);
      if (repeated.length)
        declarations.push({
          file,
          line: words[index].line,
          name,
          repeated,
          fields,
          internal: words
            .slice(Math.max(0, index - 3), index)
            .some((word) => ['internal', 'private'].includes(word.text))
        });
    }
    for (let index = 0; index < words.length; index++) {
      if (words[index].text !== 'new' || words[index + 1]?.text !== '{') continue;
      const start = index + 1;
      const names = [];
      for (let field = start + 1; field < words[start].pair; field++) {
        if (words[field + 1]?.text === '=') names.push(words[field].text);
        if (['{', '('].includes(words[field].text)) field = words[field].pair;
      }
      const repeated = duplicates(names);
      if (repeated.length)
        violations.push(
          `${normalizePath(file)}:${words[index].line}: dual anonymous deadline fields ${repeated.join(', ')}`
        );
    }
  }
  for (const declaration of declarations) {
    let absoluteRead = false;
    let escaped = false;
    let relativeRead = false;
    let published = false;
    for (const { file, text } of csharp) {
      const words = tokens(text, file);
      const receivers = new Set();
      for (let index = 0; index < words.length - 1; index++)
        if (
          words[index].text === declaration.name &&
          !['class', 'record', 'struct'].includes(words[index - 1]?.text)
        )
          receivers.add(words[index + 1].text);
      for (let index = 0; index < words.length; index++) {
        if (!receivers.has(words[index].text)) continue;
        if (words[index - 1]?.text === declaration.name) continue;
        if (words[index + 1]?.text !== '.') {
          escaped = true;
          continue;
        }
        const entry = lifetime(declaration.fields.get(words[index + 2]?.text) ?? '');
        absoluteRead ||= entry?.kind === 'absolute';
        relativeRead ||= entry?.kind === 'relative';
        if (
          entry?.kind === 'absolute' &&
          words.slice(Math.max(0, index - 12), index).some((word) => word.text === 'new')
        )
          published = true;
      }
    }
    if (!(declaration.internal && absoluteRead && published && !relativeRead && !escaped))
      violations.push(
        `${normalizePath(declaration.file)}:${declaration.line}: dual deadline fields ${declaration.repeated.join(', ')}`
      );
  }
  for (const { file, text } of csharp) {
    const words = tokens(text, file);
    const receivers = new Map();
    const durations = new Map();
    const clocks = new Set();
    for (let index = 0; index < words.length - 1; index++) {
      if (contracts.has(words[index].text))
        receivers.set(words[index + 1].text, contracts.get(words[index].text));
      if (
        words[index].text === 'var' &&
        words[index + 2]?.text === '=' &&
        receivers.has(words[index + 3]?.text)
      )
        receivers.set(words[index + 1].text, receivers.get(words[index + 3].text));
      if (words[index + 1]?.text === '=') {
        if (words[index + 2]?.text === 'DateTime' && words[index + 4]?.text === 'UtcNow')
          clocks.add(words[index].text);
        if (words[index + 3]?.text === '.' && receivers.has(words[index + 2]?.text))
          durations.set(words[index].text, {
            entry: lifetime(words[index + 4]?.text ?? ''),
            names: receivers.get(words[index + 2].text)
          });
        else if (durations.has(words[index + 2]?.text))
          durations.set(words[index].text, durations.get(words[index + 2].text));
      }
    }
    const code = words.map((word) => (word.value === undefined ? word.text : '""')).join(' ');
    for (const match of code.matchAll(
      /(?:DateTime\s*\.\s*UtcNow|\w+\s*\.\s*CreatedAt)\s*\.\s*Add(?:Seconds|Milliseconds)\s*\(\s*(\w+)\s*\.\s*(\w+)/g
    )) {
      const entry = lifetime(match[2]);
      if (
        entry?.kind === 'relative' &&
        receivers
          .get(match[1])
          ?.some(
            (field) => lifetime(field)?.kind === 'absolute' && lifetime(field).stem === entry.stem
          )
      )
        violations.push(
          `${normalizePath(file)}: reconstructs a supplied ${entry.stem || 'same-lifetime'} deadline`
        );
    }
    for (let index = 0; index < words.length; index++) {
      if (
        !['AddSeconds', 'AddMilliseconds'].includes(words[index].text) ||
        words[index - 1]?.text !== '.' ||
        !(
          ['UtcNow', 'CreatedAt'].includes(words[index - 2]?.text) ||
          clocks.has(words[index - 2]?.text)
        )
      )
        continue;
      const argument = words[index + 2]?.text;
      const value =
        words[index + 3]?.text === '.'
          ? {
              entry: lifetime(words[index + 4]?.text ?? ''),
              names: receivers.get(argument)
            }
          : durations.get(argument);
      if (
        value?.entry?.kind === 'relative' &&
        value.names?.some(
          (field) =>
            lifetime(field)?.kind === 'absolute' && lifetime(field).stem === value.entry.stem
        )
      )
        violations.push(
          `${normalizePath(file)}:${words[index].line}: reconstructs a supplied ${value.entry.stem || 'same-lifetime'} deadline`
        );
    }
  }
  return { violations: [...new Set(violations)], counts };
}

const invoked = process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url);
if (invoked && process.argv.includes('--reject-control')) {
  assertClean(
    checkDeadlines([
      {
        file: path.join(repository, 'Web/src/deadline-control.ts'),
        text: 'export interface Login { expiresAt: string; expiresIn: number }'
      }
    ]).violations
  );
} else {
  test('shipped deadline contracts retain one representation for each lifetime', () => {
    const sources = sourceFiles();
    const result = checkDeadlines(sources);
    assert.ok(result.counts.files > 0 && result.counts.types > 0 && result.counts.absolute > 0);
    for (const sentinel of [
      'Core/Services/EpicMapping/EpicApiDirectModels.cs',
      'Web/src/components/features/management/schedules/scheduled-prefill/persistentLoginStore.ts'
    ])
      assert.ok(
        sources.some(({ file }) => normalizePath(file).endsWith(sentinel)),
        `Missing ${sentinel}`
      );
    console.log('Deadline source counts:', result.counts);
    assertClean(result.violations);
  });

  test('cache checks use only their owner abort signal', () => {
    const apiSource = readFileSync(
      path.join(repository, 'Web/src/services/api.service.ts'),
      'utf8'
    );
    const panelSource = readFileSync(
      path.join(repository, 'Web/src/components/features/prefill/PrefillPanel.tsx'),
      'utf8'
    );
    const cacheStatusMethod = apiSource.slice(
      apiSource.indexOf('static async getPrefillCacheStatus'),
      apiSource.indexOf('static async clearAllPrefillCache')
    );
    const persistentGamesMethod = apiSource.slice(
      apiSource.indexOf('static async getPersistentPrefillGames'),
      apiSource.indexOf('static async startPersistentLogin')
    );
    assert.doesNotMatch(cacheStatusMethod, /AbortSignal\.timeout/);
    assert.doesNotMatch(persistentGamesMethod, /AbortSignal\.timeout/);
    assert.doesNotMatch(panelSource, /AbortSignal\.timeout\(45000\)/);
    assert.match(cacheStatusMethod, /signal/);
    assert.match(persistentGamesMethod, /signal/);
    assert.match(panelSource, /const signal = controller\.signal/);
  });

  const failures = [
    ['interface', 'export interface Login { expiresAt: string; expiresIn: number }'],
    ['type', 'export type Login = { authExpiresAtUtc: string; authTimeRemainingSeconds: number }'],
    ['class', 'export class Login { expiresAt!: string; ttlSeconds!: number }'],
    [
      'typed return',
      'function read(): { expires_at: string; time_remaining_seconds: number } { throw 1 }'
    ],
    ['object', 'send({ expiresAt: end, expiresIn: seconds });'],
    ['props', 'const view = <Login expiresAt={end} expiresIn={seconds} />;'],
    [
      'C# property',
      'public class Login { public DateTime ExpiresAt { get; set; } public int ExpiresIn { get; set; } }'
    ],
    ['C# record', 'public record Login(DateTime AuthExpiresAtUtc, int AuthTimeRemainingSeconds);'],
    [
      'wire alias',
      'public class Login { [JsonPropertyName("expires_at")] public string End { get; set; } [JsonPropertyName("expires_in")] public int Seconds { get; set; } }'
    ],
    ['anonymous', 'return new { ExpiresAt = end, ExpiresIn = seconds };'],
    ...[
      'Date.now() + reply.expiresIn * 1000',
      'reply.expiresAt ? Date.parse(reply.expiresAt) : Date.now() + reply.expiresIn * 1000',
      'Date.now() + duration * 1000',
      'Date.now() + seconds * 1000',
      'reply.createdAt + alias.expiresIn * 1000'
    ].map((expression, index) => [
      `reconstruction ${index}`,
      `declare const reply: { expiresAt: string; expiresIn: number; createdAt: number }; const alias = reply; const duration = reply.expiresIn; const { expiresIn: seconds } = reply; const end = ${expression};`
    ])
  ];
  for (const [name, text] of failures)
    for (const newline of ['\n', '\r\n']) {
      test(`rejects ${name} with ${newline.length === 1 ? 'LF' : 'CRLF'}`, () => {
        const csharp = /C#|wire alias|anonymous/.test(name);
        const file = path.join(
          repository,
          `Web/src/deadline-control.${csharp ? 'cs' : name === 'props' ? 'tsx' : 'ts'}`
        );
        const result = checkDeadlines([{ file, text: text.replaceAll('; ', `;${newline}`) }]);
        assert.ok(result.violations.length > 0, `Accepted ${name}`);
        if (name.startsWith('reconstruction'))
          assert.ok(result.violations.some((entry) => entry.includes('reconstructs')));
      });
    }
  for (const [name, text] of [
    ['different lifetimes', 'interface Login { expiresAt: string; refreshExpiresIn: number }'],
    [
      'duration ingress',
      'interface Device { expiresIn: number }; function admit(reply: Device) { return { expiresAt: Date.now() + reply.expiresIn * 1000 }; }'
    ],
    [
      'subtraction',
      'interface Login { expiresAt: number }; function remaining(reply: Login) { return Math.max(0, reply.expiresAt - Date.now()); }'
    ],
    [
      'new draft',
      'function start(seconds: number) { return { expiresAt: Date.now() + seconds * 1000 }; }'
    ],
    [
      'unrelated values',
      'interface Timing { expiresAt: string; interval: number; retrySeconds: number; retention: number; elapsed: number; remaining: number }'
    ],
    [
      'comments',
      '// interface Login { expiresAt: string; expiresIn: number }\nconst text = "expiresAt expiresIn";'
    ]
  ])
    test(`permits ${name}`, () =>
      assertClean(
        checkDeadlines([{ file: path.join(repository, 'Web/src/deadline-control.ts'), text }])
          .violations
      ));

  test('source paths normalize both separator conventions', () => {
    assert.equal(normalizePath('C:\\repo\\Web\\src\\login.ts'), 'C:/repo/Web/src/login.ts');
    assert.equal(normalizePath('/repo/Web/src/login.ts'), '/repo/Web/src/login.ts');
  });
  for (const file of ['C:\\contracts\\Login.cs', '/contracts/Login.cs'])
    test(`reports an actual binding position for ${file}`, () => {
      const result = checkDeadlines([
        { file, text: '\r\npublic record Login(DateTime ExpiresAt, int ExpiresIn);\r\n' }
      ]);
      assert.ok(result.counts.types > 0);
      assert.ok(result.violations.some((entry) => entry.startsWith(normalizePath(file) + ':2:')));
    });
  for (const [name, extension, text, rejected] of [
    [
      'private raw normalization',
      'ts',
      'interface Raw { expiresAt: string; expiresIn: number }; function admit(reply: Raw) { return { expiresAt: Date.parse(reply.expiresAt) }; }',
      false
    ],
    [
      'C# private raw normalization',
      'cs',
      'internal class Raw { public string ExpiresAt { get; set; } public int ExpiresIn { get; set; } } class Login { public DateTime ExpiresAt { get; set; } } class Api { Login Admit(Raw reply) => new Login { ExpiresAt = DateTime.Parse(reply.ExpiresAt) }; }',
      false
    ],
    [
      'raw expiry ignored',
      'ts',
      'interface Raw { expiresAt: string; expiresIn: number }; function admit(reply: Raw) { return { expiresAt: Date.now() + reply.expiresIn * 1000 }; }',
      true
    ],
    [
      'raw forwarded',
      'ts',
      'interface Raw { expiresAt: string; expiresIn: number }; function admit(reply: Raw) { const end = Date.parse(reply.expiresAt); return reply; }',
      true
    ],
    [
      'inherited shape',
      'ts',
      'interface Absolute { expiresAt: string }; export interface Login extends Absolute { expiresIn: number }',
      true
    ],
    [
      'intersection',
      'ts',
      'type Absolute = { expiresAt: string }; export type Login = Absolute & { expiresIn: number };',
      true
    ],
    [
      'object spread',
      'ts',
      'declare const original: { expiresAt: string }; send({ ...original, expiresIn: 60 });',
      true
    ],
    [
      'C# expression properties',
      'cs',
      'public class Login { public DateTime ExpiresAt => end; public int ExpiresIn => seconds; }',
      true
    ],
    [
      'independent C# lifetimes',
      'cs',
      'public record Login(DateTime ExpiresAt, int RefreshExpiresIn);',
      false
    ],
    [
      'owner replacement',
      'ts',
      'function accept(original: number, expiresAt: number) { return Math.min(original, expiresAt); }',
      false
    ]
  ])
    test(`${rejected ? 'rejects' : 'permits'} ${name}`, () => {
      const result = checkDeadlines([
        { file: path.join(repository, `Web/src/deadline-control.${extension}`), text }
      ]);
      if (rejected) assert.ok(result.violations.length > 0, `Accepted ${name}`);
      else assertClean(result.violations);
    });
  for (const [name, expression] of [
    ['clock alias', 'const now = Date.now(); const end = now + reply.expiresIn * 1000;'],
    [
      'duration assignment',
      'let seconds: number; seconds = reply.expiresIn; const end = Date.now() + seconds * 1000;'
    ],
    [
      'default argument',
      'function consume(end = Date.now() + reply.expiresIn * 1000) { return end; }'
    ],
    ['coalesce', 'const end = reply.expiresAt ?? Date.now() + reply.expiresIn * 1000;'],
    ['or', 'const end = reply.expiresAt || Date.now() + reply.expiresIn * 1000;']
  ])
    test(`rejects reconstruction through ${name}`, () => {
      const result = checkDeadlines([
        {
          file: path.join(repository, 'Web/src/deadline-control.ts'),
          text: 'declare const reply: { expiresAt: number; expiresIn: number };' + expression
        }
      ]);
      assert.ok(
        result.violations.some((entry) => entry.includes('reconstructs')),
        `Missed ${name}`
      );
    });
  for (const expression of [
    'DateTime.UtcNow.AddSeconds(reply.ExpiresIn)',
    'now.AddSeconds(seconds)'
  ])
    test(`rejects C# reconstruction ${expression}`, () => {
      const result = checkDeadlines([
        {
          file: 'Login.cs',
          text:
            'class Login { public DateTime ExpiresAt { get; set; } public int ExpiresIn { get; set; } } class Api { void Read(Login reply) { var now = DateTime.UtcNow; var seconds = reply.ExpiresIn; var end = ' +
            expression +
            '; } }'
        }
      ]);
      assert.ok(result.violations.some((entry) => entry.includes('reconstructs')));
    });
  test('missing roots and malformed source cannot report an empty pass', () => {
    assert.throws(() => sourceFiles(path.join(repository, 'missing-contract-root')));
    assert.throws(() =>
      checkDeadlines([
        { file: path.join(repository, 'Web/src/control.ts'), text: 'interface Login {' }
      ])
    );
    assert.throws(() => checkDeadlines([{ file: 'Login.cs', text: 'public class Login {' }]));
  });
  test('a rejected deadline makes the real process fail', () => {
    const result = spawnSync(
      process.execPath,
      [fileURLToPath(import.meta.url), '--reject-control'],
      { encoding: 'utf8' }
    );
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /dual deadline fields/);
  });
}
