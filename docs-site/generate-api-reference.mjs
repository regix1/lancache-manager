#!/usr/bin/env node
// Generates the downloadable API reference from a running instance's OpenAPI document.
//
// Usage:
//   node docs-site/generate-api-reference.mjs --key <api-key> [--url http://localhost:5000]
//   node docs-site/generate-api-reference.mjs --from openapi.json
//
// Writes docs-site/assets/api-reference.txt, which build.py copies into the published
// site so it downloads as raw text at /api-reference.txt. The extension is .txt rather
// than .md on purpose: MkDocs renders every .md file under its docs directory into an
// HTML page, which would leave nothing to download.
//
// Nothing regenerates this automatically. Re-run it whenever the API surface changes.

import { writeFileSync, readFileSync, mkdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const scriptDir = dirname(fileURLToPath(import.meta.url));

const METHOD_ORDER = ['get', 'post', 'put', 'patch', 'delete'];

function parseArgs(argv) {
  const args = { url: 'http://localhost:5000', key: null, from: null };
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === '--url') args.url = argv[++i];
    else if (argv[i] === '--key') args.key = argv[++i];
    else if (argv[i] === '--from') args.from = argv[++i];
  }
  return args;
}

function typeLabel(schema) {
  if (!schema) return 'unknown';
  if (schema.$ref) return schema.$ref.split('/').pop();
  if (Array.isArray(schema.type)) {
    const named = schema.type.filter((t) => t !== 'null');
    return (named[0] || 'object') + (schema.type.includes('null') ? '?' : '');
  }
  if (schema.type === 'array') return `${typeLabel(schema.items || {})}[]`;
  if (schema.enum) return `"${schema.enum.join('" | "')}"`;
  return schema.type || 'object';
}

function describeSchema(ref, components) {
  if (!ref) return null;
  const name = ref.$ref ? ref.$ref.split('/').pop() : null;
  const schema = name ? components.schemas[name] : ref;
  if (!schema) return null;
  if (!schema.properties) return { name, fields: [] };
  const required = new Set(schema.required || []);
  const fields = Object.entries(schema.properties).map(
    ([prop, propSchema]) => `${prop}${required.has(prop) ? '' : '?'}: ${typeLabel(propSchema)}`
  );
  return { name, fields };
}

function firstContentSchema(content) {
  if (!content) return null;
  const preferred = content['application/json'] || content[Object.keys(content)[0]];
  return preferred ? preferred.schema : null;
}

function operationAuth(operation, documentSecurity) {
  const requirements = Array.isArray(operation.security) ? operation.security : documentSecurity;
  return requirements && requirements.length > 0 ? 'requires a signed-in session' : 'public';
}

function shapeLine(label, described) {
  const name = described.name ? `\`${described.name}\`` : 'inline object';
  const fields = described.fields.length
    ? described.fields.map((field) => `\`${field}\``).join(', ')
    : '(no fields)';
  return `${label} (${name}): ${fields}`;
}

function groupByTag(paths) {
  const byTag = new Map();
  for (const [path, methods] of Object.entries(paths)) {
    for (const [method, operation] of Object.entries(methods)) {
      if (!METHOD_ORDER.includes(method)) continue;
      const tag = (operation.tags && operation.tags[0]) || 'Untagged';
      if (!byTag.has(tag)) byTag.set(tag, []);
      byTag.get(tag).push({ path, method, operation });
    }
  }
  return byTag;
}

function buildMarkdown(doc) {
  const components = doc.components || { schemas: {} };
  const byTag = groupByTag(doc.paths);
  const tagDescriptions = new Map((doc.tags || []).map((tag) => [tag.name, tag.description]));
  const sortedTags = [...byTag.keys()].sort((a, b) => a.localeCompare(b));
  const total = [...byTag.values()].reduce((sum, ops) => sum + ops.length, 0);

  const out = [];
  out.push('# LANCache Manager API Reference');
  out.push('');
  out.push(
    `Every HTTP endpoint LANCache Manager serves: ${total} operations across ${sortedTags.length} groups, ` +
      `generated from the OpenAPI document of \`${doc.info.title}\` version \`${doc.info.version}\`.`
  );
  out.push('');
  out.push(
    'This is a flattened summary meant to be read by a person or pasted into a chat with an ' +
      'AI assistant. It is not the OpenAPI document itself. A running instance serves the ' +
      'interactive reference at `/scalar` and the full machine-readable document at ' +
      '`/openapi/v1.json`, both of which take an admin session or the API key.'
  );
  out.push('');
  out.push('## Calling the API');
  out.push('');
  out.push(
    '- Base URL is wherever the instance is served. The bundled Docker image publishes port ' +
      '8080, so `http://<host>:8080`. A local development run uses `http://localhost:5000`.'
  );
  out.push(
    '- Endpoints marked **requires a signed-in session** need the `LancacheManager.Session` ' +
      'cookie. Sign in once at `POST /api/auth/login`, which takes an API key, a username and a ' +
      'password together, and send the cookie it returns on every later call. A call that changes ' +
      'something (POST, PUT, PATCH, DELETE) also needs the value of the `LancacheManager.Antiforgery` ' +
      'cookie sent back as an `X-Antiforgery-Token` header.'
  );
  out.push(
    '- Endpoints marked **public** answer without a session, because they have to work before a ' +
      'caller has one (sign-in, guest configuration, setup, health).'
  );
  out.push(
    '- The `X-Api-Key` header on its own opens `/scalar` and `/openapi/v1.json` and nothing else. ' +
      'Five setup endpoints read the key themselves: `POST /api/setup/credentials` and ' +
      '`POST /api/setup/external` take it in the header; `POST /api/account-setup/first-admin`, ' +
      '`POST /api/account-setup/open-main-admin-recovery`, and ' +
      '`POST /api/account-setup/recover-main-admin` take it in the request body.'
  );
  out.push(
    '- Get the key with `docker exec lancache-manager cat /data/security/api_key.txt`, or from ' +
      'Management then Integrations inside the app. Later container logs print only a hint; the ' +
      'full key is written to the logs only when it is first created or rotated.'
  );
  out.push(
    '- Request and response shapes list top-level fields only, as `name: type`, with `?` marking ' +
      'a field that is optional or nullable. Named types are referenced but not expanded; read ' +
      '`/openapi/v1.json` when the nested shape matters.'
  );
  out.push('');
  out.push('## Groups');
  out.push('');
  for (const tag of sortedTags) {
    const description = tagDescriptions.get(tag);
    const count = byTag.get(tag).length;
    out.push(`- **${tag}** (${count} endpoints)${description ? ` - ${description}` : ''}`);
  }
  out.push('');

  for (const tag of sortedTags) {
    out.push(`## ${tag}`);
    out.push('');
    const description = tagDescriptions.get(tag);
    if (description) {
      out.push(description);
      out.push('');
    }

    const operations = byTag.get(tag).sort((a, b) => {
      const byMethod = METHOD_ORDER.indexOf(a.method) - METHOD_ORDER.indexOf(b.method);
      return byMethod !== 0 ? byMethod : a.path.localeCompare(b.path);
    });

    for (const { path, method, operation } of operations) {
      out.push(`### ${method.toUpperCase()} ${path}`);
      out.push('');
      out.push(`Access: ${operationAuth(operation, doc.security)}`);
      out.push('');
      if (operation.summary) {
        out.push(operation.summary);
        out.push('');
      }

      const parameters = operation.parameters || [];
      if (parameters.length) {
        const list = parameters
          .map((p) => `\`${p.name}\`${p.required ? '' : '?'} (${p.in}): ${typeLabel(p.schema)}`)
          .join(', ');
        out.push(`Parameters: ${list}`);
      }

      const bodySchema = firstContentSchema(operation.requestBody && operation.requestBody.content);
      const describedBody = describeSchema(bodySchema, components);
      if (describedBody) out.push(shapeLine('Body', describedBody));

      const responses = operation.responses || {};
      const successStatus = responses['200'] ? '200' : responses['202'] ? '202' : null;
      if (successStatus) {
        const responseSchema = firstContentSchema(responses[successStatus].content);
        const describedResponse = describeSchema(responseSchema, components);
        out.push(
          describedResponse
            ? shapeLine(`Response ${successStatus}`, describedResponse)
            : `Response ${successStatus}: no body`
        );
      }

      out.push('');
    }
  }

  return out.join('\n');
}

async function loadDocument(args) {
  if (args.from) return JSON.parse(readFileSync(args.from, 'utf8'));

  if (!args.key) {
    console.error('error: pass --key <api-key>, or --from <openapi.json> for an already saved document');
    process.exit(1);
  }
  const response = await fetch(new URL('/openapi/v1.json', args.url), {
    headers: { 'X-Api-Key': args.key },
  });
  if (!response.ok) {
    console.error(`error: GET /openapi/v1.json returned ${response.status}`);
    process.exit(1);
  }
  return response.json();
}

const args = parseArgs(process.argv.slice(2));
const doc = await loadDocument(args);
const markdown = buildMarkdown(doc);
const outDir = join(scriptDir, 'assets');
mkdirSync(outDir, { recursive: true });
const outPath = join(outDir, 'api-reference.txt');
writeFileSync(outPath, markdown, 'utf8');
console.log(`wrote ${outPath} (${markdown.length} bytes, ${Object.keys(doc.paths).length} paths)`);
