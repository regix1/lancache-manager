import type { DownloadSource, DownloadSpeedSnapshot } from '../../types';

type GameSpeed = DownloadSpeedSnapshot['gameSpeeds'][number];
type ClientSpeed = DownloadSpeedSnapshot['clientSpeeds'][number];

const isObject = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value);

const isNonNegativeNumber = (value: unknown): value is number =>
  typeof value === 'number' && Number.isFinite(value) && value >= 0;

const isNonNegativeInteger = (value: unknown): value is number =>
  isNonNegativeNumber(value) && Number.isSafeInteger(value);

const isUtcTime = (value: unknown): value is string =>
  typeof value === 'string' && value.endsWith('Z') && Number.isFinite(Date.parse(value));

const isOptionalString = (value: unknown): value is string | null | undefined =>
  value === undefined || value === null || typeof value === 'string';

const isOptionalPositiveInteger = (value: unknown): value is number | null | undefined =>
  value === undefined || value === null || (isNonNegativeInteger(value) && value > 0);

const hasOrderedActivityTimes = (
  firstSeenUtc: unknown,
  lastSeenUtc: unknown,
  activeUntilUtc: unknown
): boolean =>
  isUtcTime(firstSeenUtc) &&
  isUtcTime(lastSeenUtc) &&
  isUtcTime(activeUntilUtc) &&
  Date.parse(firstSeenUtc) <= Date.parse(lastSeenUtc) &&
  Date.parse(lastSeenUtc) < Date.parse(activeUntilUtc);

const isDownloadSource = (value: unknown): value is DownloadSource => {
  if (!isObject(value)) return false;
  if (
    !Array.isArray(value.datasources) ||
    value.datasources.length === 0 ||
    !value.datasources.every(
      (datasource) => typeof datasource === 'string' && datasource.trim().length > 0
    ) ||
    !Array.isArray(value.depotIds) ||
    !value.depotIds.every(isNonNegativeInteger) ||
    !hasOrderedActivityTimes(value.firstSeenUtc, value.lastSeenUtc, value.activeUntilUtc) ||
    !isUtcTime(value.measuredUntilUtc)
  ) {
    return false;
  }

  const activeUntilUtc = value.activeUntilUtc;
  const measuredUntilUtc = value.measuredUntilUtc;
  if (!isUtcTime(activeUntilUtc) || !isUtcTime(measuredUntilUtc)) return false;
  const activeUntilMs = Date.parse(activeUntilUtc);
  if (Date.parse(measuredUntilUtc) > activeUntilMs) return false;

  return (
    isNonNegativeNumber(value.bytesPerSecond) &&
    isNonNegativeInteger(value.totalBytes) &&
    isNonNegativeInteger(value.requestCount) &&
    isNonNegativeInteger(value.cacheHitBytes) &&
    isNonNegativeInteger(value.cacheMissBytes)
  );
};

const isGameSpeed = (value: unknown): value is GameSpeed => {
  if (!isObject(value)) return false;
  if (
    typeof value.key !== 'string' ||
    value.key.trim().length === 0 ||
    !isNonNegativeInteger(value.depotId) ||
    !isOptionalString(value.gameName) ||
    !isOptionalPositiveInteger(value.gameAppId) ||
    typeof value.service !== 'string' ||
    value.service.trim().length === 0 ||
    typeof value.clientIp !== 'string' ||
    typeof value.isEvicted !== 'boolean' ||
    !hasOrderedActivityTimes(value.firstSeenUtc, value.lastSeenUtc, value.activeUntilUtc) ||
    !Array.isArray(value.sources) ||
    value.sources.length === 0 ||
    !value.sources.every(isDownloadSource)
  ) {
    return false;
  }

  return (
    isNonNegativeNumber(value.bytesPerSecond) &&
    isNonNegativeInteger(value.totalBytes) &&
    isNonNegativeInteger(value.requestCount) &&
    isNonNegativeInteger(value.cacheHitBytes) &&
    isNonNegativeInteger(value.cacheMissBytes) &&
    isNonNegativeNumber(value.cacheHitPercent) &&
    value.cacheHitPercent <= 100
  );
};

const isClientSpeed = (value: unknown): value is ClientSpeed => {
  if (!isObject(value)) return false;
  return (
    typeof value.clientIp === 'string' &&
    value.clientIp.trim().length > 0 &&
    isNonNegativeNumber(value.bytesPerSecond) &&
    isNonNegativeInteger(value.totalBytes) &&
    isNonNegativeInteger(value.activeGames) &&
    isNonNegativeInteger(value.cacheHitBytes) &&
    isNonNegativeInteger(value.cacheMissBytes) &&
    isUtcTime(value.activeUntilUtc)
  );
};

const hasUniqueValues = (values: readonly string[]): boolean =>
  new Set(values).size === values.length;

export const isDownloadSpeedSnapshot = (value: unknown): value is DownloadSpeedSnapshot => {
  if (!isObject(value)) return false;
  if (
    value.version !== 2 ||
    typeof value.streamId !== 'string' ||
    value.streamId.trim().length === 0 ||
    !isNonNegativeInteger(value.revision) ||
    !isUtcTime(value.timestampUtc) ||
    typeof value.isAvailable !== 'boolean' ||
    !isNonNegativeNumber(value.totalBytesPerSecond) ||
    !Array.isArray(value.gameSpeeds) ||
    !value.gameSpeeds.every(isGameSpeed) ||
    !Array.isArray(value.clientSpeeds) ||
    !value.clientSpeeds.every(isClientSpeed) ||
    !isNonNegativeInteger(value.windowSeconds) ||
    value.windowSeconds !== 2 ||
    !isNonNegativeInteger(value.entriesInWindow) ||
    typeof value.hasActiveDownloads !== 'boolean' ||
    value.hasActiveDownloads !== value.gameSpeeds.length > 0
  ) {
    return false;
  }

  return (
    hasUniqueValues(value.gameSpeeds.map((game) => game.key)) &&
    hasUniqueValues(value.clientSpeeds.map((client) => client.clientIp))
  );
};

export const canAcceptRestSnapshot = (
  current: DownloadSpeedSnapshot | null,
  next: DownloadSpeedSnapshot
): boolean =>
  current === null || current.streamId !== next.streamId || next.revision > current.revision;

export const canAcceptSignalRSnapshot = (
  current: DownloadSpeedSnapshot | null,
  next: DownloadSpeedSnapshot
): boolean =>
  current !== null && current.streamId === next.streamId && next.revision > current.revision;

const sameStrings = (left: readonly string[], right: readonly string[]): boolean =>
  left.length === right.length && left.every((value, index) => value === right[index]);

const sourceIdentity = (source: DownloadSource): string =>
  `${source.datasources.join('\u0000')}\u0001${source.depotIds.join('\u0000')}`;

const gameShape = (game: GameSpeed): string =>
  `${game.key}\u0001${game.sources.map(sourceIdentity).join('\u0002')}`;

const positiveMeasurementShape = (snapshot: DownloadSpeedSnapshot): string[] => [
  snapshot.totalBytesPerSecond > 0 ? '1' : '0',
  snapshot.entriesInWindow > 0 ? '1' : '0',
  ...snapshot.gameSpeeds.map(
    (game) =>
      `${game.key}:${[
        game.bytesPerSecond,
        game.totalBytes,
        game.requestCount,
        game.cacheHitBytes,
        game.cacheMissBytes
      ]
        .map((value) => (value > 0 ? '1' : '0'))
        .join('')}:${game.sources
        .map((source) =>
          [
            source.bytesPerSecond,
            source.totalBytes,
            source.requestCount,
            source.cacheHitBytes,
            source.cacheMissBytes
          ]
            .map((value) => (value > 0 ? '1' : '0'))
            .join('')
        )
        .join(',')}`
  ),
  ...snapshot.clientSpeeds.map(
    (client) =>
      `${client.clientIp}:${[
        client.bytesPerSecond,
        client.totalBytes,
        client.cacheHitBytes,
        client.cacheMissBytes
      ]
        .map((value) => (value > 0 ? '1' : '0'))
        .join('')}`
  )
];

/**
 * Returns true when rendering must bypass the numeric refresh throttle. The server owns membership;
 * this comparison only decides when an already accepted revision reaches React state.
 */
export const hasImmediateSnapshotChange = (
  current: DownloadSpeedSnapshot | null,
  next: DownloadSpeedSnapshot
): boolean => {
  if (current === null) return true;
  if (
    current.streamId !== next.streamId ||
    current.isAvailable !== next.isAvailable ||
    current.hasActiveDownloads !== next.hasActiveDownloads
  ) {
    return true;
  }

  return (
    !sameStrings(current.gameSpeeds.map(gameShape), next.gameSpeeds.map(gameShape)) ||
    !sameStrings(
      current.clientSpeeds.map((client) => client.clientIp),
      next.clientSpeeds.map((client) => client.clientIp)
    ) ||
    !sameStrings(positiveMeasurementShape(current), positiveMeasurementShape(next))
  );
};
