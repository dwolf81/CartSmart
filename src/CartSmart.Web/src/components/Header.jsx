import React, { useState, useRef, useEffect } from 'react';
import { Link } from 'react-router-dom';
import { useAuth } from '../context/AuthContext';
import { Menu } from '@headlessui/react';
import { UserCircleIcon } from '@heroicons/react/24/outline';
import { FaBell } from 'react-icons/fa';
import { useNotifications } from '../hooks/useNotifications';
import { Bars3Icon, XMarkIcon } from '@heroicons/react/24/outline';
import CookiePreferencesModal from './CookiePreferencesModal';

const Header = () => {
    const { isAuthenticated, user, logout, loading } = useAuth();
    const [isMenuOpen, setIsMenuOpen] = useState(false);
    const [isNotifOpen, setIsNotifOpen] = useState(false);
    const [mobileOpen, setMobileOpen] = useState(false);
    const [mobileNotifOpen, setMobileNotifOpen] = useState(false); // NEW
    const [cookieModalOpen, setCookieModalOpen] = useState(false);
    const {
        items: notifications,
        unread,
        loading: notifLoading,
        hasMore,
        loadMore,
        markRead,
        markAllRead
    } = useNotifications();

    const notifButtonRef = useRef(null);
    const notifPanelRef = useRef(null);

    useEffect(() => {
        if (!isNotifOpen) return;
        function handleClick(e) {
            if (
                notifPanelRef.current &&
                !notifPanelRef.current.contains(e.target) &&
                notifButtonRef.current &&
                !notifButtonRef.current.contains(e.target)
            ) {
                setIsNotifOpen(false);
            }
        }
        function handleKey(e) {
            if (e.key === 'Escape') setIsNotifOpen(false);
        }
        document.addEventListener('mousedown', handleClick);
        document.addEventListener('keydown', handleKey);
        return () => {
            document.removeEventListener('mousedown', handleClick);
            document.removeEventListener('keydown', handleKey);
        };
    }, [isNotifOpen]);

    const toggleMenu = () => {
        setIsMenuOpen(!isMenuOpen);
    };

    const handleLogout = async () => {
        try {
            await logout();
            window.location.href = '/';
        } catch (e) {
            console.error('Logout failed', e);
        }
    };

    return (
        <header className="bg-white shadow-md">
            <nav className="container mx-auto px-4 py-4">
                <div className="flex items-center justify-between">
                    {/* Left cluster: logo + primary nav (desktop) */}
                    <div className="flex items-center space-x-8"> {/* increased spacing */}
                        <Link to="/" className="text-2xl font-bold text-[#4CAF50]">
                            CartSmart
                        </Link>
                         <div className="hidden md:flex items-center space-x-8 ml-4"> 
                            <Link to="/categories" className="text-gray-600 hover:text-[#4CAF50]">Categories</Link>                            
                            <Link to="/stores" className="text-gray-600 hover:text-[#4CAF50]">Stores</Link>
                        </div>
                    </div>

                    {/* Right cluster: auth / notifications (desktop) + hamburger */}
                    <div className="flex items-center space-x-4">
                        {/* Hamburger (mobile only) */}
                        <button
                            type="button"
                            className="md:hidden inline-flex items-center justify-center p-2 rounded hover:bg-gray-100"
                            aria-label={mobileOpen ? 'Close menu' : 'Open menu'}
                            onClick={() => setMobileOpen(o => !o)}
                        >
                            {mobileOpen ? (
                                <XMarkIcon className="h-6 w-6 text-gray-700" />
                            ) : (
                                <Bars3Icon className="h-6 w-6 text-gray-700" />
                            )}
                        </button>

                        {/* Desktop auth / notifications (hidden when mobile menu open) */}
                        <div className={`hidden md:flex items-center space-x-4`}>
                            {loading ? (
                                <div className="animate-pulse w-20 h-8 bg-gray-200 rounded" />
                            ) : isAuthenticated ? (
                                <div className="flex items-center space-x-4">
                                    {/* Notifications */}
                                    <div className="relative">
                                        <button
                                            ref={notifButtonRef}                     // ADD
                                            type="button"
                                            onClick={() => setIsNotifOpen(o => !o)}
                                            className="relative p-2 rounded-full hover:bg-gray-100 transition"
                                            aria-label="Notifications"
                                        >
                                            <FaBell className="w-5 h-5 text-gray-600" />
                                            {unread > 0 && (
                                                <span className="absolute -top-1 -right-1 bg-red-600 text-white text-[10px] leading-none rounded-full px-1 py-[2px] min-w-[20px] text-center font-semibold shadow">
                                                    {unread}
                                                </span>
                                            )}
                                        </button>
                                        {isNotifOpen && (
                                            <div
                                                ref={notifPanelRef}                  // ADD
                                                className="absolute right-0 mt-2 w-80 max-h-[70vh] bg-white border border-gray-200 rounded-md shadow-lg flex flex-col z-50"
                                            >
                                                <div className="flex items-center justify-between px-4 py-2 border-b">
                                                    <span className="text-sm font-semibold">Notifications</span>
                                                    {unread > 0 && (
                                                        <button
                                                            onClick={markAllRead}
                                                            className="text-xs text-[#4CAF50] hover:underline"
                                                        >
                                                            Mark all read
                                                        </button>
                                                    )}
                                                </div>
                                                <div className="flex-1 overflow-y-auto">
                                                    {notifLoading && notifications.length === 0 && (
                                                        <div className="px-4 py-4 text-sm text-gray-500">Loading...</div>
                                                    )}
                                                    {!notifLoading && notifications.length === 0 && (
                                                        <div className="px-4 py-6 text-center text-sm text-gray-500">
                                                            No notifications
                                                        </div>
                                                    )}
                                                    {notifications.map(n => (
                                                        <button
                                                            key={n.id}
                                                            type="button"
                                                            onClick={() => {
                                                                if (!n.isRead) markRead(n.id);
                                                                if (n.linkUrl) window.location.href = n.linkUrl;
                                                                setIsNotifOpen(false);
                                                            }}
                                                            className={`w-full text-left px-4 py-3 border-b last:border-b-0 text-sm hover:bg-gray-50 ${
                                                                !n.isRead ? 'bg-blue-50' : 'bg-white'
                                                            }`}
                                                        >
                                                            <div className="flex justify-between gap-3">
                                                                <div className="flex-1">
                                                                    <p className="text-gray-900">{n.message}</p>
                                                                    <p className="text-xs text-gray-500 mt-1">
                                                                        {new Date(n.createdAt).toLocaleString()}
                                                                    </p>
                                                                </div>
                                                                {!n.isRead && <span className="h-2 w-2 rounded-full bg-blue-600 mt-1" />}
                                                            </div>
                                                        </button>
                                                    ))}
                                                </div>
                                                {(hasMore || notifLoading) && (
                                                    <div className="px-4 py-2 border-t">
                                                        <button
                                                            type="button"
                                                            onClick={loadMore}
                                                            disabled={notifLoading}
                                                            className="text-xs text-[#4CAF50] hover:underline disabled:opacity-50"
                                                        >
                                                            {notifLoading ? 'Loading...' : 'Load more'}
                                                        </button>
                                                    </div>
                                                )}
                                            </div>
                                        )}
                                    </div>
                                    {/* User menu */}
                                    <Menu as="div" className="relative inline-flex items-center">
                                        <Menu.Button className="inline-flex items-center justify-center h-8 w-8">
                                            <img
                                                src={(user?.imageUrl?.replace('_100x100.webp', '_32x32.webp')) || user?.imageUrl || '/default-avatar.png'}
                                                alt="User Avatar"
                                                className="h-8 w-8 rounded-full"
                                            />
                                        </Menu.Button>
                                        <Menu.Items className="absolute right-0 top-full mt-3 w-48 bg-white rounded-md shadow-lg py-1 z-50">
                                            <Menu.Item>
                                                {({ active }) => (
                                                    <Link
                                                        to="/profile"
                                                        className={`${active ? 'bg-gray-100' : ''} block px-4 py-2 text-sm text-gray-700`}
                                                    >
                                                        Profile
                                                    </Link>
                                                )}
                                            </Menu.Item>
                                            <Menu.Item>
                                                {({ active }) => (
                                                    <Link
                                                        to="/settings"
                                                        className={`${active ? 'bg-gray-100' : ''} block px-4 py-2 text-sm text-gray-700`}
                                                    >
                                                        Settings
                                                    </Link>
                                                )}
                                            </Menu.Item>
                                            {Boolean(user?.allowReview) && (
                                                <Menu.Item>
                                                    {({ active }) => (
                                                        <Link
                                                            to="/deal-review"
                                                            className={`${active ? 'bg-gray-100' : ''} block px-4 py-2 text-sm text-gray-700`}
                                                        >
                                                            Review Deals
                                                        </Link>
                                                    )}
                                                </Menu.Item>
                                            )}

                                            {Boolean(user?.admin) && (
                                                <Menu.Item>
                                                    {({ active }) => (
                                                        <Link
                                                            to="/admin/manual-price"
                                                            className={`${active ? 'bg-gray-100' : ''} block px-4 py-2 text-sm text-gray-700`}
                                                        >
                                                            Manual Price Tasks
                                                        </Link>
                                                    )}
                                                </Menu.Item>
                                            )}
                                            <Menu.Item>
                                                {({ active }) => (
                                                    <button
                                                        type="button"
                                                        onClick={handleLogout}
                                                        className={`${active ? 'bg-gray-100' : ''} block w-full text-left px-4 py-2 text-sm text-gray-700`}
                                                    >
                                                        Log out
                                                    </button>
                                                )}
                                            </Menu.Item>
                                        </Menu.Items>
                                    </Menu>
                                </div>
                            ) : (
                                <>
                                    <Link to="/login" className="text-gray-600 hover:text-[#4CAF50]">Log in</Link>
                                    <Link to="/signup" className="bg-[#4CAF50] text-white px-4 py-2 rounded-md hover:bg-[#3d8b40]">Sign up</Link>
                                </>
                            )}
                        </div>
                    </div>
                </div>
            </nav>

            {/* Mobile unified menu (navigation + account) */}
            {mobileOpen && (
                <div className="md:hidden border-t border-gray-200 bg-white">
                    <div className="px-4 py-4 space-y-6">
                        {/* Navigation section */}
                        <nav aria-label="Primary" className="flex flex-col space-y-1">
                            <Link
                                to="/categories"
                                onClick={() => { setMobileOpen(false); setMobileNotifOpen(false); }}
                                className="text-gray-700 py-2 px-2 rounded hover:bg-gray-100"
                            >
                                Categories
                            </Link>
                            <Link
                                to="/stores"
                                onClick={() => { setMobileOpen(false); setMobileNotifOpen(false); }}
                                className="text-gray-700 py-2 px-2 rounded hover:bg-gray-100"
                            >
                                Stores
                            </Link>
                        </nav>
                        {/* Notifications (moved below navigation for consistency) */}
                        {isAuthenticated && (
                            <div>
                                <button
                                    type="button"
                                    onClick={() => setMobileNotifOpen(o => !o)}
                                    className="flex items-center justify-between w-full py-2 px-2 rounded hover:bg-gray-100"
                                >
                                    <span className="text-gray-700">Notifications</span>
                                    <span className="flex items-center space-x-2">
                                        {unread > 0 && (
                                            <span className="bg-red-600 text-white text-[10px] leading-none rounded-full px-2 py-[3px] font-semibold">
                                                {unread}
                                            </span>
                                        )}
                                        <svg
                                            className={`h-4 w-4 text-gray-500 transition-transform ${mobileNotifOpen ? 'rotate-180' : ''}`}
                                            fill="none" stroke="currentColor" strokeWidth="2" viewBox="0 0 24 24"
                                        >
                                            <path strokeLinecap="round" strokeLinejoin="round" d="M6 9l6 6 6-6" />
                                        </svg>
                                    </span>
                                </button>
                                {mobileNotifOpen && (
                                    <div className="mt-2 border border-gray-200 rounded-md overflow-hidden">
                                        <div className="max-h-72 overflow-y-auto">
                                            {notifLoading && notifications.length === 0 && (
                                                <div className="px-4 py-4 text-sm text-gray-500">Loading...</div>
                                            )}
                                            {!notifLoading && notifications.length === 0 && (
                                                <div className="px-4 py-4 text-sm text-gray-500">No notifications</div>
                                            )}
                                            {notifications.map(n => (
                                                <button
                                                    key={n.id}
                                                    type="button"
                                                    onClick={() => {
                                                        if (!n.isRead) markRead(n.id);
                                                        if (n.linkUrl) window.location.href = n.linkUrl;
                                                        setMobileNotifOpen(false);
                                                        setMobileOpen(false);
                                                    }}
                                                    className={`w-full text-left px-4 py-3 border-b last:border-b-0 text-sm ${
                                                        !n.isRead ? 'bg-blue-50' : 'bg-white'
                                                    } hover:bg-gray-50`}
                                                >
                                                    <p className="text-gray-900">{n.message}</p>
                                                    <p className="text-xs text-gray-500 mt-1">
                                                        {new Date(n.createdAt).toLocaleString()}
                                                    </p>
                                                </button>
                                            ))}
                                        </div>
                                        {(hasMore || notifLoading) && (
                                            <div className="px-4 py-2 bg-white">
                                                <button
                                                    type="button"
                                                    onClick={loadMore}
                                                    disabled={notifLoading}
                                                    className="text-xs text-[#4CAF50] hover:underline disabled:opacity-50"
                                                >
                                                    {notifLoading ? 'Loading...' : 'Load more'}
                                                </button>
                                                {unread > 0 && (
                                                    <button
                                                        type="button"
                                                        onClick={markAllRead}
                                                        className="ml-4 text-xs text-[#4CAF50] hover:underline"
                                                    >
                                                        Mark all read
                                                    </button>
                                                )}
                                            </div>
                                        )}
                                    </div>
                                )}
                            </div>
                        )}

                        {/* Account section */}
                        {isAuthenticated ? (
                            <div className="pt-2 border-t">
                                <div className="flex items-center space-x-3 mb-3">
                                    <img
                                        src={(user?.imageUrl?.replace('_100x100.webp', '_32x32.webp')) || user?.imageUrl || '/default-avatar.png'}
                                        alt="User"
                                        className="h-10 w-10 rounded-full"
                                    />
                                    <div className="text-sm">
                                        <p className="font-medium text-gray-800">{user?.displayName || user?.firstName || 'User'}</p>
                                        <p className="text-xs text-gray-500">{user?.email}</p>
                                    </div>
                                </div>
                                <nav aria-label="Account" className="flex flex-col space-y-1">
                                    <Link
                                        to="/profile"
                                        onClick={() => setMobileOpen(false)}
                                        className="text-gray-700 py-2 px-2 rounded hover:bg-gray-100"
                                    >
                                        Profile
                                    </Link>
                                    <Link
                                        to="/settings"
                                        onClick={() => setMobileOpen(false)}
                                        className="text-gray-700 py-2 px-2 rounded hover:bg-gray-100"
                                    >
                                        Settings
                                    </Link>
                                    {Boolean(user?.allowReview) && (
                                        <Link
                                            to="/deal-review"
                                            onClick={() => setMobileOpen(false)}
                                            className="text-gray-700 py-2 px-2 rounded hover:bg-gray-100"
                                        >
                                            Review Deals
                                        </Link>
                                    )}
                                    {Boolean(user?.admin) && (
                                        <Link
                                            to="/admin/manual-price"
                                            onClick={() => setMobileOpen(false)}
                                            className="text-gray-700 py-2 px-2 rounded hover:bg-gray-100"
                                        >
                                            Manual Price Tasks
                                        </Link>
                                    )}
                                    <button
                                        type="button"
                                        onClick={() => { handleLogout(); setMobileOpen(false); }}
                                        className="text-left text-gray-700 py-2 px-2 rounded hover:bg-gray-100"
                                    >
                                        Log out
                                    </button>
                                </nav>
                            </div>
                        ) : (
                            <div className="pt-2 border-t">
                                <nav aria-label="Account actions" className="flex flex-col space-y-1">
                                    <Link
                                        to="/login"
                                        onClick={() => setMobileOpen(false)}
                                        className="text-gray-700 py-2 px-2 rounded hover:bg-gray-100"
                                    >
                                        Log in
                                    </Link>
                                    <Link
                                        to="/signup"
                                        onClick={() => setMobileOpen(false)}
                                        className="text-gray-700 py-2 px-2 rounded hover:bg-gray-100"
                                    >
                                        Sign up
                                    </Link>
                                </nav>
                            </div>
                        )}
                        {/* Add footer links cluster */}
                        <div className="pt-4 border-t">
                            <Link
                                to="/cookie-policy"
                                onClick={() => setMobileOpen(false)}
                                className="text-gray-700 py-2 px-2 rounded hover:bg-gray-100 text-xs"
                            >
                                Privacy Choices
                            </Link>
                            <button
                                type="button"
                                onClick={() => { setCookieModalOpen(true); }}
                                className="text-left text-gray-700 py-2 px-2 rounded hover:bg-gray-100 text-xs"
                            >
                                Do Not Sell or Share
                            </button>
                        </div>
                    </div>
                </div>
            )}
            {cookieModalOpen && (
                <CookiePreferencesModal open={cookieModalOpen} onClose={() => setCookieModalOpen(false)} />
            )}
        </header>
    );
};

export default Header;