import * as _heroui_system from '@heroui/system';
import { UseBreadcrumbItemProps } from './use-breadcrumb-item.js';
import 'react';
import '@heroui/theme';
import '@react-types/breadcrumbs';
import '@heroui/react-utils';

interface BreadcrumbItemProps extends UseBreadcrumbItemProps {
}
declare const Breadcrumbs: _heroui_system.InternalForwardRefRenderFunction<"li", BreadcrumbItemProps, never>;

export { type BreadcrumbItemProps, Breadcrumbs as default };
