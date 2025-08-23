import * as react_jsx_runtime from 'react/jsx-runtime';
import { SlotsToClasses, ToastRegionSlots, ToastRegionVariantProps } from '@heroui/theme';
import { AriaToastRegionProps } from '@react-aria/toast';
import { ToastState } from '@react-stately/toast';
import { ToastProps, ToastPlacement } from './use-toast.mjs';
import 'tailwind-variants';
import 'react';
import '@heroui/system';
import '@heroui/react-utils';
import 'framer-motion';

interface RegionProps {
    className?: string;
    classNames?: SlotsToClasses<ToastRegionSlots>;
}
interface ToastRegionProps<T> extends AriaToastRegionProps, ToastRegionVariantProps, RegionProps {
    toastQueue: ToastState<T>;
    placement?: ToastPlacement;
    maxVisibleToasts: number;
    toastOffset?: number;
    toastProps?: ToastProps;
}
declare function ToastRegion<T extends ToastProps>({ toastQueue, placement, disableAnimation, maxVisibleToasts, toastOffset, toastProps, className, classNames, ...props }: ToastRegionProps<T>): react_jsx_runtime.JSX.Element;

export { type RegionProps, ToastRegion };
