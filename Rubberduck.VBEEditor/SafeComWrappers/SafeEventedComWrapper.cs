﻿using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Rubberduck.VBEditor.SafeComWrappers.Abstract;

namespace Rubberduck.VBEditor.SafeComWrappers
{
    public abstract class SafeEventedComWrapper<TSource, TEventInterface> : SafeComWrapper<TSource>, ISafeEventedComWrapper
        where TSource : class
        where TEventInterface : class
    {
        private const int NotAdvising = -1;
        private readonly object _lock = new object();
        private IConnectionPoint _icp; // The connection point
        private int _cookie = NotAdvising;     // The cookie for the connection

        protected SafeEventedComWrapper(TSource target, bool rewrapping = false) : base(target, rewrapping)
        {
        }

        protected override void Dispose(bool disposing)
        {
            DetachEvents();
            base.Dispose(disposing);
        }

        public void AttachEvents()
        {
            if (IsWrappingNullReference)
            {
                return;
            }

            if (_cookie != NotAdvising)
            {
                return;
            }

            lock (_lock)
            {
                // Call QueryInterface for IConnectionPointContainer
                var icpc = (IConnectionPointContainer) Target;

                // Find the connection point for the source interface
                var g = typeof(TEventInterface).GUID;
                icpc.FindConnectionPoint(ref g, out _icp);

                // Pass a pointer to the host to the connection point
                _icp.Advise(this as TEventInterface, out _cookie);
            }
        }

        public void DetachEvents()
        {
            lock (_lock)
            {
                if (_icp == null)
                {
                    return;
                }

                if (_cookie != NotAdvising)
                {
                    _icp.Unadvise(_cookie);
                }

                Marshal.ReleaseComObject(_icp);
                _icp = null;
            }
        }
    }
}
