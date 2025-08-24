// Utility functions for formatting data

export const formatBytes = (bytes) => {
  // Handle null, undefined, or non-numeric values
  if (bytes === null || bytes === undefined || isNaN(bytes)) {
    return '0 B';
  }
  
  // Ensure bytes is a number
  bytes = Number(bytes);
  
  if (bytes === 0) return '0 B';
  
  const k = 1024;
  const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  
  return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
};

export const formatPercent = (value) => {
  // Handle null, undefined, or non-numeric values
  if (value === null || value === undefined || isNaN(value)) {
    return '0%';
  }
  
  // Ensure value is a number
  value = Number(value);
  
  // Clamp between 0 and 100
  value = Math.max(0, Math.min(100, value));
  
  return value.toFixed(1) + '%';
};

export const formatDateTime = (dateString) => {
  // Handle null or undefined
  if (!dateString) {
    return 'Unknown';
  }
  
  try {
    const date = new Date(dateString);
    
    // Check for invalid date
    if (isNaN(date.getTime())) {
      return 'Invalid date';
    }
    
    return date.toLocaleString();
  } catch (error) {
    console.error('Error formatting date:', dateString, error);
    return 'Invalid date';
  }
};

export const formatTime = (dateString) => {
  // Handle null or undefined
  if (!dateString) {
    return 'Unknown';
  }
  
  try {
    const date = new Date(dateString);
    
    // Check for invalid date
    if (isNaN(date.getTime())) {
      return 'Invalid time';
    }
    
    return date.toLocaleTimeString();
  } catch (error) {
    console.error('Error formatting time:', dateString, error);
    return 'Invalid time';
  }
};

export const formatDuration = (startTime, endTime) => {
  // Handle null or undefined values
  if (!startTime || !endTime) {
    return '0m';
  }
  
  try {
    const start = new Date(startTime);
    const end = new Date(endTime);
    
    // Check for invalid dates
    if (isNaN(start.getTime()) || isNaN(end.getTime())) {
      return '0m';
    }
    
    const diff = end - start;
    
    if (diff < 0) {
      return '0m';
    }
    
    const hours = Math.floor(diff / (1000 * 60 * 60));
    const minutes = Math.floor((diff % (1000 * 60 * 60)) / (1000 * 60));
    
    if (hours > 0) {
      return `${hours}h ${minutes}m`;
    }
    return `${minutes}m`;
  } catch (error) {
    console.error('Error calculating duration:', error);
    return '0m';
  }
};

export const formatNumber = (num) => {
  // Handle null, undefined, or non-numeric values
  if (num === null || num === undefined || isNaN(num)) {
    return '0';
  }
  
  // Ensure num is a number
  num = Number(num);
  
  return num.toLocaleString();
};