import { TimeValue } from '@react-types/datepicker';
import { ReactElement } from 'react';
import { UseTimeInputProps } from './use-time-input.js';
import 'tailwind-variants';
import '@react-stately/datepicker';
import '@heroui/theme';
import '@heroui/react-utils';
import '@react-types/shared';
import './date-input-group.js';
import '@heroui/system';

interface Props<T extends TimeValue> extends UseTimeInputProps<T> {
}
type TimeInputProps<T extends TimeValue = TimeValue> = Props<T>;
declare const TimeInput: <T extends TimeValue>(props: TimeInputProps<T>) => ReactElement;

export { type Props, type TimeInputProps, TimeInput as default };
