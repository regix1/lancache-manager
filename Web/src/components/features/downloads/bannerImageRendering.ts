export type BannerImageRendering = 'crisp' | 'smooth';

export const BANNER_IMAGE_RENDERING_STORAGE_KEY = 'lancache_downloads_banner_image_rendering';

export function parseBannerImageRendering(value: string | null): BannerImageRendering {
  return value === 'smooth' ? 'smooth' : 'crisp';
}

export function getBannerImageClass(
  base:
    | 'download-banner-image'
    | 'download-banner-image-natural'
    | 'drawer-banner-image'
    | 'card-grid-banner-image'
    | 'retro-banner-image',
  rendering: BannerImageRendering
): string {
  return `${base} ${base}--${rendering}`;
}
