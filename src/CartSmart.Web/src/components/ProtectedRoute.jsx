import React from 'react';
import { Navigate } from 'react-router-dom';
import { useAuth } from '../context/AuthContext';
import LoadingSpinner from './LoadingSpinner';

const toBool = (v) => {
  if (typeof v === 'boolean') return v;
  if (typeof v === 'string') return v.toLowerCase() === 'true';
  return Boolean(v);
};

const ProtectedRoute = ({ children, element, requireReviewAccess = false, requireAdmin = false }) => {
  const { loading, isAuthenticated, user } = useAuth();

  // support both patterns: children or element prop
  const content = children ?? element ?? null;

  if (loading) return <LoadingSpinner />;
  if (!isAuthenticated) return <Navigate to="/login" replace />;

  if (requireAdmin) {
    const isAdmin = toBool(user?.admin ?? user?.Admin);
    if (!isAdmin) return <Navigate to="/" replace />;
  }

  if (requireReviewAccess) {
    const raw = user?.allowReview ?? user?.AllowReview;
    const canReview = typeof raw === 'string' ? raw.toLowerCase() === 'true' : Boolean(raw);
    if (!canReview) return <Navigate to="/" replace />;
  }

  return content;
};

export default ProtectedRoute;
