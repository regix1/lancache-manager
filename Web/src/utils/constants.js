const getApiUrl = () => {
  // If we have an environment variable set, use it (for development)
  if (import.meta.env.VITE_API_URL) {
    return import.meta.env.VITE_API_URL;
  }
  
  // Since the frontend is served by the same container as the API,
  // use the same origin (empty string means same host/port/protocol)
  // This automatically works with whatever port is mapped in docker-compose
  return '';
};

export const API_BASE = `${getApiUrl()}/api`;

export const REFRESH_INTERVAL = 5000;
export const MOCK_UPDATE_INTERVAL = 3000;
export const SERVICES = ['steam', 'epic', 'origin', 'blizzard', 'wsus', 'riot'];
export const CHART_COLORS = ['#3B82F6', '#10B981', '#F59E0B', '#EF4444', '#8B5CF6', '#EC4899'];
export const COLOR_CLASSES = {
  blue: 'from-blue-500 to-blue-600',
  green: 'from-green-500 to-green-600',
  purple: 'from-purple-500 to-purple-600',
  yellow: 'from-yellow-500 to-yellow-600'
};