import React, { use } from 'react';

// Fetch version from API
const fetchVersion = async (): Promise<string> => {
  try {
    const response = await fetch('/api/version');
    if (response.ok) {
      const data = await response.json();
      return data.version;
    } else {
      return 'unknown';
    }
  } catch (error) {
    console.error('Failed to fetch version:', error);
    return 'unknown';
  }
};

// Cache the promise to avoid refetching on every render
let versionPromise: Promise<string> | null = null;

const getVersionPromise = () => {
  if (!versionPromise) {
    versionPromise = fetchVersion();
  }
  return versionPromise;
};

const Footer: React.FC = () => {
  const version = use(getVersionPromise());

  return (
    <footer className="py-4 text-center text-sm border-t bg-themed-nav border-themed-nav text-themed-secondary">
      <div className="container mx-auto px-4">
        <p>LANCache Manager v{version}</p>
      </div>
    </footer>
  );
};

export default Footer;
