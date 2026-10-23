import { useCallback, useEffect, useRef, useState } from 'react';
import { isAbortError } from '../api/errors';

interface LoaderState<T> {
  data: T | null;
  error: unknown;
  loading: boolean;
}

// Loads data for a page and cancels the request when the inputs change or the page closes, so an old, slow response
// never overwrites a newer one.
export function useLoader<T>(load: (signal: AbortSignal) => Promise<T>, deps: unknown[]) {
  const [state, setState] = useState<LoaderState<T>>({ data: null, error: null, loading: true });
  const [version, setVersion] = useState(0);
  const loadRef = useRef(load);
  loadRef.current = load;

  useEffect(() => {
    const controller = new AbortController();
    setState((previous) => ({ ...previous, loading: true, error: null }));
    loadRef
      .current(controller.signal)
      .then((data) => setState({ data, error: null, loading: false }))
      .catch((error: unknown) => {
        if (!isAbortError(error)) setState((previous) => ({ ...previous, error, loading: false }));
      });
    return () => controller.abort();
    // deps are the caller's inputs; the loader itself is read through a ref.
  }, [...deps, version]);

  const reload = useCallback(() => setVersion((v) => v + 1), []);
  const setData = useCallback((data: T) => setState({ data, error: null, loading: false }), []);

  return { ...state, reload, setData };
}
