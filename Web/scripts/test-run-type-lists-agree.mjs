import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { compileToUrl } from './transpile-module.mjs';

/**
 * The server and the browser each keep a list of operation types, and a disagreement fails
 * silently: a type the browser does not map draws no card for a run the server tracks, and a type
 * the browser folds into one card per schedule while the server does not keeps one kept ending
 * hidden that the server still holds. So the lists are read from both sides here and compared.
 */

const api = (path) =>
  readFileSync(new URL(`../../Api/LancacheManager/${path}`, import.meta.url), 'utf8');

/** OperationType member -> the camelCase wire string `ToWireString` sends. */
const wireStrings = new Map(
  [
    ...api('Models/Operations/OperationType.cs').matchAll(/OperationType\.(\w+)\s*=>\s*"(\w+)"/g)
  ].map(([, member, wire]) => [member, wire])
);

const { OPERATION_WIRE_TYPE_TO_NOTIFICATION_TYPE, SCHEDULED_NOTIFICATION_TYPE_TO_SERVICE_KEY } =
  await import(await compileToUrl('../src/contexts/notifications/constants.ts'));

const wireOf = (member) => {
  const wire = wireStrings.get(member);
  assert.ok(wire, `OperationType.${member} has no wire string in ToWireString`);
  return wire;
};

test('every server operation type either maps to a card or is listed as having none', () => {
  const initializer = api('Core/Services/Operations/UnifiedOperationTracker.cs').match(
    /_operationTypesWithoutCard\s*=\s*new(?:\(\))?\s*\{([^}]*)\}/
  );
  assert.ok(initializer, 'the tracker lists the operation types that draw no card');
  const withoutCard = [...initializer[1].matchAll(/OperationType\.(\w+)/g)].map(([, member]) =>
    wireOf(member)
  );
  assert.ok(wireStrings.size > 0, 'ToWireString was read');

  for (const wire of wireStrings.values()) {
    const places = [
      withoutCard.includes(wire) ? 'without-card set' : undefined,
      wire in OPERATION_WIRE_TYPE_TO_NOTIFICATION_TYPE ? 'client map' : undefined
    ].filter(Boolean);
    assert.equal(places.length, 1, `${wire} is in [${places.join(', ')}], not in exactly one`);
  }
  const serverTypes = new Set(wireStrings.values());
  for (const wire of Object.keys(OPERATION_WIRE_TYPE_TO_NOTIFICATION_TYPE))
    assert.ok(serverTypes.has(wire), `the client maps ${wire}, which the server never sends`);
});

test("the client's scheduled types are exactly the server's schedule types", () => {
  const schedules = api('Core/Services/System/ServiceScheduleRegistry.cs').match(
    /_runStatusOperationTypes\s*=\s*new\([^)]*\)\s*\{([^}]*)\}/
  );
  assert.ok(schedules, 'the schedule registry maps its service keys to operation types');
  const server = new Map(
    [...schedules[1].matchAll(/\["(\w+)"\]\s*=\s*OperationType\.(\w+)/g)].map(
      ([, serviceKey, member]) => [wireOf(member), serviceKey]
    )
  );

  // The two catalog announcements ride their mapping service's display setting but are not runs.
  const announcements = ['epic_catalog_update', 'xbox_catalog_update'];
  const wireByType = new Map(
    Object.entries(OPERATION_WIRE_TYPE_TO_NOTIFICATION_TYPE).map(([wire, type]) => [type, wire])
  );
  const client = new Map(
    Object.entries(SCHEDULED_NOTIFICATION_TYPE_TO_SERVICE_KEY)
      .filter(([type]) => !announcements.includes(type))
      .map(([type, serviceKey]) => {
        assert.ok(wireByType.has(type), `${type} has no wire type`);
        return [wireByType.get(type), serviceKey];
      })
  );

  assert.deepEqual([...client.keys()].sort(), [...server.keys()].sort());
  for (const [wire, serviceKey] of server)
    assert.equal(client.get(wire), serviceKey, `${wire} belongs to the ${serviceKey} schedule`);
});
