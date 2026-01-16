import React, { useState, useRef, useEffect } from 'react';
import { createPortal } from 'react-dom';
import { HexColorPicker, HexColorInput } from 'react-colorful';
import { Percent, Copy, Check, RotateCcw, X } from 'lucide-react';
import { useTranslation } from 'react-i18next';

interface ImprovedColorPickerProps {
  value: string;
  onChange: (value: string) => void;
  onStart?: () => void;
  onColorCommit?: (previousColor: string) => void; // Called when picker closes with the original color
  supportsAlpha?: boolean;
  label?: string;
  description?: string;
  affects?: string[];
  copiedColor?: string | null;
  onCopy?: (color: string) => void;
  onRestore?: () => void;
  hasHistory?: boolean;
}

export const ImprovedColorPicker: React.FC<ImprovedColorPickerProps> = ({
  value,
  onChange,
  onStart,
  onColorCommit,
  supportsAlpha = false,
  label,
  description,
  affects = [],
  copiedColor,
  onCopy,
  onRestore,
  hasHistory = false
}) => {
  const { t } = useTranslation();
  const [showPicker, setShowPicker] = useState(false);
  const [pickerPosition, setPickerPosition] = useState({ left: 0, top: 0 });
  const [hexValue, setHexValue] = useState('');
  const [alpha, setAlpha] = useState(1);
  const buttonRef = useRef<HTMLButtonElement>(null);
  const popupRef = useRef<HTMLDivElement>(null);
  const colorBeforeEdit = useRef<string | null>(null);

  // Parse color value (hex or rgba)
  useEffect(() => {
    const rgbaMatch = value.match(/rgba?\((\d+),\s*(\d+),\s*(\d+)(?:,\s*([\d.]+))?\)/);
    if (rgbaMatch) {
      const r = parseInt(rgbaMatch[1]);
      const g = parseInt(rgbaMatch[2]);
      const b = parseInt(rgbaMatch[3]);
      const a = rgbaMatch[4] ? parseFloat(rgbaMatch[4]) : 1;
      const hex = '#' + [r, g, b].map((x) => x.toString(16).padStart(2, '0')).join('');
      setHexValue(hex);
      setAlpha(a);
    } else if (value.startsWith('#')) {
      setHexValue(value);
      setAlpha(1);
    }
  }, [value]);

  // Close picker when clicking outside
  useEffect(() => {
    const handleClickOutside = (event: MouseEvent) => {
      const target = event.target as Node;
      // Check if click is outside both the button and the popup
      const isOutsideButton = buttonRef.current && !buttonRef.current.contains(target);
      const isOutsidePopup = popupRef.current && !popupRef.current.contains(target);

      if (isOutsideButton && isOutsidePopup) {
        // Commit history if color changed
        if (onColorCommit && colorBeforeEdit.current && colorBeforeEdit.current !== value) {
          onColorCommit(colorBeforeEdit.current);
        }
        colorBeforeEdit.current = null;
        setShowPicker(false);
      }
    };

    if (showPicker) {
      document.addEventListener('mousedown', handleClickOutside);
      return () => document.removeEventListener('mousedown', handleClickOutside);
    }
  }, [showPicker, value, onColorCommit]);

  // Close picker when user scrolls
  useEffect(() => {
    const handleScroll = (e: Event) => {
      // Ignore scroll events originating from inside the popup
      if (popupRef.current && popupRef.current.contains(e.target as Node)) {
        return;
      }

      // Commit history if color changed
      if (onColorCommit && colorBeforeEdit.current && colorBeforeEdit.current !== value) {
        onColorCommit(colorBeforeEdit.current);
      }
      colorBeforeEdit.current = null;
      setShowPicker(false);
    };

    if (showPicker) {
      // Listen for scroll on window and all parent elements
      window.addEventListener('scroll', handleScroll, true);
      return () => window.removeEventListener('scroll', handleScroll, true);
    }
  }, [showPicker, value, onColorCommit]);

  const hexToRgba = (hex: string, alpha: number): string => {
    const result = /^#?([a-f\d]{2})([a-f\d]{2})([a-f\d]{2})$/i.exec(hex);
    if (!result) return hex;
    const r = parseInt(result[1], 16);
    const g = parseInt(result[2], 16);
    const b = parseInt(result[3], 16);
    return `rgba(${r}, ${g}, ${b}, ${alpha})`;
  };

  const handleHexChange = (newHex: string) => {
    setHexValue(newHex);
    const colorValue = supportsAlpha && alpha < 1 ? hexToRgba(newHex, alpha) : newHex;
    onChange(colorValue);
  };

  const handleAlphaChange = (newAlpha: number) => {
    setAlpha(newAlpha);
    const colorValue = newAlpha < 1 ? hexToRgba(hexValue, newAlpha) : hexValue;
    onChange(colorValue);
  };

  const getPickerPosition = () => {
    if (!buttonRef.current) return { left: 0, top: 0 };

    const rect = buttonRef.current.getBoundingClientRect();
    const pickerWidth = 250; // Approximate width of the color picker
    const viewportWidth = window.innerWidth;
    const spaceOnRight = viewportWidth - rect.right;

    // If there's enough space on the right, position to the right
    if (spaceOnRight > pickerWidth + 20) {
      return {
        left: rect.right + 8, // 8px gap to the right
        top: rect.top // fixed positioning uses viewport coords, no scroll offset needed
      };
    }

    // Otherwise, position to the left
    return {
      left: rect.left - pickerWidth - 8, // 8px gap to the left
      top: rect.top // fixed positioning uses viewport coords, no scroll offset needed
    };
  };

  const handlePickerToggle = () => {
    if (!showPicker) {
      // Opening picker - calculate position first to prevent jitter
      if (onStart) onStart();
      const pos = getPickerPosition();
      setPickerPosition(pos);
      colorBeforeEdit.current = value;
      setShowPicker(true);
    } else {
      // Closing picker - commit history if color changed
      if (onColorCommit && colorBeforeEdit.current && colorBeforeEdit.current !== value) {
        onColorCommit(colorBeforeEdit.current);
      }
      colorBeforeEdit.current = null;
      setShowPicker(false);
    }
  };

  return (
    <div className="grid grid-cols-[1fr_auto] gap-4 items-start">
      {/* Label and description */}
      <div>
        {label && <label className="block text-sm font-medium text-themed-primary">{label}</label>}
        {description && <p className="text-xs text-themed-muted">{description}</p>}
        {affects.length > 0 && (
          <div className="flex flex-wrap gap-1 mt-1">
            {affects.map((item, idx) => (
              <span
                key={idx}
                className="text-xs px-1.5 py-0.5 rounded bg-themed-hover text-themed-secondary"
              >
                {item}
              </span>
            ))}
          </div>
        )}
      </div>

      {/* Color controls */}
      <div className="flex items-center gap-2">
        {/* Color preview button */}
        <div className="relative">
          <button
            ref={buttonRef}
            type="button"
            onClick={handlePickerToggle}
            className="w-12 h-8 rounded border-2 cursor-pointer transition-all hover:scale-105 border-themed-secondary"
            style={{ backgroundColor: value }}
            title={t('modals.theme.colorPicker.pickColor')}
          />

          {/* Color picker popover - rendered via portal */}
          {showPicker &&
            createPortal(
              <div
                ref={popupRef}
                className="fixed p-3 rounded-lg shadow-2xl overflow-hidden bg-themed-primary border border-themed-primary isolate"
                style={{
                  left: `${pickerPosition.left}px`,
                  top: `${pickerPosition.top}px`,
                  zIndex: 100001
                }}
              >
                <div className="flex items-center justify-between mb-2">
                  <span className="text-xs font-medium text-themed-secondary">{t('modals.theme.colorPicker.pickColor')}</span>
                  <button
                    type="button"
                    onClick={() => setShowPicker(false)}
                    className="p-1 rounded hover:bg-themed-hover"
                  >
                    <X className="w-3 h-3 text-themed-muted" />
                  </button>
                </div>
                <HexColorPicker color={hexValue} onChange={handleHexChange} />
                <div className="mt-3 space-y-2">
                  <HexColorInput
                    color={hexValue}
                    onChange={handleHexChange}
                    className="w-full px-2 py-1 text-xs rounded font-mono themed-input"
                    prefixed
                  />
                  {supportsAlpha && (
                    <div className="flex items-center gap-2">
                      <Percent className="w-3 h-3 text-themed-muted" />
                      <input
                        type="range"
                        min="0"
                        max="100"
                        value={Math.round(alpha * 100)}
                        onChange={(e) => handleAlphaChange(parseInt(e.target.value) / 100)}
                        className="flex-1"
                        title={`${t('modals.theme.colorPicker.opacity')} ${Math.round(alpha * 100)}%`}
                      />
                      <span className="text-xs text-themed-muted w-10 text-right">
                        {Math.round(alpha * 100)}%
                      </span>
                    </div>
                  )}
                </div>
              </div>,
              document.body
            )}
        </div>

        {/* Text input */}
        <input
          type="text"
          value={value}
          onFocus={onStart}
          onChange={(e) => onChange(e.target.value)}
          className="w-24 px-2 py-1 text-xs rounded font-mono themed-input"
          placeholder=""
        />

        {/* Action buttons */}
        {onCopy && (
          <button
            type="button"
            onClick={() => onCopy(value)}
            className="p-1 rounded-lg hover:bg-opacity-50 bg-themed-hover"
            title={t('modals.theme.colorPicker.copyColor')}
          >
            {copiedColor === value ? (
              <Check className="w-3 h-3 icon-success" />
            ) : (
              <Copy className="w-3 h-3 text-themed-muted" />
            )}
          </button>
        )}

        {/* Restore button - always visible */}
        {onRestore && (
          <button
            type="button"
            onClick={onRestore}
            disabled={!hasHistory}
            className="p-1 rounded-lg hover:bg-opacity-50 bg-themed-hover disabled:opacity-30 disabled:cursor-not-allowed transition-none"
            title={hasHistory ? t('modals.theme.colorPicker.restorePrevious') : t('modals.theme.colorPicker.noHistory')}
          >
            <RotateCcw className="w-3 h-3 text-themed-muted" />
          </button>
        )}
      </div>
    </div>
  );
};
