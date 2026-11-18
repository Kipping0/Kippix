using System;

namespace KippixOverlay
{
    /// <summary>
    /// (1.0.4, issue: Bencey17) The controller drives a streaming service Kippix opened full screen in a browser
    /// (ThemeApi.StreamOpen.cs), like Discord's controller mode: the left stick moves the mouse, the right stick scrolls,
    /// A clicks (hold to drag), X is play / pause (Space), B goes back (Alt+Left), Y brings up the on-screen keyboard (it also
    /// comes up by itself on a text box), the D-pad is the arrow keys (seek and volume in the players), LB / RB step through
    /// the page's links and buttons (Shift+Tab / Tab), Menu is Enter, View is Esc, L3 is F (the players' full screen),
    /// R3 is a right-click. Guide opens the overlay over it (Close closes just that window); when the overlay closes the
    /// controller drives the browser again. Only while that window is in front: switch away and the controller is left alone.
    /// </summary>
    public partial class OverlayPlugin
    {
        internal IntPtr browserHwnd = IntPtr.Zero;
        private bool browserMode;        // a streaming window is being driven (or waits in the background)
        private bool browserActive;      // ... and it's in front right now, with nothing of ours over it
        private BrowserHintBar browserHints;
        private System.Threading.Timer browserWatch;

        internal bool BrowserModeOn { get { return browserMode && browserActive; } }

        /// <summary>Kippix opened a streaming service full screen: the controller drives that window from now on.</summary>
        public void BeginBrowserMode(IntPtr h)
        {
            OnUi(() =>
            {
                if (h == IntPtr.Zero || !Native.IsWindow(h) || Poller == null) return;
                EndBrowserMode();
                browserHwnd = h;
                browserMode = true;
                SetBrowserActive(true);
                try { browserHints = new BrowserHintBar(h, Theme, Poller.Kind); browserHints.Show(); } catch (Exception ex) { logger.Warn("Kippix overlay: browser hints: " + ex.Message); }
                if (browserWatch == null) browserWatch = new System.Threading.Timer(_ => OnUi(WatchBrowser), null, 700, 600);
                logger.Info("Kippix overlay: the controller drives the streaming window");
            });
        }

        internal void EndBrowserMode()
        {
            if (!browserMode && browserWatch == null) return;
            SetBrowserActive(false);
            browserMode = false;
            browserHwnd = IntPtr.Zero;
            if (browserWatch != null) { browserWatch.Dispose(); browserWatch = null; }
            if (browserHints != null) { try { browserHints.Close(); } catch (Exception) { } browserHints = null; }
            HideKeyboard(false);
        }

        private void SetBrowserActive(bool on)
        {
            if (browserActive == on) return;
            browserActive = on;
            if (Poller != null) Poller.MouseMode = on;
            if (!on)
            {
                // nothing left held down
                if ((lastHeld & Btn.A) != 0) Native.mouse_event(Native.MOUSE_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                HideKeyboard(false);
            }
        }

        /// <summary>Every 0.6 s: the window gone (the mode ends), or not in front (the controller is left alone meanwhile).</summary>
        private void WatchBrowser()
        {
            if (!browserMode) return;
            if (!Native.IsWindow(browserHwnd) || !Native.IsWindowVisible(browserHwnd))
            {
                logger.Info("Kippix overlay: the streaming window closed (controller mode off)");
                EndBrowserMode();
                return;
            }
            bool overlayUp = overlay != null && (overlay.IsOpen || overlay.IsClosing);
            IntPtr fg = Native.GetForegroundWindow();
            bool ours = Native.GetProcessId(fg) == ownPid;   // our keyboard / hint bar count as the browser's
            bool front = !overlayUp && powerMenu == null && (fg == browserHwnd || (ours && keyboard != null));
            SetBrowserActive(front);
            if (!front) return;
            // a text box picked: the keyboard comes up (as in Discord's controller mode)
            if (focusBusy) return;
            focusBusy = true;
            try
            {
                string key = DiscordHelper.FocusedTextBoxKey();
                lastFocusKey = key;
                if (key == null) dismissedKey = null;
                else if (keyboard == null && key != dismissedKey) ShowKeyboard();
            }
            catch (Exception) { }
            finally { focusBusy = false; }
        }

        /// <summary>After the overlay closes: the streaming window in front again, the controller drives it again.</summary>
        private void BrowserModeAfterOverlay()
        {
            if (!browserMode) return;
            if (!Native.IsWindow(browserHwnd)) { EndBrowserMode(); return; }
            RunLater(() => OnUi(WatchBrowser), 400);
        }

        /// <summary>A controller press while the streaming window is driven: true when it was used.</summary>
        private bool BrowserModeHandles(ushort pressed, ushort released)
        {
            if (!BrowserModeOn) return false;
            if (overlay != null && (overlay.IsOpen || overlay.IsClosing)) return false;
            if ((pressed & Btn.Guide) != 0)
            {
                // the overlay over it (Close closes just this window)
                SetBrowserActive(false);
                GameWindow = browserHwnd;
                OpenOverlay(0);
                return true;
            }
            if (keyboard != null) { keyboard.HandlePress(pressed); return true; }
            if ((pressed & Btn.A) != 0) Native.mouse_event(Native.MOUSE_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            if ((released & Btn.A) != 0) Native.mouse_event(Native.MOUSE_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            if ((pressed & Btn.RThumb) != 0) { Native.mouse_event(Native.MOUSE_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero); Native.mouse_event(Native.MOUSE_RIGHTUP, 0, 0, 0, UIntPtr.Zero); }
            if ((pressed & Btn.X) != 0) Native.TapKey(0x20);              // Space: play / pause
            if ((pressed & Btn.B) != 0) Native.TapCombo(0x12, 0x25);      // Alt+Left: back
            if ((pressed & Btn.Y) != 0) ShowKeyboard();
            if ((pressed & Btn.Start) != 0) Native.TapKey(0x0D);          // Enter
            if ((pressed & Btn.Back) != 0) Native.TapKey(0x1B);           // Esc
            if ((pressed & Btn.LThumb) != 0) Native.TapKey(0x46);         // F: the player's full screen
            if ((pressed & Btn.LB) != 0) Native.TapCombo(0x10, 0x09);     // Shift+Tab
            if ((pressed & Btn.RB) != 0) Native.TapKey(0x09);             // Tab
            if ((pressed & Btn.Up) != 0) Native.TapKey(0x26);
            if ((pressed & Btn.Down) != 0) Native.TapKey(0x28);
            if ((pressed & Btn.Left) != 0) Native.TapKey(0x25);
            if ((pressed & Btn.Right) != 0) Native.TapKey(0x27);
            return true;
        }
    }
}
