import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';
import { viteSingleFile } from 'vite-plugin-singlefile';

// iCUE QtWebEngine blocks `<script src>` for widgets loaded from file:// — we
// MUST emit a single self-contained index.html.
export default defineConfig({
  plugins: [svelte(), viteSingleFile()],
  build: {
    target: 'chrome120', // QtWebEngine 6.9.3 = Chromium 130
    cssCodeSplit: false,
    assetsInlineLimit: 100_000_000,
    rollupOptions: {
      output: {
        inlineDynamicImports: true,
      },
    },
  },
  server: {
    port: 5174,
  },
});
