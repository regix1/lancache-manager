import * as react_jsx_runtime from 'react/jsx-runtime';
import { CalendarPickerProps } from './use-calendar-picker.js';
import 'react';
import '@heroui/theme';
import 'tailwind-variants';
import '@react-stately/calendar';
import '@internationalized/date';
import '@react-types/shared';
import '@heroui/system';

type PickerValue = {
    value: string;
    label: string;
};
declare function CalendarPicker(props: CalendarPickerProps): react_jsx_runtime.JSX.Element;

export { CalendarPicker, type PickerValue };
