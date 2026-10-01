import { defineConfig, presetUno, presetIcons } from 'unocss'

// Modern Dark Mode design system. Colors resolve to CSS variables (styles.css)
// so the whole UI follows `data-theme` on <html> (dark default).
export default defineConfig({
  presets: [
    presetUno(),
    // Icons resolve from the locally installed @iconify-json/carbon — no CDN.
    // (Per-icon build-time CDN fetches used to fail silently and render blank
    // buttons; the collections loader is required for local resolution.)
    presetIcons({
      scale: 1.15,
      collections: {
        carbon: () => import('@iconify-json/carbon/icons.json').then(m => m.default as any),
      },
    }),
  ],
  theme: {
    colors: {
      bg: 'var(--bg)',
      sidebar: 'var(--sidebar)',
      surface: 'var(--surface)',
      surface2: 'var(--surface-2)',
      surface3: 'var(--surface-3)',
      border: 'var(--border)',
      'border-strong': 'var(--border-strong)',
      primary: 'var(--primary)',
      primaryHover: 'var(--primary-hover)',
      'primary-soft': 'var(--primary-soft)',
      accent: 'var(--accent)',
      accentHover: 'var(--accent-hover)',
      'accent-soft': 'var(--accent-soft)',
      'on-accent': 'var(--on-accent)',
      danger: 'var(--danger)',
      'status-green': 'var(--status-green)',
      text: 'var(--text)',
      'text-soft': 'var(--text-soft)',
      muted: 'var(--muted)',
    },
    boxShadow: {
      sm: 'var(--shadow-sm)',
      md: 'var(--shadow-md)',
      lg: 'var(--shadow-lg)',
    },
    borderRadius: {
      sm: 'var(--radius-sm)',
      DEFAULT: 'var(--radius)',
      lg: 'var(--radius-lg)',
    },
  },
  shortcuts: {
    // Buttons — soft, low-contrast borders; primary lifts on hover.
    // Shared disabled + keyboard-focus treatment on all variants.
    'btn':
      'inline-flex items-center justify-center gap-1.5 px-3.5 py-2 rounded-md text-[13px] font-medium ' +
      'bg-surface2 text-text-soft border border-border shadow-sm ' +
      'hover:bg-surface3 hover:text-text transition-all duration-150 active:scale-[0.98] ' +
      'disabled:opacity-50 disabled:cursor-not-allowed disabled:pointer-events-none',
    'btn-primary':
      'inline-flex items-center justify-center gap-1.5 px-3.5 py-2 rounded-md text-[13px] font-medium ' +
      'bg-primary text-white border border-transparent shadow-md ' +
      'hover:bg-primaryHover hover:shadow-lg active:scale-[0.98] transition-all duration-150 ' +
      'disabled:opacity-50 disabled:cursor-not-allowed disabled:pointer-events-none',
    // Brand-yellow CTA (login/save-style buttons in the reference design).
    'btn-accent':
      'inline-flex items-center justify-center gap-1.5 px-3.5 py-2 rounded-md text-[13px] font-semibold ' +
      'bg-accent text-on-accent border border-transparent shadow-sm ' +
      'hover:bg-accentHover hover:shadow-md active:scale-[0.98] transition-all duration-150 ' +
      'disabled:opacity-50 disabled:cursor-not-allowed disabled:pointer-events-none',
    // Yellow outlined secondary action (选择/导入-style buttons).
    'btn-outline-accent':
      'inline-flex items-center justify-center gap-1.5 px-3.5 py-2 rounded-md text-[13px] font-medium ' +
      'bg-transparent text-accent border border-accent/60 ' +
      'hover:bg-accent-soft hover:border-accent active:scale-[0.98] transition-all duration-150 ' +
      'disabled:opacity-50 disabled:cursor-not-allowed disabled:pointer-events-none',
    'btn-ghost':
      'inline-flex items-center justify-center gap-1.5 px-2.5 py-1.5 rounded-md text-[13px] font-medium ' +
      'text-muted hover:bg-surface2 hover:text-text transition-colors duration-150 ' +
      'disabled:opacity-50 disabled:cursor-not-allowed disabled:pointer-events-none',
    // Cards — rounded, subtle border, hover lift.
    'card':
      'bg-surface border border-border rounded-lg shadow-sm overflow-hidden ' +
      'transition-all duration-200 hover:shadow-lg hover:border-border-strong',
    // Inputs — dark fill, faint border, focus ring.
    'input':
      'bg-surface2 border border-border rounded-md px-3.5 py-2 text-[13px] text-text w-full ' +
      'placeholder:text-muted transition-all duration-150 ' +
      'focus:outline-none focus:border-primary focus:ring-2 focus:ring-primary/20',
    // Chips — pill, soft.
    'chip':
      'inline-flex items-center gap-1 px-2.5 py-1 rounded-full text-xs font-medium ' +
      'bg-surface2 text-text-soft border border-border ' +
      'hover:border-primary hover:text-primary transition-colors duration-150 cursor-pointer',
    // Nav item (top bar) — plain text links; selected state turns primary blue
    // via active-class in MainLayout, matching the reference app.
    'nav-item':
      'inline-flex items-center gap-1.5 px-3 py-1.5 rounded-md text-[13px] font-medium text-text-soft leading-none ' +
      'hover:bg-surface2 hover:text-text transition-colors duration-150',
    // Square icon button for the top-bar right cluster (search/heart/settings).
    'icon-btn':
      'inline-flex items-center justify-center w-9 h-9 rounded-md text-text-soft ' +
      'hover:bg-surface2 hover:text-text transition-colors duration-150',
  },
})
