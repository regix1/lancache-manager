import React, { useState, useRef, useEffect } from 'react';
import { Plus, Tag, Check, Loader2 } from 'lucide-react';
import { useTags } from '@contexts/TagContext';
import type { Tag as TagType } from '../../../types';

interface TagPickerProps {
  downloadId: number;
  existingTags: TagType[];
  onTagAdded?: (tag: TagType) => void;
  onTagRemoved?: (tag: TagType) => void;
  onCreateTag?: () => void;
}

const TagPicker: React.FC<TagPickerProps> = ({
  downloadId,
  existingTags,
  onTagAdded,
  onTagRemoved,
  onCreateTag
}) => {
  const { tags, addTagToDownload, removeTagFromDownload } = useTags();
  const [isOpen, setIsOpen] = useState(false);
  const [searchQuery, setSearchQuery] = useState('');
  const [loadingTagId, setLoadingTagId] = useState<number | null>(null);
  const dropdownRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  // Filter tags based on search
  const filteredTags = tags.filter(tag =>
    tag.name.toLowerCase().includes(searchQuery.toLowerCase())
  );

  // Check if tag is already applied
  const isTagApplied = (tagId: number) =>
    existingTags.some(t => t.id === tagId);

  // Handle click outside to close
  useEffect(() => {
    const handleClickOutside = (event: MouseEvent) => {
      if (dropdownRef.current && !dropdownRef.current.contains(event.target as Node)) {
        setIsOpen(false);
        setSearchQuery('');
      }
    };

    if (isOpen) {
      document.addEventListener('mousedown', handleClickOutside);
      return () => document.removeEventListener('mousedown', handleClickOutside);
    }
  }, [isOpen]);

  // Focus input when opened
  useEffect(() => {
    if (isOpen && inputRef.current) {
      inputRef.current.focus();
    }
  }, [isOpen]);

  const handleToggleTag = async (tag: TagType) => {
    setLoadingTagId(tag.id);
    try {
      if (isTagApplied(tag.id)) {
        await removeTagFromDownload(tag.id, downloadId);
        onTagRemoved?.(tag);
      } else {
        await addTagToDownload(tag.id, downloadId);
        onTagAdded?.(tag);
      }
    } catch (err) {
      console.error('Failed to toggle tag:', err);
    } finally {
      setLoadingTagId(null);
    }
  };

  return (
    <div className="relative" ref={dropdownRef}>
      {/* Trigger button */}
      <button
        onClick={() => setIsOpen(!isOpen)}
        className="p-1 rounded hover:bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-muted)] hover:text-[var(--theme-text-primary)] transition-colors"
        title="Add tag"
      >
        <Plus className="w-4 h-4" />
      </button>

      {/* Dropdown */}
      {isOpen && (
        <div
          className="absolute right-0 top-full mt-1 z-50 w-56 rounded-lg shadow-lg border bg-[var(--theme-bg-secondary)] border-[var(--theme-border-primary)] animate-slideDown"
        >
          {/* Search input */}
          <div className="p-2 border-b border-[var(--theme-border-secondary)]">
            <input
              ref={inputRef}
              type="text"
              placeholder="Search tags..."
              value={searchQuery}
              onChange={(e) => setSearchQuery(e.target.value)}
              className="w-full px-2 py-1.5 text-sm rounded border bg-[var(--theme-bg-primary)] text-[var(--theme-text-primary)] border-[var(--theme-border-primary)] focus:outline-none focus:ring-1 focus:ring-[var(--theme-primary)]"
            />
          </div>

          {/* Tags list */}
          <div className="max-h-48 overflow-y-auto p-1">
            {filteredTags.length === 0 ? (
              <div className="px-3 py-2 text-sm text-[var(--theme-text-muted)]">
                {searchQuery ? 'No tags found' : 'No tags created yet'}
              </div>
            ) : (
              filteredTags.map((tag) => {
                const applied = isTagApplied(tag.id);
                const loading = loadingTagId === tag.id;

                return (
                  <button
                    key={tag.id}
                    onClick={() => handleToggleTag(tag)}
                    disabled={loading}
                    className={`w-full flex items-center gap-2 px-2 py-1.5 rounded text-sm transition-colors ${
                      applied
                        ? 'bg-[var(--theme-primary)]/10'
                        : 'hover:bg-[var(--theme-bg-tertiary)]'
                    }`}
                  >
                    {/* Color dot */}
                    <span
                      className="w-3 h-3 rounded-full flex-shrink-0"
                      style={{ backgroundColor: tag.color }}
                    />

                    {/* Tag name */}
                    <span className="flex-1 text-left truncate text-[var(--theme-text-primary)]">
                      {tag.name}
                    </span>

                    {/* Status indicator */}
                    {loading ? (
                      <Loader2 className="w-4 h-4 animate-spin text-[var(--theme-primary)]" />
                    ) : applied ? (
                      <Check className="w-4 h-4 text-[var(--theme-primary)]" />
                    ) : null}
                  </button>
                );
              })
            )}
          </div>

          {/* Create new tag option */}
          {onCreateTag && (
            <div className="border-t border-[var(--theme-border-secondary)] p-1">
              <button
                onClick={() => {
                  setIsOpen(false);
                  onCreateTag();
                }}
                className="w-full flex items-center gap-2 px-2 py-1.5 rounded text-sm text-[var(--theme-primary)] hover:bg-[var(--theme-bg-tertiary)] transition-colors"
              >
                <Tag className="w-4 h-4" />
                <span>Create new tag{searchQuery ? `: "${searchQuery}"` : ''}</span>
              </button>
            </div>
          )}
        </div>
      )}
    </div>
  );
};

export default TagPicker;
