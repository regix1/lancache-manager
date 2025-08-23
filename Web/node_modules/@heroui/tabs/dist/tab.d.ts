import * as _heroui_system from '@heroui/system';
import { TabItemProps as TabItemProps$1 } from './base/tab-item-base.js';
import { Node } from '@react-types/shared';
import { ValuesType } from './use-tabs.js';
import '@heroui/aria-utils';
import 'react';
import '@heroui/theme';
import '@heroui/react-utils';
import '@react-stately/tabs';
import '@react-aria/tabs';
import 'framer-motion';

interface TabItemProps<T extends object = object> extends TabItemProps$1<T> {
    item: Node<T>;
    state: ValuesType["state"];
    slots: ValuesType["slots"];
    listRef?: ValuesType["listRef"];
    classNames?: ValuesType["classNames"];
    isDisabled?: ValuesType["isDisabled"];
    motionProps?: ValuesType["motionProps"];
    disableAnimation?: ValuesType["disableAnimation"];
    disableCursorAnimation?: ValuesType["disableCursorAnimation"];
}
/**
 * @internal
 */
declare const Tab: _heroui_system.InternalForwardRefRenderFunction<"button", TabItemProps<object>, never>;

export { type TabItemProps, Tab as default };
