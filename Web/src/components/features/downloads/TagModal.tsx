import React, { useState, useEffect, useMemo } from 'react';
import { X, Loader2 } from 'lucide-react';
import { useTags } from '@contexts/TagContext';
import { Button } from '@components/ui/Button';
import type { Tag } from '../../../types';

// Get colors from theme CSS variables
const getThemeColors = (): string[] => {
  const computedStyle = getComputedStyle(document.documentElement);
  const colors: string[] = [];

  // Event colors (1-8)
  for (let i = 1; i <= 8; i++) {
    const color = computedStyle.getPropertyValue(`--theme-event-${i}`).trim();
    if (color) colors.push(color);
  }

  // Chart colors as additional options (1-8)
  for (let i = 1; i <= 8; i++) {
    const color = computedStyle.getPropertyValue(`--theme-chart-${i}`).trim();
    if (color && !colors.includes(color)) colors.push(color);
  }

  // Status colors as additional options
  const statusColors = ['success', 'warning', 'error', 'info'];
  for (const status of statusColors) {
    const color = computedStyle.getPropertyValue(`--theme-${status}`).trim();
    if (color && !colors.includes(color)) colors.push(color);
  }

  // Service colors as additional options
  const serviceColors = ['steam', 'epic', 'origin', 'blizzard', 'wsus', 'riot', 'xbox'];
  for (const service of serviceColors) {
    const color = computedStyle.getPropertyValue(`--theme-${service}`).trim();
    if (color && !colors.includes(color)) colors.push(color);
  }

  // Text muted for gray option
  const gray = computedStyle.getPropertyValue('--theme-text-muted').trim();
  if (gray && !colors.includes(gray)) colors.push(gray);

  // Return unique colors
  return [...new Set(colors)];
};

interface TagModalProps {
  tag?: Tag | null;
  initialName?: string;
  onClose: () => void;
  onSave: (tag: Tag) => void;
}

const TagModal: React.FC<TagModalProps> = ({
  tag,
  initialName = '',
  onClose,
  onSave
}) => {
  const { createTag, updateTag } = useTags();
  const [name, setName] = useState(tag?.name || initialName);
  const [color, setColor] = useState(tag?.color || '#3b82f6');
  const [description, setDescription] = useState(tag?.description || '');

  // Get theme colors dynamically
  const themeColors = useMemo(() => getThemeColors(), []);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const isEditing = !!tag;

  // Handle escape key
  useEffect(() => {
    const handleEscape = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        onClose();
      }
    };

    document.addEventListener('keydown', handleEscape);
    return () => document.removeEventListener('keydown', handleEscape);
  }, [onClose]);

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);

    if (!name.trim()) {
      setError('Tag name is required');
      return;
    }

    setSaving(true);
    try {
      let savedTag: Tag;
      if (isEditing) {
        savedTag = await updateTag(tag.id, { name: name.trim(), color, description: description.trim() || undefined });
      } else {
        savedTag = await createTag({ name: name.trim(), color, description: description.trim() || undefined });
      }
      onSave(savedTag);
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Failed to save tag';
      setError(message);
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4">
      {/* Backdrop */}
      <div
        className="absolute inset-0 bg-black/50 backdrop-blur-sm"
        onClick={onClose}
      />

      {/* Modal */}
      <div className="relative w-full max-w-md rounded-xl bg-[var(--theme-bg-secondary)] border border-[var(--theme-border-primary)] shadow-xl animate-slideUp">
        {/* Header */}
        <div className="flex items-center justify-between px-4 py-3 border-b border-[var(--theme-border-secondary)]">
          <h3 className="text-lg font-semibold text-[var(--theme-text-primary)]">
            {isEditing ? 'Edit Tag' : 'Create Tag'}
          </h3>
          <button
            onClick={onClose}
            className="p-1 rounded hover:bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-muted)] hover:text-[var(--theme-text-primary)] transition-colors"
          >
            <X className="w-5 h-5" />
          </button>
        </div>

        {/* Form */}
        <form onSubmit={handleSubmit} className="p-4 space-y-4">
          {/* Error message */}
          {error && (
            <div className="p-3 rounded-lg bg-[var(--theme-status-error)]/10 border border-[var(--theme-status-error)]/30 text-sm text-[var(--theme-status-error)]">
              {error}
            </div>
          )}

          {/* Name input */}
          <div>
            <label className="block text-sm font-medium text-[var(--theme-text-secondary)] mb-1">
              Name *
            </label>
            <input
              type="text"
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="e.g., Priority, Game Update"
              className="w-full px-3 py-2 rounded-lg border bg-[var(--theme-bg-primary)] text-[var(--theme-text-primary)] border-[var(--theme-border-primary)] focus:outline-none focus:ring-2 focus:ring-[var(--theme-primary)]/50 focus:border-[var(--theme-primary)]"
              autoFocus
            />
          </div>

          {/* Color picker */}
          <div>
            <label className="block text-sm font-medium text-[var(--theme-text-secondary)] mb-2">
              Color
            </label>
            <div className="flex flex-wrap gap-2">
              {themeColors.map((c) => (
                <button
                  key={c}
                  type="button"
                  onClick={() => setColor(c)}
                  className={`w-7 h-7 rounded-full transition-transform ${
                    color === c ? 'ring-2 ring-offset-2 ring-[var(--theme-primary)] scale-110' : 'hover:scale-105'
                  }`}
                  style={{
                    backgroundColor: c
                  }}
                />
              ))}
            </div>
            {/* Preview */}
            <div className="mt-3 flex items-center gap-2">
              <span className="text-sm text-[var(--theme-text-muted)]">Preview:</span>
              <span
                className="px-2 py-0.5 text-xs rounded-full font-medium"
                style={{
                  backgroundColor: `${color}20`,
                  color: color,
                  border: `1px solid ${color}40`
                }}
              >
                {name || 'Tag Name'}
              </span>
            </div>
          </div>

          {/* Description input */}
          <div>
            <label className="block text-sm font-medium text-[var(--theme-text-secondary)] mb-1">
              Description (optional)
            </label>
            <textarea
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              placeholder="What is this tag used for?"
              rows={2}
              className="w-full px-3 py-2 rounded-lg border bg-[var(--theme-bg-primary)] text-[var(--theme-text-primary)] border-[var(--theme-border-primary)] focus:outline-none focus:ring-2 focus:ring-[var(--theme-primary)]/50 focus:border-[var(--theme-primary)] resize-none"
            />
          </div>

          {/* Actions */}
          <div className="flex justify-end gap-2 pt-2">
            <Button
              type="button"
              onClick={onClose}
              color="gray"
              disabled={saving}
            >
              Cancel
            </Button>
            <Button
              type="submit"
              color="blue"
              disabled={saving || !name.trim()}
              leftSection={saving ? <Loader2 className="w-4 h-4 animate-spin" /> : undefined}
            >
              {saving ? 'Saving...' : isEditing ? 'Save Changes' : 'Create Tag'}
            </Button>
          </div>
        </form>
      </div>
    </div>
  );
};

export default TagModal;
