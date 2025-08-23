export const formatBytes = (bytes) => {
  if (bytes === 0) return '0 B';
  const k = 1024;
  const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
};

export const formatDate = (date) => {
  return new Date(date).toLocaleString();
};

export const getServiceColor = (service) => {
  const colors = {
    steam: '#1b2838',
    epic: '#313131',
    origin: '#f56c2d',
    uplay: '#0070ff',
    blizzard: '#00aeff',
    wsus: '#0078d4',
    apple: '#555555',
    other: '#6b7280'
  };
  return colors[service] || colors.other;
};

export const getServiceIcon = (service) => {
  const icons = {
    steam: '🎮',
    epic: '🎯',
    origin: '🎲',
    uplay: '🎪',
    blizzard: '❄️',
    wsus: '🪟',
    apple: '🍎',
    other: '📦'
  };
  return icons[service] || icons.other;
};