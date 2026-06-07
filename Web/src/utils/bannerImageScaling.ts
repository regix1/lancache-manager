import { logBannerImageDebug, warnBannerImageDebug } from './bannerImageDebug';

export type BannerScalingMode = 'smooth' | 'crisp';

const MAX_CACHE_ENTRIES = 200;
const crispBannerCache = new Map<string, string>();

export function getCrispBannerCacheKey(src: string, width: number, height: number): string {
  const dpr = Math.min(window.devicePixelRatio || 1, 2);
  const pixelWidth = Math.max(1, Math.round(width * dpr));
  const pixelHeight = Math.max(1, Math.round(height * dpr));
  return `${src}|${pixelWidth}x${pixelHeight}`;
}

function probeCanvasReadable(source: CanvasImageSource, label: string): boolean {
  try {
    const probe = document.createElement('canvas');
    probe.width = 1;
    probe.height = 1;
    const probeCtx = probe.getContext('2d');
    if (!probeCtx) return false;
    probeCtx.drawImage(source, 0, 0, 1, 1);
    probe.toDataURL('image/jpeg', 0.5);
    logBannerImageDebug('scaling', 'Canvas probe succeeded (image not tainted)', { label });
    return true;
  } catch (error) {
    warnBannerImageDebug('scaling', 'Canvas probe failed (image likely tainted / CORS blocked)', {
      label,
      error: error instanceof Error ? error.message : String(error)
    });
    return false;
  }
}

/**
 * Canvas step-down resize to the exact on-screen pixel size.
 * Browsers ignore CSS image-rendering for downscaled JPEG photos; pre-scaling is the reliable fix.
 */
export function renderCrispBannerImage(
  source: CanvasImageSource,
  sourceWidth: number,
  sourceHeight: number,
  displayWidth: number,
  displayHeight: number,
  debugLabel?: string
): string | null {
  const label = debugLabel ?? 'unknown';

  if (sourceWidth <= 0 || sourceHeight <= 0 || displayWidth < 2 || displayHeight < 2) {
    warnBannerImageDebug('scaling', 'Skipped crisp render — invalid dimensions', {
      label,
      sourceWidth,
      sourceHeight,
      displayWidth,
      displayHeight
    });
    return null;
  }

  if (!probeCanvasReadable(source, label)) {
    return null;
  }

  const dpr = Math.min(window.devicePixelRatio || 1, 2);
  const targetWidth = Math.max(1, Math.round(displayWidth * dpr));
  const targetHeight = Math.max(1, Math.round(displayHeight * dpr));

  logBannerImageDebug('scaling', 'Starting crisp canvas render', {
    label,
    sourceWidth,
    sourceHeight,
    displayWidth,
    displayHeight,
    targetWidth,
    targetHeight,
    dpr
  });

  let canvas = document.createElement('canvas');
  canvas.width = sourceWidth;
  canvas.height = sourceHeight;
  const ctx = canvas.getContext('2d');
  if (!ctx) {
    warnBannerImageDebug('scaling', 'Failed to get 2d context for source canvas', { label });
    return null;
  }
  ctx.drawImage(source, 0, 0, sourceWidth, sourceHeight);

  let currentWidth = sourceWidth;
  let currentHeight = sourceHeight;
  let stepCount = 0;

  while (currentWidth * 0.5 > targetWidth && currentHeight * 0.5 > targetHeight) {
    currentWidth = Math.max(1, Math.floor(currentWidth * 0.5));
    currentHeight = Math.max(1, Math.floor(currentHeight * 0.5));
    stepCount += 1;

    const stepCanvas = document.createElement('canvas');
    stepCanvas.width = currentWidth;
    stepCanvas.height = currentHeight;
    const stepCtx = stepCanvas.getContext('2d');
    if (!stepCtx) {
      warnBannerImageDebug('scaling', 'Failed to get 2d context during step-down', {
        label,
        stepCount
      });
      return null;
    }

    stepCtx.imageSmoothingEnabled = true;
    stepCtx.imageSmoothingQuality = 'high';
    stepCtx.drawImage(canvas, 0, 0, currentWidth, currentHeight);
    canvas = stepCanvas;
  }

  const output = document.createElement('canvas');
  output.width = targetWidth;
  output.height = targetHeight;
  const outputCtx = output.getContext('2d');
  if (!outputCtx) {
    warnBannerImageDebug('scaling', 'Failed to get 2d context for output canvas', { label });
    return null;
  }

  outputCtx.imageSmoothingEnabled = true;
  outputCtx.imageSmoothingQuality = 'high';
  outputCtx.drawImage(canvas, 0, 0, targetWidth, targetHeight);

  const dataUrl = output.toDataURL('image/jpeg', 0.92);
  logBannerImageDebug('scaling', 'Crisp canvas render complete', {
    label,
    stepCount,
    outputBytesApprox: Math.round(dataUrl.length * 0.75),
    dataUrlPrefix: dataUrl.slice(0, 32)
  });

  return dataUrl;
}

export function readCachedCrispBanner(key: string): string | undefined {
  return crispBannerCache.get(key);
}

export function writeCachedCrispBanner(key: string, dataUrl: string): void {
  if (crispBannerCache.has(key)) {
    crispBannerCache.delete(key);
  }
  crispBannerCache.set(key, dataUrl);

  while (crispBannerCache.size > MAX_CACHE_ENTRIES) {
    const oldestKey = crispBannerCache.keys().next().value;
    if (oldestKey === undefined) break;
    crispBannerCache.delete(oldestKey);
  }
}
