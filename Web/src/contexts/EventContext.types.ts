import { createContext } from 'react';
import type {
  Event,
  CreateEventRequest,
  UpdateEventRequest,
  EventFilterMode,
  EventDataStackMode
} from '../types';

interface EventContextType {
  // Event data
  events: Event[];
  activeEvents: Event[];
  selectedEventId: number | null;
  selectedEvent: Event | null;

  // Filter settings
  filterMode: EventFilterMode;
  dataStackMode: EventDataStackMode;

  // Loading/error states
  loading: boolean;
  error: string | null;

  // Actions
  setSelectedEventId: (id: number | null) => void;
  setFilterMode: (mode: EventFilterMode) => void;
  setDataStackMode: (mode: EventDataStackMode) => void;

  // CRUD operations
  createEvent: (data: CreateEventRequest) => Promise<Event>;
  updateEvent: (id: number, data: UpdateEventRequest) => Promise<Event>;
  deleteEvent: (id: number) => Promise<void>;
  refreshEvents: () => Promise<void>;
}

export const EventContext = createContext<EventContextType | undefined>(undefined);
