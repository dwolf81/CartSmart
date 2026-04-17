import { useEffect, useState, useCallback } from 'react';
import { useAuth } from '../context/AuthContext';

export function useActionItems() {
  const { isAuthenticated, user, authFetch } = useAuth();
  const [pendingReviews, setPendingReviews] = useState(0);
  const [pendingTasks, setPendingTasks] = useState(0);

  const load = useCallback(async () => {
    if (!isAuthenticated || (!user?.allowReview && !user?.admin)) return;
    try {
      const base = process.env.REACT_APP_API_URL;
      const res = await authFetch(`${base}/api/users/action-items`);
      if (!res.ok) return;
      const data = await res.json();
      setPendingReviews(data.pendingReviews ?? 0);
      setPendingTasks(data.pendingTasks ?? 0);
    } catch {
      // silently ignore
    }
  }, [isAuthenticated, user?.allowReview, user?.admin, authFetch]);

  useEffect(() => {
    load();
    // Refresh every 60 seconds
    const id = setInterval(load, 60_000);
    return () => clearInterval(id);
  }, [load]);

  return { pendingReviews, pendingTasks, reload: load };
}
