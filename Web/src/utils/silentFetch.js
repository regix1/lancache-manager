// utils/silentFetch.js

/**
 * Wrapper around fetch that silently handles 404s
 * Use this for operations where 404 is an expected response
 */
export const silentFetch = async (url, options = {}) => {
  try {
    const response = await fetch(url, options);
    
    // Return the response object even for 404s
    // Let the caller decide how to handle it
    return response;
  } catch (error) {
    // Only log actual network errors, not HTTP errors
    if (error.name === 'TypeError' || error.name === 'NetworkError') {
      console.error('Network error:', error);
    }
    throw error;
  }
};

/**
 * Fetch wrapper that treats 404 as null (for state operations)
 */
export const fetchStateOrNull = async (url, options = {}) => {
  try {
    const response = await silentFetch(url, options);
    
    if (response.status === 404) {
      return { ok: true, data: null, status: 404 };
    }
    
    if (!response.ok) {
      return { 
        ok: false, 
        error: `HTTP ${response.status}: ${response.statusText}`,
        status: response.status 
      };
    }
    
    const data = await response.json();
    return { ok: true, data, status: response.status };
  } catch (error) {
    return { 
      ok: false, 
      error: error.message,
      status: 0 
    };
  }
};