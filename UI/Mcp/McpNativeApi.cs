using Mesen.Debugger;
using Mesen.Interop;
using System;
using System.Runtime.InteropServices;

namespace Mesen.Mcp
{
	/// <summary>
	/// Native entry points that exist only for the MCP server, implemented in InteropDLL/McpApiWrapper.cpp.
	/// Kept out of the upstream interop classes so that rebasing the MCP patch set never conflicts on them.
	/// </summary>
	public static class McpNativeApi
	{
		private const string DllPath = EmuApi.DllName;

		[DllImport(DllPath)] public static extern Int32 TakeScreenshotToFile([MarshalAs(UnmanagedType.LPUTF8Str)] string filepath);

		[DllImport(DllPath, EntryPoint = "PeekDisassembly")] private static extern UInt32 PeekDisassemblyWrapper(CpuType type, UInt32 address, UInt32 instructionCount, [In, Out] InteropCodeLineData[] lineData, UInt32 maxRows);

		/// <summary>
		/// Decodes instructions starting exactly at <paramref name="address"/>, whether or not it is known to be code,
		/// without touching the CDL, the disassembly cache or the debugger settings. An address with a label gets an
		/// extra row (Flags = Label, Text = label) before its instruction.
		/// </summary>
		public static CodeLineData[] PeekDisassembly(CpuType type, UInt32 address, UInt32 instructionCount)
		{
			UInt32 maxRows = instructionCount * 2;
			InteropCodeLineData[] rows = new InteropCodeLineData[maxRows];
			for(int i = 0; i < maxRows; i++) {
				rows[i].Comment = new byte[1000];
				rows[i].Text = new byte[1000];
				rows[i].ByteCode = new byte[8];
			}

			UInt32 resultCount = PeekDisassemblyWrapper(type, address, instructionCount, rows, maxRows);

			CodeLineData[] result = new CodeLineData[resultCount];
			for(int i = 0; i < resultCount; i++) {
				result[i] = new CodeLineData(rows[i]);
			}
			return result;
		}
	}
}
