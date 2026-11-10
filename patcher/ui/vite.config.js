import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { viteSingleFile } from 'vite-plugin-singlefile'

// One self-contained index.html, embedded into the exe as a resource.
export default defineConfig({ plugins: [react(), viteSingleFile()] })
