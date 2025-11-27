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
  onTouchStart: (cardKey: string) => void;
  onTouchEnd: () => void;
  onCardTap: (cardKey: string) => void;
}

interface UseDraggableCardsReturn {
  cardOrder: string[];
  setCardOrder: React.Dispatch<React.SetStateAction<string[]>>;
  draggedCard: string | null;
  dragOverCard: string | null;
  isDragMode: boolean;
  showDragHint: boolean;
  dragHandlers: DragHandlers;
  resetCardOrder: () => void;
  hideDragHint: () => void;
}

export const useDraggableCards = ({
  defaultOrder,
  storageKey,
  dragHintStorageKey
}: UseDraggableCardsOptions): UseDraggableCardsReturn => {
  const [draggedCard, setDraggedCard] = useState<string | null>(null);
  const [dragOverCard, setDragOverCard] = useState<string | null>(null);
  const [isDragMode, setIsDragMode] = useState(false);
  const [holdTimeout, setHoldTimeout] = useState<NodeJS.Timeout | null>(null);
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

  // Clean up holdTimeout on unmount to prevent memory leaks
  useEffect(() => {
    return () => {
      if (holdTimeout) {
        clearTimeout(holdTimeout);
      }
    };
  }, [holdTimeout]);

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
          newOrder.splice(draggedIndex, 1);
          newOrder.splice(targetIndex, 0, draggedCard);
          return newOrder;
        });
      }

      setDragOverCard(null);
      dragCounter.current = 0;
    },
    [draggedCard]
  );

  // Touch handlers for mobile
  const handleTouchStart = useCallback(
    (cardKey: string) => {
      const timeout = setTimeout(() => {
        // If we already have a selected card, swap them
        if (draggedCard && draggedCard !== cardKey) {
          // Perform the swap
          setCardOrder((prevOrder: string[]) => {
            const newOrder = [...prevOrder];
            const draggedIndex = newOrder.indexOf(draggedCard);
            const targetIndex = newOrder.indexOf(cardKey);
            newOrder.splice(draggedIndex, 1);
            newOrder.splice(targetIndex, 0, draggedCard);
            return newOrder;
          });

          // Add haptic feedback for successful swap
          if (navigator.vibrate) {
            navigator.vibrate([50, 50, 50]);
          }

          // Clear selection
          setDraggedCard(null);
          setIsDragMode(false);
        } else {
          // Select this card
          setIsDragMode(true);
          setDraggedCard(cardKey);
          // Add haptic feedback if available
          if (navigator.vibrate) {
            navigator.vibrate(50);
          }
        }
      }, 800); // 800ms hold to activate selection mode (longer to prevent accidental drags)
      setHoldTimeout(timeout);
    },
    [draggedCard]
  );

  const handleTouchEnd = useCallback(() => {
    if (holdTimeout) {
      clearTimeout(holdTimeout);
      setHoldTimeout(null);
    }
  }, [holdTimeout]);

  const handleCardTap = useCallback(
    (cardKey: string) => {
      if (isDragMode && draggedCard) {
        if (cardKey !== draggedCard) {
          // Swap the cards
          setCardOrder((prevOrder: string[]) => {
            const newOrder = [...prevOrder];
            const draggedIndex = newOrder.indexOf(draggedCard);
            const targetIndex = newOrder.indexOf(cardKey);
            newOrder.splice(draggedIndex, 1);
            newOrder.splice(targetIndex, 0, draggedCard);
            return newOrder;
          });

          // Add haptic feedback for successful swap
          if (navigator.vibrate) {
            navigator.vibrate([50, 50, 50]);
          }
        }

        // Clear selection
        setDraggedCard(null);
        setIsDragMode(false);
        setDragOverCard(null);
      }
    },
    [isDragMode, draggedCard]
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
    showDragHint,
    dragHandlers: {
      onDragStart: handleDragStart,
      onDragEnd: handleDragEnd,
      onDragOver: handleDragOver,
      onDragEnter: handleDragEnter,
      onDragLeave: handleDragLeave,
      onDrop: handleDrop,
      onTouchStart: handleTouchStart,
      onTouchEnd: handleTouchEnd,
      onCardTap: handleCardTap
    },
    resetCardOrder,
    hideDragHint
  };
};
