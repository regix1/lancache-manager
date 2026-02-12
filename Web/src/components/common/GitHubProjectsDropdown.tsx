import React, { useState, useRef, useEffect, useCallback } from 'react';
import { createPortal } from 'react-dom';
import { Github, ExternalLink, Heart, ChevronRight } from 'lucide-react';
import { Tooltip } from '@components/ui/Tooltip';

// Custom event for firework explosion - other components can listen to this
const FIREWORK_EXPLOSION_EVENT = 'catFireworkExplosion';

// Helper to get firework colors from current theme
function getFireworkColors(): string[] {
  // Force style recalculation
  document.body.offsetHeight;

  const root = document.documentElement;
  const computedStyle = getComputedStyle(root);

  const colors: string[] = [];
  for (let i = 1; i <= 8; i++) {
    const color = computedStyle.getPropertyValue(`--theme-firework-${i}`).trim();
    if (color) {
      colors.push(color);
    }
  }

  // If no colors found from computed style, try reading from theme style element
  if (colors.length === 0) {
    const themeStyle = document.getElementById('lancache-theme');
    if (themeStyle?.textContent) {
      for (let i = 1; i <= 8; i++) {
        const match = themeStyle.textContent.match(new RegExp(`--theme-firework-${i}:\\s*(#[a-fA-F0-9]{6}|#[a-fA-F0-9]{3})`));
        if (match?.[1]) {
          colors.push(match[1].trim());
        }
      }
    }
  }

  // Final fallback: use theme primary color and generate variations
  if (colors.length === 0) {
    const primaryColor = computedStyle.getPropertyValue('--theme-primary').trim();
    if (primaryColor) {
      // Return primary color with white for contrast
      colors.push(primaryColor, '#ffffff', primaryColor, '#ffffff');
    }
  }

  // Debug log - remove after testing

  return colors;
}

/**
 * Creates a DOM-based particle burst effect that simulates falling firework sparks.
 * Particles spread outward in a hemisphere (downward-biased) and fall with gravity.
 *
 * @param x - The x coordinate in pixels (screen position)
 * @param y - The y coordinate in pixels (screen position)
 */
function createParticleBurst(x: number, y: number): void {
  const particleCount = 30;
  const particles: Array<{
    element: HTMLDivElement;
    x: number;
    y: number;
    vx: number;
    vy: number;
    lifetime: number;
    maxLifetime: number;
    color: string;
    size: number;
  }> = [];

  const colors = getFireworkColors();
  const container = document.createElement('div');
  container.className = 'firework-particle-container';
  document.body.appendChild(container);

  // Create particles
  for (let i = 0; i < particleCount; i++) {
    const particle = document.createElement('div');
    particle.className = 'firework-particle';

    // Full 360° circle burst like a real firework explosion
    const angle = Math.random() * Math.PI * 2;

    // Random initial velocity
    const velocity = 40 + Math.random() * 60;
    const vx = Math.cos(angle) * velocity;
    const vy = Math.sin(angle) * velocity;

    // Random color from theme
    const color = colors[Math.floor(Math.random() * colors.length)] || '#ffffff';

    // Random size (mostly 3px, some 2px for variety)
    const size = Math.random() > 0.3 ? 3 : 2;

    particle.style.backgroundColor = color;
    particle.style.width = `${size}px`;
    particle.style.height = `${size}px`;
    particle.style.left = `${x}px`;
    particle.style.top = `${y}px`;

    container.appendChild(particle);

    particles.push({
      element: particle,
      x,
      y,
      vx,
      vy,
      lifetime: 0,
      maxLifetime: 1500, // 1.5 seconds
      color,
      size
    });
  }

  // Animate particles
  const startTime = performance.now();
  const gravity = 0.15;

  function animate(currentTime: number) {
    const elapsed = currentTime - startTime;
    let allExpired = true;

    particles.forEach(p => {
      if (p.lifetime < p.maxLifetime) {
        allExpired = false;

        // Update velocity with gravity
        p.vy += gravity;

        // Update position
        p.x += p.vx * 0.016; // Assume ~60fps
        p.y += p.vy * 0.016;

        // Update lifetime
        p.lifetime = elapsed;

        // Calculate opacity (fade out)
        const opacity = Math.max(0, 1 - (p.lifetime / p.maxLifetime));

        // Update DOM
        p.element.style.left = `${p.x}px`;
        p.element.style.top = `${p.y}px`;
        p.element.style.opacity = String(opacity);
      }
    });

    if (!allExpired) {
      requestAnimationFrame(animate);
    } else {
      // Cleanup
      container.remove();
    }
  }

  requestAnimationFrame(animate);
}

// Cute firework rocket SVG - uses theme primary color with shades
const FirecrackerSVG: React.FC = () => (
  <svg
    height="100%"
    width="100%"
    viewBox="0 0 479.217 479.217"
    xmlns="http://www.w3.org/2000/svg"
  >
    {/* Main body - primary */}
    <path d="m197.234 340.137-68.956-68.957c-11.425-11.425-11.425-29.949 0-41.374l17.542-17.542 50.444-37.529 33.543-46.457 77.759-77.759c11.425-11.425 29.949-11.425 41.374 0l68.956 68.956c11.425 11.425 11.425 29.949 0 41.374l-179.288 179.288c-11.425 11.425-29.949 11.425-41.374 0z" fill="var(--theme-primary)" style={{ opacity: 0.85 }} />
    {/* Top wing - light */}
    <path d="m417.895 119.476-44.759-44.759v53.562h-143.33l-83.987 83.986h220.661l51.415-51.415c11.425-11.425 11.425-29.949 0-41.374z" fill="#e7effa" />
    {/* Bottom highlight - white */}
    <path d="m151.167 294.07 46.067 46.067c11.425 11.425 29.949 11.425 41.374 0l46.067-46.067z" fill="#fff" />
    {/* Body shadow - primary darker */}
    <path d="m417.895 119.476-68.956-68.956c-11.425-11.425-29.949-11.425-41.374 0l-22.177 22.177c8.599-8.598 22.539-8.598 31.138 0l51.897 51.897c8.599 8.599 8.599 22.539 0 31.138l-134.933 134.931c-8.599 8.599-22.54 8.599-31.138 0l-51.897-51.897c-8.599-8.599-8.599-22.539 0-31.138l-22.177 22.177c-11.425 11.425-11.425 29.949 0 41.374l22.89 22.89 46.067 41.172c11.425 11.425 29.949 11.425 41.374 0l46.067-41.172 133.22-133.22c11.424-11.424 11.424-29.948-.001-41.373z" fill="var(--theme-primary)" />
    {/* Accent detail - primary light */}
    <path d="m229.046 294.07c-6.854 4.023-15.396 4.023-22.251 0h-55.628l46.067 46.067c11.425 11.425 29.949 11.425 41.374 0l46.067-46.067z" fill="var(--theme-primary)" style={{ opacity: 0.5 }} />
    {/* Wing shadow - primary light */}
    <path d="m417.895 119.476-44.759-44.759v53.562h-1.756c5.479 8.524 4.5 19.996-2.958 27.454l-56.533 56.533h54.591l51.415-51.415c11.425-11.426 11.425-29.95 0-41.375z" fill="var(--theme-primary)" style={{ opacity: 0.5 }} />
    {/* Tail - primary */}
    <path d="m433.103.296-140.668 19.303c-25.451 3.492-35.916 34.601-17.751 52.766l121.366 121.365c18.165 18.165 49.273 7.7 52.766-17.751l19.302-140.668c2.797-20.385-14.63-37.812-35.015-35.015z" fill="var(--theme-primary)" style={{ opacity: 0.85 }} />
    {/* Tail shadow - primary darker */}
    <path d="m433.103.296-10.622 1.457c2.195 4.103 3.192 8.931 2.495 14.009l-14.361 104.658c-2.598 18.936-25.743 26.722-39.258 13.207l-90.298-90.297c-4.232-4.231-6.374-9.407-6.741-14.601-10.993 11.444-12.492 30.776.366 43.634l121.366 121.367c18.165 18.165 49.273 7.7 52.766-17.751l19.302-140.668c2.797-20.385-14.63-37.812-35.015-35.015z" fill="var(--theme-primary)" />
    {/* Flame outer - orange/yellow */}
    <path d="m147.689 401.87c20.827-25.373 17.142-62.825-8.23-83.652-25.373-20.827-66.558-28.954-87.385-3.581s-52.421 122.162-27.048 142.989c17.12 14.053 31.209-38.02 57.619-50.812s58.27 3.309 65.044-4.944z" fill="#ffa585" />
    {/* Flame inner - yellow */}
    <path d="m140.347 365.297c12.571-8.711 15.7-25.963 6.989-38.534s-35.552-16.738-48.122-8.027c-12.571 8.711-29.657 43.318-20.946 55.889 5.878 8.482 18.837-13.053 32.303-15.414 13.466-2.362 25.687 8.919 29.776 6.086z" fill="#ffe266" />
    {/* Flame tip - orange */}
    <path d="m121.068 456.914c5.746-6.999 4.729-17.331-2.27-23.076s-17.331-4.729-23.076 2.27c-5.746 6.999-20.334 36.341-13.334 42.087 6.998 5.745 32.934-14.281 38.68-21.281z" fill="#ffa585" />
    {/* Window highlight - white */}
    <path d="m330.16 59.204c-4.97 0-9.277-3.701-9.91-8.761-.685-5.48 3.202-10.478 8.683-11.163l45.311-5.664c5.48-.69 10.477 3.203 11.163 8.683.685 5.48-3.202 10.478-8.683 11.163l-45.311 5.663c-.42.053-.839.079-1.253.079z" fill="#fff" />
  </svg>
);

// Firework component with dotted trail
interface FireworkProps {
  startX: number;
  startY: number;
  direction: 'left' | 'center' | 'right';
  onComplete: (endX: number, endY: number) => void;
}

const Firework: React.FC<FireworkProps> = ({ startX, startY, direction, onComplete }) => {
  const fireworkRef = useRef<HTMLDivElement>(null);
  const rocketIconRef = useRef<HTMLDivElement>(null);
  const trailDotsRef = useRef<HTMLDivElement[]>([]);
  const animationFrameRef = useRef<number>(0);

  useEffect(() => {
    // Fixed angles based on direction (atan2-compatible degrees, screen coords where Y-down is positive)
    // These match Math.cos/Math.sin: 90° = straight down, 135° = down-left, 45° = down-right
    const angleMap = {
      left: 135,   // diagonal down-left
      center: 90,  // straight down
      right: 45    // diagonal down-right
    };

    const angleDeg = angleMap[direction];
    const baseAngle = (angleDeg * Math.PI) / 180;

    // Shorter, controlled distance
    const distance = 120 + Math.random() * 60; // 120-180px

    const endX = startX + Math.cos(baseAngle) * distance;
    const endY = startY + Math.sin(baseAngle) * distance;

    // Clamp to viewport bounds
    const clampedEndX = Math.max(40, Math.min(window.innerWidth - 40, endX));
    const clampedEndY = Math.max(40, Math.min(window.innerHeight - 40, endY));

    const firework = fireworkRef.current;
    const rocketIcon = rocketIconRef.current;
    const trailContainer = firework?.parentElement;
    const trailCount = 15; // Fewer trail dots
    const duration = 850; // 0.85 second flight per firework
    const startTime = performance.now();

    // Track rotation angle - initialize to travel direction
    let currentAngle = baseAngle;

    // Helper to calculate position at a given progress
    const getPosition = (progress: number) => {
      const easeOut = 1 - Math.pow(1 - progress, 3);
      const x = startX + (clampedEndX - startX) * easeOut;
      const y = startY + (clampedEndY - startY) * easeOut;

      return { x, y };
    };

    // Each dot has its own fixed position and spawn time
    const dotData: Array<{ x: number; y: number; spawnTime: number; active: boolean }> = [];
    for (let i = 0; i < trailCount; i++) {
      dotData.push({ x: 0, y: 0, spawnTime: 0, active: false });
    }
    let nextDotIndex = 0;
    let lastDotTime = 0;
    const dotSpawnInterval = 20; // Spawn a new dot every 20ms
    const dotLifetime = 850; // Match flight duration

    // Get theme colors for trail dots
    const trailColors = getFireworkColors();

    // Create trail dots with theme colors applied directly
    if (trailContainer) {
      for (let i = 0; i < trailCount; i++) {
        const dot = document.createElement('div');
        dot.className = 'firework-trail-dot';
        dot.style.opacity = '0';
        // Apply theme color directly as inline style
        const colorIndex = i % (trailColors.length || 1);
        const trailColor = trailColors[colorIndex] || '#ffffff';
        dot.style.setProperty('--trail-color', trailColor);
        trailContainer.appendChild(dot);
        trailDotsRef.current.push(dot);
      }
    }

    const animate = (currentTime: number) => {
      const elapsed = currentTime - startTime;
      const progress = Math.min(elapsed / duration, 1);

      const { x: currentX, y: currentY } = getPosition(progress);

      // Update rocket position
      if (firework) {
        firework.style.left = `${currentX}px`;
        firework.style.top = `${currentY}px`;

        // Scale animation
        const scale = 1 + (0.2 * progress) - (progress > 0.8 ? (progress - 0.8) * 4 : 0);
        firework.style.transform = `scale(${Math.max(0, scale)})`;
        firework.style.opacity = progress > 0.85 ? String(1 - (progress - 0.85) * 6.67) : '1';
      }

      // Rotate rocket to point in direction of travel (fixed angle)
      if (rocketIcon) {
        // SVG points diagonally (upper-right), add 45 to align with travel direction
        const rotationDeg = (currentAngle * 180) / Math.PI + 45;
        rocketIcon.style.transform = `rotate(${rotationDeg}deg)`;
      }

      // Spawn new dot behind the rocket
      if (elapsed - lastDotTime > dotSpawnInterval && progress < 0.95 && progress > 0.02) {
        // Place dot at the flame (offset behind rocket center)
        const behindOffset = 12; // Distance behind the rocket center
        const behindAngle = currentAngle + Math.PI;
        dotData[nextDotIndex] = {
          x: currentX + Math.cos(behindAngle) * behindOffset,
          y: currentY + Math.sin(behindAngle) * behindOffset,
          spawnTime: elapsed,
          active: true
        };
        nextDotIndex = (nextDotIndex + 1) % trailCount;
        lastDotTime = elapsed;
      }

      // Update all trail dots - they stay in place and fade out
      trailDotsRef.current.forEach((dot, index) => {
        const data = dotData[index];
        if (data.active) {
          const age = elapsed - data.spawnTime;

          if (age > dotLifetime) {
            // Dot has expired
            dot.style.opacity = '0';
            data.active = false;
          } else {
            // Position dot at its fixed location
            dot.style.left = `${data.x}px`;
            dot.style.top = `${data.y}px`;

            // Fade out over lifetime
            const fadeProgress = age / dotLifetime;
            const opacity = Math.max(0, 1 - fadeProgress) * 0.9;
            dot.style.opacity = String(opacity);
            dot.style.transform = `scale(${Math.max(0.2, 0.8 - fadeProgress * 0.5)})`;
          }
        }
      });

      if (progress < 1) {
        animationFrameRef.current = requestAnimationFrame(animate);
      } else {
        onComplete(currentX, currentY);
        trailDotsRef.current.forEach((dot) => dot.remove());
        trailDotsRef.current = [];
      }
    };

    animationFrameRef.current = requestAnimationFrame(animate);

    return () => {
      cancelAnimationFrame(animationFrameRef.current);
      trailDotsRef.current.forEach((dot) => dot.remove());
      trailDotsRef.current = [];
    };
  }, [startX, startY, direction, onComplete]);

  return (
    <div
      ref={fireworkRef}
      className="firework-rocket"
      style={{
        left: startX,
        top: startY
      }}
    >
      <div ref={rocketIconRef} className="flying-rocket-icon">
        <FirecrackerSVG />
      </div>
    </div>
  );
};

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
  const [isRocketSpinning, setIsRocketSpinning] = useState(false);
  const [isBouncing, setIsBouncing] = useState(false);
  const [fireworks, setFireworks] = useState<Array<{ x: number; y: number; direction: 'left' | 'center' | 'right' }>>([]);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const dropdownRef = useRef<HTMLDivElement>(null);
  const rocketRef = useRef<HTMLDivElement>(null);
  const spinningRef = useRef(false);
  const completedCountRef = useRef(0);
  const fireworkDirections = useRef<Array<'left' | 'center' | 'right'>>(['left', 'center', 'right']);
  const currentFireworkIndexRef = useRef(0);
  const fireworkOriginRef = useRef<{ x: number; y: number }>({ x: 0, y: 0 });
  const nextFireworkTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const fireworkKeyRef = useRef(0);

  // Launch a single firework by index from the queue
  const launchFireworkByIndex = useCallback((index: number) => {
    const direction = fireworkDirections.current[index];
    if (!direction) return;

    fireworkKeyRef.current += 1;
    const origin = fireworkOriginRef.current;
    setFireworks([{ x: origin.x, y: origin.y, direction }]);

    // Trigger bounce animation
    setIsBouncing(true);
    setTimeout(() => setIsBouncing(false), 300);
  }, []);

  // Handle firework completion - trigger particle burst, then launch next
  const handleFireworkComplete = useCallback((endX: number, endY: number) => {
    // Trigger the particle burst at the firework's end position
    createParticleBurst(endX, endY);

    // Dispatch custom event that other components can listen to
    const event = new CustomEvent(FIREWORK_EXPLOSION_EVENT, {
      detail: { x: endX, y: endY },
      bubbles: true,
    });
    window.dispatchEvent(event);

    // Track completion
    completedCountRef.current += 1;

    // Check if there are more fireworks to launch
    const nextIndex = currentFireworkIndexRef.current + 1;
    if (nextIndex < fireworkDirections.current.length) {
      currentFireworkIndexRef.current = nextIndex;
      // Launch next firework immediately after current completes
      // The rocket flight itself is ~1.2s which provides the visual delay
      nextFireworkTimerRef.current = setTimeout(() => {
        launchFireworkByIndex(nextIndex);
      }, 100); // Small 100ms gap between explosion and next launch
    } else {
      // All fireworks done - clear state
      setFireworks([]);
      completedCountRef.current = 0;
      currentFireworkIndexRef.current = 0;
    }
  }, [launchFireworkByIndex]);

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

  // Cleanup timer on unmount
  useEffect(() => {
    return () => {
      if (nextFireworkTimerRef.current) {
        clearTimeout(nextFireworkTimerRef.current);
      }
    };
  }, []);

  // Handle spin animation end - launch fireworks sequentially starting with the first
  const handleSpinAnimationEnd = useCallback(() => {
    // Guard against double-firing (glow ::after also fires animationend)
    if (!spinningRef.current) return;
    spinningRef.current = false;

    const buttonElement = triggerRef.current;
    if (buttonElement) {
      const rect = buttonElement.getBoundingClientRect();
      const buttonCenterX = rect.left + rect.width / 2;
      const buttonBottomY = rect.bottom;

      // Store origin for sequential launches
      fireworkOriginRef.current = { x: buttonCenterX, y: buttonBottomY };

      // Reset queue tracking
      currentFireworkIndexRef.current = 0;
      completedCountRef.current = 0;

      // Launch only the first firework - subsequent ones fire from handleFireworkComplete
      launchFireworkByIndex(0);
    }
    setIsRocketSpinning(false);
  }, [launchFireworkByIndex]);

  // Handle button click - toggles dropdown AND launches firework only when opening
  const handleButtonClick = useCallback((_e: React.MouseEvent) => {
    setIsOpen((prev) => {
      const willOpen = !prev;

      // Only launch firework when opening the dropdown, not when closing
      if (willOpen && !isRocketSpinning) {
        setIsRocketSpinning(true);
        spinningRef.current = true;
      }

      return willOpen;
    });
    setHoveredIndex(null);
  }, [isRocketSpinning]);

  const handleRepoClick = (url: string) => {
    window.open(url, '_blank', 'noopener,noreferrer');
    setIsOpen(false);
  };

  // Trigger button content - simplified, firework launches from button click
  const triggerContent = iconOnly ? (
    <div
      ref={rocketRef}
      className={`github-icon-container ${isRocketSpinning ? 'spinning' : ''}`}
      onAnimationEnd={handleSpinAnimationEnd}
    >
      <Github size={18} className="github-icon-spin flex-shrink-0 text-[var(--theme-primary)]" />
    </div>
  ) : (
    <>
      <div
        ref={rocketRef}
        className={`github-icon-container ${isRocketSpinning ? 'spinning' : ''}`}
        onAnimationEnd={handleSpinAnimationEnd}
      >
        <Github size={16} className="github-icon-spin flex-shrink-0 text-[var(--theme-primary)]" />
      </div>
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
            onClick={(e: React.MouseEvent) => e.stopPropagation()}
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
                <div className="github-repo-main">
                  <div className="github-repo-icon">
                    <Github size={16} />
                  </div>
                  <div className="github-repo-info">
                    <div className="github-repo-name-row">
                      <span className="github-repo-name">{repo.name}</span>
                      {repo.isFork && <span className="github-fork-pill">Fork</span>}
                    </div>
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
                <div className="github-repo-main">
                  <div className="github-repo-icon">
                    <Github size={16} />
                  </div>
                  <div className="github-repo-info">
                    <div className="github-repo-name-row">
                      <span className="github-repo-name">{repo.name}</span>
                      {repo.isFork && <span className="github-fork-pill">Fork</span>}
                    </div>
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

  // Firework portal - renders one firework at a time at document body level for proper z-index
  const fireworkPortal = fireworks.length > 0 && createPortal(
    <div className="firework-container">
      {fireworks.map((fw) => (
        <Firework
          key={fireworkKeyRef.current}
          startX={fw.x}
          startY={fw.y}
          direction={fw.direction}
          onComplete={handleFireworkComplete}
        />
      ))}
    </div>,
    document.body
  );

  return (
    <>
      <Tooltip content="GitHub Projects & Support - Click for a surprise!">
        <button
          ref={triggerRef}
          onClick={handleButtonClick}
          className={`ed-trigger github-trigger ${isBouncing ? 'bouncing' : ''} px-3 py-2 themed-border-radius border text-left flex items-center text-sm themed-card text-themed-primary ${
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
      {fireworkPortal}
    </>
  );
};

export default GitHubProjectsDropdown;
