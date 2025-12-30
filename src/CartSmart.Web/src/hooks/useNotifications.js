import { useEffect, useState, useCallback } from 'react';
import {
  fetchNotifications,
  markNotificationRead,
  markAllNotificationsRead
} from '../api/notifications';

export function useNotifications() {
  const [items, setItems] = useState([]);
  const [unread, setUnread] = useState(0);
  const [loading, setLoading] = useState(false);
  const [nextCursor, setNextCursor] = useState(null);
  const [error, setError] = useState(null);

  const load = useCallback(async (reset = false) => {
    try {
      setLoading(true);
      const data = await fetchNotifications(reset ? null : nextCursor);
      // Normalize property names (ensure isRead exists)
      const normalized = (data.notifications || []).map(n => ({
        ...n,
        isRead: n.isRead ?? n.is_read ?? false
      }));
      setUnread(data.unread);
      setNextCursor(data.nextCursor);
      setItems(prev => reset ? normalized : [...prev, ...normalized]);
    } catch (e) {
      setError(e.message);
    } finally {
      setLoading(false);
    }
  }, [nextCursor]);

  useEffect(() => {
    load(true);
  }, [load]);

  const markRead = async (id) => {
    await markNotificationRead(id);
    setItems(prev => prev.map(n => n.id === id ? { ...n, isRead: true } : n));
    setUnread(prev => Math.max(prev - 1, 0));
  };

  const markAllRead = async () => {
    await markAllNotificationsRead();
    setItems(prev => prev.map(n => ({ ...n, isRead: true })));
    setUnread(0);
  };

  return {
    items,
    unread,
    loading,
    error,
    hasMore: Boolean(nextCursor),
    loadMore: () => load(false),
    reload: () => load(true),
    markRead,
    markAllRead
  };
}