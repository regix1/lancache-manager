import React from 'react';
import { Check, Copy, RotateCcw, X, Percent, EyeOff } from 'lucide-react';
import { ColorDefinition } from './types';

interface ColorPickerProps {
  color: ColorDefinition;
  value: string;
  onChange: (key: string, value: string) => void;
  onStart?: (key: string) => void;
  onCopy?: (value: string) => void;
  onReset?: (key: string) => void;
  onRestore?: (key: string) => void;
  copiedColor?: string | null;
  historyColor?: string | null;
  cssVarMap?: Record<string, string>;
  computedStyle?: CSSStyleDeclaration;
}

export const ColorPicker: React.FC<ColorPickerProps> = ({
  color,
  value,
  onChange,
  onStart,
  onCopy,
  onReset,
  onRestore,
  copiedColor,
  historyColor,
  cssVarMap,
  computedStyle
}) => {
  const parseColorValue = (colorVal: string): { hex: string; alpha: number } => {
    // Handle rgba format
    const rgbaMatch = colorVal.match(/rgba?\((\d+),\s*(\d+),\s*(\d+)(?:,\s*([\d.]+))?\)/);
    if (rgbaMatch) {
      const r = parseInt(rgbaMatch[1]);
      const g = parseInt(rgbaMatch[2]);
      const b = parseInt(rgbaMatch[3]);
      const alpha = rgbaMatch[4] ? parseFloat(rgbaMatch[4]) : 1;
      const hex = '#' + [r, g, b].map(x => x.toString(16).padStart(2, '0')).join('');
      return { hex, alpha };
    }
    // Handle hex format
    return { hex: colorVal, alpha: 1 };
  };

  const hexToRgba = (hex: string, alpha: number = 1): string => {
    const result = /^#?([a-f\d]{2})([a-f\d]{2})([a-f\d]{2})$/i.exec(hex);
    if (!result) return hex;
    const r = parseInt(result[1], 16);
    const g = parseInt(result[2], 16);
    const b = parseInt(result[3], 16);
    return `rgba(${r}, ${g}, ${b}, ${alpha})`;
  };

  const updateColorWithAlpha = (hex: string, alpha: number) => {
    const colorValue = alpha < 1 ? hexToRgba(hex, alpha) : hex;
    onChange(color.key, colorValue);
  };

  const defaultValue = computedStyle && cssVarMap
    ? computedStyle.getPropertyValue(cssVarMap[color.key] || `--theme-${color.key.replace(/([A-Z])/g, '-$1').toLowerCase()}`).trim() || '#3b82f6'
    : value || '#3b82f6';

  const { hex, alpha } = parseColorValue(value || defaultValue);

  return (
    <div className="flex items-center gap-2">
      <div className="relative">
        <input
          type="color"
          value={hex}
          onMouseDown={() => onStart?.(color.key)}
          onFocus={() => onStart?.(color.key)}
          onChange={(e) => {
            const currentAlpha = parseColorValue(value || defaultValue).alpha;
            updateColorWithAlpha(e.target.value, currentAlpha);
          }}
          className="w-12 h-8 rounded cursor-pointer"
          style={{ backgroundColor: value || defaultValue }}
        />
      </div>
      {color.supportsAlpha && (
        <div className="flex items-center gap-1">
          <Percent className="w-3 h-3 text-themed-muted" />
          <input
            type="range"
            min="0"
            max="100"
            value={Math.round(alpha * 100)}
            onChange={(e) => {
              const newAlpha = parseInt(e.target.value) / 100;
              updateColorWithAlpha(hex, newAlpha);
            }}
            className="w-16"
            title={`Opacity: ${Math.round(alpha * 100)}%`}
          />
          <span className="text-xs text-themed-muted w-8">
            {Math.round(alpha * 100)}%
          </span>
        </div>
      )}
      <input
        type="text"
        value={value || defaultValue}
        onFocus={() => onStart?.(color.key)}
        onChange={(e) => onChange(color.key, e.target.value)}
        className="w-24 px-2 py-1 text-xs rounded font-mono themed-input"
        placeholder={color.key}
      />
      <button
        onClick={() => onCopy?.(value || defaultValue)}
        className="p-1 rounded-lg hover:bg-opacity-50 bg-themed-hover"
        title="Copy color"
      >
        {copiedColor === (value || defaultValue) ? (
          <Check
            className="w-3 h-3"
            style={{ color: 'var(--theme-success)' }}
          />
        ) : (
          <Copy className="w-3 h-3 text-themed-muted" />
        )}
      </button>
      {color.supportsAlpha && (
        <button
          onClick={() => updateColorWithAlpha(hex, alpha === 0 ? 1 : 0)}
          className="p-1 rounded-lg hover:bg-opacity-50 bg-themed-hover"
          title={alpha === 0 ? "Show color (make opaque)" : "Hide color (make transparent)"}
        >
          <EyeOff
            className="w-3 h-3"
            style={{ color: alpha === 0 ? 'var(--theme-success)' : 'var(--theme-text-muted)' }}
          />
        </button>
      )}
      {historyColor && onRestore && (
        <button
          onClick={() => onRestore(color.key)}
          className="p-1 rounded-lg hover:bg-opacity-50 bg-themed-hover"
          title={`Restore previous color: ${historyColor}`}
        >
          <RotateCcw className="w-3 h-3 text-themed-muted" />
        </button>
      )}
      {onReset && (
        <button
          onClick={() => onReset(color.key)}
          className="p-1 rounded-lg hover:bg-opacity-50 bg-themed-hover"
          title="Reset to default"
        >
          <X className="w-3 h-3 text-themed-warning" />
        </button>
      )}
    </div>
  );
};
