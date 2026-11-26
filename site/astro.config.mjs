import { defineConfig } from 'astro/config';

// Served at https://vecto.damt.xyz/ (GitHub Pages custom domain); the old
// danielmevit.github.io/vecto/ address redirects here.
export default defineConfig({
  site: 'https://vecto.damt.xyz',
  base: '/',
});
