import * as _heroui_system from '@heroui/system';
import { UseImageProps } from './use-image.mjs';
import 'tailwind-variants';
import 'react';
import '@heroui/theme';
import '@heroui/react-utils';

interface ImageProps extends Omit<UseImageProps, "showSkeleton"> {
}
declare const Image: _heroui_system.InternalForwardRefRenderFunction<"img", ImageProps, never>;

export { type ImageProps, Image as default };
