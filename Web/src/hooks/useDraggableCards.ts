import { useState, useCallback, useEffect, useRef } from 'react';
import { storage } from '@utils/storage';

interface UseDraggableCardsOptions {
  defaultOrder: string[];
  storageKey: string;
  dragHintStorageKey?: string;
}

interface DragHandlers {
  onDragStart: (e: React.DragEvent, cardKey: string) => void;
  onDragEnd: () => void;
  onDragOver: (e: React.DragEvent) => void;
  onDragEnter: (e: React.DragEvent, cardKey: string) => void;
  onDragLeave: () => void;
  onDrop: (e: React.DragEvent, targetCardKey: string) => void;
  onCardTap: (cardKey: string) => void;
}

interface UseDraggableCardsReturn {
  cardOrder: string[];
  setCardOrder: React.Dispatch<React.SetStateAction<string[]>>;
  draggedCard: string | null;
  dragOverCard: string | null;
  isDragMode: boolean;
  isEditMode: boolean;
  showDragHint: boolean;
  dragHandlers: DragHandlers;
  resetCardOrder: () => void;
  hideDragHint: () => void;
  toggleEditMode: () => void;
  exitEditMode: () => void;
}

export const useDraggableCards = ({
  defaultOrder,
  storageKey,
  dragHintStorageKey
}: UseDraggableCardsOptions): UseDraggableCardsReturn => {
  const [draggedCard, setDraggedCard] = useState<string | null>(null);
  const [dragOverCard, setDragOverCard] = useState<string | null>(null);
  const [isDragMode, setIsDragMode] = useState(false);
  const [isEditMode, setIsEditMode] = useState(false);
  const [showDragHint, setShowDragHint] = useState(() => {
    if (!dragHintStorageKey) return false;
    return storage.getItem(dragHintStorageKey) !== 'true';
  });
  const dragCounter = useRef(0);

  // Initialize card order from storage or use default
  const [cardOrder, setCardOrder] = useState<string[]>(() => {
    const saved = storage.getItem(storageKey);
    if (saved) {
      try {
        const order = JSON.parse(saved);
        const hasAllCards = defaultOrder.every((card) => order.includes(card));
        if (hasAllCards) {
          return order;
        }
      } catch (e) {
        console.error('Failed to parse card order:', e);
      }
    }
    return defaultOrder;
  });

  // Persist card order to storage
  useEffect(() => {
    storage.setItem(storageKey, JSON.stringify(cardOrder));
  }, [cardOrder, storageKey]);

  // Desktop drag handlers
  const handleDragStart = useCallback((e: React.DragEvent, cardKey: string) => {
    setDraggedCard(cardKey);
    e.dataTransfer.effectAllowed = 'move';
  }, []);

  const handleDragEnd = useCallback(() => {
    setDraggedCard(null);
    setDragOverCard(null);
    setIsDragMode(false);
    dragCounter.current = 0;
  }, []);

  const handleDragOver = useCallback((e: React.DragEvent) => {
    e.preventDefault();
    e.dataTransfer.dropEffect = 'move';
  }, []);

  const handleDragEnter = useCallback(
    (e: React.DragEvent, cardKey: string) => {
      e.preventDefault();
      dragCounter.current++;
      if (cardKey && cardKey !== draggedCard) {
        setDragOverCard(cardKey);
      }
    },
    [draggedCard]
  );

  const handleDragLeave = useCallback(() => {
    dragCounter.current--;
    if (dragCounter.current === 0) {
      setDragOverCard(null);
    }
  }, []);

  const handleDrop = useCallback(
    (e: React.DragEvent, targetCardKey: string) => {
      e.preventDefault();
      e.stopPropagation();

      if (draggedCard && targetCardKey && draggedCard !== targetCardKey) {
        setCardOrder((prevOrder: string[]) => {
          const newOrder = [...prevOrder];
          const draggedIndex = newOrder.indexOf(draggedCard);
          const targetIndex = newOrder.indexOf(targetCardKey);
          // True swap - exchange positions directly
          [newOrder[draggedIndex], newOrder[targetIndex]] = [newOrder[targetIndex], newOrder[draggedIndex]];
          return newOrder;
        });
      }

      setDragOverCard(null);
      dragCounter.current = 0;
    },
    [draggedCard]
  );

  // Edit mode toggle for mobile - clean, intentional UX
  const toggleEditMode = useCallback(() => {
    setIsEditMode(prev => {
      const newState = !prev;
      if (!newState) {
        // Exiting edit mode - clear selection
        setDraggedCard(null);
        setIsDragMode(false);
        setDragOverCard(null);
      }
      // Haptic feedback on toggle
      if (navigator.vibrate) {
        navigator.vibrate(newState ? 50 : [25, 25]);
      }
      return newState;
    });
  }, []);

  const exitEditMode = useCallback(() => {
    setIsEditMode(false);
    setDraggedCard(null);
    setIsDragMode(false);
    setDragOverCard(null);
    if (navigator.vibrate) {
      navigator.vibrate([25, 25]);
    }
  }, []);

  // Card tap handler - works in edit mode for selection/swap
  const handleCardTap = useCallback(
    (cardKey: string) => {
      // Only handle taps in edit mode on mobile
      if (!isEditMode) return;

      if (draggedCard) {
        // We have a selected card - perform swap or deselect
        if (cardKey !== draggedCard) {
          // Swap the cards
          setCardOrder((prevOrder: string[]) => {
            const newOrder = [...prevOrder];
            const draggedIndex = newOrder.indexOf(draggedCard);
            const targetIndex = newOrder.indexOf(cardKey);
            [newOrder[draggedIndex], newOrder[targetIndex]] = [newOrder[targetIndex], newOrder[draggedIndex]];
            return newOrder;
          });

          // Haptic feedback for successful swap
          if (navigator.vibrate) {
            navigator.vibrate([50, 50, 50]);
          }
        }

        // Clear selection after swap or tap on same card
        setDraggedCard(null);
        setIsDragMode(false);
        setDragOverCard(null);
      } else {
        // No card selected - select this one
        setDraggedCard(cardKey);
        setIsDragMode(true);
        // Haptic feedback for selection
        if (navigator.vibrate) {
          navigator.vibrate(50);
        }
      }
    },
    [isEditMode, draggedCard]
  );

  // Utility functions
  const resetCardOrder = useCallback(() => {
    setCardOrder(defaultOrder);
  }, [defaultOrder]);

  const hideDragHint = useCallback(() => {
    if (!dragHintStorageKey) return;
    setShowDragHint(false);
    storage.setItem(dragHintStorageKey, 'true');
  }, [dragHintStorageKey]);

  return {
    cardOrder,
    setCardOrder,
    draggedCard,
    dragOverCard,
    isDragMode,
    isEditMode,
    showDragHint,
    dragHandlers: {
      onDragStart: handleDragStart,
      onDragEnd: handleDragEnd,
      onDragOver: handleDragOver,
      onDragEnter: handleDragEnter,
      onDragLeave: handleDragLeave,
      onDrop: handleDrop,
      onCardTap: handleCardTap
    },
    resetCardOrder,
    hideDragHint,
    toggleEditMode,
    exitEditMode
  };
};
