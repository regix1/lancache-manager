import * as _heroui_system from '@heroui/system';
import { UseSnippetProps } from './use-snippet.mjs';
import 'tailwind-variants';
import 'react';
import '@heroui/theme';
import '@heroui/react-utils';
import '@heroui/tooltip';
import '@heroui/button';

interface SnippetProps extends UseSnippetProps {
}
declare const Snippet: _heroui_system.InternalForwardRefRenderFunction<"div", SnippetProps, never>;

export { type SnippetProps, Snippet as default };
