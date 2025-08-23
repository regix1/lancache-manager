import { ReactElement } from 'react';
import { UseTabsProps } from './use-tabs.js';
import '@heroui/system';
import '@heroui/theme';
import '@heroui/react-utils';
import '@react-stately/tabs';
import '@react-aria/tabs';
import '@heroui/aria-utils';
import 'framer-motion';

interface Props<T> extends UseTabsProps<T> {
}
type TabsProps<T extends object = object> = Props<T>;
declare const Tabs: <T extends object>(props: TabsProps<T>) => ReactElement;

export { type TabsProps, Tabs as default };
