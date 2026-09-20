import type { ReactNode } from 'react';
import { ChevronLeft, ChevronRight } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { EnhancedDropdown, type DropdownOption } from '@components/ui/EnhancedDropdown';

function CalendarNavigation({
  currentMonth,
  startYear,
  endYear,
  monthNames,
  onChange,
  children
}: {
  currentMonth: Date;
  startYear: number;
  endYear: number;
  monthNames: string[];
  onChange: (month: Date) => void;
  children?: ReactNode;
}) {
  const changeMonth = (increment: number): void => {
    onChange(new Date(currentMonth.getFullYear(), currentMonth.getMonth() + increment, 1));
  };

  const handleMonthSelect = (option: string): void => {
    onChange(new Date(currentMonth.getFullYear(), Number(option), 1));
  };

  const handleYearSelect = (option: string): void => {
    onChange(new Date(Number(option), currentMonth.getMonth(), 1));
  };

  const yearOptions: DropdownOption[] = Array.from(
    { length: endYear - startYear + 1 },
    (_, index) => {
      const year = startYear + index;
      return { value: String(year), label: String(year) };
    }
  );
  const monthOptions: DropdownOption[] = monthNames.map((month, index) => ({
    value: String(index),
    label: month
  }));

  return (
    <div className="calendar-navigation">
      <div className="calendar-nav">
        <Button
          variant="filled"
          color="secondary"
          size="md"
          className="btn-icon-square calendar-nav__previous"
          onClick={() => changeMonth(-1)}
        >
          <ChevronLeft className="w-5 h-5" />
        </Button>

        <EnhancedDropdown
          options={monthOptions}
          value={String(currentMonth.getMonth())}
          onChange={handleMonthSelect}
          variant="button"
          size="md"
          maxHeight="200px"
          dropdownWidth="w-40"
          className="calendar-nav__month"
        />

        <EnhancedDropdown
          options={yearOptions}
          value={String(currentMonth.getFullYear())}
          onChange={handleYearSelect}
          variant="button"
          size="md"
          alignRight
          maxHeight="200px"
          dropdownWidth="w-28"
          className="calendar-nav__year"
        />

        <Button
          variant="filled"
          color="secondary"
          size="md"
          className="btn-icon-square calendar-nav__next"
          onClick={() => changeMonth(1)}
        >
          <ChevronRight className="w-5 h-5" />
        </Button>
      </div>

      {children ? <div className="calendar-navigation__actions">{children}</div> : null}
    </div>
  );
}

export default CalendarNavigation;
