import * as react_jsx_runtime from 'react/jsx-runtime';
import { CalendarDate } from '@internationalized/date';
import { CalendarPropsBase } from '@react-types/calendar';
import { HTMLHeroUIProps } from '@heroui/system';

interface CalendarMonthProps extends HTMLHeroUIProps<"table">, CalendarPropsBase {
    startDate: CalendarDate;
    currentMonth: number;
    direction: number;
}
declare function CalendarMonth(props: CalendarMonthProps): react_jsx_runtime.JSX.Element;

export { CalendarMonth, type CalendarMonthProps };
