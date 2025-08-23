import * as react_jsx_runtime from 'react/jsx-runtime';
import { ButtonProps } from '@heroui/button';
import { CalendarDate } from '@internationalized/date';
import { HTMLHeroUIProps } from '@heroui/system';

interface CalendarHeaderProps extends HTMLHeroUIProps<"header"> {
    direction: number;
    date: CalendarDate;
    currentMonth: CalendarDate;
    buttonPickerProps?: ButtonProps;
}
declare function CalendarHeader(props: CalendarHeaderProps): react_jsx_runtime.JSX.Element;

export { CalendarHeader, type CalendarHeaderProps };
