import { defineConfig } from 'vite';

export default defineConfig({
  build: {
    lib: {
      entry: 'src/index.ts',
      formats: ['es'],
      fileName: () => 'vfx.js'
    },
    sourcemap: true,
    minify: false,
    rollupOptions: {
      external: ['three']
    }
  }
});
