import { create } from 'zustand';
import { createJSONStorage, persist } from 'zustand/middleware';

interface SessionState {
  apiKey: string | null;
  setApiKey: (apiKey: string) => void;
  clearApiKey: () => void;
}

export const useSessionStore = create<SessionState>()(
  persist(
    (set) => ({
      apiKey: null,
      setApiKey: (apiKey) => {
        set({ apiKey });
      },
      clearApiKey: () => {
        set({ apiKey: null });
      },
    }),
    {
      name: 'voidforge-session',
      storage: createJSONStorage(() => window.localStorage),
      partialize: (state) => ({ apiKey: state.apiKey }),
    },
  ),
);
