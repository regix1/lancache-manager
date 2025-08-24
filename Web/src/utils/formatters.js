export const formatBytes = (bytes) => {
  if (bytes === 0) return '0 B';
  const k = 1024;
  const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
};

export const formatPercent = (value) => {
  return `${value.toFixed(1)}%`;
};

export const formatDateTime = (dateString) => {
  return new Date(dateString).toLocaleString();
};

export const formatTime = (dateString) => {
  return new Date(dateString).toLocaleTimeString();
};