import React from 'react';
import { HelmetProvider } from 'react-helmet-async';
import { Routes, Route,Navigate } from 'react-router-dom';
import Header from './components/Header';
import HomePage from './components/HomePage';
import ProductPage from './components/ProductPage';
import LoginPage from './components/LoginPage';
import SignupPage from './components/SignupPage';
import DealReviewPage from './components/DealReviewPage';
import ProfilePage from './components/ProfilePage';
import FeedPage from './components/FeedPage';
import SettingsPage from './components/SettingsPage';
import Footer from './components/Footer';
import CategoriesPage from './components/CategoriesPage';
import CategoryProductsPage from './components/CategoryProductsPage';
import StoresPage from './components/StoresPage';
import StorePage from './components/StorePage';
import { GoogleOAuthProvider } from '@react-oauth/google';
import ProtectedRoute from "./components/ProtectedRoute";
import { AuthProvider, useAuth } from './context/AuthContext';
import AboutUs from './pages/AboutUs';
import Contact from './pages/Contact';
import FAQ from './pages/FAQ';
import PrivacyPolicy from './pages/PrivacyPolicy';
import TermsOfService from './pages/TermsOfService';
import CookiePolicy from './pages/CookiePolicy';
import Disclaimer from './pages/Disclaimer';
import ForgotPasswordPage from './components/ForgotPasswordPage';
import ResetPasswordPage from './components/ResetPasswordPage';
import ActivateAccountPage from './components/ActivateAccountPage';
import { CookieConsentProvider } from './context/CookieConsentContext';
import { TermsConsentProvider } from './context/TermsConsentContext';
import CookieBanner from './components/CookieBanner';
import AdminManualPriceTasksPage from './components/AdminManualPriceTasksPage';

function App() {
  const googleClientId = process.env.REACT_APP_GOOGLE_CLIENT_ID;

  return (
    <HelmetProvider>
     <CookieConsentProvider>
    <AuthProvider>
     <TermsConsentProvider>
      <GoogleOAuthProvider clientId={googleClientId}>
        <div className="min-h-screen flex flex-col">
          <div className="flex-grow">
            <Routes>
              <Route 
                path="/login" 
                element={<LoginPage />} 
              />
              <Route 
                path="/signup" 
                element={<SignupPage />} 
              />
              <Route 
                path="/" 
                element={
                  <>
                    <Header />
                    <HomePage />
                  </>
                } 
              />
              <Route
                path="/categories"
                element={
                  <>
                    <Header />
                    <CategoriesPage />
                  </>
                }
              />
              <Route
                path="/categories/:productType"
                element={
                  <>
                    <Header />
                    <CategoryProductsPage />
                  </>
                }
              />
              <Route
                path="/stores"
                element={
                  <>
                    <Header />
                    <StoresPage />
                  </>
                }
              />
              <Route
                path="/stores/:slug"
                element={
                  <>
                    <Header />
                    <StorePage />
                  </>
                }
              />
              <Route 
                path="products/:productSlug" 
                element={
                  <>
                    <Header />
                    <ProductPage />
                  </>
                } 
              />
              <Route 
                path="/deal-review" 
                element={
                  <ProtectedRoute requireReviewAccess>
                    <>
                      <Header />
                      <DealReviewPage />
                    </>
                  </ProtectedRoute>
                }
              />
              <Route 
                path="/settings" 
                element={
                  <ProtectedRoute>
                    <>
                      <Header />
                      <SettingsPage />
                    </>
                  </ProtectedRoute>
                }
              />

              <Route
                path="/admin/manual-price"
                element={
                  <ProtectedRoute requireAdmin>
                    <>
                      <Header />
                      <AdminManualPriceTasksPage />
                    </>
                  </ProtectedRoute>
                }
              />
              <Route 
                path="/profile" 
                element={
                <>
                  <Header />
                  <ProfilePage />
                </>
          } 
              />              
              <Route 
                path="/profile/:username" 
                element={
                  <>
                    <Header />
                    <ProfilePage />
                  </>
                } 
              />
              <Route 
                path="/feed" 
                element={
                  <>
                    <Header />
                    <FeedPage />
                  </>
                } 
              />
                            <Route 
                path="/forgot-password" 
                element={
                  <>
                    <Header />
                    <ForgotPasswordPage />
                  </>
                } 
              />
                            <Route 
                path="/reset-password" 
                element={
                  <>
                    <Header />
                    <ResetPasswordPage />
                  </>
                } 
              />
                            <Route 
                path="/activate" 
                element={
                  <>
                    <Header />
                    <ActivateAccountPage />
                  </>
                } 
              />


              <Route path="/about" element={<AboutUs />} />
              <Route path="/contact" element={<Contact />} />
              <Route path="/faq" element={<FAQ />} />              
              <Route path="/privacy" element={<PrivacyPolicy />} />
              <Route path="/terms" element={<TermsOfService />} />
              <Route path="/cookies" element={<CookiePolicy />} />
              <Route path="/disclaimer" element={<Disclaimer />} />
              <Route path="/cookie-policy" element={<CookiePolicy />} />

            </Routes>
          </div>
          <Footer />
        </div>
      </GoogleOAuthProvider>
     </TermsConsentProvider>
    </AuthProvider>
    <CookieBanner />
    </CookieConsentProvider>
    </HelmetProvider>
   
  );
}

// Only allow users with allowReview to proceed
function RequireAllowReview({ children }) {
  const { user, loading } = useAuth();
  if (loading) return null; // or a spinner component

  const raw = user?.allowReview ?? user?.AllowReview;
  const canReview = typeof raw === 'string' ? raw.toLowerCase() === 'true' : Boolean(raw);

  if (!canReview) return <Navigate to="/" replace />;
  return children;
}

export default App;