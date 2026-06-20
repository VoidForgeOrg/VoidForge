import '@testing-library/jest-dom/vitest';
import { cleanup } from '@testing-library/react';
import { afterEach } from 'vitest';

function createMemoryStorage(): Storage {
  const entries = new Map<string, string>();

  return {
    get length() {
      return entries.size;
    },
    clear() {
      entries.clear();
    },
    getItem(key) {
      return entries.get(key) ?? null;
    },
    key(index) {
      return Array.from(entries.keys())[index] ?? null;
    },
    removeItem(key) {
      entries.delete(key);
    },
    setItem(key, value) {
      entries.set(key, value);
    },
  };
}

const storage = createMemoryStorage();

Object.defineProperty(window, 'localStorage', {
  configurable: true,
  value: storage,
});

Object.defineProperty(globalThis, 'localStorage', {
  configurable: true,
  value: storage,
});

Object.defineProperty(window, 'scrollTo', {
  configurable: true,
  value: () => undefined,
});

afterEach(() => {
  cleanup();
});
