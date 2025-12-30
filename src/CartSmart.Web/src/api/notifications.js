export const fetchNotifications = async (cursor = null, limit = 20) => {
  const base = process.env.REACT_APP_API_URL;
  const url = new URL(`${base}/api/notifications`);
  if (cursor) url.searchParams.set('cursor', cursor);
  url.searchParams.set('limit', limit);
  const res = await fetch(url, { credentials: 'include' });
  if (!res.ok) throw new Error('Failed to load notifications');
  return res.json();
};

export const markNotificationRead = async (id) => {
  const base = process.env.REACT_APP_API_URL;
  await fetch(`${base}/api/notifications/${id}/read`, {
    method: 'PATCH',
    credentials: 'include'
  });
};

export const markAllNotificationsRead = async () => {
  const base = process.env.REACT_APP_API_URL;
  await fetch(`${base}/api/notifications/read-all`, {
    method: 'PATCH',
    credentials: 'include'
  });
};