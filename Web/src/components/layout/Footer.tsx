import React, { useEffect, useState } from 'react';

const Footer: React.FC = () => {
  const [version, setVersion] = useState<string>('loading...');

  useEffect(() => {
    const fetchVersion = async () => {
      try {
        const response = await fetch('/api/version');
        if (response.ok) {
          const data = await response.json();
          setVersion(data.version);
        } else {
          setVersion('unknown');
        }
      } catch (error) {
        console.error('Failed to fetch version:', error);
        setVersion('unknown');
      }
    };

    fetchVersion();
  }, []);

  return (
    <footer
      className="py-4 text-center text-sm border-t"
      style={{
        backgroundColor: 'var(--theme-nav-bg)',
        borderColor: 'var(--theme-nav-border)',
        color: 'var(--theme-text-secondary)'
      }}
    >
      <div className="container mx-auto px-4">
        <p>LANCache Manager v{version}</p>
      </div>
    </footer>
  );
};

export default Footer;
