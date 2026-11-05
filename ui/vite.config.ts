import { defineConfig, type Plugin } from "vitest/config";
import react from "@vitejs/plugin-react";

/** The shipped page may load only its own files (the WebView2 host also blocks navigation elsewhere). Dev needs inline scripts. */
const contentSecurityPolicy: Plugin = {
  name: "content-security-policy",
  apply: "build",
  transformIndexHtml: () => [{
    tag: "meta",
    attrs: { "http-equiv": "Content-Security-Policy", content: "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:" },
    injectTo: "head-prepend",
  }],
};

// The build lands in ui/dist; RGBcontroller.UI copies it to wwwroot and serves it under a private host name.
export default defineConfig({
  plugins: [react(), contentSecurityPolicy],
  base: "./",
  build: { outDir: "dist", emptyOutDir: true, sourcemap: false },
  server: { port: 5173, strictPort: true },
  test: { environment: "node" },
});
