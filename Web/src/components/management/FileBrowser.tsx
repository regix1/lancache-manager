import React, { useState, useEffect } from 'react';
import {
  Folder,
  File,
  ChevronRight,
  ArrowLeft,
  Home,
  Loader2,
  Database,
  Search
} from 'lucide-react';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import ApiService from '@services/api.service';
import { formatDateTime } from '@utils/formatters';

interface FileSystemItem {
  name: string;
  path: string;
  isDirectory: boolean;
  size: number;
  lastModified: string;
  isAccessible: boolean;
}

interface DirectoryListing {
  currentPath: string;
  parentPath: string | null;
  items: FileSystemItem[];
}

interface FileBrowserProps {
  onSelectFile: (path: string) => void;
  isAuthenticated: boolean;
  mockMode: boolean;
}

const FileBrowser: React.FC<FileBrowserProps> = ({ onSelectFile, isAuthenticated, mockMode }) => {
  const [currentPath, setCurrentPath] = useState<string | null>(null);
  const [items, setItems] = useState<FileSystemItem[]>([]);
  const [parentPath, setParentPath] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [selectedFile, setSelectedFile] = useState<string | null>(null);
  const [searching, setSearching] = useState(false);
  const [searchResults, setSearchResults] = useState<FileSystemItem[]>([]);

  // Load common locations on mount
  useEffect(() => {
    if (!mockMode && isAuthenticated) {
      loadDirectory(null);
    }
  }, [mockMode, isAuthenticated]);

  const loadDirectory = async (path: string | null) => {
    setLoading(true);
    setError(null);
    setSearchResults([]);

    try {
      const queryParam = path ? `?path=${encodeURIComponent(path)}` : '';
      const res = await fetch(
        `/api/filebrowser/list${queryParam}`,
        ApiService.getFetchOptions({ method: 'GET' })
      );

      const result = await ApiService.handleResponse<DirectoryListing>(res);
      setCurrentPath(result.currentPath);
      setParentPath(result.parentPath);
      setItems(result.items);
    } catch (err: any) {
      setError(err.message || 'Failed to load directory');
    } finally {
      setLoading(false);
    }
  };

  const handleItemClick = (item: FileSystemItem) => {
    if (!item.isAccessible) {
      setError('Access denied to this location');
      return;
    }

    if (item.isDirectory) {
      loadDirectory(item.path);
    } else {
      setSelectedFile(item.path);
    }
  };

  const handleBack = () => {
    if (parentPath !== null) {
      loadDirectory(parentPath);
    }
  };

  const handleGoHome = () => {
    loadDirectory(null);
  };

  const handleSelectFile = () => {
    if (selectedFile) {
      onSelectFile(selectedFile);
    }
  };

  const handleSearch = async (searchPath: string) => {
    setSearching(true);
    setError(null);

    try {
      const res = await fetch(
        `/api/filebrowser/search?searchPath=${encodeURIComponent(searchPath)}`,
        ApiService.getFetchOptions({ method: 'GET' })
      );

      const result = await ApiService.handleResponse<{ results: FileSystemItem[] }>(res);
      setSearchResults(result.results);

      if (result.results.length === 0) {
        setError('No database files found in this location');
      }
    } catch (err: any) {
      setError(err.message || 'Search failed');
    } finally {
      setSearching(false);
    }
  };

  const formatSize = (bytes: number): string => {
    if (bytes === 0) return '-';
    const units = ['B', 'KB', 'MB', 'GB'];
    const i = Math.floor(Math.log(bytes) / Math.log(1024));
    return `${(bytes / Math.pow(1024, i)).toFixed(2)} ${units[i]}`;
  };

  const displayItems = searchResults.length > 0 ? searchResults : items;

  return (
    <Card>
      <div className="space-y-4">
        {/* Header */}
        <div className="flex items-center justify-between">
          <h4 className="text-md font-semibold text-themed-primary flex items-center gap-2">
            <Database className="w-4 h-4" />
            Browse Server Filesystem
          </h4>
          {currentPath && (
            <div className="flex items-center gap-2">
              <Button
                onClick={handleGoHome}
                size="xs"
                variant="default"
                leftSection={<Home className="w-3 h-3" />}
                disabled={loading || mockMode}
              >
                Home
              </Button>
              {currentPath !== '/' && parentPath !== null && (
                <Button
                  onClick={handleBack}
                  size="xs"
                  variant="default"
                  leftSection={<ArrowLeft className="w-3 h-3" />}
                  disabled={loading || mockMode}
                >
                  Back
                </Button>
              )}
            </div>
          )}
        </div>

        {/* Current Path & Search */}
        {currentPath && (
          <div className="space-y-2">
            <div className="flex items-center gap-2 text-sm text-themed-secondary">
              <span className="font-mono bg-themed-tertiary px-2 py-1 rounded break-all">
                {currentPath}
              </span>
            </div>
            {currentPath !== '/' && (
              <Button
                onClick={() => handleSearch(currentPath)}
                size="sm"
                variant="default"
                leftSection={searching ? <Loader2 className="w-4 h-4 animate-spin" /> : <Search className="w-4 h-4" />}
                disabled={searching || loading || mockMode}
                fullWidth
              >
                {searching ? 'Searching...' : 'Search for .db files in subdirectories'}
              </Button>
            )}
          </div>
        )}

        {/* Error */}
        {error && (
          <Alert color="red">
            <span className="text-sm">{error}</span>
          </Alert>
        )}

        {/* Loading */}
        {loading && (
          <div className="flex items-center justify-center py-8">
            <Loader2 className="w-6 h-6 animate-spin text-themed-accent" />
          </div>
        )}

        {/* Items List */}
        {!loading && displayItems.length > 0 && (
          <div className="space-y-1 max-h-96 overflow-y-auto rounded-lg border-themed-secondary">
            {displayItems.map((item, index) => (
              <button
                key={index}
                onClick={() => handleItemClick(item)}
                disabled={!item.isAccessible}
                className={`w-full px-3 py-2 flex items-center gap-3 transition-colors text-left
                  ${!item.isAccessible ? 'opacity-50 cursor-not-allowed' : 'hover:bg-themed-hover cursor-pointer'}
                  ${selectedFile === item.path ? 'bg-themed-accent-subtle' : ''}
                `}
              >
                {/* Icon */}
                <div className="flex-shrink-0">
                  {item.isDirectory ? (
                    <Folder className="w-5 h-5 text-themed-accent" />
                  ) : (
                    <File className="w-5 h-5 text-themed-secondary" />
                  )}
                </div>

                {/* Name */}
                <div className="flex-1 min-w-0">
                  <div className="font-medium text-themed-primary truncate">{item.name}</div>
                  {!item.isDirectory && (
                    <div className="text-xs text-themed-muted">
                      {formatSize(item.size)} • {formatDateTime(item.lastModified)}
                    </div>
                  )}
                </div>

                {/* Arrow for directories */}
                {item.isDirectory && item.isAccessible && (
                  <ChevronRight className="w-4 h-4 text-themed-muted flex-shrink-0" />
                )}
              </button>
            ))}
          </div>
        )}

        {/* Empty State */}
        {!loading && displayItems.length === 0 && !error && currentPath && (
          <div className="text-center py-8 text-themed-muted">
            <p>No directories or .db files found</p>
          </div>
        )}

        {/* Selected File Display */}
        {selectedFile && (
          <Alert color="blue">
            <div className="space-y-2">
              <p className="text-sm font-medium">Selected database file:</p>
              <p className="text-xs font-mono bg-themed-tertiary px-2 py-1 rounded break-all">
                {selectedFile}
              </p>
              <Button
                onClick={handleSelectFile}
                size="sm"
                variant="filled"
                color="green"
                fullWidth
              >
                Use This Database
              </Button>
            </div>
          </Alert>
        )}

        {/* Search Results Info */}
        {searchResults.length > 0 && (
          <Alert color="blue">
            <div className="flex items-center justify-between">
              <span className="text-sm">Found {searchResults.length} database file(s)</span>
              <Button
                onClick={() => setSearchResults([])}
                size="xs"
                variant="default"
              >
                Clear Search
              </Button>
            </div>
          </Alert>
        )}
      </div>
    </Card>
  );
};

export default FileBrowser;
