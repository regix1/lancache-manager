import * as _heroui_system from '@heroui/system';
import { UsePaginationProps } from './use-pagination.js';
import 'tailwind-variants';
import '@heroui/theme';
import 'react';
import '@heroui/use-pagination';
import '@react-types/shared';

interface PaginationProps extends UsePaginationProps {
}
declare const Pagination: _heroui_system.InternalForwardRefRenderFunction<"nav", PaginationProps, never>;

export { type PaginationProps, Pagination as default };
