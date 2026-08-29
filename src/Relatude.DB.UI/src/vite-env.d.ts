/// <reference types="vite/client" />

// File System Access API (Chromium): not yet part of TypeScript's dom lib
interface Window {
  showDirectoryPicker?: (options?: { id?: string; mode?: "read" | "readwrite" }) => Promise<FileSystemDirectoryHandle>;
}
