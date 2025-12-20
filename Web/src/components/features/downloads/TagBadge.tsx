import React from 'react';
import { X } from 'lucide-react';
import type { TagSummary } from '../../../types';

interface TagBadgeProps {
  tag: TagSummary;
  onClick?: () => void;
  onRemove?: () => void;
  size?: 'sm' | 'md';
  showRemove?: boolean;
}

const TagBadge: React.FC<TagBadgeProps> = ({
  tag,
  onClick,
  onRemove,
  size = 'sm',
  showRemove = false
}) => {
  const handleRemoveClick = (e: React.MouseEvent) => {
    e.stopPropagation();
    onRemove?.();
  };

  const sizeClasses = size === 'sm'
    ? 'px-1.5 py-0.5 text-[10px]'
    : 'px-2 py-1 text-xs';

  return (
    <span
      className={`inline-flex items-center gap-1 rounded-full font-medium transition-all ${sizeClasses} ${onClick ? 'cursor-pointer hover:opacity-80' : ''}`}
      style={{
        backgroundColor: `${tag.color}20`,
        color: tag.color,
        border: `1px solid ${tag.color}40`
      }}
      onClick={onClick}
      title={tag.description || tag.name}
    >
      <span className="truncate max-w-[100px]">{tag.name}</span>
      {showRemove && onRemove && (
        <button
          onClick={handleRemoveClick}
          className="hover:bg-black/10 rounded-full p-0.5 -mr-0.5"
        >
          <X className="w-3 h-3" />
        </button>
      )}
    </span>
  );
};

export default TagBadge;
