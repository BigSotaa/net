﻿// Contains code from: https://stackoverflow.com/questions/5028598/hosting-external-app-in-wpf-window

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System;
using System.Windows.Threading;
using System.Diagnostics;
using NETworkManager.Utils;

namespace NETworkManager.Controls
{
    public partial class PuTTYControl : UserControl, INotifyPropertyChanged
    {
        #region PropertyChangedEventHandler
        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        #endregion

        #region Variables
        private bool _initialized = false;

        Process PuTTYProcess = null;
        IntPtr AppWin;

        DispatcherTimer resizeTimer = new DispatcherTimer();

        #endregion

        #region Constructor, load
        public PuTTYControl()
        {
            InitializeComponent();
            DataContext = this;

            resizeTimer.Tick += ResizeTimer_Tick;
            resizeTimer.Interval = new TimeSpan(0, 0, 0, 0, 500);

            Dispatcher.ShutdownStarted += Dispatcher_ShutdownStarted;
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            // Connect after the control is drawn and only on the first init
            if (!_initialized)
            {
                Connect();
                _initialized = true;
            }
        }

        private void Dispatcher_ShutdownStarted(object sender, EventArgs e)
        {
            //   OnClose();
        }
        #endregion

        #region ICommands & Actions



        #endregion

        #region Methods       
        private void Connect()
        {
            ProcessStartInfo info = new ProcessStartInfo
            {
                FileName = @"C:\Tools\PuTTY\putty.exe",
                Arguments = @"192.168.178.30"
            };

            PuTTYProcess = Process.Start(info);

            PuTTYProcess.WaitForInputIdle();

            AppWin = PuTTYProcess.MainWindowHandle;

            NativeMethods.SetParent(AppWin, panel.Handle);

            // Show window before set style and resize
            NativeMethods.ShowWindow(AppWin, NativeMethods.WindowShowStyle.Maximize);

            // Remove border etc.
            long style = NativeMethods.GetWindowLong(AppWin, NativeMethods.GWL_STYLE);
            style &= ~(NativeMethods.WS_BORDER | NativeMethods.WS_THICKFRAME);
            NativeMethods.SetWindowLong(AppWin, NativeMethods.GWL_STYLE, style);

            // Resize embedded application & refresh
            if (PuTTYProcess != null)
                ResizeEmbeddedPuTTY();
        }

        private void ResizeEmbeddedPuTTY()
        {
            NativeMethods.SetWindowPos(PuTTYProcess.MainWindowHandle, IntPtr.Zero, 0, 0, panel.ClientSize.Width, panel.ClientSize.Height, NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }
        #endregion

        #region Events
        private void puTTYGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (PuTTYProcess != null)
                ResizeEmbeddedPuTTY();
        }

        private void ResizeTimer_Tick(object sender, EventArgs e)
        {
            resizeTimer.Stop();

            ResizeEmbeddedPuTTY();
        }
        #endregion
    }
}