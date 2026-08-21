import React, { useState, useRef, useEffect, useCallback } from 'react';
import { createPortal } from 'react-dom';
import { HexColorPicker, HexColorInput } from 'react-colorful';
import { Percent, Copy, Check, RotateCcw, X } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Tooltip } from '@components/ui/Tooltip';
import { Slider } from '@components/ui/Slider';
import Badge from '@components/ui/Badge';
import { useAnchoredPanel, type PanelPlacement, type PanelSpace } from '@hooks/useAnchoredPanel';
import { clampToViewport, MENU_GUTTER_PX } from '@utils/viewportClamp';

/** Gap between the swatch and the picker on whichever side it opens. */
const SWATCH_GAP_PX = 8;
/** Pre-measurement guess, used only on the pass before the picker is in the DOM. */
const PICKER_WIDTH_PX = 250;
/** Extra room the right side must offer before the picker prefers it. */
const RIGHT_SIDE_MARGIN_PX = 20;

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
  const [hexValue, setHexValue] = useState('');
  const [alpha, setAlpha] = useState(1);
  const buttonRef = useRef<HTMLButtonElement>(null);
  const popupRef = useRef<HTMLDivElement>(null);
  const colorBeforeEdit = useRef<string | null>(null);

  // Parse color value (hex or rgba) - skip while picker is open (local state is authoritative during drag)
  useEffect(() => {
    if (showPicker) return;
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
  }, [value, showPicker]);

  /** Closes the picker, recording the colour it opened on so the edit can be undone. */
  const closePicker = useCallback((): void => {
    if (onColorCommit && colorBeforeEdit.current && colorBeforeEdit.current !== value) {
      onColorCommit(colorBeforeEdit.current);
    }
    colorBeforeEdit.current = null;
    setShowPicker(false);
  }, [onColorCommit, value]);

  /**
   * The picker sits BESIDE its swatch rather than under it, so the shared below/above
   * placement does not apply. It prefers the right, falls back to the left, and clamps
   * on both axes: a swatch low in a long theme form used to hang the picker off the
   * bottom of the screen, because only the horizontal edge was ever pulled back.
   */
  const place = useCallback((space: PanelSpace): PanelPlacement => {
    const { anchor, panelWidth, panelHeight, viewportWidth, viewportHeight, gutter } = space;
    const pickerWidth = panelWidth || PICKER_WIDTH_PX;
    const spaceOnRight = viewportWidth - anchor.right;

    // Prefer the right of the swatch, fall back to its left. Neither side is
    // guaranteed to fit on a narrow screen, where a swatch close to the left edge
    // leaves less than the picker's width beside it, so the chosen edge is then
    // pulled back onto the screen.
    const desiredLeft =
      spaceOnRight > pickerWidth + RIGHT_SIDE_MARGIN_PX
        ? anchor.right + SWATCH_GAP_PX
        : anchor.left - pickerWidth - SWATCH_GAP_PX;

    return {
      left: clampToViewport(desiredLeft, pickerWidth, viewportWidth, gutter),
      top: clampToViewport(anchor.top, panelHeight, viewportHeight, gutter),
      openUpward: false
    };
  }, []);

  const { present, closing, position } = useAnchoredPanel({
    open: showPicker,
    anchorRef: buttonRef,
    panelRef: popupRef,
    onClose: closePicker,
    gutter: MENU_GUTTER_PX,
    place
  });

  // Click-outside only. Escape now closes the picker through the shared hook, which it
  // never did before, and the close-on-scroll listener is gone: the picker is
  // positioned on the page and carried by its swatch, so scrolling keeps them together.
  useEffect(() => {
    if (!showPicker) return;

    const handleClickOutside = (event: MouseEvent) => {
      const target = event.target as Node;
      // Check if click is outside both the button and the popup
      const isOutsideButton = buttonRef.current && !buttonRef.current.contains(target);
      const isOutsidePopup = popupRef.current && !popupRef.current.contains(target);

      if (isOutsideButton && isOutsidePopup) {
        closePicker();
      }
    };

    document.addEventListener('mousedown', handleClickOutside);
    return () => document.removeEventListener('mousedown', handleClickOutside);
  }, [showPicker, closePicker]);

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

  const handlePickerToggle = () => {
    if (!showPicker) {
      if (onStart) onStart();
      colorBeforeEdit.current = value;
      setShowPicker(true);
    } else {
      closePicker();
    }
  };

  return (
    <div className="grid grid-cols-[1fr_auto] gap-4 items-start">
      {/* Label and description */}
      <div>
        {label && <label className="form-field-label">{label}</label>}
        {description && <p className="text-xs text-themed-muted">{description}</p>}
        {affects.length > 0 && (
          <div className="flex flex-wrap gap-1 mt-1">
            {affects.map((item, idx) => (
              <Badge key={idx} variant="neutral">
                {item}
              </Badge>
            ))}
          </div>
        )}
      </div>

      {/* Color controls */}
      <div className="flex items-center gap-2">
        {/* Color preview button */}
        <div className="relative">
          <Tooltip content={t('modals.theme.colorPicker.pickColor')} position="top">
            <button
              ref={buttonRef}
              type="button"
              onClick={handlePickerToggle}
              className="w-12 h-8 rounded border-2 cursor-pointer transition hover:scale-105 border-themed-secondary"
              style={{ backgroundColor: value }}
            />
          </Tooltip>

          {/* Color picker popover - rendered via portal.
              Rendered while `present` (not just `showPicker`) so the exit animation plays. */}
          {present &&
            createPortal(
              <div
                ref={popupRef}
                className={`absolute z-[100001] p-3 rounded-lg shadow-2xl overflow-hidden bg-themed-primary border border-themed-primary isolate motion-reduce:animate-none ${
                  closing
                    ? 'animate-[dropdownSlideOutDown_0.14s_ease-in_forwards]'
                    : 'animate-[dropdownSlideDown_0.15s_cubic-bezier(0.16,1,0.3,1)]'
                }`}
                style={{
                  left: position.left,
                  top: position.top,
                  pointerEvents: closing ? 'none' : undefined
                }}
              >
                <div className="flex items-center justify-between mb-2">
                  <span className="text-xs font-medium text-themed-secondary">
                    {t('modals.theme.colorPicker.pickColor')}
                  </span>
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
                      {/* The word only. The percentage already sits at the end of the row, and a
                        per-cent glyph is the sole other clue as to what this slider drives. */}
                      <Tooltip
                        content={t('modals.theme.colorPicker.opacity')}
                        position="top"
                        className="flex flex-1"
                      >
                        <Slider
                          min={0}
                          max={100}
                          value={Math.round(alpha * 100)}
                          onChange={(percent: number) => handleAlphaChange(percent / 100)}
                          className="flex-1"
                          aria-label={t('modals.theme.colorPicker.opacity')}
                        />
                      </Tooltip>
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
          className="w-24 px-2 py-[7px] text-xs rounded font-mono themed-input"
          placeholder=""
        />

        {/* Action buttons */}
        {onCopy && (
          <Tooltip content={t('modals.theme.colorPicker.copyColor')} position="top">
            <button
              type="button"
              onClick={() => onCopy(value)}
              className="p-[10px] rounded-lg hover:bg-opacity-50 bg-themed-hover"
            >
              {copiedColor === value ? (
                <Check className="w-3 h-3 icon-success" />
              ) : (
                <Copy className="w-3 h-3 text-themed-muted" />
              )}
            </button>
          </Tooltip>
        )}

        {/* Restore button - always visible */}
        {onRestore && (
          <Tooltip
            content={
              hasHistory
                ? t('modals.theme.colorPicker.restorePrevious')
                : t('modals.theme.colorPicker.noHistory')
            }
            position="top"
          >
            <button
              type="button"
              onClick={onRestore}
              disabled={!hasHistory}
              className="p-[10px] rounded-lg hover:bg-opacity-50 bg-themed-hover disabled:opacity-30 disabled:cursor-not-allowed transition-none"
            >
              <RotateCcw className="w-3 h-3 text-themed-muted" />
            </button>
          </Tooltip>
        )}
      </div>
    </div>
  );
};
