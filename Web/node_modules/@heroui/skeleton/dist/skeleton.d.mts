import * as _heroui_system from '@heroui/system';
import { UseSkeletonProps } from './use-skeleton.mjs';
import 'tailwind-variants';
import 'react';
import '@heroui/theme';

interface SkeletonProps extends UseSkeletonProps {
}
declare const Skeleton: _heroui_system.InternalForwardRefRenderFunction<"div", SkeletonProps, never>;

export { type SkeletonProps, Skeleton as default };
