using Mesen.Interop;
using System.Runtime.InteropServices;

namespace Mesen.Mcp
{
	/// <summary>
	/// Remembers why emulation last stopped in the debugger (breakpoint, step, pause...), for mesen_get_status.
	/// The core only reports this through the CodeBreak notification, so this listens to notifications for as
	/// long as the MCP server runs.
	/// </summary>
	public static class McpBreakTracker
	{
		private static readonly object _lock = new();
		private static NotificationListener? _listener;
		private static BreakEvent? _lastBreak;

		/// <summary>The event that stopped emulation, or null if it is running or stopped for an unknown reason.</summary>
		public static BreakEvent? LastBreak
		{
			get { lock(_lock) { return _lastBreak; } }
		}

		public static void Start()
		{
			lock(_lock) {
				if(_listener == null) {
					_listener = new NotificationListener();
					_listener.OnNotification += OnNotification;
				}
			}
		}

		public static void Stop()
		{
			lock(_lock) {
				_listener?.Dispose();
				_listener = null;
				_lastBreak = null;
			}
		}

		private static void OnNotification(NotificationEventArgs e)
		{
			switch(e.NotificationType) {
				case ConsoleNotificationType.CodeBreak:
					BreakEvent evt = Marshal.PtrToStructure<BreakEvent>(e.Parameter);
					lock(_lock) {
						_lastBreak = evt;
					}
					break;

				case ConsoleNotificationType.DebuggerResumed:
				case ConsoleNotificationType.GameLoaded:
				case ConsoleNotificationType.EmulationStopped:
					lock(_lock) {
						_lastBreak = null;
					}
					break;
			}
		}
	}
}
