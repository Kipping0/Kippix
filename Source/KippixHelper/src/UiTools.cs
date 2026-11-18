using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Playnite.SDK;

namespace KippixHelper
{
    /// <summary>Small helpers for finding the theme's elements in Playnite's window and moving controller focus.</summary>
    internal static class UiTools
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private static Window MainWindow()
        {
            return Application.Current != null ? Application.Current.MainWindow : null;
        }

        public static FrameworkElement FindByName(DependencyObject root, string name)
        {
            if (root == null) return null;
            var queue = new Queue<DependencyObject>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var d = queue.Dequeue();
                var fe = d as FrameworkElement;
                if (fe != null && fe.Name == name) return fe;
                int n = 0;
                try { n = VisualTreeHelper.GetChildrenCount(d); } catch (Exception) { }
                for (int i = 0; i < n; i++) queue.Enqueue(VisualTreeHelper.GetChild(d, i));
            }
            return null;
        }

        /// <summary>Every element of a type under root (visual tree, breadth first).</summary>
        public static List<T> FindAll<T>(DependencyObject root) where T : class
        {
            var found = new List<T>();
            if (root == null) return found;
            var queue = new Queue<DependencyObject>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var d = queue.Dequeue();
                var t = d as T;
                if (t != null) found.Add(t);
                int n = 0;
                try { n = VisualTreeHelper.GetChildrenCount(d); } catch (Exception) { }
                for (int i = 0; i < n; i++) queue.Enqueue(VisualTreeHelper.GetChild(d, i));
            }
            return found;
        }

        public static T FindChild<T>(DependencyObject root) where T : class
        {
            if (root == null) return null;
            var queue = new Queue<DependencyObject>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var d = queue.Dequeue();
                var t = d as T;
                if (t != null && !ReferenceEquals(d, root)) return t;
                int n = 0;
                try { n = VisualTreeHelper.GetChildrenCount(d); } catch (Exception) { }
                for (int i = 0; i < n; i++) queue.Enqueue(VisualTreeHelper.GetChild(d, i));
            }
            return null;
        }

        private static UIElement FirstFocusable(DependencyObject root)
        {
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var ui = child as UIElement;
                if (ui != null && !ui.IsVisible) continue;
                if (child is Control && ui.Focusable && ui.IsEnabled) return ui;
                var deeper = FirstFocusable(child);
                if (deeper != null) return deeper;
            }
            return null;
        }

        /// <summary>Moves focus to the first button inside a panel, or to the panel itself so B still closes it.</summary>
        public static void FocusInside(string name)
        {
            try
            {
                var panel = FindByName(MainWindow(), name);
                if (panel == null) { logger.Warn("Kippix: panel not found in theme: " + name); return; }
                var target = FirstFocusable(panel);
                if (target == null)
                {
                    panel.Focusable = true;
                    panel.FocusVisualStyle = null;
                    target = panel;
                }
                Keyboard.Focus(target);
            }
            catch (Exception ex) { logger.Error(ex, "Kippix: couldn't move focus into " + name); }
        }

        public static void FocusByName(string name)
        {
            try
            {
                var el = FindByName(MainWindow(), name);
                if (el != null && el.IsVisible) Keyboard.Focus(el);
            }
            catch (Exception) { }
        }

        /// <summary>Focuses a named element if it's on screen; false when it isn't.</summary>
        public static bool FocusByNameIfVisible(string name)
        {
            try
            {
                var el = FindByName(MainWindow(), name);
                if (el == null || !el.IsVisible) return false;
                Keyboard.Focus(el);
                return el.IsKeyboardFocused;
            }
            catch (Exception) { return false; }
        }

        /// <summary>Focuses a named element inside the row of an ItemsControl that shows the given item.</summary>
        public static void FocusItemPart(string listName, object item, string partName)
        {
            try
            {
                var list = FindByName(MainWindow(), listName) as ItemsControl;
                if (list == null) return;
                var container = list.ItemContainerGenerator.ContainerFromItem(item) as DependencyObject;
                // a row that was moved keeps the highlight, so nothing scrolls by itself: its whole row is brought into view
                var row = container as FrameworkElement;
                if (row != null) row.BringIntoView();
                var part = container != null ? FindByName(container, partName) : null;
                if (part != null) Keyboard.Focus(part);
            }
            catch (Exception) { }
        }

        /// <summary>Focuses the first button in the row of an ItemsControl that shows the given item (scrolling it into view).</summary>
        public static void FocusItem(string listName, object item)
        {
            try
            {
                if (item == null) return;
                var list = FindByName(MainWindow(), listName) as ItemsControl;
                if (list == null) return;
                var container = list.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;
                if (container == null) return;
                container.BringIntoView();
                var target = (container is Control && container.Focusable) ? container : FirstFocusable(container);
                if (target != null) Keyboard.Focus(target);
            }
            catch (Exception) { }
        }

        /// <summary>Focuses the visible button inside a panel whose CommandParameter is the given text (an options row). True if found.</summary>
        public static bool FocusCommandParameter(string panelName, string parameter)
        {
            try
            {
                var panel = FindByName(MainWindow(), panelName);
                var target = panel != null ? FindWithParameter(panel, parameter) : null;
                if (target == null) return false;
                var fe = target as FrameworkElement;
                if (fe != null) fe.BringIntoView();
                return Keyboard.Focus(target) == target;
            }
            catch (Exception) { return false; }
        }

        private static UIElement FindWithParameter(DependencyObject root, string parameter)
        {
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var ui = child as UIElement;
                if (ui != null && !ui.IsVisible) continue;
                var b = child as ButtonBase;
                if (b != null && b.Focusable && parameter.Equals(b.CommandParameter as string)) return b;
                var deeper = FindWithParameter(child, parameter);
                if (deeper != null) return deeper;
            }
            return null;
        }

        /// <summary>Scrolls the cover row back to the first game and selects it.</summary>
        public static void ScrollLibraryToStart()
        {
            var list = FindByName(MainWindow(), "PART_ListGameItems") as ListBox;
            if (list == null || list.Items.Count == 0) return;
            try
            {
                var panel = FindChild<IScrollInfo>(list);
                if (panel != null) panel.SetHorizontalOffset(0);
            }
            catch (Exception) { }
            list.SelectedIndex = 0;
            list.Dispatcher.BeginInvoke(new Action(() =>
            {
                var item = list.ItemContainerGenerator.ContainerFromIndex(0) as UIElement;
                if (item != null) Keyboard.Focus(item);
                else Keyboard.Focus(list);
            }), DispatcherPriority.ContextIdle);
        }

        /// <summary>Runs the first ICommand property found on the object (or on its Settings / ViewModel objects).</summary>
        public static bool TryRunCommand(object target, string[] names)
        {
            foreach (object o in Candidates(target))
            {
                foreach (string n in names)
                {
                    try
                    {
                        PropertyInfo p = o.GetType().GetProperty(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                        var cmd = p != null ? p.GetValue(p.GetGetMethod(true).IsStatic ? null : o, null) as ICommand : null;
                        if (cmd != null && cmd.CanExecute(null))
                        {
                            cmd.Execute(null);
                            return true;
                        }
                    }
                    catch (Exception ex) { logger.Warn(ex, "Kippix: command " + n + " failed"); }
                }
            }
            return false;
        }

        /// <summary>Calls the first parameterless method found with one of the names.</summary>
        public static bool TryRunMethod(object target, string[] names)
        {
            foreach (object o in Candidates(target))
            {
                foreach (string n in names)
                {
                    try
                    {
                        MethodInfo m = o.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                            .FirstOrDefault(x => x.Name == n && x.GetParameters().Length == 0);
                        if (m != null)
                        {
                            m.Invoke(m.IsStatic ? null : o, null);
                            return true;
                        }
                    }
                    catch (Exception ex) { logger.Warn(ex, "Kippix: method " + n + " failed"); }
                }
            }
            return false;
        }

        private static IEnumerable<object> Candidates(object target)
        {
            yield return target;
            foreach (string n in new[] { "Settings", "SettingsViewModel", "ViewModel", "Theme", "ThemeSettings" })
            {
                object v = null;
                try
                {
                    PropertyInfo p = target.GetType().GetProperty(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (p != null) v = p.GetValue(target, null);
                }
                catch (Exception) { }
                if (v != null)
                {
                    yield return v;
                    object inner = null;
                    try
                    {
                        PropertyInfo p2 = v.GetType().GetProperty("Settings", BindingFlags.Public | BindingFlags.Instance);
                        if (p2 != null) inner = p2.GetValue(v, null);
                    }
                    catch (Exception) { }
                    if (inner != null) yield return inner;
                }
            }
        }
    }
}
