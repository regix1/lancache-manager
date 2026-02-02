import React, { useState, useRef, useEffect, useCallback } from 'react';
import { createPortal } from 'react-dom';
import { Github, ExternalLink, Heart, ChevronRight } from 'lucide-react';
import { Tooltip } from '@components/ui/Tooltip';

interface GitHubRepo {
  name: string;
  url: string;
  description: string;
  shortName: string;
  type: 'installable' | 'dependency';
  isFork?: boolean;
}

const INSTALLABLE_REPOS: GitHubRepo[] = [
  {
    name: 'LANcache Manager',
    shortName: 'lancache-manager',
    url: 'https://github.com/regix1/lancache-manager',
    description: 'A powerful GUI for managing your gaming LAN cache with real-time monitoring and analytics',
    type: 'installable'
  },
  {
    name: 'Monolithic',
    shortName: 'monolithic',
    url: 'https://github.com/regix1/monolithic',
    description: 'Enhanced fork with improved performance and additional features for LAN caching',
    type: 'installable',
    isFork: true
  }
];

const DEPENDENCY_REPOS: GitHubRepo[] = [
  {
    name: 'LANcache Pics',
    shortName: 'lancache-pics',
    url: 'https://github.com/regix1/lancache-pics',
    description: 'Game artwork repository for mapping Steam depot downloads to game icons',
    type: 'dependency'
  },
  {
    name: 'Steam Prefill Daemon',
    shortName: 'steam-prefill-daemon',
    url: 'https://github.com/regix1/steam-prefill-daemon',
    description: 'Background daemon for scheduled Steam game prefilling and cache warming',
    type: 'dependency',
    isFork: true
  }
];

const DONATION_URL = 'https://buymeacoffee.com/regix';
const GITHUB_PROFILE = 'https://github.com/regix1';

interface GitHubProjectsDropdownProps {
  iconOnly?: boolean;
}

const GitHubProjectsDropdown: React.FC<GitHubProjectsDropdownProps> = ({ iconOnly = false }) => {
  const [isOpen, setIsOpen] = useState(false);
  const [hoveredIndex, setHoveredIndex] = useState<number | null>(null);
  const [position, setPosition] = useState<{ top: number; left: number; width: number } | null>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const dropdownRef = useRef<HTMLDivElement>(null);

  const updatePosition = useCallback(() => {
    if (triggerRef.current) {
      const rect = triggerRef.current.getBoundingClientRect();
      const dropdownWidth = 320;
      let left = rect.right - dropdownWidth;
      
      // Ensure dropdown doesn't go off-screen on the left
      if (left < 8) {
        left = 8;
      }
      
      // Ensure dropdown doesn't go off-screen on the right
      if (left + dropdownWidth > window.innerWidth - 8) {
        left = window.innerWidth - dropdownWidth - 8;
      }

      setPosition({
        top: rect.bottom + 4,
        left,
        width: dropdownWidth
      });
    }
  }, []);

  useEffect(() => {
    if (isOpen) {
      updatePosition();
      window.addEventListener('resize', updatePosition);
      window.addEventListener('scroll', updatePosition, true);
      return () => {
        window.removeEventListener('resize', updatePosition);
        window.removeEventListener('scroll', updatePosition, true);
      };
    }
  }, [isOpen, updatePosition]);

  useEffect(() => {
    const handleClickOutside = (event: MouseEvent) => {
      if (
        dropdownRef.current &&
        !dropdownRef.current.contains(event.target as Node) &&
        triggerRef.current &&
        !triggerRef.current.contains(event.target as Node)
      ) {
        setIsOpen(false);
      }
    };

    const handleEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        setIsOpen(false);
      }
    };

    if (isOpen) {
      document.addEventListener('mousedown', handleClickOutside);
      document.addEventListener('keydown', handleEscape);
      return () => {
        document.removeEventListener('mousedown', handleClickOutside);
        document.removeEventListener('keydown', handleEscape);
      };
    }
  }, [isOpen]);

  const handleToggle = () => {
    setIsOpen((prev) => !prev);
    setHoveredIndex(null);
  };

  const handleRepoClick = (url: string) => {
    window.open(url, '_blank', 'noopener,noreferrer');
    setIsOpen(false);
  };

  const triggerContent = iconOnly ? (
    <Github size={18} className="github-icon-spin flex-shrink-0 text-[var(--theme-primary)]" />
  ) : (
    <>
      <Github size={16} className="github-icon-spin flex-shrink-0 text-[var(--theme-primary)]" />
      <span className="hidden sm:inline">Projects</span>
    </>
  );

  const dropdown = isOpen && position && createPortal(
    <div
      ref={dropdownRef}
      className="github-dropdown-container"
      style={{
        position: 'fixed',
        top: position.top,
        left: position.left,
        width: position.width,
        zIndex: 85,
        animation: 'dropdownSlideDown 0.15s cubic-bezier(0.16, 1, 0.3, 1)'
      }}
    >
      <div className="github-dropdown themed-border-radius border border-themed-primary bg-themed-secondary shadow-[0_10px_25px_-5px_rgba(0,0,0,0.3),0_8px_10px_-6px_rgba(0,0,0,0.2)]">
        {/* Header */}
        <div className="github-dropdown-header">
          <div className="github-dropdown-header-content">
            <Github size={18} />
            <span>My Projects</span>
          </div>
          <a
            href={GITHUB_PROFILE}
            target="_blank"
            rel="noopener noreferrer"
            className="github-profile-link"
            onClick={(e) => e.stopPropagation()}
          >
            <span>@regix1</span>
            <ExternalLink size={12} />
          </a>
        </div>

        {/* Installable Projects */}
        <div className="github-dropdown-section">
          <div className="github-section-header">
            <span className="github-section-label installable">Installable</span>
            <span className="github-section-hint">Ready to use</span>
          </div>
          <div className="github-dropdown-repos">
            {INSTALLABLE_REPOS.map((repo, index) => (
              <div
                key={repo.shortName}
                className={`github-repo-item installable ${hoveredIndex === index ? 'hovered' : ''}`}
                style={{ animationDelay: `${index * 50}ms` }}
                onMouseEnter={() => setHoveredIndex(index)}
                onMouseLeave={() => setHoveredIndex(null)}
                onClick={() => handleRepoClick(repo.url)}
                role="button"
                tabIndex={0}
                onKeyDown={(e) => {
                  if (e.key === 'Enter' || e.key === ' ') {
                    handleRepoClick(repo.url);
                  }
                }}
              >
                {repo.isFork && <span className="github-fork-pill">Fork</span>}
                <div className="github-repo-main">
                  <div className="github-repo-icon">
                    <Github size={16} />
                  </div>
                  <div className="github-repo-info">
                    <span className="github-repo-name">{repo.name}</span>
                    <span className="github-repo-short">/{repo.shortName}</span>
                  </div>
                  <div className="github-repo-actions">
                    <ChevronRight size={14} className="github-repo-chevron" />
                  </div>
                </div>
                
                {/* Description - slides in on hover */}
                <div className="github-repo-description">
                  <p>{repo.description}</p>
                </div>
              </div>
            ))}
          </div>
        </div>

        {/* Dependencies */}
        <div className="github-dropdown-section">
          <div className="github-section-header">
            <span className="github-section-label dependency">Dependencies</span>
            <span className="github-section-hint">Used by LANcache Manager</span>
          </div>
          <div className="github-dropdown-repos">
            {DEPENDENCY_REPOS.map((repo, index) => (
              <div
                key={repo.shortName}
                className={`github-repo-item dependency ${hoveredIndex === index + INSTALLABLE_REPOS.length ? 'hovered' : ''}`}
                style={{ animationDelay: `${(index + INSTALLABLE_REPOS.length) * 50}ms` }}
                onMouseEnter={() => setHoveredIndex(index + INSTALLABLE_REPOS.length)}
                onMouseLeave={() => setHoveredIndex(null)}
                onClick={() => handleRepoClick(repo.url)}
                role="button"
                tabIndex={0}
                onKeyDown={(e) => {
                  if (e.key === 'Enter' || e.key === ' ') {
                    handleRepoClick(repo.url);
                  }
                }}
              >
                {repo.isFork && <span className="github-fork-pill">Fork</span>}
                <div className="github-repo-main">
                  <div className="github-repo-icon">
                    <Github size={16} />
                  </div>
                  <div className="github-repo-info">
                    <span className="github-repo-name">{repo.name}</span>
                    <span className="github-repo-short">/{repo.shortName}</span>
                  </div>
                  <div className="github-repo-actions">
                    <ChevronRight size={14} className="github-repo-chevron" />
                  </div>
                </div>
                
                {/* Description - slides in on hover */}
                <div className="github-repo-description">
                  <p>{repo.description}</p>
                </div>
              </div>
            ))}
          </div>
        </div>

        {/* Divider */}
        <div className="github-dropdown-divider" />

        {/* Donation Button */}
        <a
          href={DONATION_URL}
          target="_blank"
          rel="noopener noreferrer"
          className="github-donation-btn"
          onClick={(e) => e.stopPropagation()}
        >
          <div className="github-donation-icon">
            <Heart size={16} className="heart-pulse" />
          </div>
          <div className="github-donation-text">
            <span className="github-donation-title">Support Development</span>
            <span className="github-donation-subtitle">Buy me a coffee</span>
          </div>
          <ExternalLink size={14} className="github-donation-external" />
        </a>
      </div>
    </div>,
    document.body
  );

  return (
    <>
      <Tooltip content="GitHub Projects & Support">
        <button
          ref={triggerRef}
          onClick={handleToggle}
          className={`ed-trigger github-trigger px-3 py-2 themed-border-radius border text-left flex items-center text-sm themed-card text-themed-primary ${
            isOpen ? 'border-themed-focus' : 'border-themed-primary'
          } ${iconOnly ? 'justify-center' : 'gap-1.5'} cursor-pointer`}
          aria-label="GitHub Projects"
          aria-expanded={isOpen}
          aria-haspopup="true"
        >
          {triggerContent}
        </button>
      </Tooltip>
      {dropdown}
    </>
  );
};

export default GitHubProjectsDropdown;
