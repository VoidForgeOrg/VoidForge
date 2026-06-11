import { beforeEach, describe, expect, it } from 'vitest';

import { useSessionStore } from './sessionStore';

describe('session store', () => {
  beforeEach(() => {
    window.localStorage.clear();
    useSessionStore.setState({ apiKey: null });
  });

  it('stores an API key in local state and persisted storage', () => {
    useSessionStore.getState().setApiKey('vf_test_key');

    expect(useSessionStore.getState().apiKey).toBe('vf_test_key');
    expect(window.localStorage.getItem('voidforge-session')).toContain(
      'vf_test_key',
    );
  });

  it('clears an API key from local state', () => {
    useSessionStore.getState().setApiKey('vf_test_key');

    useSessionStore.getState().clearApiKey();

    expect(useSessionStore.getState().apiKey).toBeNull();
  });
});
