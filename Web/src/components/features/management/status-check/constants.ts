/**
 * Domain probed from the device viewing the page. Steam's trigger domain is the
 * canonical always-present cache domain, and the /lancache-heartbeat endpoint
 * answers 204 with an X-LanCache-Processed-By header on a real cache node.
 */
export const CLIENT_PROBE_HOST = 'lancache.steamcontent.com';
export const CLIENT_PROBE_URL = `http://${CLIENT_PROBE_HOST}/lancache-heartbeat`;
export const CLIENT_PROBE_TIMEOUT_MS = 4000;
