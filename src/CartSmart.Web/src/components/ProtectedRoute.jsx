import React from 'react';
import { Navigate } from 'react-router-dom';
import { useAuth } from '../context/AuthContext';
import LoadingSpinner from './LoadingSpinner';

const ProtectedRoute = ({ children, element, requireReviewAccess = false }) => {
  const { loading, isAuthenticated, user } = useAuth();

  // support both patterns: children or element prop
  const content = children ?? element ?? null;

  if (loading) return <LoadingSpinner />;
  if (!isAuthenticated) return <Navigate to="/login" replace />;

  if (requireReviewAccess) {
    const raw = user?.allowReview ?? user?.AllowReview;
    const canReview = typeof raw === 'string' ? raw.toLowerCase() === 'true' : Boolean(raw);
    if (!canReview) return <Navigate to="/" replace />;
  }

  return content;
};

export default ProtectedRoute;
