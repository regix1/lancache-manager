export type BannerScalingMode = 'smooth' | 'crisp';

const MAX_CACHE_ENTRIES = 200;
const crispBannerCache = new Map<string, string>();

export function getCrispBannerCacheKey(src: string, width: number, height: number): string {
  const dpr = Math.min(window.devicePixelRatio || 1, 2);
  const pixelWidth = Math.max(1, Math.round(width * dpr));
  const pixelHeight = Math.max(1, Math.round(height * dpr));
  return `${src}|${pixelWidth}x${pixelHeight}`;
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
  displayHeight: number
): string | null {
  if (sourceWidth <= 0 || sourceHeight <= 0 || displayWidth < 2 || displayHeight < 2) {
    return null;
  }

  const dpr = Math.min(window.devicePixelRatio || 1, 2);
  const targetWidth = Math.max(1, Math.round(displayWidth * dpr));
  const targetHeight = Math.max(1, Math.round(displayHeight * dpr));

  let canvas = document.createElement('canvas');
  canvas.width = sourceWidth;
  canvas.height = sourceHeight;
  const ctx = canvas.getContext('2d');
  if (!ctx) return null;
  ctx.drawImage(source, 0, 0, sourceWidth, sourceHeight);

  let currentWidth = sourceWidth;
  let currentHeight = sourceHeight;

  while (currentWidth * 0.5 > targetWidth && currentHeight * 0.5 > targetHeight) {
    currentWidth = Math.max(1, Math.floor(currentWidth * 0.5));
    currentHeight = Math.max(1, Math.floor(currentHeight * 0.5));

    const stepCanvas = document.createElement('canvas');
    stepCanvas.width = currentWidth;
    stepCanvas.height = currentHeight;
    const stepCtx = stepCanvas.getContext('2d');
    if (!stepCtx) return null;

    stepCtx.imageSmoothingEnabled = true;
    stepCtx.imageSmoothingQuality = 'high';
    stepCtx.drawImage(canvas, 0, 0, currentWidth, currentHeight);
    canvas = stepCanvas;
  }

  const output = document.createElement('canvas');
  output.width = targetWidth;
  output.height = targetHeight;
  const outputCtx = output.getContext('2d');
  if (!outputCtx) return null;

  outputCtx.imageSmoothingEnabled = true;
  outputCtx.imageSmoothingQuality = 'high';
  outputCtx.drawImage(canvas, 0, 0, targetWidth, targetHeight);

  return output.toDataURL('image/jpeg', 0.92);
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
