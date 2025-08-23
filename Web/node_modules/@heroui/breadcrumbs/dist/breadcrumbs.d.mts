import * as _heroui_system from '@heroui/system';
import { UseBreadcrumbsProps } from './use-breadcrumbs.mjs';
import './breadcrumb-item.mjs';
import './use-breadcrumb-item.mjs';
import 'tailwind-variants';
import 'react';
import '@heroui/theme';
import '@react-types/breadcrumbs';
import '@heroui/react-utils';

interface BreadcrumbsProps extends UseBreadcrumbsProps {
}
declare const Breadcrumbs: _heroui_system.InternalForwardRefRenderFunction<"div", BreadcrumbsProps, never>;

export { type BreadcrumbsProps, Breadcrumbs as default };
