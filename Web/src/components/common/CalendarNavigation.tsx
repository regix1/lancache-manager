import { ChevronLeft, ChevronRight } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { EnhancedDropdown, type DropdownOption } from '@components/ui/EnhancedDropdown';

function CalendarNavigation({
  currentMonth,
  startYear,
  endYear,
  monthNames,
  onChange
}: {
  currentMonth: Date;
  startYear: number;
  endYear: number;
  monthNames: string[];
  onChange: (month: Date) => void;
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
    <div className="mb-4 flex items-center justify-between">
      {/* md is the shared 40px control height. Below the phone breakpoint, the dropdown takes a
          44px touch floor, so the buttons follow it with min-h-11. */}
      <Button
        variant="filled"
        color="gray"
        size="md"
        className="max-sm:min-h-11"
        onClick={() => changeMonth(-1)}
      >
        <ChevronLeft className="w-5 h-5" />
      </Button>

      <div className="flex items-center gap-2">
        <EnhancedDropdown
          options={monthOptions}
          value={String(currentMonth.getMonth())}
          onChange={handleMonthSelect}
          variant="button"
          size="md"
          maxHeight="200px"
          dropdownWidth="w-40"
          className="w-[104px] sm:w-[128px]"
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
          className="w-[76px] sm:w-[92px]"
        />
      </div>

      <Button
        variant="filled"
        color="gray"
        size="md"
        className="max-sm:min-h-11"
        onClick={() => changeMonth(1)}
      >
        <ChevronRight className="w-5 h-5" />
      </Button>
    </div>
  );
}

export default CalendarNavigation;
