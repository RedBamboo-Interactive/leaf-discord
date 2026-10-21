import { fileURLToPath } from "node:url"
import { defineConfig } from "vite"
import react from "@vitejs/plugin-react"
import tailwindcss from "@tailwindcss/vite"

export default defineConfig({
  root: fileURLToPath(new URL("./visual", import.meta.url)),
  plugins: [react(), tailwindcss()],
  resolve: { dedupe: ["react", "react-dom", "react/jsx-runtime"] },
  server: { host: "127.0.0.1", port: 43127, strictPort: true },
})
