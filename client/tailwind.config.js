/** @type {import('tailwindcss').Config} */
module.exports = {
  content: ['./src/**/*.{html,ts,scss}'],
  corePlugins: {
    // Bulma already ships its own CSS reset; running Tailwind's preflight on top
    // would fight Bulma's normalization (especially headings, lists, tables). We
    // ship utilities + components only and let Bulma keep owning the base layer
    // for pages that still use it.
    preflight: false,
  },
  theme: {
    extend: {
      colors: {
        surface: {
          DEFAULT: '#0b0e14',
          card: '#10141c',
          raised: '#171c27',
          line: '#222a3a',
          'line-strong': '#2c364c',
        },
        ink: {
          DEFAULT: '#e6e9ef',
          muted: '#8b94a7',
          subtle: '#5d6679',
        },
        accent: {
          DEFAULT: '#6cc4ff',
          strong: '#3ba6ff',
        },
        status: {
          queued: '#7c8aa1',
          downloading: '#3ba6ff',
          processing: '#f6c453',
          finished: '#4ed3a4',
          error: '#ff6b6b',
          waiting: '#bf8cff',
        },
      },
      fontFamily: {
        sans: [
          'system-ui',
          '-apple-system',
          'BlinkMacSystemFont',
          'Segoe UI',
          'Roboto',
          'Helvetica Neue',
          'Arial',
          'sans-serif',
        ],
        mono: ['ui-monospace', 'SFMono-Regular', 'Menlo', 'Consolas', 'monospace'],
      },
      boxShadow: {
        card: '0 1px 0 rgba(255,255,255,0.02) inset, 0 1px 2px rgba(0,0,0,0.4)',
        elevated: '0 4px 18px rgba(0,0,0,0.45)',
      },
    },
  },
  plugins: [],
};
