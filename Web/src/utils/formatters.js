import { format, formatDistanceToNow } from 'date-fns';

export const formatBytes = (bytes) => {
  if (!bytes || bytes === 0) return '0 B';
  const k = 1024;
  const sizes = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return `${parseFloat((bytes / Math.pow(k, i)).toFixed(2))} ${sizes[i]}`;
};

export const formatDate = (date) => {
  if (!date) return 'Never';
  try {
    const d = new Date(date);
    const now = new Date();
    const diffInHours = (now - d) / (1000 * 60 * 60);
    
    if (diffInHours < 1) {
      return formatDistanceToNow(d, { addSuffix: true });
    } else if (diffInHours < 24) {
      return format(d, 'HH:mm');
    } else if (diffInHours < 168) { // 7 days
      return format(d, 'EEE HH:mm');
    } else {
      return format(d, 'MMM dd, yyyy');
    }
  } catch {
    return 'Invalid date';
  }
};

export const getServiceColor = (service) => {
  const colors = {
    steam: '#1b2838',
    epic: '#313131',
    origin: '#f56c2d',
    uplay: '#0070ff',
    blizzard: '#00aeff',
    riot: '#d32936',
    wsus: '#0078d4',
    apple: '#555555',
    xboxlive: '#107c10',
    playstation: '#003791',
    nintendo: '#e60012',
    other: '#6b7280'
  };
  return colors[service?.toLowerCase()] || colors.other;
};

export const getServiceIcon = (service) => {
  const icons = {
    steam: '🎮',
    epic: '🎯',
    origin: '🎲',
    uplay: '🎪',
    blizzard: '❄️',
    riot: '⚔️',
    wsus: '🪟',
    apple: '🍎',
    xboxlive: '🎮',
    playstation: '🎮',
    nintendo: '🎮',
    other: '📦'
  };
  return icons[service?.toLowerCase()] || icons.other;
};

export const getServiceName = (service) => {
  const names = {
    steam: 'Steam',
    epic: 'Epic Games',
    origin: 'Origin/EA',
    uplay: 'Ubisoft Connect',
    blizzard: 'Battle.net',
    riot: 'Riot Games',
    wsus: 'Windows Update',
    apple: 'Apple',
    xboxlive: 'Xbox Live',
    playstation: 'PlayStation',
    nintendo: 'Nintendo',
    other: 'Other'
  };
  return names[service?.toLowerCase()] || service?.toUpperCase() || 'Unknown';
};